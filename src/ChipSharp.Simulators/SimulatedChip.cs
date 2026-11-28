using System.Globalization;

namespace ChipSharp.Simulators;

/// <summary>
/// What a program did that the chip cannot do (an instruction it does not have, an address with no memory, a stack past
/// the end of RAM): the run stops there as a failure, with this message.
/// </summary>
public sealed class ProgramFault(string message) : Exception(message);

/// <summary>
/// A simulated chip: its CPU, memories and peripherals, and the simulated time. Time is counted in ticks of a 16 MHz
/// base clock (62.5 ns), which both chips' clocks divide (the STM8S103's HSI is 16 MHz, the PFS154's IHRC too); a CPU
/// cycle is as many ticks as its clock divides that by. The chip prints what it does (pin changes, serial lines) with
/// the time, as tools/avr-sim/run.mjs does, through <see cref="Log"/>, and keeps a timeline of events: the inputs a
/// simulation gives (<see cref="At"/>), and the chip's own (a PWM output that went steady).
/// </summary>
public abstract class SimulatedChip
{
    /// <summary>The base clock: ticks a second.</summary>
    public const long TicksPerSecond = 16_000_000;

    private readonly PriorityQueue<(Action Action, bool External), (long Tick, long Order)> _events = new();
    private long _order;
    private int _externalEvents;

    /// <summary>The simulated time, in ticks of <see cref="TicksPerSecond"/>.</summary>
    public long Ticks { get; protected set; }

    /// <summary>
    /// Sets the time for a moment, for what a peripheral prints while it catches up with an instruction's cycles: the
    /// tick its edge really came at, before the end of the instruction. The chip sets it back.
    /// </summary>
    internal void SetTime(long ticks) => Ticks = ticks;

    /// <summary>Why the run stopped (the program finished, or failed); null while it runs.</summary>
    public string? StopReason { get; private set; }

    /// <summary>Whether the run stopped because the program went wrong (a fault, a breakpoint, a watchdog reset).</summary>
    public bool Failed { get; private set; }

    /// <summary>Where the lines the chip prints go, each with its time (<see cref="Print"/>).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The chip's name, e.g. "PFS154".</summary>
    public abstract string Name { get; }

    /// <summary>The chip's GPIO ports.</summary>
    public abstract IReadOnlyList<GpioPort> Ports { get; }

    /// <summary>The serial port --send sends to, or null when the chip has none.</summary>
    public virtual ISerialInput? SerialInput => null;

    /// <summary>What the chip's serial port has written, or null when it has none.</summary>
    public virtual SerialMonitor? SerialOutput => null;

    /// <summary>Every serial port --send can send to (by its name: --send UART0=...), the default one (<see cref="SerialInput"/>) first.</summary>
    public virtual IReadOnlyList<ISerialInput> SerialInputs => SerialInput is { } input ? [input] : [];

    /// <summary>What every serial port has written, the default one (<see cref="SerialOutput"/>) first.</summary>
    public virtual IReadOnlyList<SerialMonitor> SerialOutputs => SerialOutput is { } output ? [output] : [];

    /// <summary>How many bytes of program memory the image filled.</summary>
    public abstract int ImageBytes { get; }

    /// <summary>The RAM, from data address 0.</summary>
    public abstract byte[] Ram { get; }

    /// <summary>The address of the next instruction, as the chip's listings give it (in bytes).</summary>
    public abstract int ProgramCounter { get; }

    /// <summary>
    /// Calls made before the instruction at an address (in bytes, as <see cref="ProgramCounter"/>) runs, e.g. for a test
    /// that reads a result whenever the program calls a function. Null for none.
    /// </summary>
    public IReadOnlyDictionary<int, Action<SimulatedChip>>? Hooks { get; set; }

    /// <summary>The simulated time in nanoseconds, as the devices on the chip's buses count it.</summary>
    public double Nanoseconds => Ticks * 1e9 / TicksPerSecond;

    /// <summary>
    /// Called for every pin whose level changed, after the change is printed: its port, its bit, whether it is high now.
    /// The devices that watch a pin of the chip (a shift register's latch, a chip select) listen to it.
    /// </summary>
    public event Action<GpioPort, int, bool>? PinLevelChanged;

    /// <summary>The I2C buses the chip's controllers drive, by number, for --i2c; none unless the chip has them.</summary>
    public virtual IReadOnlyList<Devices.I2cBus> I2cBuses => [];

    /// <summary>The SPI buses the chip's controllers drive, by number, for --spi; none unless the chip has them.</summary>
    public virtual IReadOnlyList<Devices.SpiBus> SpiBuses => [];

    /// <summary>The analog inputs, by pin name, and the volts a reading of the full scale would be; null for none.</summary>
    public virtual Devices.AnalogInputs? Analog => null;

    /// <summary>Whether events from outside (inputs, text to send) are still to come: what could wake a sleeping chip.</summary>
    protected bool InputsAhead => _externalEvents > 0;

    /// <summary>An alarm for a device: it runs <paramref name="callback"/> once each time it is scheduled, unless cancelled first.</summary>
    public Devices.DeviceAlarm CreateAlarm(Action callback) => new(this, callback);

    internal void OnPinLevelChanged(GpioPort port, int bit, bool high) => PinLevelChanged?.Invoke(port, bit, high);

    /// <summary>The tick of the next event, or <see cref="long.MaxValue"/>.</summary>
    protected long NextEventTick => _events.TryPeek(out _, out var key) ? key.Tick : long.MaxValue;

    /// <summary>
    /// Runs <paramref name="action"/> at <paramref name="tick"/> (or as soon as the current instruction is done, when that
    /// has passed). <paramref name="external"/>: an event from outside the chip, which can wake it (an input, a byte sent).
    /// </summary>
    public void At(long tick, Action action, bool external = false)
    {
        ArgumentNullException.ThrowIfNull(action);
        _events.Enqueue((action, external), (tick, _order++));
        _externalEvents += external ? 1 : 0;
    }

    /// <summary>Runs until the time is <paramref name="end"/> or the run stops (<see cref="StopReason"/>).</summary>
    public void Run(long end)
    {
        while (StopReason is null && Ticks < end)
        {
            while (_events.TryPeek(out _, out var key) && key.Tick <= Ticks)
            {
                var (action, external) = _events.Dequeue();
                _externalEvents -= external ? 1 : 0;
                action();
            }

            if (StopReason is not null)
            {
                break;
            }

            try
            {
                Step(Math.Min(end, NextEventTick));
            }
            catch (ProgramFault fault)
            {
                Fail(fault.Message);
            }
        }
    }

    /// <summary>Prints <paramref name="text"/> with the simulated time, as the other simulators do.</summary>
    public void Print(string text) => Log?.Invoke($"{Timestamp(Ticks)} {text}");

    /// <summary>The time a line starts with: "[   12.000 ms]".</summary>
    public static string Timestamp(long ticks) =>
        $"[{(ticks * 1000.0 / TicksPerSecond).ToString("F3", CultureInfo.InvariantCulture),10} ms]";

    /// <summary>The port named <paramref name="letter"/> ('A', 'B', ...), or null.</summary>
    public GpioPort? Port(char letter) => Ports.FirstOrDefault(port => port.Letter == char.ToUpperInvariant(letter));

    /// <summary>The pin named <paramref name="name"/> ("PA3"; "GPIO11" on a chip that numbers its pins), or null.</summary>
    public virtual (GpioPort Port, int Bit)? FindPin(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length != 3 || char.ToUpperInvariant(name[0]) != 'P' || Port(name[1]) is not { } port || name[2] is < '0' or > '7')
        {
            return null;
        }

        var bit = name[2] - '0';
        return (port.Pins & (1 << bit)) != 0 ? (port, bit) : null;
    }

    /// <summary>Stops the run: the program has finished.</summary>
    protected void Finish(string why) => StopReason ??= why;

    /// <summary>Stops the run: the program went wrong.</summary>
    protected void Fail(string why)
    {
        if (StopReason is null)
        {
            StopReason = why;
            Failed = true;
        }
    }

    /// <summary>
    /// Runs one instruction, with what it makes the peripherals do, and the interrupt it lets in; when the CPU sleeps, lets
    /// time pass instead, to <paramref name="limit"/> at most (the next event, or the end of the run).
    /// </summary>
    protected abstract void Step(long limit);

    /// <summary>Runs the hook for <paramref name="address"/>, if there is one.</summary>
    protected void RunHook(int address)
    {
        if (Hooks is not null && Hooks.TryGetValue(address, out var hook))
        {
            hook(this);
        }
    }
}

/// <summary>A serial port's receiver, which --send sends bytes to.</summary>
public interface ISerialInput
{
    /// <summary>The port's name, as lines print it: "UART1".</summary>
    string Name { get; }

    /// <summary>The port's speed as the program set it, in bits a second, or 0 before it has.</summary>
    int BaudRate { get; }

    /// <summary>A byte arrives (its stop bit has).</summary>
    void Receive(byte value);
}

/// <summary>What a serial port writes: every byte, and each line printed as it ends ("UART1: Hello").</summary>
public sealed class SerialMonitor(SimulatedChip chip, string name)
{
    private readonly List<byte> _bytes = [];
    private readonly List<byte> _line = [];

    /// <summary>The port's name, as lines print it.</summary>
    public string Name { get; } = name;

    /// <summary>Every byte written.</summary>
    public IReadOnlyList<byte> Bytes => _bytes;

    /// <summary>A byte has gone out (its stop bit has).</summary>
    public void Write(byte value)
    {
        _bytes.Add(value);
        if (value == '\n')
        {
            chip.Print($"{Name}: {Text(_line).TrimEnd('\r')}");
            _line.Clear();
        }
        else
        {
            _line.Add(value);
        }
    }

    /// <summary>Prints the last line when it has no line ending.</summary>
    public void Finish()
    {
        if (_line.Count > 0)
        {
            chip.Print($"{Name}: {Text(_line)} (no line ending)");
            _line.Clear();
        }
    }

    private static string Text(List<byte> bytes) => System.Text.Encoding.Latin1.GetString([.. bytes]);
}
