using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Conformance;

/// <summary>How a simulated run ended.</summary>
internal enum ChipStop
{
    /// <summary>The program ended: the chip sleeps for good, as the runtime's chipsharp_halt leaves it.</summary>
    Finished,

    /// <summary>The simulated time ran out with the program still running.</summary>
    TimeUp,

    /// <summary>The program failed: the runtime's failure stop (a breakpoint), or the chip went wrong.</summary>
    Failed,
}

/// <summary>A pin's new state, when (simulated milliseconds): "low" or "high" when driven, else "input" and the like.</summary>
internal sealed record ChipPinChange(double Milliseconds, int Pin, string State);

/// <summary>A PWM output's period as the simulator measured it from its edges: when, which pin, its frequency, its high time.</summary>
internal sealed record ChipPwmPeriod(double Milliseconds, int Pin, double Hertz, double HighMicroseconds);

/// <summary>A pin driven from outside, as a button would: from the start, or from a time on.</summary>
internal sealed record ChipInput(int Pin, bool High, double? AtMilliseconds = null);

/// <summary>Bytes sent to a serial port's receiver at its speed: from the start, or from a time on.</summary>
internal sealed record ChipSend(int Port, byte[] Bytes, double? AtMilliseconds = null);

/// <summary>The voltage on an analog input: from the start, or from a time on.</summary>
internal sealed record ChipAnalog(int Pin, double Volts, double? AtMilliseconds = null);

/// <summary>
/// A device of the simulators' shared catalog (tools/rp2040-sim/devices/catalog.mjs) on a bus: an I2C device at an
/// address (<c>mpu6050</c>, <c>bmp280</c>, <c>ds1307</c>, ...), or on an SPI bus (<c>loopback</c>, <c>74hc595</c>, with its
/// latch or chip select pin).
/// </summary>
internal sealed record ChipDevice(string Bus, int Number, string Device, int? Address = null, int? Pin = null);

/// <summary>What the world around the chip does while a program runs, and for how long it runs at most.</summary>
internal sealed record ChipStimulus(int Milliseconds)
{
    public IReadOnlyList<ChipInput> Inputs { get; init; } = [];

    public IReadOnlyList<ChipSend> Sends { get; init; } = [];

    public IReadOnlyList<ChipAnalog> Analog { get; init; } = [];

    public IReadOnlyList<ChipDevice> Devices { get; init; } = [];
}

/// <summary>What a simulator saw a program do, in the same words for every chip.</summary>
internal sealed class ChipRun
{
    public required ChipStop Stop { get; init; }

    /// <summary>Every pin change the chip made, in order, by the target's pin numbers.</summary>
    public required IReadOnlyList<ChipPinChange> Pins { get; init; }

    /// <summary>The bytes each serial port sent, by port number.</summary>
    public required IReadOnlyDictionary<int, byte[]> Serial { get; init; }

    /// <summary>The speeds the program set on each serial port, in order.</summary>
    public required IReadOnlyDictionary<int, IReadOnlyList<int>> BaudRates { get; init; }

    /// <summary>What the buses did, as catalog.mjs prints it: <c>I2C0 0x68 write 75, read 68</c>, <c>SPI0 mode 0, ... Hz</c>.</summary>
    public required IReadOnlyList<string> Bus { get; init; }

    /// <summary>Each PWM period printed (each that differs from the one before), and <c>steady</c> ones as 0 Hz.</summary>
    public required IReadOnlyList<ChipPwmPeriod> Pwm { get; init; }

    /// <summary>Everything the simulator printed, for messages.</summary>
    public required string Output { get; init; }

    /// <summary>The levels <paramref name="pin"/> was driven to, low or high, each change once, with their times.</summary>
    public IReadOnlyList<ChipPinChange> Levels(int pin)
    {
        var levels = new List<ChipPinChange>();
        foreach (var change in Pins.Where(p => p.Pin == pin && p.State is "low" or "high"))
        {
            if (levels.Count == 0 || levels[^1].State != change.State)
            {
                levels.Add(change);
            }
        }

        return levels;
    }

    /// <summary>The bytes a serial port sent; none when it sent nothing.</summary>
    public byte[] SerialOf(int port) => Serial.TryGetValue(port, out var bytes) ? bytes : [];

    public override string ToString() => $"{Stop}\n{Output}";
}

/// <summary>Which pins of a chip the conformance suite's programs use: none of them shares one.</summary>
/// <param name="Output">A plain output.</param>
/// <param name="Second">Another plain output.</param>
/// <param name="Pass">The output a program toggles for each of its checks that holds.</param>
/// <param name="Fail">The output it toggles for each that does not.</param>
/// <param name="Input">An input the simulator drives, which the chip can also watch (the pin event).</param>
internal sealed record ChipPins(int Output, int Second, int Pass, int Fail, int Input)
{
    /// <summary>An analog input.</summary>
    public int? Adc { get; init; }

    /// <summary>A PWM output.</summary>
    public int? Pwm { get; init; }

    /// <summary>Serial port 0's TX and RX pins.</summary>
    public (int Tx, int Rx)? Uart0 { get; init; }

    /// <summary>I2C bus 0's SDA and SCL pins.</summary>
    public (int Sda, int Scl)? I2c0 { get; init; }

    /// <summary>SPI bus 0's SCK, TX (MOSI) and RX (MISO) pins.</summary>
    public (int Sck, int Tx, int Rx)? Spi0 { get; init; }
}

/// <summary>How far a chip's runtime, as its simulator measures it, may be from the exact times and values.</summary>
/// <param name="DelaySlackMilliseconds">How much longer than given a delay may take, the loop around it included.</param>
/// <param name="LatencyMilliseconds">How long after its time, or its pin's change, an event's handler may toggle a pin.</param>
/// <param name="ClockMilliseconds">How far from the time a busy loop on the clock may toggle a pin.</param>
internal sealed record ChipTolerances(double DelaySlackMilliseconds, double LatencyMilliseconds, double ClockMilliseconds)
{
    /// <summary>The part a serial port's speed may be off what was asked for (the dividers' rounding).</summary>
    public double Baud { get; init; } = 0.025;

    /// <summary>How many microseconds a PWM output's high time may be off (the timer's resolution, the simulator's).</summary>
    public double PwmMicroseconds { get; init; } = 4;
}

/// <summary>An analog input's range: the reading the reference voltage would be (one more than the highest), and the reference.</summary>
internal sealed record ChipAdc(int FullScale, double ReferenceVolts);

/// <summary>
/// The simulator adapter: what the conformance suite needs of a chip to run the same C# on every target and see what
/// it did in the same words: the target, the pins its programs use there, its tolerances, a build of C# for it (the
/// build pipeline's, the same for every target), and a run of the image in its simulator with the stimulus given (pin
/// levels, serial input, analog inputs, devices on its buses), which comes back as a <see cref="ChipRun"/>: pin changes,
/// serial output, bus transfers, PWM periods and why it stopped.
/// </summary>
/// <remarks>
/// A chip whose tools are not on this machine, or whose runtime or simulator is not in ChipSharp yet, says why in
/// <see cref="Missing"/>, and its cases are skipped with that reason: a new chip gets its adapter first, and passes the
/// suite when its runtime does.
/// </remarks>
internal abstract class ChipSimulator
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private readonly Lazy<TargetDescription> _description;

    protected ChipSimulator(string target)
    {
        Target = target;
        _description = new(() => Targets.Load(target));
    }

    /// <summary>The target, e.g. "pico".</summary>
    public string Target { get; }

    /// <summary>The target's file, merged.</summary>
    public TargetDescription Description => _description.Value;

    /// <summary>Null when programs build and run for this chip here; otherwise why not.</summary>
    public abstract string? Missing { get; }

    /// <summary>
    /// Null when a test that needs <paramref name="needs"/> (the hardware contract's guards) runs on this chip here;
    /// otherwise why not: <see cref="Missing"/>, or a part of the hardware the simulator does not model yet.
    /// </summary>
    public virtual string? MissingFor(IReadOnlyList<string> needs) => Missing;

    public abstract ChipPins Pins { get; }

    public abstract ChipTolerances Tolerances { get; }

    /// <summary>The analog inputs' range; null for a chip without them.</summary>
    public virtual ChipAdc? Adc => null;

    /// <summary>The serial port Console.Write writes to.</summary>
    public virtual int ConsolePort => 0;

    /// <summary>
    /// True when the target has <paramref name="need"/>: "chip" (every chip), a capability (true, or a number above 0),
    /// or an event of its interrupts table ("interrupts.timer"), as the hardware contract's guards say.
    /// </summary>
    public bool Has(string need) => need == "chip" || HardwareContract.Has(Description, need);

    /// <summary>
    /// <paramref name="source"/> (C# with ChipSharp.Hardware) compiled and built for the target by the build pipeline,
    /// as <c>chipsharp build -t</c> does, in <paramref name="folder"/>: the image's path (.uf2, .hex).
    /// </summary>
    public virtual string Build(string source, string name, string folder)
    {
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, name + ".dll");
        File.WriteAllBytes(dll, CSharpCompiler.Compile([source], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], name));
        return ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = Target,
            TargetsFolder = Path.Combine(AppContext.BaseDirectory, "targets"),
            OutputFolder = Path.Combine(folder, Target),
        }).OutputPath;
    }

    /// <summary>Runs <paramref name="image"/> in the chip's simulator with <paramref name="stimulus"/>.</summary>
    public abstract Task<ChipRun> RunAsync(string image, ChipStimulus stimulus);

    public override string ToString() => Target;

    /// <summary>A time as the simulators' options take it, e.g. "@12.5ms"; nothing for the start.</summary>
    protected static string At(double? milliseconds) =>
        milliseconds is { } at ? "@" + at.ToString("0.###", CultureInfo.InvariantCulture) + "ms" : "";

    /// <summary>Bytes as the simulators' --send takes them: each as \xHH.</summary>
    protected static string Escaped(byte[] bytes) => string.Concat(bytes.Select(b => $"\\x{b:x2}"));
}
