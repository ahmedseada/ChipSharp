using SubstituteArray = ChipSharp.Library.Array;

namespace ChipSharp.Tests.Library;

/// <summary>
/// ChipSharp.Library's Array.Sort and Array.BinarySearch (the substitute ChipSharp compiles for them), run on .NET next
/// to .NET's own: on thousands of arrays, short ones its insertion sort takes and long ones it partitions, the sorted
/// elements are the same bits (NaNs, -0.0 and 0.0 where .NET puts them) and every search finds what .NET finds.
/// </summary>
public sealed class ArraySortTests
{
    [Fact]
    public void Doubles_with_NaN_and_both_zeros_sort_to_the_same_bits_as_dotnet()
    {
        var random = new Random(1);
        for (var round = 0; round < 3000; round++)
        {
            var length = round < 300 ? round % 60 : random.Next(0, 400);
            var values = new double[length];
            for (var i = 0; i < length; i++)
            {
                values[i] = random.Next(8) switch
                {
                    0 => double.NaN,
                    1 => -0.0,
                    2 => 0.0,
                    3 => double.NegativeInfinity,
                    _ => random.Next(-5, 6) / 2.0,
                };
            }

            var expected = (double[])values.Clone();
            System.Array.Sort(expected);
            SubstituteArray.Sort(values);

            Assert.Equal(expected.Select(BitConverter.DoubleToInt64Bits), values.Select(BitConverter.DoubleToInt64Bits));
        }
    }

    [Fact]
    public void Floats_and_a_range_of_them_sort_to_the_same_bits_as_dotnet()
    {
        var random = new Random(2);
        for (var round = 0; round < 2000; round++)
        {
            var values = new float[random.Next(0, 200)];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = random.Next(6) switch
                {
                    0 => float.NaN,
                    1 => -0f,
                    2 => 0f,
                    _ => random.Next(-3, 4),
                };
            }

            var index = values.Length == 0 ? 0 : random.Next(values.Length);
            var length = random.Next(values.Length - index + 1);
            var expected = (float[])values.Clone();
            System.Array.Sort(expected, index, length);
            SubstituteArray.Sort(values, index, length);

            Assert.Equal(expected.Select(BitConverter.SingleToInt32Bits), values.Select(BitConverter.SingleToInt32Bits));
        }
    }

    [Fact]
    public void Whole_numbers_and_bools_sort_as_dotnet_sorts_them_random_sorted_reversed_and_in_an_organ_pipe()
    {
        var random = new Random(3);
        for (var round = 0; round < 1000; round++)
        {
            var values = new int[random.Next(0, 500)];
            var pattern = round % 4;
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = pattern switch
                {
                    0 => random.Next(),
                    1 => i,
                    2 => values.Length - i,
                    _ => i % 2 == 0 ? i : values.Length - i, // an organ pipe
                };
            }

            var expected = (int[])values.Clone();
            System.Array.Sort(expected);
            SubstituteArray.Sort(values);
            Assert.Equal(expected, values);
        }

        bool[] flags = [true, false, true, true, false];
        SubstituteArray.Sort(flags);
        Assert.Equal([false, false, true, true, true], flags);
        long[] longs = [long.MaxValue, long.MinValue, 0, -1];
        SubstituteArray.Sort(longs);
        Assert.Equal([long.MinValue, -1, 0, long.MaxValue], longs);
    }

    [Fact]
    public void BinarySearch_finds_what_dotnet_finds_or_the_same_complement()
    {
        var random = new Random(4);
        for (var round = 0; round < 2000; round++)
        {
            var values = new double[random.Next(0, 50)];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = random.Next(10) == 0 ? double.NaN : random.Next(-10, 10);
            }

            System.Array.Sort(values);
            var wanted = random.Next(12) == 0 ? double.NaN : random.Next(-12, 12);
            Assert.Equal(System.Array.BinarySearch(values, wanted), SubstituteArray.BinarySearch(values, wanted));
            var index = values.Length == 0 ? 0 : random.Next(values.Length);
            var length = random.Next(values.Length - index + 1);
            Assert.Equal(System.Array.BinarySearch(values, index, length, wanted),
                SubstituteArray.BinarySearch(values, index, length, wanted));
        }
    }

    [Fact]
    public void On_dotnet_the_checks_throw_what_dotnet_throws()
    {
        int[] values = [1, 2, 3];

        Assert.Equal("array", Assert.Throws<ArgumentNullException>(() => SubstituteArray.Sort<int>(null!)).ParamName);
        Assert.Equal("index", Assert.Throws<ArgumentOutOfRangeException>(() => SubstituteArray.Sort(values, -1, 1)).ParamName);
        Assert.Equal("length", Assert.Throws<ArgumentOutOfRangeException>(() => SubstituteArray.Sort(values, 0, -1)).ParamName);
        Assert.Throws<ArgumentException>(() => SubstituteArray.Sort(values, 2, 2));
        Assert.Equal("array", Assert.Throws<ArgumentNullException>(() => SubstituteArray.BinarySearch<int>(null!, 1)).ParamName);
        Assert.Throws<ArgumentException>(() => SubstituteArray.BinarySearch(values, 1, 3, 1));
        SubstituteArray.Sort(System.Array.Empty<int>());
        Assert.Equal(-1, SubstituteArray.BinarySearch(System.Array.Empty<int>(), 5));
    }
}
