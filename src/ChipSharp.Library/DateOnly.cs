namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.DateOnly"/>: a date with no time of day, as .NET's one number, the days since 1
/// January 0001. The members are .NET 10's own (System.Private.CoreLib's DateOnly.cs) over <see cref="DateTime"/>'s; it
/// is a struct of 4 bytes and needs no heap. Text of it is in DateTimeFormat.cs; Parse and the object overloads are not
/// here.
/// </summary>
#pragma warning disable CS0660, CS0661 // no Equals(object): see DateTime
[Substitute(typeof(System.DateOnly))]
internal readonly struct DateOnly
#pragma warning restore CS0660, CS0661
{
    private const int MinDayNumber = 0;
    private const int MaxDayNumber = DateTime.DaysTo10000 - 1; // 31 December 9999

    private readonly uint _dayNumber;

    private DateOnly(uint dayNumber) => _dayNumber = dayNumber;

    public DateOnly(int year, int month, int day) => _dayNumber = DayNumberFromDateTime(new DateTime(year, month, day));

    public static DateOnly MinValue => new((uint)MinDayNumber);

    public static DateOnly MaxValue => new((uint)MaxDayNumber);

    public int Year => GetEquivalentDateTime().Year;

    public int Month => GetEquivalentDateTime().Month;

    public int Day => GetEquivalentDateTime().Day;

    public DayOfWeek DayOfWeek => (DayOfWeek)((_dayNumber + 1) % 7);

    public int DayOfYear => GetEquivalentDateTime().DayOfYear;

    public int DayNumber => (int)_dayNumber;

    public static DateOnly FromDayNumber(int dayNumber)
    {
        if ((uint)dayNumber > MaxDayNumber)
        {
            Runtime.ThrowValue("Day number must be between 0 and DateOnly.MaxValue.DayNumber.", "dayNumber", dayNumber);
        }

        return new DateOnly((uint)dayNumber);
    }

    public static DateOnly FromDateTime(DateTime dateTime) => new(DayNumberFromDateTime(dateTime));

    public DateOnly AddDays(int value)
    {
        uint newDayNumber = _dayNumber + (uint)value;
        if (newDayNumber > MaxDayNumber)
        {
            Runtime.Throw("System.ArgumentOutOfRangeException", "Value to add was out of range.", "value");
        }

        return new DateOnly(newDayNumber);
    }

    public DateOnly AddMonths(int value) => new(DayNumberFromDateTime(GetEquivalentDateTime().AddMonths(value)));

    public DateOnly AddYears(int value) => new(DayNumberFromDateTime(GetEquivalentDateTime().AddYears(value)));

    public DateTime ToDateTime(TimeOnly time) => DateTime.CreateUnchecked(_dayNumber * DateTime.TicksPerDay + time.Ticks);

    public DateTime ToDateTime(TimeOnly time, DateTimeKind kind) => DateTime.SpecifyKind(ToDateTime(time), kind);

    public void Deconstruct(out int year, out int month, out int day) =>
        GetEquivalentDateTime().GetDate(out year, out month, out day);

    // uint.CompareTo, written out: -1, 0 or 1
    public int CompareTo(DateOnly value) =>
        _dayNumber < value._dayNumber ? -1 : _dayNumber > value._dayNumber ? 1 : 0;

    public bool Equals(DateOnly value) => _dayNumber == value._dayNumber;

    public override int GetHashCode() => (int)_dayNumber;

    public static bool operator ==(DateOnly left, DateOnly right) => left._dayNumber == right._dayNumber;

    public static bool operator !=(DateOnly left, DateOnly right) => left._dayNumber != right._dayNumber;

    public static bool operator >(DateOnly left, DateOnly right) => left._dayNumber > right._dayNumber;

    public static bool operator >=(DateOnly left, DateOnly right) => left._dayNumber >= right._dayNumber;

    public static bool operator <(DateOnly left, DateOnly right) => left._dayNumber < right._dayNumber;

    public static bool operator <=(DateOnly left, DateOnly right) => left._dayNumber <= right._dayNumber;

    /// <summary>The invariant culture's d: <c>MM/dd/yyyy</c> (see DateTimeFormat.cs). It makes a string: a heap.</summary>
    public override string ToString() => Text(default, 0, true);

    /// <summary>The text of a date format (<c>d D m o r y</c>, or a custom one without a time). It makes a string.</summary>
    public string ToString(string? format) => Text(format.AsSpan(), 0, true);

    public string ToLongDateString() => Text("D", 0, true);

    public string ToShortDateString() => Text(default, 0, true);

    /// <summary>The text of <paramref name="format"/> into <paramref name="destination"/>, with no heap (see DateTime's).</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null)
    {
        var text = DateTimeText.Into(destination);
        DateTimeFormat.WriteDateOnly(GetEquivalentDateTime(), format, false, ref text);
        text.Finish();
        return text.Result(out charsWritten);
    }

    /// <summary>Printed text's hole of a DateOnly, with no string made (see DateTime's).</summary>
    internal void Print(ReadOnlySpan<char> format, int alignment, bool toString)
    {
        Span<char> buffer = stackalloc char[DateTimeText.PrintBuffer];
        var text = DateTimeText.Printing(buffer);
        if (alignment > 0)
        {
            var counted = DateTimeText.Counting();
            DateTimeFormat.WriteDateOnly(GetEquivalentDateTime(), format, toString, ref counted);
            counted.Finish();
            text.Pad(alignment - counted.Length);
        }

        DateTimeFormat.WriteDateOnly(GetEquivalentDateTime(), format, toString, ref text);
        text.PadTo(-alignment);
        text.Finish();
    }

    /// <summary>Kept text's hole of a DateOnly, and ToString: the text as a string, aligned.</summary>
    internal string Text(ReadOnlySpan<char> format, int alignment, bool toString)
    {
        var counted = DateTimeText.Counting();
        DateTimeFormat.WriteDateOnly(GetEquivalentDateTime(), format, toString, ref counted);
        counted.Finish();
        int width = alignment < 0 ? -alignment : alignment;
        var chars = new char[width > counted.Length ? width : counted.Length];
        var text = DateTimeText.Into(chars);
        text.Pad(alignment - counted.Length);
        DateTimeFormat.WriteDateOnly(GetEquivalentDateTime(), format, toString, ref text);
        text.PadTo(-alignment);
        text.Finish();
        return new string(chars);
    }

    /// <summary>The DateTime of this date at midnight, Kind Unspecified.</summary>
    internal DateTime GetEquivalentDateTime() => DateTime.CreateUnchecked(_dayNumber * DateTime.TicksPerDay);

    private static uint DayNumberFromDateTime(DateTime dt) => (uint)((ulong)dt.Ticks / DateTime.TicksPerDay);
}

/// <summary>
/// ChipSharp's <see cref="System.TimeOnly"/>: a time of day with no date, as .NET's ticks since midnight. The members are
/// .NET 10's own (System.Private.CoreLib's TimeOnly.cs); it is a struct of 8 bytes and needs no heap. Text of it is in
/// DateTimeFormat.cs; Parse and the object overloads are not here.
/// </summary>
#pragma warning disable CS0660, CS0661 // no Equals(object): see DateTime
[Substitute(typeof(System.TimeOnly))]
internal readonly struct TimeOnly
#pragma warning restore CS0660, CS0661
{
    private const long MaxTimeTicks = DateTime.TicksPerDay - 1;

    private readonly ulong _ticks;

    public TimeOnly(int hour, int minute)
        : this(DateTime.TimeToTicks(hour, minute, 0, 0))
    {
    }

    public TimeOnly(int hour, int minute, int second)
        : this(DateTime.TimeToTicks(hour, minute, second, 0))
    {
    }

    public TimeOnly(int hour, int minute, int second, int millisecond)
        : this(DateTime.TimeToTicks(hour, minute, second, millisecond))
    {
    }

    public TimeOnly(int hour, int minute, int second, int millisecond, int microsecond)
        : this(DateTime.TimeToTicks(hour, minute, second, millisecond, microsecond))
    {
    }

    public TimeOnly(long ticks)
    {
        if ((ulong)ticks > MaxTimeTicks)
        {
            Runtime.Throw("System.ArgumentOutOfRangeException", "Ticks must be between 0 and and TimeOnly.MaxValue.Ticks.",
                "ticks");
        }

        _ticks = (ulong)ticks;
    }

    private TimeOnly(ulong ticks) => _ticks = ticks;

    public static TimeOnly MinValue => new(0UL);

    public static TimeOnly MaxValue => new((ulong)MaxTimeTicks);

    public int Hour => (int)(_ticks / System.TimeSpan.TicksPerHour);

    public int Minute => (int)((uint)(_ticks / System.TimeSpan.TicksPerMinute) % (uint)System.TimeSpan.MinutesPerHour);

    public int Second => (int)((uint)(_ticks / System.TimeSpan.TicksPerSecond) % (uint)System.TimeSpan.SecondsPerMinute);

    public int Millisecond =>
        (int)((uint)(_ticks / System.TimeSpan.TicksPerMillisecond) % (uint)System.TimeSpan.MillisecondsPerSecond);

    public int Microsecond =>
        (int)(_ticks / System.TimeSpan.TicksPerMicrosecond % (uint)System.TimeSpan.MicrosecondsPerMillisecond);

    public int Nanosecond => (int)(_ticks % System.TimeSpan.TicksPerMicrosecond * System.TimeSpan.NanosecondsPerTick);

    public long Ticks => (long)_ticks;

    public TimeOnly Add(System.TimeSpan value) => AddTicks(value.Ticks);

    public TimeOnly Add(System.TimeSpan value, out int wrappedDays) => AddTicks(value.Ticks, out wrappedDays);

    public TimeOnly AddHours(double value) => AddTicks((long)(value * System.TimeSpan.TicksPerHour));

    public TimeOnly AddHours(double value, out int wrappedDays) =>
        AddTicks((long)(value * System.TimeSpan.TicksPerHour), out wrappedDays);

    public TimeOnly AddMinutes(double value) => AddTicks((long)(value * System.TimeSpan.TicksPerMinute));

    public TimeOnly AddMinutes(double value, out int wrappedDays) =>
        AddTicks((long)(value * System.TimeSpan.TicksPerMinute), out wrappedDays);

    /// <summary>True when this time is from <paramref name="start"/> (included) to <paramref name="end"/> (excluded), across midnight too.</summary>
    public bool IsBetween(TimeOnly start, TimeOnly end)
    {
        ulong time = _ticks;
        ulong startTicks = start._ticks;
        ulong endTicks = end._ticks;
        return startTicks <= endTicks
            ? (time - startTicks < endTicks - startTicks)
            : (time - endTicks >= startTicks - endTicks);
    }

    public static TimeOnly FromTimeSpan(System.TimeSpan timeSpan) => new(timeSpan.Ticks);

    public static TimeOnly FromDateTime(DateTime dateTime) => new((ulong)dateTime.TimeOfDay.Ticks);

    public System.TimeSpan ToTimeSpan() => new((long)_ticks);

    public void Deconstruct(out int hour, out int minute)
    {
        hour = Hour;
        minute = Minute;
    }

    public void Deconstruct(out int hour, out int minute, out int second) => ToDateTime().GetTime(out hour, out minute, out second);

    // ulong.CompareTo, written out: -1, 0 or 1
    public int CompareTo(TimeOnly value) => _ticks < value._ticks ? -1 : _ticks > value._ticks ? 1 : 0;

    public bool Equals(TimeOnly value) => _ticks == value._ticks;

    public override int GetHashCode()
    {
        ulong ticks = _ticks;
        return unchecked((int)ticks) ^ (int)(ticks >> 32);
    }

    public static bool operator ==(TimeOnly left, TimeOnly right) => left._ticks == right._ticks;

    public static bool operator !=(TimeOnly left, TimeOnly right) => left._ticks != right._ticks;

    public static bool operator >(TimeOnly left, TimeOnly right) => left._ticks > right._ticks;

    public static bool operator >=(TimeOnly left, TimeOnly right) => left._ticks >= right._ticks;

    public static bool operator <(TimeOnly left, TimeOnly right) => left._ticks < right._ticks;

    public static bool operator <=(TimeOnly left, TimeOnly right) => left._ticks <= right._ticks;

    /// <summary>The time from <paramref name="t2"/> to <paramref name="t1"/>, forward: 24 hours added when it is negative.</summary>
    public static System.TimeSpan operator -(TimeOnly t1, TimeOnly t2)
    {
        long diff = (long)(t1._ticks - t2._ticks);
        return new System.TimeSpan(diff + ((diff >> 63) & DateTime.TicksPerDay));
    }

    /// <summary>The invariant culture's t: <c>HH:mm</c> (see DateTimeFormat.cs). It makes a string: a heap.</summary>
    public override string ToString() => Text(default, 0, true);

    /// <summary>The text of a time format (<c>o r t T</c>, or a custom one without a date). It makes a string.</summary>
    public string ToString(string? format) => Text(format.AsSpan(), 0, true);

    public string ToLongTimeString() => Text("T", 0, true);

    public string ToShortTimeString() => Text(default, 0, true);

    /// <summary>The text of <paramref name="format"/> into <paramref name="destination"/>, with no heap (see DateTime's).</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null)
    {
        var text = DateTimeText.Into(destination);
        DateTimeFormat.WriteTimeOnly(ToDateTime(), format, false, ref text);
        text.Finish();
        return text.Result(out charsWritten);
    }

    /// <summary>Printed text's hole of a TimeOnly, with no string made (see DateTime's).</summary>
    internal void Print(ReadOnlySpan<char> format, int alignment, bool toString)
    {
        Span<char> buffer = stackalloc char[DateTimeText.PrintBuffer];
        var text = DateTimeText.Printing(buffer);
        if (alignment > 0)
        {
            var counted = DateTimeText.Counting();
            DateTimeFormat.WriteTimeOnly(ToDateTime(), format, toString, ref counted);
            counted.Finish();
            text.Pad(alignment - counted.Length);
        }

        DateTimeFormat.WriteTimeOnly(ToDateTime(), format, toString, ref text);
        text.PadTo(-alignment);
        text.Finish();
    }

    /// <summary>Kept text's hole of a TimeOnly, and ToString: the text as a string, aligned.</summary>
    internal string Text(ReadOnlySpan<char> format, int alignment, bool toString)
    {
        var counted = DateTimeText.Counting();
        DateTimeFormat.WriteTimeOnly(ToDateTime(), format, toString, ref counted);
        counted.Finish();
        int width = alignment < 0 ? -alignment : alignment;
        var chars = new char[width > counted.Length ? width : counted.Length];
        var text = DateTimeText.Into(chars);
        text.Pad(alignment - counted.Length);
        DateTimeFormat.WriteTimeOnly(ToDateTime(), format, toString, ref text);
        text.PadTo(-alignment);
        text.Finish();
        return new string(chars);
    }

    /// <summary>The DateTime of this time of day on 1 January 0001, Kind Unspecified.</summary>
    internal DateTime ToDateTime() => DateTime.CreateUnchecked((long)_ticks);

    private TimeOnly AddTicks(long ticks) =>
        new((_ticks + DateTime.TicksPerDay + (ulong)(ticks % DateTime.TicksPerDay)) % DateTime.TicksPerDay);

    private TimeOnly AddTicks(long ticks, out int wrappedDays)
    {
        long days = ticks / DateTime.TicksPerDay;
        long newTicks = ticks - (days * DateTime.TicksPerDay);
        newTicks += (long)_ticks;
        if (newTicks < 0)
        {
            days--;
            newTicks += DateTime.TicksPerDay;
        }
        else if (newTicks >= DateTime.TicksPerDay)
        {
            days++;
            newTicks -= DateTime.TicksPerDay;
        }

        wrappedDays = (int)days;
        return new TimeOnly((ulong)newTicks);
    }
}
