namespace ChipSharp.Tests.Library;

/// <summary>
/// The values the DateTime tests try: the edges of the calendar (year 1 and 9999, leap days, month ends, the turn of
/// centuries), and random dates and times (always the same: a fixed seed), with the formats .NET's invariant culture
/// writes.
/// </summary>
internal static class DateTimeValues
{
    /// <summary>Ticks of dates: the first and last, leap days, ends of months and years, the Unix epoch, now-ish.</summary>
    public static readonly long[] EdgeTicks =
    [
        0, 1, 9_999_999, 10_000_000, TimeSpan.TicksPerDay - 1, TimeSpan.TicksPerDay, DateTime.MaxValue.Ticks,
        DateTime.MaxValue.Ticks - 1, DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay, DateTime.UnixEpoch.Ticks,
        new DateTime(2000, 2, 29).Ticks, new DateTime(1900, 2, 28, 23, 59, 59).Ticks, new DateTime(1900, 3, 1).Ticks,
        new DateTime(2024, 2, 29, 12, 0, 0).Ticks, new DateTime(2023, 12, 31, 23, 59, 59, 999).Ticks,
        new DateTime(2026, 11, 27, 18, 0, 0).Ticks, new DateTime(1600, 12, 31).Ticks, new DateTime(1601, 1, 1).Ticks,
        new DateTime(400, 2, 29).Ticks, new DateTime(100, 3, 1).Ticks, new DateTime(9999, 12, 31).Ticks,
        new DateTime(1, 12, 31, 13, 5, 7, 8).AddTicks(9).Ticks, new DateTime(2038, 1, 19, 3, 14, 7).Ticks,
    ];

    /// <summary><paramref name="count"/> ticks of dates from all over year 1 to 9999, some at whole seconds and days.</summary>
    public static IEnumerable<long> RandomTicks(int count, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var ticks = random.NextInt64(0, DateTime.MaxValue.Ticks + 1);
            yield return (i % 4) switch
            {
                0 => ticks,
                1 => ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
                2 => ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond,
                _ => ticks / TimeSpan.TicksPerDay * TimeSpan.TicksPerDay + random.Next(0, 1000) * 1000,
            };
        }
    }

    /// <summary>The standard formats .NET has for a DateTime, and ones it refuses.</summary>
    public static readonly string[] StandardFormats =
        ["", "d", "D", "f", "F", "g", "G", "m", "M", "o", "O", "r", "R", "s", "t", "T", "u", "y", "Y"];

    /// <summary>Custom formats: every pattern, its repeats, quotes, escapes, % and the F formats that take back a '.'.</summary>
    public static readonly string[] CustomFormats =
    [
        "yyyy-MM-dd HH:mm:ss.fff", "HH:mm:ss", "h:mm tt", "hh:mm:ss t", "dddd, MMMM d, yyyy", "ddd MMM yy",
        "y yy yyy yyyy yyyyy", "%d", "%M", "%h", "%y", "%H", "%s", "%t", "%f", "%F", "M/d/yyyy", "'Date:' yyyy",
        "\"quoted\" HH", "\\H\\H HH", "ss.FFF", "ss.FFFFFFF", "HH:mm:ss.f ff fff ffff fffff ffffff fffffff",
        "F FF FFF FFFF FFFFF FFFFFF FFFFFFF", "g gg", "yyyyyyyyyyyyyyyyyyy", "MMMMM ddddd", "s.F", "H.F", ".F", "..F",
        "s..FF", "%F%F", "hh:mm:ss.FFFF tt", "dd/MM/yyyy", "yyyyMMddHHmmss", "HH'h'mm", "'it''s'", "mm:ss", "m s h H",
        "d dd ddd dddd", "M MM MMM MMMM", "tt t", "'a\\'b'", "\"a\\\"b\"", "xx", "abc", "%m %s", "yyyy-MM-ddTHH:mm:ss",
        "HH:mm:ss.ffffff", "F.F", "g yyyy", "hh", "HHH", "mmm", "sss", "ttt", "HH K", "MM'/'dd",
    ];

    /// <summary>Formats .NET refuses with a FormatException, each in its own way.</summary>
    public static readonly string[] BadFormats =
        ["x", "K", "% %", "Q", "ffffffff", "FFFFFFFF", "'abc", "\"abc", "abc\\", "%", "%%", "yyyy'", "HH \"mm"];

    /// <summary>The formats of a DateOnly, those it refuses, and custom ones with a time in them.</summary>
    public static readonly string[] DateOnlyFormats =
    [
        "", "d", "D", "m", "M", "o", "O", "r", "R", "y", "Y", "yyyy-MM-dd", "dddd, dd MMMM yyyy", "ddd d MMM", "'y'yy",
        "t", "T", "f", "s", "u", "x", "HH:mm", "yyyy-MM-dd HH", "dd 'at' tt", "MM/dd\\", "'abc", "\"q", "z", "K",
        "yyyy:MM",
    ];

    /// <summary>The formats of a TimeOnly, those it refuses, and custom ones with a date in them.</summary>
    public static readonly string[] TimeOnlyFormats =
    [
        "", "t", "T", "o", "O", "r", "R", "HH:mm:ss.fffffff", "h:mm tt", "HH'h'mm", "ss.FFF", "d", "D", "m", "s", "x",
        "yyyy", "HH:mm dd", "MM", "HH/mm", "H\\", "'abc", "z", "K", "hh:mm:ss.FFFFFFF tt",
    ];
}
