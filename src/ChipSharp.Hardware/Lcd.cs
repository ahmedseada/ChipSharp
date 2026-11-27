namespace ChipSharp.Hardware;

/// <summary>
/// A character LCD on an I2C bus: an HD44780 (or a chip that works like one: a 16×2 "1602", a 20×4 "2004") behind a
/// PCF8574 "backpack", the 8-bit port expander that turns the bus into the display's pins, usually at address 0x27
/// (0x3F for a PCF8574A). <see cref="I2c.Open"/> the bus, then <see cref="Open"/> the display before writing on it:
/// <code>
/// I2c.Open(1, sda: 26, scl: 27, frequency: 100_000);
/// Lcd.Open(bus: 1, address: 0x27, columns: 16, rows: 2);
/// Lcd.Write(0, 0, "Hello");
/// Lcd.Write(6, 0, 42);
/// Lcd.Line(1, "ready");
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Columns and rows count from 0 at the top left. Text that does not fit is cut off at the end of its row (it never
/// runs on into another row), and a row or column outside the display shows nothing of it. The display shows ASCII,
/// from ' ' to '~', as its ROM (the HD44780's A00, the usual one) has it, except that it shows '\' as '¥' and '~' as
/// '→'; any other char (é, ü, a line end) shows as '?'.
/// </para>
/// <para>
/// Nothing allocates: text is a string (a string literal needs no heap), a span of chars (e.g. of <c>stackalloc</c>)
/// or a number, which is written digit by digit. The backpack's bit layout is the one of the common modules and of
/// Wokwi's <c>wokwi-lcd1602</c>: P0 RS, P1 RW, P2 E, P3 the backlight, P4 to P7 D4 to D7. Each character is one I2C
/// transfer of five bytes (each half of it on D4 to D7, and E's pulses), then a wait of 50 us, the HD44780's time to
/// take it, so any clock of the bus works (the PCF8574 is made for 100 kHz; most take 400 kHz): at 100 kHz a whole
/// row of 16 takes about 10 ms. <see cref="Open"/> and <see cref="Clear"/> wait longer (about 60 ms and 2 ms).
/// </para>
/// <para>
/// One display at a time: the last <see cref="Open"/> chooses it. Before it (or after an <see cref="Open"/> that said
/// false) the other methods do nothing. These methods run on a chip whose target has I2C, in a program built with
/// ChipSharp; <c>chipsharp check</c> names a constant size no HD44780 has. On .NET itself they throw
/// <see cref="PlatformNotSupportedException"/>.
/// </para>
/// </remarks>
public static class Lcd
{
    /// <summary>
    /// Sets up the display of <paramref name="columns"/> by <paramref name="rows"/> characters (16 and 2 for a 1602, 20
    /// and 4 for a 2004; 1 to 4 rows, at most 40 columns, and 20 on more than two rows) whose backpack is at
    /// <paramref name="address"/> on <paramref name="bus"/>, which <see cref="I2c.Open"/> has set up: it waits for the
    /// display to power up (40 ms), puts it in 4-bit mode the HD44780's datasheet's way, clears it and turns it and the
    /// backlight on, with no cursor. True when the backpack answered; false, at once, when nothing answers at the
    /// address or the size is not one an HD44780 has, and then the other methods do nothing.
    /// </summary>
    public static bool Open(int bus, int address, int columns, int rows) => throw ChipOnly.Method("Lcd.Open");

    /// <summary>Clears the display (every character a space) and waits the 2 ms it takes.</summary>
    public static void Clear() => throw ChipOnly.Method("Lcd.Clear");

    /// <summary>
    /// Writes <paramref name="text"/> from <paramref name="column"/> on, in <paramref name="row"/>; what is there after it
    /// stays. Nothing for null.
    /// </summary>
    public static void Write(int column, int row, string text) => throw ChipOnly.Method("Lcd.Write");

    /// <summary>Writes the chars of <paramref name="text"/> from <paramref name="column"/> on, in <paramref name="row"/>.</summary>
    public static void Write(int column, int row, ReadOnlySpan<char> text) => throw ChipOnly.Method("Lcd.Write");

    /// <summary>Writes one char at <paramref name="column"/> in <paramref name="row"/>.</summary>
    public static void Write(int column, int row, char character) => throw ChipOnly.Method("Lcd.Write");

    /// <summary>
    /// Writes <paramref name="number"/> in decimal, from <paramref name="column"/> on, in <paramref name="row"/>: '-' and
    /// its digits, as many as it has ("7", "-120"), with no string made.
    /// </summary>
    public static void Write(int column, int row, int number) => throw ChipOnly.Method("Lcd.Write");

    /// <summary>
    /// Writes <paramref name="text"/> as the whole of <paramref name="row"/>: from column 0, then spaces to the end, so
    /// nothing of what was there before is left (the way to show a status line). Spaces only for null.
    /// </summary>
    public static void Line(int row, string text) => throw ChipOnly.Method("Lcd.Line");

    /// <summary>Writes the chars of <paramref name="text"/> as the whole of <paramref name="row"/>, then spaces to its end.</summary>
    public static void Line(int row, ReadOnlySpan<char> text) => throw ChipOnly.Method("Lcd.Line");

    /// <summary>Turns the backlight on (true, as <see cref="Open"/> leaves it) or off; the text stays.</summary>
    public static void Backlight(bool on) => throw ChipOnly.Method("Lcd.Backlight");
}
