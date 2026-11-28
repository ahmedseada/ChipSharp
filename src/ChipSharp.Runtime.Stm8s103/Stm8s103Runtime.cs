using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Stm8;

namespace ChipSharp.Runtime.Stm8s103;

/// <summary>
/// The runtime of ST STM8S103F3 programs (target "stm8s103f3" and its boards, e.g. "stm8s103-board", the blue
/// STM8S103F3P6 board), at 16 MHz from the HSI: the STM8 runtime (<see cref="Stm8Runtime"/>: start-up, the stops, the
/// failures, strings) plus what this chip needs, each C file a module of the program's library (SDCC's linker takes only
/// those the program uses), the chip's registers, interrupts and pins from the headers ChipSharp.Chips writes
/// (<see cref="ChipHeaderFileNames"/>):
/// <list type="bullet">
/// <item><see cref="SourceFileName"/>: in every program: the start-up (the master clock to 16 MHz, TIM1 counting
/// microseconds), the clock's interrupt, the handlers of the vector table (<see cref="Stm8Runtime.VectorsFileName"/>, this
/// runtime's own) and what runs before the program stops.</item>
/// <item><see cref="PinsFileName"/>, <see cref="DelaysFileName"/>, <see cref="RegistersFileName"/> and
/// <see cref="ClockFileName"/>: <c>ChipSharp_Runtime_Pin*</c> by the chip's numbers (port * 8 + bit),
/// <c>ChipSharp_Runtime_Delay*</c>, <c>ChipSharp_Runtime_Register*</c> (one byte at an address) and
/// <c>ChipSharp_Runtime_Clock*</c> (DateTime.Now, Stopwatch: TIM1's microseconds).</item>
/// <item><see cref="TimerFileName"/> and <see cref="PinEventsFileName"/>: the timer event (TIM1's compare 1) and the pin
/// event (EXTI), which call the entries of the program's <c>[Interrupt]</c> methods by the numbers of the target's
/// "interrupts" table.</item>
/// <item><see cref="ConsoleFileName"/>: UART1's sending (PD5, 115200 baud), where Console.Write's text
/// (<see cref="ConsoleTextFileName"/>, shared with the other runtimes, in modules; floats as text from
/// <see cref="FloatTextFileName"/>) and the failures' messages go; <see cref="UartFileName"/>: UART1 as
/// ChipSharp.Hardware.Uart's port 0 and the console's input (Console.Read), a receive buffer of 32 bytes filled by its
/// interrupt.</item>
/// <item><see cref="I2cFileName"/>, <see cref="SpiFileName"/>, <see cref="AdcFileName"/> and <see cref="PwmFileName"/>:
/// ChipSharp.Hardware's I2c (bus 0 on PB5 and PB4), Spi (PC5, PC6, PC7), Adc (AIN2-AIN6, 0 to 1023 against VDD) and Pwm
/// (TIM2's channels on PD4, PD3 and PA3).</item>
/// </list>
/// </summary>
public sealed class Stm8s103Runtime : IArchRuntime
{
    /// <summary>The C file every program has: start-up, the clock's timer, the runtime's interrupts.</summary>
    public const string SourceFileName = "stm8s103f3_runtime.c";

    /// <summary>The header the chip runtime's C files share.</summary>
    public const string HeaderFileName = "stm8s103f3_runtime.h";

    public const string PinsFileName = "stm8s103f3_pins.c";
    public const string DelaysFileName = "stm8s103f3_delays.c";
    public const string RegistersFileName = "stm8s103f3_registers.c";
    public const string ClockFileName = "stm8s103f3_clock.c";
    public const string TimerFileName = "stm8s103f3_timer.c";
    public const string PinEventsFileName = "stm8s103f3_pin_events.c";

    /// <summary>The C file with UART1's sending: the console's output and the failures'.</summary>
    public const string ConsoleFileName = "stm8s103f3_console.c";

    /// <summary>Console.Write and Console.WriteLine of every type (src/ChipSharp.Runtime.Shared), in modules.</summary>
    public const string ConsoleTextFileName = "chipsharp_console.c";

    /// <summary>Floats as text, as .NET writes them (src/ChipSharp.Runtime.Shared): with SDCC, a float's alone.</summary>
    public const string FloatTextFileName = "chipsharp_float_text.c";

    public const string UartFileName = "stm8s103f3_uart.c";
    public const string I2cFileName = "stm8s103f3_i2c.c";
    public const string SpiFileName = "stm8s103f3_spi.c";
    public const string AdcFileName = "stm8s103f3_adc.c";
    public const string PwmFileName = "stm8s103f3_pwm.c";

    /// <summary>
    /// The chip's registers, interrupts and pins, as ChipSharp.Chips writes them from chips/stm8s103f3.json (kept in
    /// src/ChipSharp.Chips/Generated).
    /// </summary>
    public static IReadOnlyList<string> ChipHeaderFileNames { get; } =
        ["stm8s103f3_registers.h", "stm8s103f3_interrupts.h", "stm8s103f3_pins.h"];

    /// <summary>
    /// This runtime's C files, each a module of the program's library, or several (<see cref="Stm8Runtime.Modules"/>).
    /// </summary>
    public static IReadOnlyList<string> SourceFileNames { get; } =
    [
        SourceFileName, PinsFileName, DelaysFileName, RegistersFileName, ClockFileName, TimerFileName, PinEventsFileName,
        ConsoleFileName, UartFileName, I2cFileName, SpiFileName, AdcFileName, PwmFileName,
    ];

    private readonly Stm8Runtime _stm8 = new();

    public string Name => "stm8s103f3";

    /// <summary>
    /// The STM8 runtime's files but those this one replaces (its vector table's handlers; the defaults of the hooks it
    /// defines), the chip's headers, and its own files and the console's, in modules.
    /// </summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        HashSet<string> replaced = [Stm8Runtime.VectorsFileName, .. Stm8Runtime.DefaultModuleNames];
        return new RuntimeSources(
        [
            .. _stm8.Startup(target).Files.Where(file => !replaced.Contains(file.FileName)),
            Source(Stm8Runtime.VectorsFileName),
            .. ChipHeaderFileNames.Select(Source),
            Source(HeaderFileName),
            .. SourceFileNames.SelectMany(fileName => Stm8Runtime.Modules(Source(fileName))),
            .. Stm8Runtime.Modules(Source(ConsoleTextFileName)),
            .. Stm8Runtime.Modules(Source(FloatTextFileName)),
        ]);
    }

    /// <summary>The STM8 runtime's defaults of the program's <c>[Interrupt]</c> methods' entries.</summary>
    public RuntimeSources InterruptEntry(TargetDescription target) => _stm8.InterruptEntry(target);

    public RuntimeSources Atomics(TargetDescription target) => _stm8.Atomics(target);

    /// <summary>One of this runtime's files by name, e.g. <see cref="UartFileName"/>.</summary>
    public static GeneratedFile Source(string fileName) =>
        new(fileName, EmbeddedText.Read(typeof(Stm8s103Runtime), fileName));
}
