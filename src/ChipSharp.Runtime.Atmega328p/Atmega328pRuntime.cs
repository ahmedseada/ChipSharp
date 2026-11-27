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
/// <item><see cref="I2cFileName"/>, <see cref="SpiFileName"/>, <see cref="AdcFileName"/> and <see cref="PwmFileName"/>:
/// ChipSharp.Hardware's I2c (the TWI on A4 and A5), Spi (D13, D11, D12), Adc (A0-A5, 0 to 1023 against 5 V, and the
/// temperature sensor) and Pwm (D3, D5, D6 and D11 on Timer0 and Timer2, D9 on Timer1's compare A), with
/// <see cref="FailuresFileName"/>, the messages of their mistakes.</item>
/// <item><see cref="InterruptsFileName"/>, with the vector table: the entries of the interrupts the runtime uses (INT0,
/// INT1, PCINT0-2, TIMER1_COMPB, USART_RX, TIMER1_COMPA), which go to the handler the runtime file that uses each put in, if any.</item>
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

    /// <summary>The C file with the I2C bus (the TWI) as ChipSharp.Hardware.I2c's bus 0.</summary>
    public const string I2cFileName = "atmega328p_i2c.c";

    /// <summary>The C file with the SPI controller as ChipSharp.Hardware.Spi's bus 0.</summary>
    public const string SpiFileName = "atmega328p_spi.c";

    /// <summary>The C file with the analog inputs (Adc): A0-A5 and the temperature sensor.</summary>
    public const string AdcFileName = "atmega328p_adc.c";

    /// <summary>The C file with the PWM outputs (Pwm): D3, D5, D6, D9 and D11.</summary>
    public const string PwmFileName = "atmega328p_pwm.c";

    /// <summary>The C file with the messages the peripherals' functions stop a program with.</summary>
    public const string FailuresFileName = "atmega328p_failures.c";

    /// <summary>The assembly file with the entries of the interrupts the runtime uses.</summary>
    public const string InterruptsFileName = "atmega328p_interrupts.S";

    /// <summary>The C file with the pin and timer events of <c>[Interrupt]</c> methods.</summary>
    public const string EventsFileName = "atmega328p_events.c";

    private readonly AvrRuntime _avr = new();

    public string Name => "atmega328p";

    /// <summary>
    /// The AVR start-up and helpers, the chip's own start-up and runtime functions, the console output, the serial port
    /// and console input, the buses, analog inputs and PWM outputs (which the linker leaves out of a program that does
    /// not use them).
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
            Source(I2cFileName),
            Source(SpiFileName),
            Source(AdcFileName),
            Source(PwmFileName),
            Source(FailuresFileName),
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
