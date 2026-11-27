namespace ChipSharp.Hardware;

/// <summary>
/// The chip's analog inputs (ADC): the voltage on a pin, as a number from 0 to 4095 (12 bits) or in volts, and the
/// chip's own temperature sensor. On the Raspberry Pi Pico the analog pins are GPIO 26, 27 and 28 (and 29, which
/// measures a third of the supply voltage VSYS), from 0 V to 3.3 V. Make a pin an <see cref="Input"/> before reading
/// it.
/// </summary>
/// <remarks>
/// <para>
/// A pin that is not an analog input, or one read before <see cref="Input"/>, stops the program with a message saying
/// so; <c>chipsharp check</c> finds the pins a program gives as constants before it runs. A reading takes about 2
/// microseconds; the methods do not use interrupts, so an interrupt handler can call them.
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
    /// The voltage on <paramref name="pin"/>, from 0 (0 V) to 4095 (the reference, 3.3 V on the Pico): one reading of
    /// the 12-bit converter.
    /// </summary>
    public static int Read(int pin) => throw ChipOnly.Method("Adc.Read");

    /// <summary>
    /// The voltage on <paramref name="pin"/> in volts: <see cref="Read"/> times the reference (3.3 V on the Pico) and
    /// divided by 4096, as the RP2040 datasheet converts it.
    /// </summary>
    public static double Volts(int pin) => throw ChipOnly.Method("Adc.Volts");

    /// <summary>
    /// The chip's temperature in degrees Celsius, from its own sensor (the ADC's fifth input), with the RP2040
    /// datasheet's formula: 27 - (volts - 0.706) / 0.001721. It needs no <see cref="Input"/>. The sensor is not
    /// calibrated: a reading can be a few degrees off.
    /// </summary>
    public static double Temperature() => throw ChipOnly.Method("Adc.Temperature");
}
