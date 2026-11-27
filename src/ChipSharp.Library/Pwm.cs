namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's C# for <see cref="ChipSharp.Hardware.Pwm"/>: the runtime's functions (on the RP2040, rp2040_pwm.c), which
/// work out the slice's divider and levels and check the pins, frequencies and slices.
/// </summary>
[Substitute(typeof(ChipSharp.Hardware.Pwm))]
internal static class Pwm
{
    public static void Output(int pin, int frequency) => Runtime.PwmOutput(pin, frequency);

    public static void Duty(int pin, double duty) => Runtime.PwmDuty(pin, duty);

    public static void PulseMicroseconds(int pin, int microseconds) => Runtime.PwmPulse(pin, microseconds);

    public static void Stop(int pin) => Runtime.PwmStop(pin);
}
