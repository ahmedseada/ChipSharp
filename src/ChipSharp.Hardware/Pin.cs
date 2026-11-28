namespace ChipSharp.Hardware;

/// <summary>
/// The chip's general-purpose input/output pins, by number: on the Raspberry Pi Pico, GPIO 0 to 29, with the LED on
/// GPIO 25. Make a pin an <see cref="Output"/> before writing or toggling it, an <see cref="Input(int)"/> before reading or
/// watching it. A pin number the chip does not have is ignored (and reads as low).
/// </summary>
/// <remarks>
/// These methods run on a chip, in a program built with ChipSharp; there each call becomes a call into the chip's
/// runtime. On .NET itself they throw <see cref="PlatformNotSupportedException"/>.
/// </remarks>
public static class Pin
{
    /// <summary>Makes <paramref name="pin"/> an output, driven low until written.</summary>
    public static void Output(int pin) => throw ChipOnly.Method("Pin.Output");

    /// <summary>
    /// Makes <paramref name="pin"/> an input with no pull, the same on every chip: the chip stops driving it and
    /// <see cref="Read"/> reads it. A pin nothing drives floats: give it a <see cref="Pull"/> with
    /// <see cref="Input(int, Pull)"/>, or wire a resistor.
    /// </summary>
    public static void Input(int pin) => throw ChipOnly.Method("Pin.Input");

    /// <summary>
    /// Makes <paramref name="pin"/> an input with the chip's own resistor: <see cref="Pull.Up"/> reads high until something
    /// (a button to ground) pulls the pin low, <see cref="Pull.Down"/> reads low until something (a button to the supply)
    /// pulls it high. Every chip has pull-ups; the Raspberry Pi Pico and the ESP32-C5 also have pull-downs, and
    /// <c>chipsharp check</c> refuses <see cref="Pull.Down"/> for a chip without (the ATmega328P of the Uno, the PFS154,
    /// the STM8S103F3). <see cref="Pull.None"/> is <see cref="Input(int)"/>.
    /// </summary>
    public static void Input(int pin, Pull pull) => throw ChipOnly.Method("Pin.Input");

    /// <summary>Drives the output <paramref name="pin"/> high (true) or low (false).</summary>
    public static void Write(int pin, bool high) => throw ChipOnly.Method("Pin.Write");

    /// <summary>Drives the output <paramref name="pin"/> to the other level: high if it was low, low if it was high.</summary>
    public static void Toggle(int pin) => throw ChipOnly.Method("Pin.Toggle");

    /// <summary>True when <paramref name="pin"/> is high.</summary>
    public static bool Read(int pin) => throw ChipOnly.Method("Pin.Read");

    /// <summary>
    /// Runs the program's <c>[Interrupt(Event.Pin)]</c> method each time <paramref name="pin"/> changes, from low to
    /// high and from high to low. Changes from before this call are forgotten.
    /// </summary>
    public static void Watch(int pin) => throw ChipOnly.Method("Pin.Watch");

    /// <summary>
    /// Runs the program's <c>[Interrupt(Event.Pin)]</c> method each time <paramref name="pin"/> goes from low to high,
    /// e.g. when a button between the pin and 3.3 V is pressed. Changes from before this call are forgotten.
    /// </summary>
    public static void WatchRising(int pin) => throw ChipOnly.Method("Pin.WatchRising");

    /// <summary>
    /// Runs the program's <c>[Interrupt(Event.Pin)]</c> method each time <paramref name="pin"/> goes from high to low,
    /// e.g. when a button between the pin and ground is pressed. Changes from before this call are forgotten.
    /// </summary>
    public static void WatchFalling(int pin) => throw ChipOnly.Method("Pin.WatchFalling");

    /// <summary>Stops watching <paramref name="pin"/>: its changes no longer run the <c>[Interrupt(Event.Pin)]</c> method.</summary>
    public static void Unwatch(int pin) => throw ChipOnly.Method("Pin.Unwatch");
}

/// <summary>The chip's own resistor on an input pin: <see cref="Pin.Input(int, Pull)"/>.</summary>
public enum Pull
{
    /// <summary>No pull: the pin floats until something drives it.</summary>
    None = 0,

    /// <summary>A pull-up to the supply: the pin reads high until something pulls it low. Every chip has one.</summary>
    Up = 1,

    /// <summary>A pull-down to ground: the pin reads low until something pulls it high. Only some chips have one.</summary>
    Down = 2,
}
