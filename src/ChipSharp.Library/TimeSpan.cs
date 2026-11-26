namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.TimeSpan"/>: a duration, as a whole number of ticks of 100 nanoseconds. Like .NET's it
/// is one long, and its members give .NET 10's results bit for bit: the code is .NET's own (System.Private.CoreLib's
/// TimeSpan.cs), written without what ChipSharp does not compile yet.
/// </summary>
/// <remarks>
/// <para>
/// <c>TimeSpan.FromMilliseconds(500)</c> calls <c>FromMilliseconds(long)</c>: C# prefers long to double for an int.
/// The double factories (<c>FromSeconds(1.5)</c>) multiply by the ticks in one unit and drop the fraction of a tick
/// (towards zero), as .NET does since .NET Core 3.0; a double outside the ticks a long holds (or infinity) overflows,
/// and exactly 2^63 ticks gives <see cref="MaxValue"/>. <c>*</c> and <c>/</c> by a double round to the nearest tick,
/// halfway to the even one.
/// </para>
/// <para>
/// Where .NET throws (a result too long, NaN), the program stops with .NET's message (see <see cref="Runtime"/>).
/// Members that need text, objects or 128-bit numbers (Parse, ToString, the factories of several units such as
/// <c>FromSeconds(long, long, long)</c>, GetHashCode and the object overloads) are not here yet.
/// </para>
/// </remarks>
// Equals(object) and GetHashCode are virtual methods of an object, which ChipSharp does not compile yet: no overrides
#pragma warning disable CS0660, CS0661
[Substitute(typeof(System.TimeSpan))]
internal readonly struct TimeSpan
#pragma warning restore CS0660, CS0661
{
    public static readonly TimeSpan Zero = new(0);

    public static readonly TimeSpan MaxValue = new(MaxTicks);

    public static readonly TimeSpan MinValue = new(MinTicks);

    private const long NanosecondsPerTick = System.TimeSpan.NanosecondsPerTick;
    private const long TicksPerMicrosecond = System.TimeSpan.TicksPerMicrosecond;
    private const long TicksPerMillisecond = System.TimeSpan.TicksPerMillisecond;
    private const long TicksPerSecond = System.TimeSpan.TicksPerSecond;
    private const long TicksPerMinute = System.TimeSpan.TicksPerMinute;
    private const long TicksPerHour = System.TimeSpan.TicksPerHour;
    private const long TicksPerDay = System.TimeSpan.TicksPerDay;
    private const long MicrosecondsPerMillisecond = System.TimeSpan.MicrosecondsPerMillisecond;
    private const long MicrosecondsPerSecond = System.TimeSpan.MicrosecondsPerSecond;
    private const long MicrosecondsPerMinute = System.TimeSpan.MicrosecondsPerMinute;
    private const long MicrosecondsPerHour = System.TimeSpan.MicrosecondsPerHour;
    private const long MicrosecondsPerDay = System.TimeSpan.MicrosecondsPerDay;
    private const long MillisecondsPerSecond = System.TimeSpan.MillisecondsPerSecond;
    private const long SecondsPerMinute = System.TimeSpan.SecondsPerMinute;
    private const long SecondsPerHour = System.TimeSpan.SecondsPerHour;
    private const long MinutesPerHour = System.TimeSpan.MinutesPerHour;
    private const int HoursPerDay = System.TimeSpan.HoursPerDay;

    private const long MinTicks = long.MinValue;
    private const long MaxTicks = long.MaxValue;
    private const long MinMicroseconds = MinTicks / TicksPerMicrosecond;
    private const long MaxMicroseconds = MaxTicks / TicksPerMicrosecond;
    private const long MinMilliseconds = MinTicks / TicksPerMillisecond;
    private const long MaxMilliseconds = MaxTicks / TicksPerMillisecond;
    private const long MinSeconds = MinTicks / TicksPerSecond;
    private const long MaxSeconds = MaxTicks / TicksPerSecond;
    private const long MinMinutes = MinTicks / TicksPerMinute;
    private const long MaxMinutes = MaxTicks / TicksPerMinute;
    private const long MinHours = MinTicks / TicksPerHour;
    private const long MaxHours = MaxTicks / TicksPerHour;
    private const long MinDays = MinTicks / TicksPerDay;
    private const long MaxDays = MaxTicks / TicksPerDay;

    private readonly long _ticks;

    public TimeSpan(long ticks) => _ticks = ticks;

    public TimeSpan(int hours, int minutes, int seconds) => _ticks = TimeToTicks(hours, minutes, seconds);

    public TimeSpan(int days, int hours, int minutes, int seconds)
        : this(days, hours, minutes, seconds, 0)
    {
    }

    public TimeSpan(int days, int hours, int minutes, int seconds, int milliseconds)
        : this(days, hours, minutes, seconds, milliseconds, 0)
    {
    }

    public TimeSpan(int days, int hours, int minutes, int seconds, int milliseconds, int microseconds)
    {
        // as in .NET, the sum wraps around for days near int.MaxValue before the range is checked
        long totalMicroseconds = (days * MicrosecondsPerDay)
                               + (hours * MicrosecondsPerHour)
                               + (minutes * MicrosecondsPerMinute)
                               + (seconds * MicrosecondsPerSecond)
                               + (milliseconds * MicrosecondsPerMillisecond)
                               + microseconds;

        if ((totalMicroseconds > MaxMicroseconds) || (totalMicroseconds < MinMicroseconds))
        {
            Runtime.TimeSpanOutOfRange();
        }

        _ticks = totalMicroseconds * TicksPerMicrosecond;
    }

    public long Ticks => _ticks;

    public int Days => (int)(_ticks / TicksPerDay);

    public int Hours => (int)(_ticks / TicksPerHour % HoursPerDay);

    public int Minutes => (int)(_ticks / TicksPerMinute % MinutesPerHour);

    public int Seconds => (int)(_ticks / TicksPerSecond % SecondsPerMinute);

    public int Milliseconds => (int)(_ticks / TicksPerMillisecond % MillisecondsPerSecond);

    public int Microseconds => (int)(_ticks / TicksPerMicrosecond % MicrosecondsPerMillisecond);

    public int Nanoseconds => (int)(_ticks % TicksPerMicrosecond * NanosecondsPerTick);

    public double TotalDays => (double)_ticks / TicksPerDay;

    public double TotalHours => (double)_ticks / TicksPerHour;

    public double TotalMinutes => (double)_ticks / TicksPerMinute;

    public double TotalSeconds => (double)_ticks / TicksPerSecond;

    public double TotalMilliseconds
    {
        get
        {
            double temp = (double)_ticks / TicksPerMillisecond;
            if (temp > MaxMilliseconds)
            {
                return MaxMilliseconds;
            }

            if (temp < MinMilliseconds)
            {
                return MinMilliseconds;
            }

            return temp;
        }
    }

    public double TotalMicroseconds => (double)_ticks / TicksPerMicrosecond;

    public double TotalNanoseconds => (double)_ticks * NanosecondsPerTick;

    public static TimeSpan FromTicks(long value) => new(value);

    public static TimeSpan FromDays(int days) => FromUnits(days, TicksPerDay, MinDays, MaxDays);

    public static TimeSpan FromHours(int hours) => FromUnits(hours, TicksPerHour, MinHours, MaxHours);

    public static TimeSpan FromMinutes(long minutes) => FromUnits(minutes, TicksPerMinute, MinMinutes, MaxMinutes);

    public static TimeSpan FromSeconds(long seconds) => FromUnits(seconds, TicksPerSecond, MinSeconds, MaxSeconds);

    public static TimeSpan FromMilliseconds(long milliseconds) =>
        FromUnits(milliseconds, TicksPerMillisecond, MinMilliseconds, MaxMilliseconds);

    public static TimeSpan FromMicroseconds(long microseconds) =>
        FromUnits(microseconds, TicksPerMicrosecond, MinMicroseconds, MaxMicroseconds);

    public static TimeSpan FromDays(double value) => Interval(value, TicksPerDay);

    public static TimeSpan FromHours(double value) => Interval(value, TicksPerHour);

    public static TimeSpan FromMinutes(double value) => Interval(value, TicksPerMinute);

    public static TimeSpan FromSeconds(double value) => Interval(value, TicksPerSecond);

    public static TimeSpan FromMilliseconds(double value) => Interval(value, TicksPerMillisecond);

    public static TimeSpan FromMicroseconds(double value) => Interval(value, TicksPerMicrosecond);

    public TimeSpan Add(TimeSpan ts) => this + ts;

    public TimeSpan Subtract(TimeSpan ts) => this - ts;

    public TimeSpan Negate() => -this;

    public TimeSpan Multiply(double factor) => this * factor;

    public TimeSpan Divide(double divisor) => this / divisor;

    public double Divide(TimeSpan ts) => this / ts;

    public TimeSpan Duration()
    {
        if (_ticks == MinTicks)
        {
            Runtime.TimeSpanDurationOverflow();
        }

        return new TimeSpan(_ticks >= 0 ? _ticks : -_ticks);
    }

    // long.CompareTo, written out: -1, 0 or 1
    public static int Compare(TimeSpan t1, TimeSpan t2) => t1._ticks < t2._ticks ? -1 : t1._ticks > t2._ticks ? 1 : 0;

    public int CompareTo(TimeSpan value) => Compare(this, value);

    public bool Equals(TimeSpan obj) => Equals(this, obj);

    public static bool Equals(TimeSpan t1, TimeSpan t2) => t1 == t2;

    public static TimeSpan operator -(TimeSpan t)
    {
        if (t._ticks == MinTicks)
        {
            Runtime.NegateOverflow();
        }

        return new TimeSpan(-t._ticks);
    }

    public static TimeSpan operator -(TimeSpan t1, TimeSpan t2)
    {
        long result = t1._ticks - t2._ticks;
        long t1Sign = t1._ticks >> 63;

        // overflow when the signs of the two differ and the result's differs from the first's
        if ((t1Sign != (t2._ticks >> 63)) && (t1Sign != (result >> 63)))
        {
            Runtime.TimeSpanOverflow();
        }

        return new TimeSpan(result);
    }

    public static TimeSpan operator +(TimeSpan t) => t;

    public static TimeSpan operator +(TimeSpan t1, TimeSpan t2)
    {
        long result = t1._ticks + t2._ticks;
        long t1Sign = t1._ticks >> 63;

        // overflow when the signs of the two are the same and the result's differs
        if ((t1Sign == (t2._ticks >> 63)) && (t1Sign != (result >> 63)))
        {
            Runtime.TimeSpanOverflow();
        }

        return new TimeSpan(result);
    }

    public static TimeSpan operator *(TimeSpan timeSpan, double factor)
    {
        if (IsNaN(factor))
        {
            Runtime.TimeSpanNaNFactor();
        }

        return IntervalFromDoubleTicks(Round(timeSpan.Ticks * factor));
    }

    public static TimeSpan operator *(double factor, TimeSpan timeSpan) => timeSpan * factor;

    public static TimeSpan operator /(TimeSpan timeSpan, double divisor)
    {
        if (IsNaN(divisor))
        {
            Runtime.TimeSpanNaNDivisor();
        }

        return IntervalFromDoubleTicks(Round(timeSpan.Ticks / divisor));
    }

    // infinity for a division by zero, NaN for zero by zero, as in .NET
    public static double operator /(TimeSpan t1, TimeSpan t2) => t1.Ticks / (double)t2.Ticks;

    public static bool operator ==(TimeSpan t1, TimeSpan t2) => t1._ticks == t2._ticks;

    public static bool operator !=(TimeSpan t1, TimeSpan t2) => t1._ticks != t2._ticks;

    public static bool operator <(TimeSpan t1, TimeSpan t2) => t1._ticks < t2._ticks;

    public static bool operator <=(TimeSpan t1, TimeSpan t2) => t1._ticks <= t2._ticks;

    public static bool operator >(TimeSpan t1, TimeSpan t2) => t1._ticks > t2._ticks;

    public static bool operator >=(TimeSpan t1, TimeSpan t2) => t1._ticks >= t2._ticks;

    private static long TimeToTicks(int hour, int minute, int second)
    {
        // totalSeconds is less than 2^44: it cannot overflow
        long totalSeconds = (hour * SecondsPerHour)
                          + (minute * SecondsPerMinute)
                          + second;

        if ((totalSeconds > MaxSeconds) || (totalSeconds < MinSeconds))
        {
            Runtime.TimeSpanOutOfRange();
        }

        return totalSeconds * TicksPerSecond;
    }

    private static TimeSpan FromUnits(long units, long ticksPerUnit, long minUnits, long maxUnits)
    {
        if (units > maxUnits || units < minUnits)
        {
            Runtime.TimeSpanOutOfRange();
        }

        return FromTicks(units * ticksPerUnit);
    }

    private static TimeSpan Interval(double value, double scale)
    {
        if (IsNaN(value))
        {
            Runtime.TimeSpanNaN();
        }

        return IntervalFromDoubleTicks(value * scale);
    }

    private static TimeSpan IntervalFromDoubleTicks(double ticks)
    {
        // MaxTicks as a double is 2^63, one more than a long holds: that many ticks is MaxValue
        if ((ticks > MaxTicks) || (ticks < MinTicks) || IsNaN(ticks))
        {
            Runtime.TimeSpanOverflow();
        }

        if (ticks == MaxTicks)
        {
            return MaxValue;
        }

        return new TimeSpan((long)ticks);
    }

    // double.IsNaN, which is a library method: NaN is the one value not equal to itself
#pragma warning disable CS1718 // comparison made to the same variable: that is the test
    private static bool IsNaN(double value) => value != value;
#pragma warning restore CS1718

    /// <summary>
    /// Math.Round(double): the nearest whole number, halfway to the even one. From 2^52 on every double is whole, and
    /// below it adding 2^52 leaves no bits for a fraction, so the addition itself rounds (to nearest, ties to even).
    /// A zero may lose its sign, which a number of ticks does not have.
    /// </summary>
    private static double Round(double value)
    {
        const double Whole = 4503599627370496.0;
        if (!(value < Whole && value > -Whole))
        {
            return value;
        }

        return value >= 0 ? (value + Whole) - Whole : (value - Whole) + Whole;
    }
}
