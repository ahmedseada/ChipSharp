using ChipSharp.Hardware;

namespace ChipSharp.Components;

/// <summary>
/// A 7-segment display, one digit of seven LEDs (segments A to G) and often a decimal point (DP), each on a pin of its
/// own. Make one for each display: <c>new</c> makes its pins outputs and leaves it blank; then it shows digits, the
/// hex letters and raw segments:
/// <code>
/// var count = new SevenSegment(firstPin: 2);           // A to G on GPIO 2 to 8, common cathode
/// var other = new SevenSegment(a: 10, b: 11, c: 12, d: 13, e: 14, f: 15, g: 16, dp: 17, commonAnode: true);
/// count.Show(7);
/// other.Show('F');
/// other.Point(true);
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// The segments as the datasheets name them: A at the top, then clockwise B, C, D (at the bottom), E and F, and G in
/// the middle. As a number, a display's segments are one bit each, bit 0 for A to bit 6 for G and bit 7 for DP:
/// <see cref="Pattern(int)"/> gives a digit's (7 is 0x07: A, B and C), the same byte a 74HC595 on <see cref="Spi"/>
/// takes when its outputs Q0 to Q7 drive A to DP. A common-cathode display (its COM to GND) lights a segment whose pin
/// is high; a common-anode one (its COM to 3.3 V) one whose pin is low, and the display turns its bits round.
/// </para>
/// <para>
/// It shows the digits 0 to 9 and the chars '0' to '9', 'A' to 'F' (as A b C d E F, also for 'a' to 'f'), '-', '_' and
/// ' '. A digit outside 0 to 9, or a char it has no shape for, shows as '-' (as <see cref="Lcd"/> shows a char it does
/// not have as '?'): nothing stops the program. <see cref="Show(int)"/> sets A to G and leaves DP as it is, so a point
/// stays on from digit to digit; <see cref="Write"/> and <see cref="Blank"/> set DP too.
/// </para>
/// <para>
/// A struct of 9 bytes and no heap: several displays at once, on a chip without a heap too. The shapes are a constant
/// table, in flash. A display made with <c>default</c> rather than <c>new</c> has no pins and shows nothing. A pin
/// number below 0 or above 254 is no pin, as is a missing DP; <see cref="Pin"/> ignores a pin the chip does not have.
/// Nothing waits, so an interrupt handler can call it.
/// </para>
/// <para>
/// Plain C# over <see cref="Pin.Output"/> and <see cref="Pin.Write"/>: ChipSharp compiles what a program calls of it with
/// the program, and nothing of it into one that does not. <see cref="Pattern(int)"/> runs on .NET too; the rest reaches
/// ChipSharp.Hardware, which throws <see cref="PlatformNotSupportedException"/> there.
/// </para>
/// </remarks>
public readonly struct SevenSegment
{
    // the shapes of '-' and '_': G alone, D alone
    private const byte Dash = 0x40;
    private const byte Underscore = 0x08;

    // each segment's pin + 1, so that 0 (a missing DP, or a display made with default) is pin -1, which Pin ignores
    private readonly byte _a, _b, _c, _d, _e, _f, _g, _dp;
    private readonly bool _commonAnode;

    /// <summary>
    /// A display whose segments A to G are on the pins <paramref name="firstPin"/> to <paramref name="firstPin"/> + 6
    /// (e.g. GPIO 2 to 8), with no DP: makes those pins outputs and the display blank.
    /// </summary>
    public SevenSegment(int firstPin, bool commonAnode = false)
        : this(firstPin, firstPin + 1, firstPin + 2, firstPin + 3, firstPin + 4, firstPin + 5, firstPin + 6, -1,
            commonAnode)
    {
    }

    /// <summary>
    /// A display whose segments A to G are on the pins <paramref name="a"/> to <paramref name="g"/>, and its decimal
    /// point on <paramref name="dp"/> (-1: none): makes those pins outputs and the display blank.
    /// </summary>
    public SevenSegment(int a, int b, int c, int d, int e, int f, int g, int dp = -1, bool commonAnode = false)
    {
        _a = Stored(a);
        _b = Stored(b);
        _c = Stored(c);
        _d = Stored(d);
        _e = Stored(e);
        _f = Stored(f);
        _g = Stored(g);
        _dp = Stored(dp);
        _commonAnode = commonAnode;

        Pin.Output(_a - 1);
        Pin.Output(_b - 1);
        Pin.Output(_c - 1);
        Pin.Output(_d - 1);
        Pin.Output(_e - 1);
        Pin.Output(_f - 1);
        Pin.Output(_g - 1);
        Pin.Output(_dp - 1);
        Blank();
    }

    /// <summary>Shows <paramref name="digit"/>, 0 to 9 (any other as '-'), on A to G; DP stays as it is.</summary>
    public void Show(int digit) => Segments(Pattern(digit));

    /// <summary>
    /// Shows <paramref name="character"/>: '0' to '9', 'A' to 'F' or 'a' to 'f' (as A b C d E F), '-', '_' or ' ' (any
    /// other as '-'), on A to G; DP stays as it is.
    /// </summary>
    public void Show(char character) => Segments(Pattern(character));

    /// <summary>
    /// Lights the segments whose bits are set in <paramref name="segments"/>, bit 0 for A to bit 6 for G and bit 7 for
    /// DP, and turns the others off (0b0111111, 0x3F: a 0 with no point; the bits above 7 do nothing).
    /// </summary>
    public void Write(int segments)
    {
        Segments(segments);
        Pin.Write(_dp - 1, Lit(segments, 0x80));
    }

    /// <summary>Turns the decimal point on (true) or off; A to G stay. Nothing without a DP.</summary>
    public void Point(bool on) => Pin.Write(_dp - 1, on != _commonAnode);

    /// <summary>Turns every segment off, DP too.</summary>
    public void Blank() => Write(0);

    /// <summary>
    /// The segments that show <paramref name="digit"/>, 0 to 9, bit 0 for A to bit 6 for G (0x3F, 0x06, 0x5B, 0x4F,
    /// 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F); '-' (0x40) for any other number.
    /// </summary>
    public static byte Pattern(int digit) => (uint)digit < 10 ? Shapes[digit] : Dash;

    /// <summary>
    /// The segments that show <paramref name="character"/>: '0' to '9' as the digits, 'A' to 'F' and 'a' to 'f' as
    /// A b C d E F (0x77, 0x7C, 0x39, 0x5E, 0x79, 0x71), '-' 0x40, '_' 0x08 and ' ' 0; '-' for any other char.
    /// </summary>
    public static byte Pattern(char character)
    {
        if (character is >= '0' and <= '9')
        {
            return Shapes[character - '0'];
        }

        // 'A' to 'F' and 'a' to 'f' alike: setting bit 5 makes an upper-case letter lower-case
        var letter = character | 0x20;
        if (letter is >= 'a' and <= 'f')
        {
            return Shapes[letter - 'a' + 10];
        }

        return character switch
        {
            ' ' => 0,
            '_' => Underscore,
            _ => Dash,
        };
    }

    /// <summary>The segments of 0 to 9, then of A b C d E F: constant data, in flash.</summary>
    private static ReadOnlySpan<byte> Shapes =>
        [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F, 0x77, 0x7C, 0x39, 0x5E, 0x79, 0x71];

    /// <summary>A to G as <paramref name="segments"/> has them.</summary>
    private void Segments(int segments)
    {
        Pin.Write(_a - 1, Lit(segments, 0x01));
        Pin.Write(_b - 1, Lit(segments, 0x02));
        Pin.Write(_c - 1, Lit(segments, 0x04));
        Pin.Write(_d - 1, Lit(segments, 0x08));
        Pin.Write(_e - 1, Lit(segments, 0x10));
        Pin.Write(_f - 1, Lit(segments, 0x20));
        Pin.Write(_g - 1, Lit(segments, 0x40));
    }

    /// <summary>The level of the pin of the segment <paramref name="bit"/>: high to light it, low on a common anode.</summary>
    private bool Lit(int segments, int bit) => ((segments & bit) != 0) != _commonAnode;

    /// <summary>A pin as the fields keep it: its number + 1; 0 for -1 and any other that is not 0 to 254.</summary>
    private static byte Stored(int pin) => (uint)pin < 255 ? (byte)(pin + 1) : (byte)0;
}
