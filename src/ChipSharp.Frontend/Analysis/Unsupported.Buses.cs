namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// What a target says about ChipSharp.Hardware's buses (see <see cref="HardwareBuses"/>), in C# words. Like a rule of
/// interrupt handlers each never "arrives": its heading says what is wrong with the program for that target.
/// </summary>
internal sealed partial record Unsupported
{
    /// <summary>A use of ChipSharp.Hardware.I2c (or Spi) on a target without the capability "i2c" (or "spi").</summary>
    /// <param name="type">The class, e.g. "I2c".</param>
    /// <param name="bus">The bus as people write it, e.g. "I2C".</param>
    /// <param name="capability">The capability, e.g. "i2c".</param>
    /// <param name="target">The target's name.</param>
    public static Unsupported NoBus(string type, string bus, string capability, string target) => Rule(
        $"ChipSharp.Hardware.{type} needs an {bus} bus, and the target '{target}' has none",
        $"Build the program for a chip that has one: its target file says \"{capability}\": true among its " +
        "capabilities, as the Pico's does.");

    /// <summary>A constant argument of I2c.Open (or Spi.Open) that the target does not have.</summary>
    /// <param name="what">What is wrong, e.g. "GPIO 6 is not an SDA pin of I2C bus 0 (I2c.Open's sda) on target 'pico'".</param>
    /// <param name="hint">What the target has, e.g. "Its SDA pins are GPIO 0, 4, 8, 12, 16, 20, 24 and 28."</param>
    public static Unsupported BusArgument(string what, string hint) => Rule(what, hint);
}
