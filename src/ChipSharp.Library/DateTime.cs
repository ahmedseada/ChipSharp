namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.DateTime"/>: a date and a time of day from 1 January of year 1 to 31 December 9999, as
/// .NET's one 64-bit number, its ticks of 100 nanoseconds in its low 62 bits and its <see cref="DateTimeKind"/> in the
/// top two. Its members give .NET 10's results bit for bit: the code is .NET's own (System.Private.CoreLib's
/// DateTime.cs, with its Euclidean affine functions for the year, month and day), written without what ChipSharp does
/// not compile yet. It is a struct of 8 bytes and needs no heap; text of it, the formats of the invariant culture, is in
/// DateTimeFormat.cs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Now"/>, <see cref="UtcNow"/> and <see cref="Today"/> read the target's clock (<c>"clock": true</c> in its
/// file): on a chip the software clock that <c>ChipSharp.Hardware.Clock.Start</c> sets (UTC, there are no time zones:
/// Now and UtcNow differ only in their Kind), on a PC Windows' clock and time zone until the program starts a clock of
/// its own. Where .NET throws (a date that does not exist, arithmetic past year 9999), the program stops with .NET's
/// message (see <see cref="Runtime.Throw"/>).
/// </para>
/// <para>
/// Not here: the overloads with a Calendar or an IFormatProvider, the time zone conversions (ToLocalTime,
/// ToUniversalTime, ToBinary, the file times, IsDaylightSavingTime), the OLE Automation dates, Parse and the object
/// overloads. A second of 60 is refused, as on a PC whose Windows does not count leap seconds.
/// </para>
/// </remarks>
// Equals(object) needs a box of a library struct, which ChipSharp does not make yet: == without it
#pragma warning disable CS0660, CS0661
[Substitute(typeof(System.DateTime))]
internal readonly struct DateTime
#pragma warning restore CS0660, CS0661
{
    private const int DaysPerYear = 365;
    private const int DaysPer4Years = DaysPerYear * 4 + 1;       // 1461
    private const int DaysPer100Years = DaysPer4Years * 25 - 1;  // 36524
    private const int DaysPer400Years = DaysPer100Years * 4 + 1; // 146097
    private const int DaysTo1970 = DaysPer400Years * 4 + DaysPer100Years * 3 + DaysPer4Years * 17 + DaysPerYear;
    internal const int DaysTo10000 = DaysPer400Years * 25 - 366; // 3652059

    private const long TicksPerMicrosecond = System.TimeSpan.TicksPerMicrosecond;
    private const long TicksPerMillisecond = System.TimeSpan.TicksPerMillisecond;
    private const long TicksPerSecond = System.TimeSpan.TicksPerSecond;
    private const long TicksPerMinute = System.TimeSpan.TicksPerMinute;
    private const long TicksPerHour = System.TimeSpan.TicksPerHour;
    internal const long TicksPerDay = System.TimeSpan.TicksPerDay;

    internal const long MaxTicks = DaysTo10000 * TicksPerDay - 1;
    private const long MaxMicroseconds = MaxTicks / TicksPerMicrosecond;
    private const long MaxMillis = MaxTicks / TicksPerMillisecond;
    private const long MaxSeconds = MaxTicks / TicksPerSecond;
    private const long MaxMinutes = MaxTicks / TicksPerMinute;
    private const long MaxHours = MaxTicks / TicksPerHour;
    private const long MaxDays = (long)DaysTo10000 - 1;
    private const long UnixEpochTicks = DaysTo1970 * TicksPerDay;

    // the Euclidean affine functions' constants: x / DaysPer4Years and x % DaysPer4Years / 4 by a multiplication
    private const uint EafMultiplier = (uint)(((1UL << 32) + DaysPer4Years - 1) / DaysPer4Years); // 2,939,745
    private const uint EafDivider = EafMultiplier * 4;                                            // 11,758,980
    private const ulong TicksPer6Hours = TicksPerHour * 6;
    private const int March1BasedDayOfNewYear = 306; // days from 1 March to 1 January

    private const ulong TicksMask = 0x3FFFFFFFFFFFFFFF;
    private const ulong FlagsMask = 0xC000000000000000;
    private const ulong KindUtc = 0x4000000000000000;
    private const ulong KindLocal = 0x8000000000000000;
    private const int KindShift = 62;

    private const string OutOfRange = "System.ArgumentOutOfRangeException";

    public static readonly DateTime MinValue;
    public static readonly DateTime MaxValue = new((ulong)MaxTicks);
    public static readonly DateTime UnixEpoch = new((ulong)UnixEpochTicks | KindUtc);

    // bits 0-61 the ticks, bits 62-63 the kind (3: local, in the repeated hour of a change from daylight saving time)
    private readonly ulong _dateData;

    public DateTime(long ticks)
    {
        if ((ulong)ticks > MaxTicks)
        {
            ThrowTicksOutOfRange();
        }

        _dateData = (ulong)ticks;
    }

    private DateTime(ulong dateData) => _dateData = dateData;

    public DateTime(long ticks, DateTimeKind kind)
    {
        if ((ulong)ticks > MaxTicks)
        {
            ThrowTicksOutOfRange();
        }

        if ((uint)kind > (uint)DateTimeKind.Local)
        {
            ThrowInvalidKind();
        }

        _dateData = (ulong)ticks | ((ulong)(uint)kind << KindShift);
    }

    public DateTime(DateOnly date, TimeOnly time) => _dateData = (ulong)(date.DayNumber * TicksPerDay + time.Ticks);

    public DateTime(DateOnly date, TimeOnly time, DateTimeKind kind)
    {
        if ((uint)kind > (uint)DateTimeKind.Local)
        {
            ThrowInvalidKind();
        }

        _dateData = (ulong)(date.DayNumber * TicksPerDay + time.Ticks) | ((ulong)(uint)kind << KindShift);
    }

    public DateTime(int year, int month, int day) => _dateData = DateToTicks(year, month, day);

    public DateTime(int year, int month, int day, int hour, int minute, int second) =>
        _dateData = DateToTicks(year, month, day) + TimeToTicks(hour, minute, second);

    public DateTime(int year, int month, int day, int hour, int minute, int second, DateTimeKind kind)
    {
        if ((uint)kind > (uint)DateTimeKind.Local)
        {
            ThrowInvalidKind();
        }

        ulong ticks = DateToTicks(year, month, day) | ((ulong)(uint)kind << KindShift);
        _dateData = ticks + TimeToTicks(hour, minute, second);
    }

    public DateTime(int year, int month, int day, int hour, int minute, int second, int millisecond)
        : this(year, month, day, hour, minute, second)
    {
        if ((uint)millisecond >= System.TimeSpan.MillisecondsPerSecond)
        {
            ThrowMillisecondOutOfRange();
        }

        _dateData += (uint)millisecond * (uint)TicksPerMillisecond;
    }

    public DateTime(int year, int month, int day, int hour, int minute, int second, int millisecond, DateTimeKind kind)
        : this(year, month, day, hour, minute, second, kind)
    {
        if ((uint)millisecond >= System.TimeSpan.MillisecondsPerSecond)
        {
            ThrowMillisecondOutOfRange();
        }

        _dateData += (uint)millisecond * (uint)TicksPerMillisecond;
    }

    public DateTime(int year, int month, int day, int hour, int minute, int second, int millisecond, int microsecond)
        : this(year, month, day, hour, minute, second, millisecond, microsecond, DateTimeKind.Unspecified)
    {
    }

    public DateTime(int year, int month, int day, int hour, int minute, int second, int millisecond, int microsecond,
        DateTimeKind kind)
        : this(year, month, day, hour, minute, second, millisecond, kind)
    {
        if ((uint)microsecond >= System.TimeSpan.MicrosecondsPerMillisecond)
        {
            ThrowMicrosecondOutOfRange();
        }

        _dateData += (uint)microsecond * (uint)TicksPerMicrosecond;
    }

    /// <summary>
    /// The current date and time as a local time. On a chip, where there are no time zones, it is
    /// <see cref="UtcNow"/>'s, Kind Local: the clock that <c>Clock.Start</c> sets; on a PC, Windows' local time (the
    /// time zone's offset now), until the program starts a clock of its own.
    /// </summary>
    public static DateTime Now
    {
        get
        {
            long tick = Runtime.ClockLocalTicks();
            if ((ulong)tick <= MaxTicks)
            {
                return new DateTime((ulong)tick | KindLocal);
            }

            return new DateTime(tick < 0 ? KindLocal : MaxTicks | KindLocal);
        }
    }

    /// <summary>The current date and time as a UTC time: the target's clock (see <see cref="Now"/>).</summary>
    public static DateTime UtcNow
    {
        get
        {
            long tick = Runtime.ClockUtcTicks();
            ulong ticks = (ulong)tick <= MaxTicks ? (ulong)tick : tick < 0 ? 0UL : (ulong)MaxTicks;
            return new DateTime(ticks | KindUtc);
        }
    }

    /// <summary>Today's date, at midnight, Kind Local (see <see cref="Now"/>).</summary>
    public static DateTime Today => Now.Date;

    public long Ticks => (long)(_dateData & TicksMask);

    public DateTimeKind Kind
    {
        get
        {
            // 0 to 2 are the kinds; 3 (local, in a repeated hour) is local: bit 0 NAND bit 1
            uint kind = (uint)(_dateData >> KindShift);
            return (DateTimeKind)(kind & ~(kind >> 1));
        }
    }

    public DateTime Date => new((UTicks / TicksPerDay * TicksPerDay) | InternalKind);

    public System.TimeSpan TimeOfDay => new((long)(UTicks % TicksPerDay));

    public int Year
    {
        get
        {
            // y100: whole 100-year periods since 1 January 0001; r1: the day in that period, times 4
            uint n = (uint)(UTicks / TicksPer6Hours) | 3U;
            uint y100 = n / DaysPer400Years;
            uint r1 = n - (y100 * DaysPer400Years);
            return 1 + (int)(100 * y100 + (r1 | 3) / DaysPer4Years);
        }
    }

    public int Month
    {
        get
        {
            uint r1 = (((uint)(UTicks / TicksPer6Hours) | 3U) + 1224) % DaysPer400Years;
            ulong u2 = (ulong)EafMultiplier * (r1 | 3U);
            ushort daySinceMarch1 = (ushort)((uint)u2 / EafDivider);
            int n3 = 2141 * daySinceMarch1 + 197913;
            return (ushort)(n3 >> 16) - (daySinceMarch1 >= March1BasedDayOfNewYear ? 12 : 0);
        }
    }

    public int Day
    {
        get
        {
            uint r1 = (((uint)(UTicks / TicksPer6Hours) | 3U) + 1224) % DaysPer400Years;
            ulong u2 = (ulong)EafMultiplier * (r1 | 3U);
            ushort daySinceMarch1 = (ushort)((uint)u2 / EafDivider);
            int n3 = 2141 * daySinceMarch1 + 197913;
            return (ushort)n3 / 2141 + 1;
        }
    }

    public DayOfWeek DayOfWeek => (DayOfWeek)(((uint)(UTicks / TicksPerDay) + 1) % 7);

    public int DayOfYear =>
        1 + (int)(((((uint)(UTicks / TicksPer6Hours) | 3U) % (uint)DaysPer400Years) | 3U) * EafMultiplier / EafDivider);

    public int Hour => (int)((uint)(UTicks / TicksPerHour) % 24);

    public int Minute => (int)((UTicks / TicksPerMinute) % 60);

    public int Second => (int)((UTicks / TicksPerSecond) % 60);

    public int Millisecond => (int)((UTicks / TicksPerMillisecond) % 1000);

    public int Microsecond => (int)((UTicks / TicksPerMicrosecond) % 1000);

    public int Nanosecond => (int)(UTicks % TicksPerMicrosecond) * 100;

    private ulong UTicks => _dateData & TicksMask;

    private ulong InternalKind => _dateData & FlagsMask;

    public static int DaysInMonth(int year, int month)
    {
        if (month < 1 || month > 12)
        {
            Runtime.ThrowValue("Month must be between one and twelve.", "month", month);
        }

        // IsLeapYear checks the year
        return (IsLeapYear(year) ? DaysInMonth366 : DaysInMonth365)[month - 1];
    }

    public static bool IsLeapYear(int year)
    {
        if (year < 1 || year > 9999)
        {
            Runtime.Throw(OutOfRange, "Year must be between 1 and 9999.", "year");
        }

        if ((year & 3) != 0)
        {
            return false;
        }

        if ((year & 15) == 0)
        {
            return true;
        }

        return (uint)year % 25 != 0;
    }

    public static DateTime SpecifyKind(DateTime value, DateTimeKind kind)
    {
        if ((uint)kind > (uint)DateTimeKind.Local)
        {
            ThrowInvalidKind();
        }

        return new DateTime(value.UTicks | ((ulong)(uint)kind << KindShift));
    }

    public DateTime Add(System.TimeSpan value) => AddTicks(value.Ticks);

    public DateTime AddDays(double value) => AddUnits(value, MaxDays, TicksPerDay);

    public DateTime AddHours(double value) => AddUnits(value, MaxHours, TicksPerHour);

    public DateTime AddMinutes(double value) => AddUnits(value, MaxMinutes, TicksPerMinute);

    public DateTime AddSeconds(double value) => AddUnits(value, MaxSeconds, TicksPerSecond);

    public DateTime AddMilliseconds(double value) => AddUnits(value, MaxMillis, TicksPerMillisecond);

    public DateTime AddMicroseconds(double value) => AddUnits(value, MaxMicroseconds, TicksPerMicrosecond);

    public DateTime AddTicks(long value)
    {
        ulong ticks = (ulong)(Ticks + value);
        if (ticks > MaxTicks)
        {
            ThrowDateArithmetic("value");
        }

        return new DateTime(ticks | InternalKind);
    }

    /// <summary>
    /// The year and month moved by <paramref name="months"/>, the day the last of the new month when it has fewer, the
    /// time of day kept.
    /// </summary>
    public DateTime AddMonths(int months)
    {
        if (months < -120000 || months > 120000)
        {
            Runtime.Throw(OutOfRange, "Months value must be between +/-120000.", "months");
        }

        GetDate(out int year, out int month, out int day);
        int y = year, d = day;
        int m = month + months;
        int q = m > 0 ? (int)((uint)(m - 1) / 12) : m / 12 - 1;
        y += q;
        m -= q * 12;
        if (y < 1 || y > 9999)
        {
            ThrowDateArithmetic("months");
        }

        ReadOnlySpan<uint> daysTo = IsLeapYear(y) ? DaysToMonth366 : DaysToMonth365;
        uint daysToMonth = daysTo[m - 1];
        int days = (int)(daysTo[m] - daysToMonth);
        if (d > days)
        {
            d = days;
        }

        uint n = DaysToYear((uint)y) + daysToMonth + (uint)d - 1;
        return new DateTime((n * (ulong)TicksPerDay) + (UTicks % TicksPerDay) | InternalKind);
    }

    /// <summary>The year moved by <paramref name="value"/>; 29 February becomes 28 February in a year that is not a leap year.</summary>
    public DateTime AddYears(int value)
    {
        if (value < -10000 || value > 10000)
        {
            Runtime.Throw(OutOfRange, "Years value must be between +/-10000.", "value");
        }

        GetDate(out int year, out int month, out int day);
        int y = year + value;
        if (y < 1 || y > 9999)
        {
            ThrowDateArithmetic("value");
        }

        uint n = DaysToYear((uint)y);
        int m = month - 1, d = day - 1;
        if (IsLeapYear(y))
        {
            n += DaysToMonth366[m];
        }
        else
        {
            if (d == 28 && m == 1)
            {
                d--;
            }

            n += DaysToMonth365[m];
        }

        n += (uint)d;
        return new DateTime((n * (ulong)TicksPerDay) + (UTicks % TicksPerDay) | InternalKind);
    }

    public System.TimeSpan Subtract(DateTime value) => new(Ticks - value.Ticks);

    public DateTime Subtract(System.TimeSpan value)
    {
        ulong ticks = (ulong)(Ticks - value.Ticks);
        if (ticks > MaxTicks)
        {
            ThrowDateArithmetic("value");
        }

        return new DateTime(ticks | InternalKind);
    }

    // long.CompareTo, written out: -1, 0 or 1
    public static int Compare(DateTime t1, DateTime t2)
    {
        long ticks1 = t1.Ticks;
        long ticks2 = t2.Ticks;
        if (ticks1 > ticks2)
        {
            return 1;
        }

        if (ticks1 < ticks2)
        {
            return -1;
        }

        return 0;
    }

    public int CompareTo(DateTime value) => Compare(this, value);

    public bool Equals(DateTime value) => this == value;

    public static bool Equals(DateTime t1, DateTime t2) => t1 == t2;

    /// <summary>.NET's: the ticks' two halves XORed, the same in every run and for every Kind.</summary>
    public override int GetHashCode()
    {
        long ticks = Ticks;
        return unchecked((int)ticks) ^ (int)(ticks >> 32);
    }

    public void Deconstruct(out DateOnly date, out TimeOnly time)
    {
        date = DateOnly.FromDateTime(this);
        time = TimeOnly.FromDateTime(this);
    }

    public void Deconstruct(out int year, out int month, out int day) => GetDate(out year, out month, out day);

    public static DateTime operator +(DateTime d, System.TimeSpan t)
    {
        ulong ticks = (ulong)(d.Ticks + t.Ticks);
        if (ticks > MaxTicks)
        {
            ThrowDateArithmetic("t");
        }

        return new DateTime(ticks | d.InternalKind);
    }

    public static DateTime operator -(DateTime d, System.TimeSpan t)
    {
        ulong ticks = (ulong)(d.Ticks - t.Ticks);
        if (ticks > MaxTicks)
        {
            ThrowDateArithmetic("t");
        }

        return new DateTime(ticks | d.InternalKind);
    }

    public static System.TimeSpan operator -(DateTime d1, DateTime d2) => new(d1.Ticks - d2.Ticks);

    // the Kind is not part of the value: only the ticks compare
    public static bool operator ==(DateTime d1, DateTime d2) => ((d1._dateData ^ d2._dateData) << 2) == 0;

    public static bool operator !=(DateTime d1, DateTime d2) => !(d1 == d2);

    public static bool operator <(DateTime t1, DateTime t2) => t1.Ticks < t2.Ticks;

    public static bool operator <=(DateTime t1, DateTime t2) => t1.Ticks <= t2.Ticks;

    public static bool operator >(DateTime t1, DateTime t2) => t1.Ticks > t2.Ticks;

    public static bool operator >=(DateTime t1, DateTime t2) => t1.Ticks >= t2.Ticks;

    /// <summary>The invariant culture's G: <c>MM/dd/yyyy HH:mm:ss</c> (see DateTimeFormat.cs). It makes a string: a heap.</summary>
    public override string ToString() => Text(default, 0, true);

    /// <summary>
    /// The text of <paramref name="format"/>, a standard format (<c>d D f F g G m o r s t T u U y</c>) or a custom one
    /// (<c>yyyy-MM-dd HH:mm:ss.fff</c>), as the invariant culture writes it. It makes a string: a heap.
    /// </summary>
    public string ToString(string? format) => Text(format.AsSpan(), 0, true);

    public string ToLongDateString() => Text("D", 0, true);

    public string ToLongTimeString() => Text("T", 0, true);

    public string ToShortDateString() => Text("d", 0, true);

    public string ToShortTimeString() => Text("t", 0, true);

    /// <summary>
    /// The text of <paramref name="format"/> into <paramref name="destination"/>, as ToString gives it: true and its
    /// length when it fits, else false and 0. No heap: <c>Span&lt;char&gt; buffer = stackalloc char[20];</c>.
    /// <paramref name="provider"/> is not used: the culture is always the invariant one.
    /// </summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null)
    {
        var text = DateTimeText.Into(destination);
        DateTimeFormat.Write(this, format, ref text);
        text.Finish();
        return text.Result(out charsWritten);
    }

    /// <summary>
    /// Printed text's hole of a DateTime, <c>$"{now:HH:mm}"</c> printed straight away (see ChipSharp.Frontend's
    /// FormattedValues): the text of <paramref name="format"/> written to the console a few chars at a time, aligned
    /// as <paramref name="alignment"/> says, with no string made.
    /// </summary>
    internal void Print(ReadOnlySpan<char> format, int alignment, bool toString)
    {
        Span<char> buffer = stackalloc char[DateTimeText.PrintBuffer];
        var text = DateTimeText.Printing(buffer);
        if (alignment > 0)
        {
            var counted = DateTimeText.Counting();
            DateTimeFormat.Write(this, format, ref counted);
            counted.Finish();
            text.Pad(alignment - counted.Length);
        }

        DateTimeFormat.Write(this, format, ref text);
        text.PadTo(-alignment);
        text.Finish();
    }

    /// <summary>Kept text's hole of a DateTime, and ToString: the text as a string, aligned (see <see cref="Print"/>).</summary>
    internal string Text(ReadOnlySpan<char> format, int alignment, bool toString)
    {
        var counted = DateTimeText.Counting();
        DateTimeFormat.Write(this, format, ref counted);
        counted.Finish();
        int width = alignment < 0 ? -alignment : alignment;
        var chars = new char[width > counted.Length ? width : counted.Length];
        var text = DateTimeText.Into(chars);
        text.Pad(alignment - counted.Length);
        DateTimeFormat.Write(this, format, ref text);
        text.PadTo(-alignment);
        text.Finish();
        return new string(chars);
    }

    /// <summary>A DateTime of ticks known to be in range, with no check (DateOnly's and TimeOnly's).</summary>
    internal static DateTime CreateUnchecked(long ticks) => new((ulong)ticks);

    /// <summary>
    /// The year, month and day at once: the Euclidean affine functions of Cassio Neri and Lorenz Schneider (2021), as
    /// .NET computes them, from 1 March of year 0.
    /// </summary>
    internal void GetDate(out int year, out int month, out int day)
    {
        // y100: whole 100-year periods since 1 March 0000; r1: the day in that period, times 4
        uint n = ((uint)(UTicks / TicksPer6Hours) | 3U) + 1224;
        uint y100 = n / DaysPer400Years;
        uint r1 = n - (y100 * DaysPer400Years);
        ulong u2 = (ulong)EafMultiplier * (r1 | 3U);
        uint daySinceMarch1 = (uint)u2 / EafDivider;
        uint n3 = 2141 * daySinceMarch1 + 197913;
        year = (int)(100 * y100 + (uint)(u2 >> 32));
        month = (int)(n3 >> 16);
        day = (ushort)n3 / 2141 + 1;

        // January and February are the 11th and 12th months of the year that starts on 1 March
        if (daySinceMarch1 >= March1BasedDayOfNewYear)
        {
            ++year;
            month -= 12;
        }
    }

    internal void GetTime(out int hour, out int minute, out int second)
    {
        ulong seconds = UTicks / TicksPerSecond;
        ulong minutes = seconds / 60;
        second = (int)(seconds - (minutes * 60));
        ulong hours = minutes / 60;
        minute = (int)(minutes - (hours * 60));
        hour = (int)((uint)hours % 24);
    }

    /// <summary>The hour, minute and second, and the ticks after the second (0 to 9,999,999).</summary>
    internal void GetTimePrecise(out int hour, out int minute, out int second, out int tick)
    {
        ulong ticks = UTicks;
        ulong seconds = ticks / TicksPerSecond;
        tick = (int)(ticks - (seconds * TicksPerSecond));
        ulong minutes = seconds / 60;
        second = (int)(seconds - (minutes * 60));
        ulong hours = minutes / 60;
        minute = (int)(minutes - (hours * 60));
        hour = (int)((uint)hours % 24);
    }

    /// <summary>The ticks of hour:minute:second, which must be a time of a day (0:0:0 to 23:59:59).</summary>
    internal static ulong TimeToTicks(int hour, int minute, int second)
    {
        if ((uint)hour >= 24 || (uint)minute >= 60 || (uint)second >= 60)
        {
            Runtime.Throw(OutOfRange, "Hour, Minute, and Second parameters describe an un-representable DateTime.", null);
        }

        int totalSeconds = hour * 3600 + minute * 60 + second;
        return (uint)totalSeconds * (ulong)TicksPerSecond;
    }

    internal static ulong TimeToTicks(int hour, int minute, int second, int millisecond)
    {
        ulong ticks = TimeToTicks(hour, minute, second);
        if ((uint)millisecond >= System.TimeSpan.MillisecondsPerSecond)
        {
            ThrowMillisecondOutOfRange();
        }

        return ticks + ((uint)millisecond * (uint)TicksPerMillisecond);
    }

    internal static ulong TimeToTicks(int hour, int minute, int second, int millisecond, int microsecond)
    {
        ulong ticks = TimeToTicks(hour, minute, second, millisecond);
        if ((uint)microsecond >= System.TimeSpan.MicrosecondsPerMillisecond)
        {
            ThrowMicrosecondOutOfRange();
        }

        return ticks + ((uint)microsecond * (uint)TicksPerMicrosecond);
    }

    /// <summary>The ticks of midnight at the start of the date, which must exist (1 to 9999, 1 to 12, 1 to 28..31).</summary>
    private static ulong DateToTicks(int year, int month, int day)
    {
        if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1)
        {
            ThrowBadYearMonthDay();
        }

        ReadOnlySpan<uint> days = IsLeapYear(year) ? DaysToMonth366 : DaysToMonth365;
        if ((uint)day > days[month] - days[month - 1])
        {
            ThrowBadYearMonthDay();
        }

        uint n = DaysToYear((uint)year) + days[month - 1] + (uint)day - 1;
        return n * (ulong)TicksPerDay;
    }

    /// <summary>The days from 1 January 0001 to 1 January of <paramref name="year"/>.</summary>
    private static uint DaysToYear(uint year)
    {
        uint y = year - 1;
        uint cent = y / 100;
        return y * (365 * 4 + 1) / 4 - cent + cent / 4;
    }

    /// <summary>
    /// .NET's AddDays, AddHours, ...: the whole units and the fraction of one apart, each in ticks (the fraction's
    /// towards zero), so that a whole number of units is exact.
    /// </summary>
    private DateTime AddUnits(double value, long maxUnitCount, long ticksPerUnit)
    {
        if (System.Math.Abs(value) > maxUnitCount)
        {
            Runtime.Throw(OutOfRange, "Value to add was out of range.", "value");
        }

        double integralPart = System.Math.Truncate(value);
        double fractionalPart = value - integralPart;
        long ticks = (long)integralPart * ticksPerUnit;
        ticks += (long)(fractionalPart * ticksPerUnit);
        return AddTicks(ticks);
    }

    private static ReadOnlySpan<uint> DaysToMonth365 => new uint[] { 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334, 365 };

    private static ReadOnlySpan<uint> DaysToMonth366 => new uint[] { 0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335, 366 };

    private static ReadOnlySpan<byte> DaysInMonth365 => new byte[] { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };

    private static ReadOnlySpan<byte> DaysInMonth366 => new byte[] { 31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };

    private static void ThrowTicksOutOfRange() => Runtime.Throw(OutOfRange,
        "Ticks must be between DateTime.MinValue.Ticks and DateTime.MaxValue.Ticks.", "ticks");

    private static void ThrowInvalidKind() => Runtime.Throw("System.ArgumentException", "Invalid DateTimeKind value.", "kind");

    private static void ThrowMillisecondOutOfRange() =>
        Runtime.Throw(OutOfRange, "Valid values are between 0 and 999, inclusive.", "millisecond");

    private static void ThrowMicrosecondOutOfRange() =>
        Runtime.Throw(OutOfRange, "Valid values are between 0 and 999, inclusive.", "microsecond");

    private static void ThrowBadYearMonthDay() =>
        Runtime.Throw(OutOfRange, "Year, Month, and Day parameters describe an un-representable DateTime.", null);

    private static void ThrowDateArithmetic(string parameter) =>
        Runtime.Throw(OutOfRange, "The added or subtracted value results in an un-representable DateTime.", parameter);
}
