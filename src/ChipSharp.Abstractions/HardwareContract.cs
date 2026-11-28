using System.Globalization;
using System.Text.Json.Nodes;
using ChipSharp.Ir;

namespace ChipSharp.Abstractions;

/// <summary>A value a function of the hardware contract takes or gives, as its C names it.</summary>
public enum HardwareType
{
    /// <summary>Nothing: <c>void</c>.</summary>
    Void,

    /// <summary>A C# int, and a char or bool as C# passes them: <c>chipsharp_i32</c>.</summary>
    Int32,

    /// <summary>A C# uint: <c>chipsharp_u32</c>.</summary>
    UInt32,

    /// <summary>A C# long: <c>chipsharp_i64</c>.</summary>
    Int64,

    /// <summary>A C# double, IEEE's 64 bits: <c>chipsharp_f64</c>; a chip without one has no such function.</summary>
    Float64,

    /// <summary>The first of a span's bytes, read: <c>const chipsharp_u8 *</c>.</summary>
    Bytes,

    /// <summary>The first of a span's bytes, written: <c>chipsharp_u8 *</c>.</summary>
    WritableBytes,

    /// <summary>The runtime's own text in RAM: <c>const char *</c>.</summary>
    Text,

    /// <summary>A runtime's message, in program memory where the chip reads that apart: <c>CHIPSHARP_MESSAGE_CHAR *</c>.</summary>
    Message,

    /// <summary>The size of a piece of the runtime's text: <c>chipsharp_count</c>, C's unsigned int.</summary>
    Count,
}

/// <summary>A parameter of a function of the hardware contract: its name in the C header, and its type.</summary>
public sealed record HardwareParameter(string Name, HardwareType Type);

/// <summary>
/// One function of the hardware contract (<see cref="HardwareContract"/>), as src/ChipSharp.Runtime.Shared's
/// chipsharp_hardware.h declares it.
/// </summary>
/// <param name="Name">Its C symbol, e.g. <c>ChipSharp_Runtime_PinOutput</c> or <c>chipsharp_console_write</c>.</param>
/// <param name="Returns">What it gives.</param>
/// <param name="Parameters">What it takes, in order.</param>
/// <param name="Section">The section of the header it is in, e.g. "Pins".</param>
/// <param name="Guard">
/// What a target needs to have it (see <see cref="HardwareContract.Has"/>): null for every chip, a capability
/// (<c>clock</c>, <c>uart</c>, ...) or an event of the target's interrupts table (<c>interrupts.timer</c>).
/// </param>
/// <param name="CSharp">
/// The C# method a call to it is made from: a method of ChipSharp.Hardware (<c>ChipSharp.Hardware.Pin.Output(int)</c>,
/// which ChipSharp.Frontend's KnownExternals maps) or of ChipSharp.Library's runtime classes
/// (<c>ChipSharp.Library.ChipRuntime.I2cOpen</c>, <c>ChipSharp.Library.Runtime.ClockUtcTicks</c>); null for a function
/// only the runtime calls, or only the program defines.
/// </param>
/// <param name="Kind">Who defines it and who calls it.</param>
/// <param name="Reentrant">May be called from an interrupt handler while Main is inside it (<c>CHIPSHARP_REENTRANT</c>).</param>
/// <param name="NoReturn">Never returns (<c>CHIPSHARP_NORETURN</c>).</param>
public sealed record HardwareFunction(
    string Name,
    HardwareType Returns,
    IReadOnlyList<HardwareParameter> Parameters,
    string Section,
    string? Guard,
    string? CSharp,
    HardwareFunctionKind Kind,
    bool Reentrant,
    bool NoReturn)
{
    /// <summary>
    /// The name of the TIR external function a call is made of, as KnownExternals names them:
    /// <c>ChipSharp.Runtime.PinOutput</c> for <c>ChipSharp_Runtime_PinOutput</c>; null for a runtime hook.
    /// </summary>
    public string? ExternalName => Name.StartsWith(HardwareContract.RuntimePrefix, StringComparison.Ordinal)
        ? "ChipSharp.Runtime." + Name[HardwareContract.RuntimePrefix.Length..]
        : null;

    /// <summary>True when a parameter or the result is a double, which a chip without a 64-bit double does not have.</summary>
    public bool NeedsDouble64 => Returns == HardwareType.Float64 || Parameters.Any(p => p.Type == HardwareType.Float64);

    /// <summary>The TIR type of the result, for a function the program calls.</summary>
    public TirType TirReturns() => HardwareContract.TirTypeOf(Returns);

    /// <summary>The TIR types of the parameters, for a function the program calls.</summary>
    public IReadOnlyList<TirType> TirParameters() => [.. Parameters.Select(p => HardwareContract.TirTypeOf(p.Type))];
}

/// <summary>Who defines a function of the hardware contract, and who calls it.</summary>
public enum HardwareFunctionKind
{
    /// <summary>The chip's runtime defines it; the program's code calls it (ChipSharp.Hardware, Console.Read, the clock).</summary>
    Runtime,

    /// <summary>The chip's runtime defines it; the shared runtime code calls it (the console's output, the stops).</summary>
    Hook,

    /// <summary>The program defines it (the code generator writes it); the runtime calls it.</summary>
    Program,
}

/// <summary>
/// ChipSharp's hardware contract: every function a chip's runtime provides, the same on every chip, so that one
/// frontend, one code generator and one conformance suite serve them all. src/ChipSharp.Runtime.Shared's
/// <see cref="HeaderFileName"/> declares them in C, with what each must do; this is the same list for C#, which
/// ChipSharp.Frontend's KnownExternals maps ChipSharp.Hardware's calls with. A chip's runtime ships the header and each
/// of its C files includes it, so the compiler checks every definition against it; the tests check that the header and
/// this list say the same, and that every chip's runtime defines what its target's file says it has
/// (<see cref="For"/>).
/// </summary>
/// <remarks>
/// A function's guard says which targets have it: none (every chip), a capability that is true or a count above 0
/// (<c>"clock": true</c>, <c>"uart": 2</c>), or an event of the target's <c>"interrupts"</c> table
/// (<c>interrupts.timer</c>). A target without a 64-bit double (<c>"double64": false</c>, SDCC's chips) has none of the
/// functions that take or give one.
/// </remarks>
public static class HardwareContract
{
    /// <summary>The C header: in src/ChipSharp.Runtime.Shared, shipped with each architecture's runtime.</summary>
    public const string HeaderFileName = "chipsharp_hardware.h";

    /// <summary>What the symbols of the functions the program's code calls start with.</summary>
    public const string RuntimePrefix = "ChipSharp_Runtime_";

    private const string Pin = "ChipSharp.Hardware.Pin";
    private const string Timing = "ChipSharp.Hardware.Timing";
    private const string Registers = "ChipSharp.Hardware.Registers";
    private const string ChipRuntime = "ChipSharp.Library.ChipRuntime";
    private const string Runtime = "ChipSharp.Library.Runtime";

    private static readonly HardwareType I32 = HardwareType.Int32;
    private static readonly HardwareType U32 = HardwareType.UInt32;
    private static readonly HardwareType I64 = HardwareType.Int64;
    private static readonly HardwareType F64 = HardwareType.Float64;
    private static readonly HardwareType Void = HardwareType.Void;

    /// <summary>Every function, in the header's order.</summary>
    public static IReadOnlyList<HardwareFunction> Functions { get; } =
    [
        // start-up and the program
        Hook("Start-up and the program", null, "chipsharp_chip_init", Void),
        new("chipsharp_entry", I32, [], "Start-up and the program", null, null, HardwareFunctionKind.Program, false, false),

        // ChipSharp.Hardware.Pin
        Call("Pins", null, "PinOutput", $"{Pin}.Output(int)", Void, ("pin", I32)),
        Call("Pins", null, "PinInput", $"{Pin}.Input(int)", Void, ("pin", I32)),
        Call("Pins", null, "PinWrite", $"{Pin}.Write(int, bool)", Void, ("pin", I32), ("high", I32)),
        Call("Pins", null, "PinToggle", $"{Pin}.Toggle(int)", Void, ("pin", I32)),
        Call("Pins", null, "PinRead", $"{Pin}.Read(int)", I32, ("pin", I32)),

        // Timing's delays: they wait, so a handler never calls them
        Wait("Delays", null, "DelayMilliseconds", $"{Timing}.DelayMilliseconds(int)", Void, ("milliseconds", I32)),
        Wait("Delays", null, "DelayMicroseconds", $"{Timing}.DelayMicroseconds(int)", Void, ("microseconds", I32)),

        // ChipSharp.Hardware.Registers
        Call("Registers", null, "RegisterRead", $"{Registers}.Read(uint)", U32, ("address", U32)),
        Call("Registers", null, "RegisterWrite", $"{Registers}.Write(uint, uint)", Void, ("address", U32), ("value", U32)),

        // the timer event
        Call("The timer event", "interrupts.timer", "TimerEveryMilliseconds", $"{Timing}.EveryMilliseconds(int)", Void,
            ("milliseconds", I32)),
        Call("The timer event", "interrupts.timer", "TimerEveryMicroseconds", $"{Timing}.EveryMicroseconds(int)", Void,
            ("microseconds", I32)),
        Call("The timer event", "interrupts.timer", "TimerAfterMilliseconds", $"{Timing}.AfterMilliseconds(int)", Void,
            ("milliseconds", I32)),
        Call("The timer event", "interrupts.timer", "TimerAfterMicroseconds", $"{Timing}.AfterMicroseconds(int)", Void,
            ("microseconds", I32)),
        Call("The timer event", "interrupts.timer", "TimerStop", $"{Timing}.Stop()", Void),

        // the pin event
        Call("The pin event", "interrupts.pin", "PinWatch", $"{Pin}.Watch(int)", Void, ("pin", I32)),
        Call("The pin event", "interrupts.pin", "PinWatchRising", $"{Pin}.WatchRising(int)", Void, ("pin", I32)),
        Call("The pin event", "interrupts.pin", "PinWatchFalling", $"{Pin}.WatchFalling(int)", Void, ("pin", I32)),
        Call("The pin event", "interrupts.pin", "PinUnwatch", $"{Pin}.Unwatch(int)", Void, ("pin", I32)),

        // the clock and the uptime (ChipSharp.Library's Runtime.Clock...)
        Call("The clock", "clock", "ClockUtcTicks", $"{Runtime}.ClockUtcTicks", I64),
        Call("The clock", "clock", "ClockLocalTicks", $"{Runtime}.ClockLocalTicks", I64),
        Call("The clock", "clock", "ClockStart", $"{Runtime}.ClockStart", Void, ("ticks", I64)),
        Call("The clock", "clock", "ClockTimestamp", $"{Runtime}.ClockTimestamp", I64),
        Call("The clock", "clock", "ClockFrequency", $"{Runtime}.ClockFrequency", I64),
        Call("The clock", "clock", "ClockElapsedTicks", $"{Runtime}.ClockElapsedTicks", I64, ("start", I64), ("end", I64)),
        Call("The clock", "clock", "ClockTickCount64", $"{Runtime}.ClockTickCount64", I64),
        Call("Time zones", null, "LocalUtcOffset", $"{Runtime}.LocalUtcOffset", I64),

        // the console, both ways
        new("chipsharp_console_write", Void, [new("bytes", HardwareType.Text), new("count", HardwareType.Count)],
            "The console", "console", null, HardwareFunctionKind.Hook, true, false),
        Wait("The console's input", "consoleInput", "ConsoleRead", $"{ChipRuntime}.ConsoleRead", I32),

        // ChipSharp.Hardware.Uart (ChipSharp.Library's ChipRuntime.Uart...)
        Call("Serial ports", "uart", "UartOpen", $"{ChipRuntime}.UartOpen", Void, ("port", I32), ("tx", I32), ("rx", I32),
            ("baud", I32)),
        Call("Serial ports", "uart", "UartWriteByte", $"{ChipRuntime}.UartWriteByte", Void, ("port", I32), ("value", I32)),
        Call("Serial ports", "uart", "UartReadByte", $"{ChipRuntime}.UartReadByte", I32, ("port", I32)),
        Call("Serial ports", "uart", "UartAvailable", $"{ChipRuntime}.UartAvailable", I32, ("port", I32)),
        Call("Serial ports", "uart", "UartWatch", $"{ChipRuntime}.UartWatch", Void, ("port", I32)),
        Call("Serial ports", "uart", "UartUnwatch", $"{ChipRuntime}.UartUnwatch", Void, ("port", I32)),

        // ChipSharp.Hardware.I2c
        Call("The I2C buses", "i2c", "I2cOpen", $"{ChipRuntime}.I2cOpen", Void, ("bus", I32), ("sda", I32), ("scl", I32),
            ("frequency", I32)),
        Call("The I2C buses", "i2c", "I2cWrite", $"{ChipRuntime}.I2cWrite", I32, ("bus", I32), ("address", I32),
            ("data", HardwareType.Bytes), ("count", I32), ("stop", I32)),
        Call("The I2C buses", "i2c", "I2cRead", $"{ChipRuntime}.I2cRead", I32, ("bus", I32), ("address", I32),
            ("data", HardwareType.WritableBytes), ("count", I32)),

        // ChipSharp.Hardware.Spi
        Call("The SPI buses", "spi", "SpiOpen", $"{ChipRuntime}.SpiOpen", Void, ("bus", I32), ("sck", I32), ("tx", I32),
            ("rx", I32), ("frequency", I32), ("mode", I32)),
        Call("The SPI buses", "spi", "SpiWrite", $"{ChipRuntime}.SpiWrite", Void, ("bus", I32), ("data", HardwareType.Bytes),
            ("count", I32)),
        Call("The SPI buses", "spi", "SpiRead", $"{ChipRuntime}.SpiRead", Void, ("bus", I32),
            ("data", HardwareType.WritableBytes), ("count", I32)),
        Call("The SPI buses", "spi", "SpiTransfer", $"{ChipRuntime}.SpiTransfer", Void, ("bus", I32),
            ("write", HardwareType.Bytes), ("write_count", I32), ("read", HardwareType.WritableBytes), ("read_count", I32)),

        // ChipSharp.Hardware.Adc
        Call("Analog inputs", "adc", "AdcInput", $"{ChipRuntime}.AdcInput", Void, ("pin", I32)),
        Call("Analog inputs", "adc", "AdcRead", $"{ChipRuntime}.AdcRead", I32, ("pin", I32)),
        Call("Analog inputs", "adc", "AdcTemperature", $"{ChipRuntime}.AdcTemperature", F64),
        Call("Analog inputs", "adc", "AdcReference", $"{ChipRuntime}.AdcReference", F64),
        Call("Analog inputs", "adc", "AdcFullScale", $"{ChipRuntime}.AdcFullScale", I32),

        // ChipSharp.Hardware.Pwm
        Call("PWM outputs", "pwm", "PwmOutput", $"{ChipRuntime}.PwmOutput", Void, ("pin", I32), ("frequency", I32)),
        Call("PWM outputs", "pwm", "PwmDuty", $"{ChipRuntime}.PwmDuty", Void, ("pin", I32), ("duty", F64)),
        Call("PWM outputs", "pwm", "PwmPulse", $"{ChipRuntime}.PwmPulse", Void, ("pin", I32), ("microseconds", I32)),
        Call("PWM outputs", "pwm", "PwmStop", $"{ChipRuntime}.PwmStop", Void, ("pin", I32)),

        // the failure output and the stops, which the architecture's failures and start-up call
        new("chipsharp_failure_write", Void, [new("text", HardwareType.Text), new("count", HardwareType.Count)],
            "Failure output and the stops", null, null, HardwareFunctionKind.Hook, false, false),
        Hook("Failure output and the stops", null, "chipsharp_before_halt", Void),
        Hook("Failure output and the stops", null, "chipsharp_halt", Void, noReturn: true),
        Hook("Failure output and the stops", null, "chipsharp_halt_failure", Void, noReturn: true),
        new("chipsharp_hardware_failure", Void,
            [new("text", HardwareType.Message), new("n0", I32), new("n1", I32), new("n2", I32), new("n3", I32)],
            "Failure output and the stops", null, null, HardwareFunctionKind.Hook, false, true),
    ];

    /// <summary>The function with the C symbol <paramref name="name"/>; null when the contract has none.</summary>
    public static HardwareFunction? Find(string name) => Functions.FirstOrDefault(f => f.Name == name);

    /// <summary>
    /// The function a call to the C# method <paramref name="csharp"/> becomes, e.g. for
    /// <c>ChipSharp.Hardware.Pin.Write(int, bool)</c>; null when the contract has none.
    /// </summary>
    public static HardwareFunction? ForCSharp(string csharp) => Functions.FirstOrDefault(f => f.CSharp == csharp);

    /// <summary>
    /// The functions <paramref name="target"/>'s runtime must define (not those the program does): those whose guard it
    /// has (<see cref="Has"/>), without the ones of a double on a target whose C has none.
    /// </summary>
    public static IReadOnlyList<HardwareFunction> For(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var double64 = !target.Capabilities.TryGetValue("double64", out var value)
            || !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
        return
        [
            .. Functions.Where(f => f.Kind != HardwareFunctionKind.Program && Has(target, f.Guard)
                && (double64 || !f.NeedsDouble64)),
        ];
    }

    /// <summary>
    /// True when <paramref name="target"/> has what <paramref name="guard"/> names: always for null; for
    /// <c>interrupts.&lt;event&gt;</c>, when its interrupts table has the event; for a capability, when it is true or a
    /// number above 0.
    /// </summary>
    public static bool Has(TargetDescription target, string? guard)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (guard is null)
        {
            return true;
        }

        if (guard.StartsWith("interrupts.", StringComparison.Ordinal))
        {
            return target.Settings["interrupts"] is JsonObject events && events.ContainsKey(guard["interrupts.".Length..]);
        }

        return target.Capabilities.TryGetValue(guard, out var value)
            && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0));
    }

    /// <summary>The TIR type a value of <paramref name="type"/> is passed as.</summary>
    public static TirType TirTypeOf(HardwareType type) => type switch
    {
        HardwareType.Void => TirType.Void,
        HardwareType.Int32 or HardwareType.UInt32 => TirType.I32,
        HardwareType.Int64 => TirType.I64,
        HardwareType.Float64 => TirType.F64,
        HardwareType.Bytes or HardwareType.WritableBytes or HardwareType.Text or HardwareType.Message => TirType.Ptr,
        HardwareType.Count => throw new ArgumentException(
            "A count is the runtime's own (C's unsigned int, as wide as the chip's int): no program passes one.", nameof(type)),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>A function the program calls, which a handler may call too.</summary>
    private static HardwareFunction Call(string section, string? guard, string name, string csharp, HardwareType returns,
        params (string Name, HardwareType Type)[] parameters) =>
        new(RuntimePrefix + name, returns, [.. parameters.Select(p => new HardwareParameter(p.Name, p.Type))], section, guard,
            csharp, HardwareFunctionKind.Runtime, true, false);

    /// <summary>A function the program calls that waits, which a handler never calls.</summary>
    private static HardwareFunction Wait(string section, string? guard, string name, string csharp, HardwareType returns,
        params (string Name, HardwareType Type)[] parameters) =>
        Call(section, guard, name, csharp, returns, parameters) with { Reentrant = false };

    /// <summary>A hook of the runtime that takes nothing.</summary>
    private static HardwareFunction Hook(string section, string? guard, string name, HardwareType returns,
        bool noReturn = false) =>
        new(name, returns, [], section, guard, null, HardwareFunctionKind.Hook, false, noReturn);
}
