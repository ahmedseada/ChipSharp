namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's C# for <see cref="ChipSharp.Hardware.Adc"/>: the readings come from the runtime's functions (on the
/// RP2040, rp2040_adc.c), which check the pins; volts and degrees are worked out here.
/// </summary>
[Substitute(typeof(ChipSharp.Hardware.Adc))]
internal static class Adc
{
    /// <summary>The number the converter's reference voltage would read as: 2 to the 12th.</summary>
    private const double FullScale = 4096;

    public static void Input(int pin) => ChipRuntime.AdcInput(pin);

    public static int Read(int pin) => ChipRuntime.AdcRead(pin);

    public static double Volts(int pin) => ChipRuntime.AdcRead(pin) * ChipRuntime.AdcReference() / FullScale;

    /// <summary>The RP2040 datasheet's formula (4.9.5): the sensor gives 0.706 V at 27 degrees, 1.721 mV less per degree.</summary>
    public static double Temperature() =>
        27 - (ChipRuntime.AdcTemperature() * ChipRuntime.AdcReference() / FullScale - 0.706) / 0.001721;
}
