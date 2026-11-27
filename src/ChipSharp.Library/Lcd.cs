namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's C# for <see cref="ChipSharp.Hardware.Lcd"/>: an HD44780 in 4-bit mode behind a PCF8574, written with
/// <see cref="ChipSharp.Hardware.I2c.Write"/> and the waits of <see cref="ChipSharp.Hardware.Timing"/>, as the
/// HD44780's datasheet (Hitachi ADE-207-272, figure 24, "4-bit interface") has it. Each byte the PCF8574 takes is at
/// once the level of its eight outputs: P0 RS, P1 RW (always 0: the program only writes), P2 E, P3 the backlight, P4
/// to P7 D4 to D7.
/// </summary>
/// <remarks>
/// <para>
/// The HD44780 takes D4 to D7 when E falls. A byte for it, an instruction (RS 0) or a character (RS 1), goes as its
/// upper four bits, then its lower four, in one I2C transfer of five bytes: the upper bits with E low (RS and the data
/// set before E rises), with E high, with E low again (taken), then the lower bits with E high and with E low (taken).
/// Then it waits the 37 us the HD44780 takes to carry the byte out (50 us, for a slower oscillator), whatever the bus's
/// clock; clearing the display takes 1.52 ms (2 ms). The HD44780's busy flag is not read: the waits are the datasheet's.
/// </para>
/// <para>
/// Where a row starts in the HD44780's memory (its DDRAM): row 0 at 0x00 and row 1 at 0x40; on a display of four rows,
/// rows 2 and 3 go on after rows 0 and 1, at 0x00 + columns and 0x40 + columns (0x14 and 0x54 for 20 columns).
/// </para>
/// <para>
/// Nothing stops the program: a display not open takes nothing (its size is 0 by 0), and <see cref="Open"/> of a size
/// no HD44780 has says false (chipsharp check names a constant one). A message would cost the runtime's buffer for the
/// text of an exception, 900 bytes of RAM.
/// </para>
/// </remarks>
[Substitute(typeof(ChipSharp.Hardware.Lcd))]
internal static class Lcd
{
    // the PCF8574's outputs
    private const int RegisterSelect = 0x01;   // P0, RS: 0 for an instruction, 1 for a character
    private const int Enable = 0x04;           // P2, E: the HD44780 takes D4 to D7 as it falls
    private const int Light = 0x08;            // P3: the backlight on

    // the HD44780's instructions
    private const int ClearDisplay = 0x01;
    private const int EntryMode = 0x06;        // the address goes up after each character; the display does not move
    private const int DisplayOff = 0x08;
    private const int DisplayOn = 0x0C;        // no cursor, no blinking
    private const int FourBits = 0x20;         // function set: 4-bit data, one line, 5x8 dots
    private const int TwoLines = 0x08;         // in a function set: two lines (also for a display of four rows)
    private const int EightBits = 0x3;         // the upper four bits of a function set of 8-bit data
    private const int SetAddress = 0x80;       // + the DDRAM address

    // the waits: after each byte, and after clearing the display
    private const int ByteMicroseconds = 50;
    private const int ClearMilliseconds = 2;

    private static int _bus;
    private static int _address;
    private static int _columns;               // 0 until Open
    private static int _rows;
    private static int _light;                 // Light, or 0 with the backlight off

    public static bool Open(int bus, int address, int columns, int rows)
    {
        _columns = 0;
        _rows = 0;
        if (!Fits(columns, rows))
        {
            return false;
        }

        _bus = bus;
        _address = address;
        _light = Light;

        // every output low but the backlight's: E low. Nothing answers: no display there
        Span<byte> port = stackalloc byte[1];
        port[0] = Light;
        if (!ChipSharp.Hardware.I2c.Write(bus, address, port))
        {
            return false;
        }

        _columns = columns;
        _rows = rows;

        // more than 40 ms after the supply reaches 2.7 V; then a function set of 8-bit data three times (4.1 ms after
        // the first, 100 us after the second), which puts it in 8-bit mode from any state, also from the middle of a
        // byte in 4-bit mode; then one of 4-bit data
        ChipSharp.Hardware.Timing.DelayMilliseconds(50);
        var answered = Half(EightBits);
        ChipSharp.Hardware.Timing.DelayMicroseconds(4500);
        answered &= Half(EightBits);
        ChipSharp.Hardware.Timing.DelayMicroseconds(100);
        answered &= Half(EightBits);
        answered &= Half(FourBits >> 4);

        // in 4-bit mode: the lines and the font, the display off, cleared, the address moving right; then on
        answered &= Send(FourBits | (rows > 1 ? TwoLines : 0), 0);
        answered &= Send(DisplayOff, 0);
        answered &= Send(ClearDisplay, 0);
        ChipSharp.Hardware.Timing.DelayMilliseconds(ClearMilliseconds);
        answered &= Send(EntryMode, 0);
        answered &= Send(DisplayOn, 0);
        return answered;
    }

    public static void Clear()
    {
        if (_columns > 0)
        {
            Send(ClearDisplay, 0);
            ChipSharp.Hardware.Timing.DelayMilliseconds(ClearMilliseconds);
        }
    }

    public static void Write(int column, int row, string text)
    {
        var length = text is null ? 0 : text.Length;
        var end = End(column, row, length);
        var first = column < 0 ? -column : 0;
        if (first < end)
        {
            MoveTo(column + first, row);
            for (var i = first; i < end; i++)
            {
                Send(Code(text![i]), RegisterSelect);
            }
        }
    }

    public static void Write(int column, int row, ReadOnlySpan<char> text)
    {
        var end = End(column, row, text.Length);
        var first = column < 0 ? -column : 0;
        if (first < end)
        {
            MoveTo(column + first, row);
            for (var i = first; i < end; i++)
            {
                Send(Code(text[i]), RegisterSelect);
            }
        }
    }

    public static void Write(int column, int row, char character)
    {
        if (column >= 0 && End(column, row, 1) > 0)
        {
            MoveTo(column, row);
            Send(Code(character), RegisterSelect);
        }
    }

    public static void Write(int column, int row, int number)
    {
        // the digits from the last, then '-': int.MinValue's magnitude is a uint
        Span<char> digits = stackalloc char[11];
        var magnitude = number < 0 ? 0u - (uint)number : (uint)number;
        var at = digits.Length;
        do
        {
            digits[--at] = (char)('0' + (int)(magnitude % 10));
            magnitude /= 10;
        }
        while (magnitude != 0);

        if (number < 0)
        {
            digits[--at] = '-';
        }

        Write(column, row, (ReadOnlySpan<char>)digits[at..]);
    }

    public static void Line(int row, string text)
    {
        if (row >= 0 && row < _rows)
        {
            var length = text is null ? 0 : text.Length;
            MoveTo(0, row);
            for (var i = 0; i < _columns; i++)
            {
                Send(i < length ? Code(text![i]) : ' ', RegisterSelect);
            }
        }
    }

    public static void Line(int row, ReadOnlySpan<char> text)
    {
        if (row >= 0 && row < _rows)
        {
            MoveTo(0, row);
            for (var i = 0; i < _columns; i++)
            {
                Send(i < text.Length ? Code(text[i]) : ' ', RegisterSelect);
            }
        }
    }

    public static void Backlight(bool on)
    {
        if (_columns > 0)
        {
            _light = on ? Light : 0;
            Span<byte> port = stackalloc byte[1];
            port[0] = (byte)_light;
            ChipSharp.Hardware.I2c.Write(_bus, _address, port);
        }
    }

    /// <summary>
    /// True for a size an HD44780 shows: 1 to 4 rows, at most 80 characters in all (its DDRAM), 40 to a row (a line of
    /// its DDRAM) and 20 when there are more than two rows (a row of four is half a line).
    /// </summary>
    private static bool Fits(int columns, int rows) =>
        columns >= 1 && rows >= 1 && rows <= 4 && columns <= (rows > 2 ? 20 : 40);

    /// <summary>
    /// Where a text of <paramref name="length"/> chars at <paramref name="column"/> in <paramref name="row"/> stops being
    /// on the display: the index of the first char past the row's end (or past the text); 0 when the row is not there.
    /// </summary>
    private static int End(int column, int row, int length)
    {
        if (row < 0 || row >= _rows || column >= _columns)
        {
            return 0;
        }

        return _columns - column < length ? _columns - column : length;
    }

    /// <summary>The HD44780's address of the column in the row (both on the display).</summary>
    private static void MoveTo(int column, int row)
    {
        var start = (row & 1) == 0 ? 0x00 : 0x40;
        if (row >= 2)
        {
            start += _columns;
        }

        Send(SetAddress | (start + column), 0);
    }

    /// <summary>The character the HD44780's ROM A00 has for <paramref name="character"/>: ' ' to '~' as they are, any other '?'.</summary>
    private static int Code(char character) => character is >= ' ' and <= '~' ? character : '?';

    /// <summary>
    /// One byte for the HD44780, an instruction (<paramref name="select"/> 0) or a character
    /// (<see cref="RegisterSelect"/>), in one transfer: its upper four bits, then its lower four; then the wait for it.
    /// </summary>
    private static bool Send(int value, int select)
    {
        var upper = (value & 0xF0) | _light | select;
        var lower = ((value << 4) & 0xF0) | _light | select;
        Span<byte> bytes = stackalloc byte[5];
        bytes[0] = (byte)upper;
        bytes[1] = (byte)(upper | Enable);
        bytes[2] = (byte)upper;
        bytes[3] = (byte)(lower | Enable);
        bytes[4] = (byte)lower;
        var answered = ChipSharp.Hardware.I2c.Write(_bus, _address, bytes);
        ChipSharp.Hardware.Timing.DelayMicroseconds(ByteMicroseconds);
        return answered;
    }

    /// <summary>
    /// The upper four bits of an instruction alone, while the HD44780 may be in 8-bit mode: on D4 to D7, E high, then
    /// low; then the wait for it.
    /// </summary>
    private static bool Half(int bits)
    {
        var port = (bits << 4) | _light;
        Span<byte> bytes = stackalloc byte[3];
        bytes[0] = (byte)port;
        bytes[1] = (byte)(port | Enable);
        bytes[2] = (byte)port;
        var answered = ChipSharp.Hardware.I2c.Write(_bus, _address, bytes);
        ChipSharp.Hardware.Timing.DelayMicroseconds(ByteMicroseconds);
        return answered;
    }
}
