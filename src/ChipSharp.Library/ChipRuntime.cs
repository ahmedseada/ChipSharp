namespace ChipSharp.Library;

/// <summary>
/// The functions of a chip's runtime that the substitutes of ChipSharp.Hardware's buses (I2c.cs, Spi.cs), serial ports,
/// analog inputs and PWM outputs (Uart.cs, Adc.cs, Pwm.cs) and of the console's input (Console.cs) call. As for
/// <see cref="Runtime"/>'s, in a program ChipSharp compiles a call to one of these methods is a call to the runtime
/// function of the same name, e.g. <c>ChipSharp.Runtime.I2cWrite</c>, and the method's own body is left out. Unlike
/// those, only a chip whose target has the hardware has them (the RP2040's rp2040_i2c.c, rp2040_spi.c, rp2040_uart.c,
/// rp2040_adc.c and rp2040_pwm.c; the ATmega328P's atmega328p_i2c.c, ...), and <c>chipsharp check</c> refuses them on
/// any other target; on .NET they throw, as ChipSharp.Hardware does.
/// </summary>
/// <remarks>
/// A span arrives as a reference to its first byte and its number of bytes (an empty one as a reference to a local
/// and 0); a bool as an int. Each function checks its bus, pins and numbers itself, and stops the program with a
/// message on a mistake.
/// </remarks>
internal static class ChipRuntime
{
    /// <summary>I2c.Open.</summary>
    public static void I2cOpen(int bus, int sda, int scl, int frequency) => throw ChipOnly("I2c.Open");

    /// <summary>
    /// Sends <paramref name="count"/> bytes from <paramref name="data"/> on, with a repeated start when the transfer
    /// before was a write without a stop, and with a stop after them when <paramref name="stop"/>.
    /// </summary>
    public static bool I2cWrite(int bus, int address, in byte data, int count, bool stop) => throw ChipOnly("I2c.Write");

    /// <summary>Reads <paramref name="count"/> bytes into <paramref name="data"/> on (a repeated start as for I2cWrite), then stops.</summary>
    public static bool I2cRead(int bus, int address, ref byte data, int count) => throw ChipOnly("I2c.Read");

    /// <summary>Spi.Open.</summary>
    public static void SpiOpen(int bus, int sck, int tx, int rx, int frequency, int mode) => throw ChipOnly("Spi.Open");

    /// <summary>Sends <paramref name="count"/> bytes from <paramref name="data"/> on, and drops what comes back.</summary>
    public static void SpiWrite(int bus, in byte data, int count) => throw ChipOnly("Spi.Write");

    /// <summary>Receives <paramref name="count"/> bytes into <paramref name="data"/> on, sending zeros.</summary>
    public static void SpiRead(int bus, ref byte data, int count) => throw ChipOnly("Spi.Read");

    /// <summary>
    /// Sends <paramref name="writeCount"/> bytes from <paramref name="write"/> on and receives as many into
    /// <paramref name="read"/> on; the program stops when <paramref name="readCount"/> is not the same.
    /// </summary>
    public static void SpiTransfer(int bus, in byte write, int writeCount, ref byte read, int readCount) =>
        throw ChipOnly("Spi.Transfer");

    /// <summary>Uart.Open: the port on its TX and RX pins at a speed; it receives into a buffer from then on.</summary>
    public static void UartOpen(int port, int tx, int rx, int baud) => throw ChipOnly("Uart.Open");

    /// <summary>Sends a byte (0 to 255), once the port's send FIFO has room.</summary>
    public static void UartWriteByte(int port, int value) => throw ChipOnly("Uart.WriteByte");

    /// <summary>The next byte received, or -1 when none is waiting.</summary>
    public static int UartReadByte(int port) => throw ChipOnly("Uart.ReadByte");

    /// <summary>How many bytes received are waiting.</summary>
    public static int UartAvailable(int port) => throw ChipOnly("Uart.Available");

    /// <summary>Starts the uart event for the port (see ChipSharp.Frontend's Interrupts: it starts the event "uart").</summary>
    public static void UartWatch(int port) => throw ChipOnly("Uart.Watch");

    /// <summary>Stops the uart event for the port.</summary>
    public static void UartUnwatch(int port) => throw ChipOnly("Uart.Unwatch");

    /// <summary>The next char of the console's input, waiting for it (see Interrupts: it waits).</summary>
    public static int ConsoleRead() => throw ChipOnly("Console.Read");

    /// <summary>Adc.Input: makes a pin an analog input.</summary>
    public static void AdcInput(int pin) => throw ChipOnly("Adc.Input");

    /// <summary>One reading of an analog input, in the chip's own range: 0 to 4095 on the RP2040, 0 to 1023 on the ATmega328P.</summary>
    public static int AdcRead(int pin) => throw ChipOnly("Adc.Read");

    /// <summary>The chip's temperature in degrees Celsius, by the chip's datasheet's formula for its sensor's reading.</summary>
    public static double AdcTemperature() => throw ChipOnly("Adc.Temperature");

    /// <summary>The voltage a reading of <see cref="AdcFullScale"/> would be: the board's ADC reference.</summary>
    public static double AdcReference() => throw ChipOnly("Adc.Volts");

    /// <summary>The reading the reference voltage would be, one more than the highest: 4096 (12 bits), 1024 (10 bits).</summary>
    public static int AdcFullScale() => throw ChipOnly("Adc.Volts");

    /// <summary>Pwm.Output: a pin a PWM output at a frequency, low.</summary>
    public static void PwmOutput(int pin, int frequency) => throw ChipOnly("Pwm.Output");

    /// <summary>The part of each period a PWM output is high, 0 to 1.</summary>
    public static void PwmDuty(int pin, double duty) => throw ChipOnly("Pwm.Duty");

    /// <summary>How long a PWM output is high at the start of each period, in microseconds.</summary>
    public static void PwmPulse(int pin, int microseconds) => throw ChipOnly("Pwm.PulseMicroseconds");

    /// <summary>Pwm.Stop.</summary>
    public static void PwmStop(int pin) => throw ChipOnly("Pwm.Stop");

    private static PlatformNotSupportedException ChipOnly(string method) => new(
        $"ChipSharp.Hardware.{method} runs on a chip, not on .NET: build this program with ChipSharp for a target " +
        "such as 'pico' and run it there (or in the rp2040js simulator).");
}
