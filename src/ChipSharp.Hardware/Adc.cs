namespace ChipSharp.Hardware;

/// <summary>
/// The chip's analog inputs (ADC): the voltage on a pin, as a number in the chip's own range or in volts, and the chip's
/// own temperature sensor. On the Raspberry Pi Pico the analog pins are GPIO 26, 27 and 28 (and 29, which measures a
/// third of the supply voltage VSYS), from 0 V to 3.3 V, read in 12 bits (0 to 4095). On the Arduino Uno they are A0 to
/// A5 (pins 14 to 19), from 0 V to 5 V, read in the ATmega328P's 10 bits (0 to 1023), as Arduino's analogRead reads
/// them; <see cref="Volts"/> is the same on both. Make a pin an <see cref="Input"/> before reading it.
/// </summary>
/// <remarks>
/// <para>
/// A pin that is not an analog input, or one read before <see cref="Input"/>, stops the program with a message saying
/// so; <c>chipsharp check</c> finds the pins a program gives as constants before it runs. A reading takes about 2
/// microseconds on the Pico, 104 on the Uno (where interrupts stay on meanwhile); the methods do not use interrupts, so
/// an interrupt handler can call them.
/// </para>
/// <para>
/// These methods run on a chip, in a program built with ChipSharp. On .NET itself they throw
/// <see cref="PlatformNotSupportedException"/>.
/// </para>
/// </remarks>
public static class Adc
{
    /// <summary>Makes <paramref name="pin"/> an analog input: the pin's digital input and output are switched off.</summary>
    public static void Input(int pin) => throw ChipOnly.Method("Adc.Input");

    /// <summary>
    /// The voltage on <paramref name="pin"/>, from 0 (0 V) to the converter's highest: 4095 (3.3 V) on the Pico, one
    /// reading of its 12 bits; 1023 (5 V) on the Uno, of its 10.
    /// </summary>
    public static int Read(int pin) => throw ChipOnly.Method("Adc.Read");

    /// <summary>
    /// The voltage on <paramref name="pin"/> in volts: <see cref="Read"/> times the reference and divided by the
    /// readings there are, as the datasheets convert it: 3.3 V and 4096 on the Pico, 5 V (the Uno's AVcc, its supply) and
    /// 1024 on the Uno.
    /// </summary>
    public static double Volts(int pin) => throw ChipOnly.Method("Adc.Volts");

    /// <summary>
    /// The chip's temperature in degrees Celsius, from its own sensor, with its datasheet's numbers: on the Pico (the
    /// ADC's fifth input) 27 - (volts - 0.706) / 0.001721; on the Uno (ADC8, against the chip's 1.1 V) 25 + (reading -
    /// 352) / 1.28, the ATmega328P's typical readings at 25 and 125 degrees. It needs no <see cref="Input"/>. The sensor
    /// is not calibrated: a reading can be a few degrees off on the Pico, up to 10 on the Uno.
    /// </summary>
    public static double Temperature() => throw ChipOnly.Method("Adc.Temperature");
}
