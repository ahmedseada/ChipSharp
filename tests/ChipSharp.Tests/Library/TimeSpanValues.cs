namespace ChipSharp.Tests.Library;

/// <summary>
/// The values the TimeSpan tests try: the edges of every range (ticks, each unit's whole-number factory, each double
/// factory, the components of the constructors), numbers that round, and random ones (always the same: a fixed seed).
/// </summary>
internal static class TimeSpanValues
{
    private const long TicksPerDay = TimeSpan.TicksPerDay;

    /// <summary>Numbers of ticks: zero, small, each unit's size and its neighbours, and the ends of a long.</summary>
    public static readonly long[] EdgeTicks =
    [
        0, 1, -1, 5, -5, 9, 10, 11, -10, 9_999, 10_000, 10_001, -10_000, 15_000, 9_999_999, 10_000_000, -10_000_000,
        599_999_999, 600_000_000, 35_999_999_999, 36_000_000_000, -36_000_000_000, TicksPerDay - 1, TicksPerDay,
        TicksPerDay + 1, -TicksPerDay, 937_845_050_000, 123_456_789_012_345, -123_456_789_012_345,
        long.MaxValue, long.MaxValue - 1, long.MinValue, long.MinValue + 1, long.MaxValue / 2, long.MinValue / 2,
        TimeSpan.MaxValue.Ticks / TicksPerDay * TicksPerDay, int.MaxValue, int.MinValue, uint.MaxValue,
    ];

    /// <summary>Whole numbers for the factories of one unit: around zero, around each unit's limit, and the long's ends.</summary>
    public static readonly long[] EdgeUnits =
    [
        0, 1, -1, 2, 500, -500, 59, 60, 61, 1_000, 3_600, 86_400, int.MaxValue, int.MinValue, 10_675_199, 10_675_200,
        -10_675_199, -10_675_200, 256_204_778, 256_204_779, -256_204_778, -256_204_779, 15_372_286_728,
        15_372_286_729, -15_372_286_728, -15_372_286_729, 922_337_203_685, 922_337_203_686, -922_337_203_685,
        -922_337_203_686, 922_337_203_685_477, 922_337_203_685_478, -922_337_203_685_477, -922_337_203_685_478,
        922_337_203_685_477_580, 922_337_203_685_477_581, -922_337_203_685_477_580, -922_337_203_685_477_581,
        long.MaxValue, long.MinValue,
    ];

    /// <summary>Doubles for the double factories and for * and /: fractions of a tick, halves, limits, NaN, infinities.</summary>
    public static readonly double[] EdgeDoubles =
    [
        0.0, -0.0, 1, -1, 0.5, -0.5, 1.5, -1.5, 2.5, -2.5, 0.1, 0.2, 0.3, 1.0 / 3, -2.0 / 3, 0.00005, -0.00005, 0.00015,
        0.0001, 0.00025, 1e-7, -1e-7, 1e-9, 123.456789, 86_399.999_999_9, 1e6, 1e10, -1e10, 1e15, 1e17, 1e18,
        9.2e18, 9.223372036854775e18, 9.223372036854776e18, -9.223372036854776e18, 1e19, -1e19, 1e300,
        10_675_199.116730064, 10_675_199.116730065, -10_675_199.116730064, 256_204_778.80152154, 15_372_286_728.091293,
        922_337_203_685.4775807, 922_337_203_685_477.5807, -922_337_203_685_477.5807, 922_337_203_685_477_580.7,
        922_337_203_685_477_580.8, 4_503_599_627_370_495.5, 4_503_599_627_370_496.0, double.Epsilon, double.MaxValue,
        double.MinValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
    ];

    /// <summary>Ints for the constructors' days, hours, minutes, ...: small, negative, and the ends of an int.</summary>
    public static readonly int[] EdgeInts =
    [
        0, 1, -1, 2, 23, 24, 59, 60, -60, 999, 1_000, 10_675_199, 10_675_200, -10_675_199, 2_147_483, int.MaxValue,
        int.MinValue, 106_751_991, 213_503_982,
    ];

    /// <summary><paramref name="count"/> longs from all over: any bits, and numbers of each size.</summary>
    public static IEnumerable<long> RandomLongs(int count, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            yield return (i % 3) switch
            {
                0 => random.NextInt64(long.MinValue, long.MaxValue),
                1 => random.NextInt64(-1L << random.Next(1, 63), 1L << random.Next(1, 63)),
                _ => random.NextInt64(-1_000_000_000_000, 1_000_000_000_000),
            };
        }
    }

    /// <summary><paramref name="count"/> doubles from all over: any bits (NaN too), and numbers of each size with fractions.</summary>
    public static IEnumerable<double> RandomDoubles(int count, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            yield return (i % 3) switch
            {
                0 => BitConverter.Int64BitsToDouble(random.NextInt64(long.MinValue, long.MaxValue)),
                1 => (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-8, 22)),
                _ => Math.Round((random.NextDouble() - 0.5) * 1e6, random.Next(0, 9)),
            };
        }
    }

    /// <summary><paramref name="count"/> ints from all over.</summary>
    public static IEnumerable<int> RandomInts(int count, int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            yield return (i % 2) == 0 ? random.Next(int.MinValue, int.MaxValue) : random.Next(-100_000, 100_000);
        }
    }
}
