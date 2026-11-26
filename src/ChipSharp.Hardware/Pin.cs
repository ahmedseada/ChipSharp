namespace ChipSharp.Hardware;

/// <summary>
/// The chip's general-purpose input/output pins, by number: on the Raspberry Pi Pico, GPIO 0 to 29, with the LED on
/// GPIO 25. A pin number the chip does not have is ignored (and reads as low).
/// </summary>
/// <remarks>
/// These methods run on a chip, in a program built with ChipSharp; there each call becomes a call into the chip's
/// runtime. On .NET itself they throw <see cref="PlatformNotSupportedException"/>.
/// </remarks>
public static class Pin
{
    /// <summary>Makes <paramref name="pin"/> an output, driven low until written.</summary>
    public static void SetOutput(int pin) => throw ChipOnly.Method("Pin.SetOutput");

    /// <summary>Makes <paramref name="pin"/> an input: the chip stops driving it and <see cref="Read"/> reads it.</summary>
    public static void SetInput(int pin) => throw ChipOnly.Method("Pin.SetInput");

    /// <summary>Drives the output <paramref name="pin"/> high (true) or low (false).</summary>
    public static void Write(int pin, bool high) => throw ChipOnly.Method("Pin.Write");

    /// <summary>Drives the output <paramref name="pin"/> to the other level: high if it was low, low if it was high.</summary>
    public static void Toggle(int pin) => throw ChipOnly.Method("Pin.Toggle");

    /// <summary>True when <paramref name="pin"/> is high.</summary>
    public static bool Read(int pin) => throw ChipOnly.Method("Pin.Read");
}
