using System.Globalization;
using System.Numerics;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Pdk14;

namespace ChipSharp.Runtime.Pfs154;

/// <summary>
/// The runtime of Padauk PFS154 programs (target "pfs154" and its boards, e.g. "pfs154-bare"): the PDK14 runtime
/// (<see cref="Pdk14Runtime"/>: start-up, the interrupt handler, the stops and failures) plus the hardware contract's
/// functions of this chip, from the headers ChipSharp.Chips writes from chips/pfs154.json (<see cref="ChipHeaders"/>).
/// Each file but <see cref="StartupFileName"/> is in the library SDCC's linker takes from only what a program uses
/// (<see cref="GeneratedFile.Library"/>): 2K words of program memory and 128 bytes of RAM.
/// <list type="bullet">
/// <item><see cref="StartupFileName"/> (linked whole): the clock before anything else (the factory's trim of the IHRC,
/// CLKMD, easypdkprog's calibration marker), <c>chipsharp_chip_init</c> (T16 counting microseconds, the digital
/// inputs on) and <c>chipsharp_before_halt</c>, with <see cref="ClockHeaderFileName"/>, written for the target from its
/// "clockHz" and "clock" (<see cref="ClockHeader"/>).</item>
/// <item><see cref="LibraryFileNames"/>, most in assembly, as SDCC's C keeps every value it works out in RAM of its
/// own: Pin by ChipSharp's numbers (port * 8 + bit; an input's pull-up off, as after reset: Registers.Write of PAPH or
/// PBPH turns it on); T16's count of microseconds, the delays on it, and the clock (T16 and its rounds, 48 bits:
/// Stopwatch, Environment.TickCount64, DateTime.UtcNow and Clock.Start); the timer event (TM2 wakes the handler when
/// the clock says) and the pin event (PA0 and PB0, the pins with an interrupt); PWM on TM3's pins (PB5-PB7) and TM2's
/// (PA3, PB2, PB4, while the timer event is off), a pin a timer; and Registers, the I/O space by its addresses.</item>
/// </list>
/// The PFS154 has no console: a failure and a mistake (a pin that cannot do what it is asked) are the failure stop, the
/// reset instruction, which chipsim stops at.
/// </summary>
public sealed class Pfs154Runtime : IArchRuntime
{
    /// <summary>The C file with the clock's set-up, chipsharp_chip_init and chipsharp_before_halt, linked whole.</summary>
    public const string StartupFileName = "pfs154_startup.c";

    /// <summary>The header the runtime writes for the target: its system clock and the calibration marker.</summary>
    public const string ClockHeaderFileName = "pfs154_clock.h";

    /// <summary>The assembly's include file the runtime writes for the target: its system clock.</summary>
    public const string ClockIncludeFileName = "pfs154_clock.inc";

    /// <summary>The header of what the chip's files share.</summary>
    public const string HeaderFileName = "pfs154_runtime.h";

    /// <summary>The headers ChipSharp.Chips writes from chips/pfs154.json (src/ChipSharp.Chips/Generated).</summary>
    public static IReadOnlyList<string> ChipHeaders { get; } = ["pfs154_registers.h", "pfs154_interrupts.h", "pfs154_pins.h"];

    /// <summary>
    /// The files of the contract's functions, each in the library, so that a program has the ones it uses: the pins (a
    /// file each, and pfs154_pin.s, the pin of a number), T16's count, the delays (a file each), the clock
    /// (pfs154_clock.s: its rounds, Stopwatch's timestamp and frequency; Environment.TickCount64, GetElapsedTime's ticks
    /// and the date, a file each), the timer and pin events, PWM, and Registers (a table of the I/O space each way).
    /// </summary>
    public static IReadOnlyList<string> LibraryFileNames { get; } =
    [
        "pfs154_pin.s", "pfs154_pin_output.s", "pfs154_pin_input.s", "pfs154_pin_write.s", "pfs154_pin_toggle.s",
        "pfs154_pin_read.s", "pfs154_t16.s", "pfs154_delay_milliseconds.s", "pfs154_delay_microseconds.s",
        "pfs154_clock.s", "pfs154_tick_count.s", "pfs154_elapsed.s", "pfs154_date.s", "pfs154_timer.s",
        "pfs154_pin_event.s", "pfs154_pwm.s", "pfs154_register_read.s", "pfs154_register_write.s",
    ];

    /// <summary>The supply easypdkprog calibrates the IHRC at, in millivolts: the Easy PDK programmer's 5.0 V.</summary>
    public const int CalibrationMillivolts = 5000;

    /// <summary>The PFS154's internal oscillator, the IHRC, once trimmed.</summary>
    public const int IhrcHertz = 16_000_000;

    /// <summary>CLKMD for the system clock IHRC / divider: the IHRC and ILRC on, the watchdog off, PA5 a pin.</summary>
    private static readonly Dictionary<int, byte> ClockModes = new()
    {
        [2] = 0x34, [4] = 0x14, [8] = 0x3c, [16] = 0x1c, [32] = 0x7c, [64] = 0x9c,
    };

    private readonly Pdk14Runtime _pdk14 = new();

    public string Name => "pfs154";

    /// <summary>
    /// The PDK14 start-up and helpers, the chip's headers, its start-up (linked whole) and the files of the contract's
    /// functions (each in the library).
    /// </summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources(
        [
            .. _pdk14.Startup(target).Files,
            .. ChipHeaders.Select(Source),
            Source(HeaderFileName),
            new GeneratedFile(ClockHeaderFileName, ClockHeader(target)),
            new GeneratedFile(ClockIncludeFileName, ClockInclude(target)),
            Source(StartupFileName),
            .. LibraryFileNames.Select(name => Source(name) with { Library = true }),
        ]);
    }

    /// <summary>The PDK14 interrupt handler, into which the event and clock files put their parts.</summary>
    public RuntimeSources InterruptEntry(TargetDescription target) => _pdk14.InterruptEntry(target);

    public RuntimeSources Atomics(TargetDescription target) => _pdk14.Atomics(target);

    /// <summary>
    /// The system clock of <paramref name="target"/>: the IHRC divided by its "clock.divider" (2, 4, 8, 16, 32 or 64;
    /// "clock.source" "ihrc"), which its "clockHz" must be; and CLKMD's value for it.
    /// </summary>
    /// <exception cref="ChipSharpException">The target's clock is not one the runtime sets up.</exception>
    public static (int Hertz, int Divider, byte ClockMode) SystemClock(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var source = target.Setting("clock.source");
        var dividerText = target.Setting("clock.divider");
        var hertzText = target.Setting("clockHz");
        if (source != "ihrc" || !int.TryParse(dividerText, NumberStyles.None, CultureInfo.InvariantCulture, out var divider)
            || !ClockModes.TryGetValue(divider, out var mode))
        {
            throw new ChipSharpException($"Target '{target.Name}': the PFS154's runtime runs it on the IHRC divided by 2, " +
                $"4, 8, 16, 32 or 64 (\"clock\": {{ \"source\": \"ihrc\", \"divider\": 8 }}), not \"clock.source\" " +
                $"\"{source}\" and \"clock.divider\" \"{dividerText}\".");
        }

        var hertz = IhrcHertz / divider;
        return hertzText == hertz.ToString(CultureInfo.InvariantCulture)
            ? (hertz, divider, mode)
            : throw new ChipSharpException($"Target '{target.Name}': its \"clockHz\" is \"{hertzText}\", but the IHRC / " +
                $"{divider} is {hertz} Hz.");
    }

    /// <summary>
    /// <see cref="ClockHeaderFileName"/> for <paramref name="target"/>: its system clock (<see cref="SystemClock"/>), the
    /// shift of its ticks in the IHRC's, CLKMD's value, and easypdkprog's IHRC calibration marker for that frequency at
    /// <see cref="CalibrationMillivolts"/>, for C.
    /// </summary>
    /// <exception cref="ChipSharpException">The target's clock is not one the runtime sets up.</exception>
    public static string ClockHeader(TargetDescription target)
    {
        var (hertz, divider, mode) = SystemClock(target);
        var marker = IhrcCalibrationMarker((uint)hertz, CalibrationMillivolts);
        var text = new StringBuilder()
            .Append("// pfs154 clock: written by the pfs154 runtime for target '").Append(target.Name).Append("'.\n\n")
            .Append("#ifndef PFS154_CLOCK_H\n#define PFS154_CLOCK_H\n\n")
            .Append("// the system clock: the IHRC / ").Append(divider).Append(", the target's \"clockHz\"\n")
            .Append("#define PFS154_SYSTEM_CLOCK_HZ ").Append(hertz).Append("ul\n")
            .Append("// its ticks, in ticks of the IHRC: 2 to this power\n")
            .Append("#define PFS154_SYSTEM_CLOCK_SHIFT ").Append(BitOperations.Log2((uint)divider)).Append("u\n")
            .Append("// CLKMD for it: the IHRC and the ILRC on, the watchdog off, PA5 a pin\n")
            .Append("#define PFS154_CLKMD \"0x").Append(mode.ToString("x2", CultureInfo.InvariantCulture)).Append("\"\n")
            .Append("// easypdkprog's IHRC calibration marker (Free-PDK's EASY_PDK_CALIBRATE_IHRC) for it at ")
            .Append(CalibrationMillivolts).Append(" mV\n")
            .Append("#define PFS154_IHRC_CALIBRATION");
        foreach (var word in marker)
        {
            text.Append(" \\\n    \"and a, #0x").Append((word & 0xff).ToString("x2", CultureInfo.InvariantCulture)).Append("\\n\"");
        }

        return text.Append("\n\n#endif\n").ToString();
    }

    /// <summary>
    /// <see cref="ClockIncludeFileName"/> for <paramref name="target"/>: the shift of its system clock's ticks in the
    /// IHRC's (<see cref="SystemClock"/>), for the assembly (PWM on the system clock).
    /// </summary>
    /// <exception cref="ChipSharpException">The target's clock is not one the runtime sets up.</exception>
    public static string ClockInclude(TargetDescription target)
    {
        var (_, divider, _) = SystemClock(target);
        return $"; pfs154 clock: written by the pfs154 runtime for target '{target.Name}'.\n\n" +
            $"; the system clock's ticks, in ticks of the IHRC: 2 to this power (the IHRC / {divider})\n" +
            $"    PFS154_SYSTEM_CLOCK_SHIFT = {BitOperations.Log2((uint)divider)}\n";
    }

    /// <summary>
    /// The ten words of easypdkprog's IHRC calibration marker, as easypdkprog finds them (Free-PDK's
    /// EASY_PDK_CALIBRATE_IHRC; ChipSharp.Flasher.EasyPdkProg's IhrcCalibrationMarker): <c>and a, #k</c> (0x2c00 | k) for
    /// 'R', 'C', 1 (the IHRC), the system clock in hertz (4 bytes) and the supply in millivolts (2 bytes), each low byte
    /// first, and IHRCR's address, 0x0b.
    /// </summary>
    public static IReadOnlyList<ushort> IhrcCalibrationMarker(uint frequency, ushort millivolts)
    {
        byte[] values =
        [
            (byte)'R', (byte)'C', 1,
            (byte)frequency, (byte)(frequency >> 8), (byte)(frequency >> 16), (byte)(frequency >> 24),
            (byte)millivolts, (byte)(millivolts >> 8),
            0x0b,
        ];
        return [.. values.Select(value => (ushort)(0x2c00 | value))];
    }

    /// <summary>One of this runtime's files by name, e.g. <see cref="StartupFileName"/>, linked whole.</summary>
    public static GeneratedFile Source(string fileName) =>
        new(fileName, EmbeddedText.Read(typeof(Pfs154Runtime), fileName));
}
