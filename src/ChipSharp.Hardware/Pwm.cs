namespace ChipSharp.Hardware;

/// <summary>
/// Pulse-width modulation (PWM): a pin switched on and off by the chip at a fixed frequency, high for a part of each
/// period (the duty cycle), with no work for the program: an LED's brightness, a motor's speed, a servo's angle. Make a
/// pin a PWM <see cref="Output"/> at a frequency first; then set how much of each period it is high with
/// <see cref="Duty"/> (0 to 1, as .NET IoT's PwmChannel.DutyCycle) or <see cref="PulseMicroseconds"/>.
/// </summary>
/// <remarks>
/// <para>
/// On the Raspberry Pi Pico every GPIO can be a PWM output. Its 8 PWM slices have two outputs each, A and B, which run
/// at the slice's one frequency: GPIO 2n and 2n + 1 share slice n (GPIO 0 and 1 slice 0, GPIO 2 and 3 slice 1, ...,
/// GPIO 14 and 15 slice 7), and from GPIO 16 on the slices come round again (GPIO 16 and 17 share slice 0 with GPIO 0
/// and 1). Two pins of one slice can have different duty cycles but not different frequencies, and GPIO n and n + 16
/// are the same output: <see cref="Output"/> stops the program, saying so, when a pin's slice runs at another
/// frequency or its output already drives another pin (<see cref="Stop"/> the other pin first).
/// </para>
/// <para>
/// The frequency can be from 8 Hz to 62.5 MHz; the chip makes it as exactly as its clock (125 MHz) divides, and the
/// duty cycle in up to 65536 steps (fewer above 1907 Hz: 125 MHz / frequency steps).
/// </para>
/// <para>
/// These methods run on a chip, in a program built with ChipSharp. On .NET itself they throw
/// <see cref="PlatformNotSupportedException"/>.
/// </para>
/// </remarks>
public static class Pwm
{
    /// <summary>
    /// Makes <paramref name="pin"/> a PWM output at <paramref name="frequency"/> periods per second (Hz), low until
    /// <see cref="Duty"/> or <see cref="PulseMicroseconds"/> says otherwise.
    /// </summary>
    public static void Output(int pin, int frequency) => throw ChipOnly.Method("Pwm.Output");

    /// <summary>
    /// Makes the PWM output <paramref name="pin"/> high for <paramref name="duty"/> of each period, from 0 (always low)
    /// to 1 (always high): 0.25 is high a quarter of the time. The change starts with the next period.
    /// </summary>
    public static void Duty(int pin, double duty) => throw ChipOnly.Method("Pwm.Duty");

    /// <summary>
    /// Makes the PWM output <paramref name="pin"/> high for the first <paramref name="microseconds"/> of each period,
    /// e.g. a servo at 50 Hz: 1000 (1 ms) for one end, 1500 for the middle, 2000 for the other end. A pulse as long as
    /// the period, or longer, keeps the pin high.
    /// </summary>
    public static void PulseMicroseconds(int pin, int microseconds) => throw ChipOnly.Method("Pwm.PulseMicroseconds");

    /// <summary>
    /// Stops the PWM output <paramref name="pin"/>: the pin is no longer driven, as after start-up, and its slice can
    /// run at another frequency once neither of its outputs is in use.
    /// </summary>
    public static void Stop(int pin) => throw ChipOnly.Method("Pwm.Stop");
}
