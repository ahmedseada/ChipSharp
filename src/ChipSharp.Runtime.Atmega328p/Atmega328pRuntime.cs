using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Avr;

namespace ChipSharp.Runtime.Atmega328p;

/// <summary>
/// The runtime of Microchip ATmega328P programs (target "atmega328p" and its boards, e.g. "uno", the Arduino Uno): the AVR
/// runtime (<see cref="AvrRuntime"/>: reset handler, vector table, math helpers, float in software, failures, the stops)
/// plus what this chip needs, at 16 MHz:
/// <list type="bullet">
/// <item><see cref="SourceFileName"/>: chip start-up (Timer1, the clock) and the runtime functions programs call:
/// <c>ChipSharp_Runtime_Pin*</c> by the Uno's pin numbers (0-13 are D0-D13, 14-19 are A0-A5),
/// <c>ChipSharp_Runtime_Delay*</c>, <c>ChipSharp_Runtime_Clock*</c> (DateTime.Now, Stopwatch: Timer1 counting half
/// microseconds), <c>ChipSharp_Runtime_Register*</c> (one byte at a data address); <c>chipsharp_console_write</c>, where
/// the console output goes, and <c>chipsharp_failure_write</c>, where the AVR runtime's failure messages go: USART0 (D1,
/// 115200 baud).</item>
/// <item><see cref="ConsoleFileName"/>: Console.Write and Console.WriteLine of every type, shared with the other runtimes
/// (src/ChipSharp.Runtime.Shared).</item>
/// <item><see cref="UartFileName"/>: USART0 as ChipSharp.Hardware.Uart's port 0 and the console's input (Console.Read):
/// a receive buffer of 64 bytes, filled by its interrupt.</item>
/// <item><see cref="InterruptsFileName"/>, with the vector table: the entries of the interrupts the runtime uses (INT0,
/// INT1, PCINT0-2, TIMER1_COMPB, USART_RX), which go to the handler the runtime file that uses each put in, if any.</item>
/// <item><see cref="EventsFileName"/>: the pin and timer events of the program's <c>[Interrupt]</c> methods
/// (<c>ChipSharp_Runtime_Pin*Watch</c>, <c>ChipSharp_Runtime_Timer*</c>), which call the methods' entries by the numbers
/// of the target's "interrupts" table.</item>
/// </list>
/// </summary>
public sealed class Atmega328pRuntime : IArchRuntime
{
    /// <summary>The C file with the chip's start-up and runtime functions.</summary>
    public const string SourceFileName = "atmega328p_runtime.c";

    /// <summary>The C file with Console.Write and Console.WriteLine, which write through <see cref="SourceFileName"/>.</summary>
    public const string ConsoleFileName = "chipsharp_console.c";

    /// <summary>The C file with USART0 as a serial port (Uart) and the console's input (Console.Read).</summary>
    public const string UartFileName = "atmega328p_uart.c";

    /// <summary>The assembly file with the entries of the interrupts the runtime uses.</summary>
    public const string InterruptsFileName = "atmega328p_interrupts.S";

    /// <summary>The C file with the pin and timer events of <c>[Interrupt]</c> methods.</summary>
    public const string EventsFileName = "atmega328p_events.c";

    private readonly AvrRuntime _avr = new();

    public string Name => "atmega328p";

    /// <summary>
    /// The AVR start-up and helpers, the chip's own start-up and runtime functions, the console output, and the serial
    /// port and console input (which the linker leaves out of a program that does not use them).
    /// </summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources(
        [
            .. _avr.Startup(target).Files,
            Source(SourceFileName),
            Source(ConsoleFileName),
            Source(UartFileName),
        ]);
    }

    /// <summary>
    /// The AVR vector table, the entries of the interrupts the runtime uses (<see cref="InterruptsFileName"/>) and the
    /// events of <c>[Interrupt]</c> methods (<see cref="EventsFileName"/>); the clock's overflow interrupt is in
    /// <see cref="SourceFileName"/>.
    /// </summary>
    public RuntimeSources InterruptEntry(TargetDescription target) =>
        new([.. _avr.InterruptEntry(target).Files, Source(InterruptsFileName), Source(EventsFileName)]);

    public RuntimeSources Atomics(TargetDescription target) => _avr.Atomics(target);

    /// <summary>One of this runtime's files by name, e.g. <see cref="UartFileName"/>.</summary>
    public static GeneratedFile Source(string fileName) =>
        new(fileName, EmbeddedText.Read(typeof(Atmega328pRuntime), fileName));
}
