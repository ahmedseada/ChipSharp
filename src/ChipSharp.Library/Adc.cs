namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's C# for <see cref="ChipSharp.Hardware.Adc"/>: the readings come from the runtime's functions (on the
/// RP2040, rp2040_adc.c; on the ATmega328P, atmega328p_adc.c), which check the pins and work out the temperature by
/// their chip's datasheet; volts are worked out here, from the chip's reference and range.
/// </summary>
[Substitute(typeof(ChipSharp.Hardware.Adc))]
internal static class Adc
{
    public static void Input(int pin) => ChipRuntime.AdcInput(pin);

    public static int Read(int pin) => ChipRuntime.AdcRead(pin);

    public static double Volts(int pin) => ChipRuntime.AdcRead(pin) * ChipRuntime.AdcReference() / ChipRuntime.AdcFullScale();

    public static double Temperature() => ChipRuntime.AdcTemperature();
}
