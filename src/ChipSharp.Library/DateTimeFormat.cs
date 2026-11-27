namespace ChipSharp.Library;

/// <summary>
/// Text of <see cref="DateTime"/>, <see cref="DateOnly"/> and <see cref="TimeOnly"/> as .NET 10 writes it with the
/// invariant culture: .NET's own DateTimeFormat.cs, for the Gregorian calendar and the invariant culture's names and
/// patterns (<c>MM/dd/yyyy HH:mm:ss</c>, <c>Sunday</c>, <c>January</c>, <c>AM</c>, <c>A.D.</c>), with its checks and
/// messages. It writes into a <see cref="DateTimeText"/>: a span (TryFormat), the console a few chars at a time
/// (printed text, no string made), or nowhere, counting (to align); only ToString makes a string.
/// </summary>
/// <remarks>
/// .NET formats with the current culture; ChipSharp always with the invariant one, as .NET does in its invariant mode.
/// The offset of the formats <c>z</c>, <c>zz</c>, <c>zzz</c>, <c>K</c> and <c>o</c> of a local or unspecified time is the
/// target's offset now (<see cref="Runtime.LocalUtcOffset"/>: 0 on a chip, the PC's time zone's now on a PC), where
/// .NET takes the offset at the date being formatted (they differ for a date on the other side of a change to or from
/// daylight saving time).
/// </remarks>
internal static class DateTimeFormat
{
    private const int MaxSecondsFractionDigits = 7;
    private const string Format = "System.FormatException";
    private const string InvalidString = "Input string was not in a correct format.";

    /// <summary>
    /// Writes <paramref name="dateTime"/> with <paramref name="format"/>: empty for the invariant G
    /// (<c>MM/dd/yyyy HH:mm:ss</c>), one char for a standard format, more for a custom one. A format .NET refuses
    /// stops the program with its FormatException.
    /// </summary>
    public static void Write(DateTime dateTime, ReadOnlySpan<char> format, ref DateTimeText text)
    {
        if (format.Length == 0)
        {
            WriteInvariantG(dateTime, ref text);
            return;
        }

        if (format.Length > 1)
        {
            WriteCustom(dateTime, format, ref text);
            return;
        }

        switch (format[0])
        {
            case 'o':
            case 'O':
                WriteRoundtrip(dateTime, ref text);
                break;
            case 'r':
            case 'R':
                WriteRfc1123(dateTime, ref text);
                break;
            case 's':
                WriteSortable(dateTime, 'T', ref text);
                break;
            case 'u':
                WriteSortable(dateTime, ' ', ref text);
                text.Append('Z');
                break;
            case 'U':
                // the universal time, in the full pattern
                WriteCustom(ToUniversalTime(dateTime), "dddd, dd MMMM yyyy HH:mm:ss", ref text);
                break;
            case 'd':
                WriteCustom(dateTime, "MM/dd/yyyy", ref text);
                break;
            case 'D':
                WriteCustom(dateTime, "dddd, dd MMMM yyyy", ref text);
                break;
            case 'f':
                WriteCustom(dateTime, "dddd, dd MMMM yyyy HH:mm", ref text);
                break;
            case 'F':
                WriteCustom(dateTime, "dddd, dd MMMM yyyy HH:mm:ss", ref text);
                break;
            case 'g':
                WriteCustom(dateTime, "MM/dd/yyyy HH:mm", ref text);
                break;
            case 'G':
                WriteCustom(dateTime, "MM/dd/yyyy HH:mm:ss", ref text);
                break;
            case 'm':
            case 'M':
                WriteCustom(dateTime, "MMMM dd", ref text);
                break;
            case 't':
                WriteCustom(dateTime, "HH:mm", ref text);
                break;
            case 'T':
                WriteCustom(dateTime, "HH:mm:ss", ref text);
                break;
            case 'y':
            case 'Y':
                WriteCustom(dateTime, "yyyy MMMM", ref text);
                break;
            default:
                Runtime.Throw(Format, InvalidString, null);
                break;
        }
    }

    /// <summary>
    /// Writes a DateOnly (its date at midnight, <paramref name="dateTime"/>) with <paramref name="format"/> as
    /// DateOnly's ToString (<paramref name="toString"/>) or TryFormat (and a <c>$"..."</c> hole) writes it: empty for d;
    /// o, r, d, m and y (either case); a custom format without a time. The two refuse the others with different messages.
    /// </summary>
    public static void WriteDateOnly(DateTime dateTime, ReadOnlySpan<char> format, bool toString, ref DateTimeText text)
    {
        if (format.Length == 0)
        {
            WriteCustom(dateTime, "MM/dd/yyyy", ref text);
            return;
        }

        if (format.Length == 1)
        {
            switch (format[0] | 0x20)
            {
                case 'o':
                    WriteDate(dateTime, ref text);
                    return;
                case 'r':
                    WriteDayAndDate(dateTime, ref text);
                    return;
                case 'm':
                case 'd':
                case 'y':
                    Write(dateTime, format, ref text);
                    return;
                default:
                    Runtime.Throw(Format, toString ? InvalidString : "Format specifier was invalid.", null);
                    return;
            }
        }

        CheckCustom(format, "tfFhHmszK:", toString, "DateOnly");
        WriteCustom(dateTime, format, ref text);
    }

    /// <summary>
    /// Writes a TimeOnly (its time on 1 January 0001, <paramref name="dateTime"/>) as TimeOnly's ToString or TryFormat
    /// writes it: empty for t; o, r and t (either case); a custom format without a date.
    /// </summary>
    public static void WriteTimeOnly(DateTime dateTime, ReadOnlySpan<char> format, bool toString, ref DateTimeText text)
    {
        if (format.Length == 0)
        {
            WriteCustom(dateTime, "HH:mm", ref text);
            return;
        }

        if (format.Length == 1)
        {
            switch (format[0] | 0x20)
            {
                case 'o':
                    WriteTime(dateTime, ref text);
                    text.Append('.');
                    dateTime.GetTimePrecise(out _, out _, out _, out int fraction);
                    WriteDigits(ref text, fraction, 7);
                    return;
                case 'r':
                    WriteTime(dateTime, ref text);
                    return;
                case 't':
                    Write(dateTime, format, ref text);
                    return;
                default:
                    Runtime.Throw(Format, toString ? InvalidString : "Format specifier was invalid.", null);
                    return;
            }
        }

        CheckCustom(format, "dMy/zk", toString, "TimeOnly");
        WriteCustom(dateTime, format, ref text);
    }

    /// <summary>
    /// .NET's IsValidCustomDateOnlyFormat and IsValidCustomTimeOnlyFormat: a custom format of a DateOnly (TimeOnly) has
    /// none of the <paramref name="refused"/> pattern chars outside quotes, and its quotes and escapes end. ToString
    /// refuses one that does not with .NET's FormatException; TryFormat with "String '...' contains parts which are
    /// not specific to the DateOnly." (or TimeOnly).
    /// </summary>
    private static void CheckCustom(ReadOnlySpan<char> format, string refused, bool toString, string type)
    {
        int i = 0;
        while (i < format.Length)
        {
            char c = format[i];
            if (c == '\\')
            {
                if (i == format.Length - 1)
                {
                    Refuse(format, toString, type, InvalidString);
                }

                i += 2;
            }
            else if (c == '\'' || c == '"')
            {
                i++;
                while (i < format.Length && format[i] != c)
                {
                    i++;
                }

                if (i >= format.Length)
                {
                    Refuse(format, toString, type, c == '\'' ? BadSingleQuote : BadDoubleQuote);
                }

                i++;
            }
            else
            {
                for (int r = 0; r < refused.Length; r++)
                {
                    if (refused[r] == c)
                    {
                        Refuse(format, toString, type, InvalidString);
                    }
                }

                i++;
            }
        }
    }

    private const string BadSingleQuote = "Cannot find a matching quote character for the character '''.";
    private const string BadDoubleQuote = "Cannot find a matching quote character for the character '\"'.";

    private static void Refuse(ReadOnlySpan<char> format, bool toString, string type, string message)
    {
        if (toString)
        {
            Runtime.Throw(Format, message, null);
        }

        Runtime.ThrowText(Format, "String '", format[0], format.Length,
            type == "DateOnly" ? "' contains parts which are not specific to the DateOnly."
                : "' contains parts which are not specific to the TimeOnly.");
    }

    /// <summary>
    /// A custom format: each pattern (<c>yyyy</c>, <c>MM</c>, <c>dddd</c>, <c>HH</c>, <c>fff</c>, <c>tt</c>, ...) as its
    /// part of the date, quoted text and escaped chars as they are, any other char as itself (.NET's FormatCustomized).
    /// </summary>
    private static void WriteCustom(DateTime dateTime, ReadOnlySpan<char> format, ref DateTimeText text)
    {
        // whether only hour, minute and second came so far: the z formats of a time before 2 January 0001 then take
        // the offset now
        bool timeOnly = true;
        int i = 0;
        while (i < format.Length)
        {
            char ch = format[i];
            int tokenLen;
            switch (ch)
            {
                case 'g':
                    tokenLen = RepeatCount(format, i, ch);
                    text.Append("A.D.", 4);
                    break;

                case 'h':
                    tokenLen = RepeatCount(format, i, ch);
                    int hour12 = dateTime.Hour;
                    if (hour12 > 12)
                    {
                        hour12 -= 12;
                    }
                    else if (hour12 == 0)
                    {
                        hour12 = 12;
                    }

                    WriteDigits(ref text, hour12, tokenLen < 2 ? tokenLen : 2);
                    break;

                case 'H':
                    tokenLen = RepeatCount(format, i, ch);
                    WriteDigits(ref text, dateTime.Hour, tokenLen < 2 ? tokenLen : 2);
                    break;

                case 'm':
                    tokenLen = RepeatCount(format, i, ch);
                    WriteDigits(ref text, dateTime.Minute, tokenLen < 2 ? tokenLen : 2);
                    break;

                case 's':
                    tokenLen = RepeatCount(format, i, ch);
                    WriteDigits(ref text, dateTime.Second, tokenLen < 2 ? tokenLen : 2);
                    break;

                case 'f':
                case 'F':
                    tokenLen = RepeatCount(format, i, ch);
                    if (tokenLen > MaxSecondsFractionDigits)
                    {
                        Runtime.Throw(Format, InvalidString, null);
                    }

                    int fraction = (int)(dateTime.Ticks % System.TimeSpan.TicksPerSecond);
                    fraction /= PowerOf10(MaxSecondsFractionDigits - tokenLen);
                    if (ch == 'f')
                    {
                        WriteDigits(ref text, fraction, tokenLen);
                    }
                    else
                    {
                        // F: the digits without the zeros at their end, and no '.' before them when none is left
                        int effectiveDigits = tokenLen;
                        while (effectiveDigits > 0 && fraction % 10 == 0)
                        {
                            fraction /= 10;
                            effectiveDigits--;
                        }

                        if (effectiveDigits > 0)
                        {
                            WriteDigits(ref text, fraction, effectiveDigits);
                        }
                        else
                        {
                            text.TakeBackDot();
                        }
                    }

                    break;

                case 't':
                    tokenLen = RepeatCount(format, i, ch);
                    text.Append(dateTime.Hour < 12 ? 'A' : 'P');
                    if (tokenLen > 1)
                    {
                        text.Append('M');
                    }

                    break;

                case 'd':
                    tokenLen = RepeatCount(format, i, ch);
                    if (tokenLen <= 2)
                    {
                        WriteDigits(ref text, dateTime.Day, tokenLen);
                    }
                    else
                    {
                        string day = DayName((int)dateTime.DayOfWeek);
                        text.Append(day, tokenLen == 3 ? 3 : day.Length);
                    }

                    timeOnly = false;
                    break;

                case 'M':
                    tokenLen = RepeatCount(format, i, ch);
                    int month = dateTime.Month;
                    if (tokenLen <= 2)
                    {
                        WriteDigits(ref text, month, tokenLen);
                    }
                    else
                    {
                        string name = MonthName(month);
                        text.Append(name, tokenLen == 3 ? 3 : name.Length);
                    }

                    timeOnly = false;
                    break;

                case 'y':
                    // y and yy: the year's last two digits; more: the whole year, with zeros in front
                    int year = dateTime.Year;
                    tokenLen = RepeatCount(format, i, ch);
                    WriteDigits(ref text, tokenLen <= 2 ? year % 100 : year, tokenLen);
                    timeOnly = false;
                    break;

                case 'z':
                    tokenLen = RepeatCount(format, i, ch);
                    WriteOffset(dateTime, tokenLen, timeOnly, ref text);
                    break;

                case 'K':
                    tokenLen = 1;
                    WriteKind(dateTime, ref text);
                    break;

                case ':':
                case '/':
                    // the invariant culture's time and date separators
                    text.Append(ch);
                    tokenLen = 1;
                    break;

                case '\'':
                case '"':
                    tokenLen = WriteQuoted(format, i, ref text);
                    break;

                case '%':
                    // the next char as a format of its own: "%d" is the day without a leading zero
                    if (i + 1 < format.Length && format[i + 1] != '%')
                    {
                        WriteCustom(dateTime, format.Slice(i + 1, 1), ref text);
                        tokenLen = 2;
                    }
                    else
                    {
                        Runtime.Throw(Format, InvalidString, null);
                        tokenLen = 1;
                    }

                    break;

                case '\\':
                    // the next char as itself
                    if (i + 1 < format.Length)
                    {
                        text.Append(format[i + 1]);
                        tokenLen = 2;
                    }
                    else
                    {
                        Runtime.Throw(Format, InvalidString, null);
                        tokenLen = 1;
                    }

                    break;

                default:
                    text.Append(ch);
                    tokenLen = 1;
                    break;
            }

            i += tokenLen;
        }
    }

    /// <summary>How many times <paramref name="patternChar"/> repeats from <paramref name="pos"/> on.</summary>
    private static int RepeatCount(ReadOnlySpan<char> format, int pos, char patternChar)
    {
        int index = pos + 1;
        while (index < format.Length && format[index] == patternChar)
        {
            index++;
        }

        return index - pos;
    }

    /// <summary>
    /// Text in quotes, <c>'at'</c> or <c>"at"</c>, as it is, a <c>\</c> in it taking the char after it as itself; the
    /// number of chars it takes, quotes too.
    /// </summary>
    private static int WriteQuoted(ReadOnlySpan<char> format, int pos, ref DateTimeText text)
    {
        int beginPos = pos;
        char quoteChar = format[pos++];
        bool foundQuote = false;
        while (pos < format.Length)
        {
            char ch = format[pos++];
            if (ch == quoteChar)
            {
                foundQuote = true;
                break;
            }

            if (ch == '\\')
            {
                if (pos < format.Length)
                {
                    text.Append(format[pos++]);
                }
                else
                {
                    Runtime.Throw(Format, InvalidString, null);
                }
            }
            else
            {
                text.Append(ch);
            }
        }

        if (!foundQuote)
        {
            Runtime.Throw(Format, quoteChar == '\'' ? BadSingleQuote : BadDoubleQuote, null);
        }

        return pos - beginPos;
    }

    /// <summary>The z formats: the UTC offset, <c>+2</c>, <c>+02</c> or <c>+02:00</c>; a UTC time's is +0.</summary>
    private static void WriteOffset(DateTime dateTime, int tokenLen, bool timeOnly, ref DateTimeText text)
    {
        long offset = !(timeOnly && dateTime.Ticks < DateTime.TicksPerDay) && dateTime.Kind == DateTimeKind.Utc
            ? 0
            : Runtime.LocalUtcOffset();
        if (offset >= 0)
        {
            text.Append('+');
        }
        else
        {
            text.Append('-');
            offset = -offset;
        }

        var span = new System.TimeSpan(offset);
        if (tokenLen <= 1)
        {
            int hours = span.Hours;
            if (hours >= 10)
            {
                text.Append((char)('0' + (hours / 10)));
            }

            text.Append((char)('0' + (hours % 10)));
        }
        else if (tokenLen == 2)
        {
            WriteDigits(ref text, span.Hours, 2);
        }
        else
        {
            WriteDigits(ref text, span.Hours, 2);
            text.Append(':');
            WriteDigits(ref text, span.Minutes, 2);
        }
    }

    /// <summary>The K format: a local time's offset (<c>+02:00</c>), Z for a UTC time, nothing for an unspecified one.</summary>
    private static void WriteKind(DateTime dateTime, ref DateTimeText text)
    {
        switch (dateTime.Kind)
        {
            case DateTimeKind.Local:
                WriteOffset(dateTime, 3, false, ref text);
                break;
            case DateTimeKind.Utc:
                text.Append('Z');
                break;
        }
    }

    /// <summary>The invariant G and the default: <c>MM/dd/yyyy HH:mm:ss</c>.</summary>
    private static void WriteInvariantG(DateTime value, ref DateTimeText text)
    {
        value.GetDate(out int year, out int month, out int day);
        WriteDigits(ref text, month, 2);
        text.Append('/');
        WriteDigits(ref text, day, 2);
        text.Append('/');
        WriteDigits(ref text, year, 4);
        text.Append(' ');
        WriteTime(value, ref text);
    }

    /// <summary>s (<c>yyyy-MM-ddTHH:mm:ss</c>) and u without its Z (<c>yyyy-MM-dd HH:mm:ss</c>).</summary>
    private static void WriteSortable(DateTime value, char separator, ref DateTimeText text)
    {
        WriteDate(value, ref text);
        text.Append(separator);
        WriteTime(value, ref text);
    }

    /// <summary>o: <c>yyyy-MM-ddTHH:mm:ss.fffffff</c> and the Kind (K).</summary>
    private static void WriteRoundtrip(DateTime value, ref DateTimeText text)
    {
        WriteSortable(value, 'T', ref text);
        text.Append('.');
        value.GetTimePrecise(out _, out _, out _, out int tick);
        WriteDigits(ref text, tick, 7);
        WriteKind(value, ref text);
    }

    /// <summary>r: <c>ddd, dd MMM yyyy HH:mm:ss GMT</c>, the time as it is (a DateTime is not converted).</summary>
    private static void WriteRfc1123(DateTime value, ref DateTimeText text)
    {
        WriteDayAndDate(value, ref text);
        text.Append(' ');
        WriteTime(value, ref text);
        text.Append(" GMT", 4);
    }

    /// <summary><c>ddd, dd MMM yyyy</c>.</summary>
    private static void WriteDayAndDate(DateTime value, ref DateTimeText text)
    {
        value.GetDate(out int year, out int month, out int day);
        text.Append(DayName((int)value.DayOfWeek), 3);
        text.Append(", ", 2);
        WriteDigits(ref text, day, 2);
        text.Append(' ');
        text.Append(MonthName(month), 3);
        text.Append(' ');
        WriteDigits(ref text, year, 4);
    }

    /// <summary><c>yyyy-MM-dd</c>.</summary>
    private static void WriteDate(DateTime value, ref DateTimeText text)
    {
        value.GetDate(out int year, out int month, out int day);
        WriteDigits(ref text, year, 4);
        text.Append('-');
        WriteDigits(ref text, month, 2);
        text.Append('-');
        WriteDigits(ref text, day, 2);
    }

    /// <summary><c>HH:mm:ss</c>.</summary>
    private static void WriteTime(DateTime value, ref DateTimeText text)
    {
        value.GetTime(out int hour, out int minute, out int second);
        WriteDigits(ref text, hour, 2);
        text.Append(':');
        WriteDigits(ref text, minute, 2);
        text.Append(':');
        WriteDigits(ref text, second, 2);
    }

    /// <summary>A whole number of 0 or more in decimal, with zeros in front up to <paramref name="minimumLength"/> digits.</summary>
    internal static void WriteDigits(ref DateTimeText text, int value, int minimumLength)
    {
        int digits = 1;
        int power = 1;
        while (power <= value / 10)
        {
            power *= 10;
            digits++;
        }

        for (int zeros = minimumLength - digits; zeros > 0; zeros--)
        {
            text.Append('0');
        }

        while (power > 0)
        {
            text.Append((char)('0' + (value / power % 10)));
            power /= 10;
        }
    }

    private static int PowerOf10(int exponent)
    {
        int result = 1;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }

    /// <summary>
    /// U's universal time: a UTC time as it is, any other moved by the target's offset now (none on a chip), as
    /// ToUniversalTime moves it.
    /// </summary>
    private static DateTime ToUniversalTime(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc)
        {
            return value;
        }

        long ticks = value.Ticks - Runtime.LocalUtcOffset();
        return new DateTime(ticks < 0 ? 0 : ticks > DateTime.MaxTicks ? DateTime.MaxTicks : ticks, DateTimeKind.Utc);
    }

    /// <summary>The invariant culture's name of a day of the week, 0 for Sunday; its first three chars are its abbreviation.</summary>
    internal static string DayName(int day) => day switch
    {
        0 => "Sunday",
        1 => "Monday",
        2 => "Tuesday",
        3 => "Wednesday",
        4 => "Thursday",
        5 => "Friday",
        _ => "Saturday",
    };

    /// <summary>The invariant culture's name of a month, 1 for January; its first three chars are its abbreviation.</summary>
    internal static string MonthName(int month) => month switch
    {
        1 => "January",
        2 => "February",
        3 => "March",
        4 => "April",
        5 => "May",
        6 => "June",
        7 => "July",
        8 => "August",
        9 => "September",
        10 => "October",
        11 => "November",
        _ => "December",
    };
}

/// <summary>
/// Where text of a date goes, a char at a time: into a span (<see cref="Into"/>, for TryFormat and ToString), to the
/// console through a small buffer (<see cref="Printing"/>, for printed text), or nowhere, only counted
/// (<see cref="Counting"/>, to align). A '.' waits until a char comes after it, so that the F formats can take it back
/// (<c>ss.FFF</c> of a whole second is <c>05</c>, not <c>05.</c>) wherever the text goes.
/// </summary>
internal ref struct DateTimeText
{
    private readonly Span<char> _chars;
    private readonly bool _prints;

    // the chars in _chars (into a span: every char so far, also those past its end); printed already; '.' waiting
    private int _count;
    private int _printed;
    private int _dots;

    private DateTimeText(Span<char> chars, bool prints)
    {
        _chars = chars;
        _prints = prints;
    }

    /// <summary>Text into <paramref name="destination"/>; <see cref="Fits"/> says whether it all went in.</summary>
    public static DateTimeText Into(Span<char> destination) => new(destination, false);

    /// <summary>Text to the console, <paramref name="buffer"/> at a time; <see cref="Finish"/> prints the rest.</summary>
    public static DateTimeText Printing(Span<char> buffer) => new(buffer, true);

    /// <summary>Text only counted.</summary>
    public static DateTimeText Counting() => new(default, false);

    /// <summary>How many chars the text has so far.</summary>
    public readonly int Length => _printed + _count + _dots;

    /// <summary>True when the text into a span went in whole.</summary>
    public readonly bool Fits => Length <= _chars.Length;

    public void Append(char c)
    {
        if (c == '.')
        {
            _dots++;
            return;
        }

        while (_dots > 0)
        {
            _dots--;
            Put('.');
        }

        Put(c);
    }

    /// <summary>The first <paramref name="count"/> chars of <paramref name="text"/>.</summary>
    public void Append(string text, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Append(text[i]);
        }
    }

    /// <summary><paramref name="count"/> spaces (none for 0 or less): the left of a hole aligned to the right.</summary>
    public void Pad(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Append(' ');
        }
    }

    /// <summary>Spaces until the text is <paramref name="width"/> chars long: the right of a hole aligned to the left.</summary>
    public void PadTo(int width)
    {
        while (Length < width)
        {
            Append(' ');
        }
    }

    /// <summary>
    /// TryFormat's result, after <see cref="Finish"/>: true and the number of chars when the text went in whole, else
    /// false and 0.
    /// </summary>
    public readonly bool Result(out int charsWritten)
    {
        charsWritten = Fits ? Length : 0;
        return Fits;
    }

    /// <summary>The size of printed text's buffer on the stack: the chars printed at a time.</summary>
    public const int PrintBuffer = 32;

    /// <summary>A '.' at the end of the text taken back (.NET's <c>result.Length--</c> after one); nothing when it ends otherwise.</summary>
    public void TakeBackDot()
    {
        if (_dots > 0)
        {
            _dots--;
        }
    }

    /// <summary>The text's end: the dots that wait go in, and printed text is printed to its end.</summary>
    public void Finish()
    {
        while (_dots > 0)
        {
            _dots--;
            Put('.');
        }

        if (_prints && _count > 0)
        {
            Runtime.WriteChars(_chars[0], _count);
            _printed += _count;
            _count = 0;
        }
    }

    private void Put(char c)
    {
        if (_prints)
        {
            if (_count == _chars.Length)
            {
                Runtime.WriteChars(_chars[0], _count);
                _printed += _count;
                _count = 0;
            }

            _chars[_count] = c;
        }
        else if (_count < _chars.Length)
        {
            _chars[_count] = c;
        }

        _count++;
    }
}
