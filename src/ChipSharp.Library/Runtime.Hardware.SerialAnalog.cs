namespace ChipSharp.Library;

/// <summary>
/// The runtime functions of the serial ports, the console's input, the analog inputs and the PWM outputs (Uart.cs,
/// Console.cs, Adc.cs, Pwm.cs), in a chip's runtime (on the RP2040: rp2040_uart.c, rp2040_adc.c, rp2040_pwm.c). They
/// check their arguments and stop the program, saying what is wrong, where .NET IoT would throw. On .NET there is no
/// such hardware: they throw <see cref="PlatformNotSupportedException"/>.
/// </summary>
internal static partial class Runtime
{
    /// <summary>Opens a UART on its TX and RX pins at a speed; it receives into a buffer from then on.</summary>
    public static void UartOpen(int port, int tx, int rx, int baud) => throw SerialAnalogOnChip();

    /// <summary>Sends a byte (0 to 255), once the UART's send FIFO has room.</summary>
    public static void UartWriteByte(int port, int value) => throw SerialAnalogOnChip();

    /// <summary>The next byte received, or -1 when none is waiting.</summary>
    public static int UartReadByte(int port) => throw SerialAnalogOnChip();

    /// <summary>How many bytes received are waiting.</summary>
    public static int UartAvailable(int port) => throw SerialAnalogOnChip();

    /// <summary>Starts the uart event for the port (see Interrupts: it starts the event "uart").</summary>
    public static void UartWatch(int port) => throw SerialAnalogOnChip();

    /// <summary>Stops the uart event for the port.</summary>
    public static void UartUnwatch(int port) => throw SerialAnalogOnChip();

    /// <summary>The next char of the console's input, waiting for it (see Interrupts: it waits).</summary>
    public static int ConsoleRead() => throw SerialAnalogOnChip();

    /// <summary>Makes a pin an analog input.</summary>
    public static void AdcInput(int pin) => throw SerialAnalogOnChip();

    /// <summary>One reading of an analog input, 0 to 4095.</summary>
    public static int AdcRead(int pin) => throw SerialAnalogOnChip();

    /// <summary>One reading of the temperature sensor, 0 to 4095.</summary>
    public static int AdcTemperature() => throw SerialAnalogOnChip();

    /// <summary>The voltage a reading of 4096 would be: the board's ADC reference.</summary>
    public static double AdcReference() => throw SerialAnalogOnChip();

    /// <summary>Makes a pin a PWM output at a frequency, low.</summary>
    public static void PwmOutput(int pin, int frequency) => throw SerialAnalogOnChip();

    /// <summary>The part of each period a PWM output is high, 0 to 1.</summary>
    public static void PwmDuty(int pin, double duty) => throw SerialAnalogOnChip();

    /// <summary>How long a PWM output is high at the start of each period, in microseconds.</summary>
    public static void PwmPulse(int pin, int microseconds) => throw SerialAnalogOnChip();

    /// <summary>Stops a PWM output.</summary>
    public static void PwmStop(int pin) => throw SerialAnalogOnChip();

    private static PlatformNotSupportedException SerialAnalogOnChip() =>
        new("Serial ports, analog inputs and PWM outputs are a chip's: build the program with ChipSharp for one.");
}
