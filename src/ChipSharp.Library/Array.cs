using System.Runtime.InteropServices;

namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.Array"/> for sorting: <c>Array.Sort&lt;T&gt;</c> and <c>Array.BinarySearch&lt;T&gt;</c>
/// of an array of numbers or bools, with .NET 10's results and exceptions. The code is .NET's own (System.Private.CoreLib's
/// Array.cs and ArraySortHelper.cs, its GenericArraySortHelper): an introsort (insertion sort for 16 elements or fewer,
/// a median-of-three quicksort, a heapsort past the depth limit) that moves every NaN to the front first. So equal
/// elements that are not the same bits, -0.0 and 0.0 or NaNs, end up exactly where .NET puts them.
/// </summary>
/// <remarks>
/// The other members of System.Array are ChipSharp's own intrinsics (see ChipSharp.Frontend's ArrayMethods), or not here
/// yet: the overloads of an IComparer or a Comparison, of keys and items, and of Array. The constraint T : struct,
/// IComparable&lt;T&gt; is this library's; .NET's T has none, and ChipSharp makes these for numbers and bools only. A
/// null array is told apart from an empty one by its span: null's has no elements' address, and every array has one.
/// </remarks>
[Substitute(typeof(System.Array))]
internal static class Array
{
    /// <summary>.NET's Array.IntrosortSizeThreshold: a partition this small is sorted by insertion.</summary>
    private const int IntrosortSizeThreshold = 16;

    public static void Sort<T>(T[] array) where T : struct, IComparable<T>
    {
        if (IsNull(array))
        {
            Runtime.ArrayNull();
        }

        if (array.Length > 1)
        {
            Sort(array.AsSpan());
        }
    }

    public static void Sort<T>(T[] array, int index, int length) where T : struct, IComparable<T>
    {
        if (IsNull(array))
        {
            Runtime.ArrayNull();
        }

        CheckRange(array.Length, index, length);
        if (length > 1)
        {
            Sort(array.AsSpan(index, length));
        }
    }

    public static int BinarySearch<T>(T[] array, T value) where T : struct, IComparable<T>
    {
        if (IsNull(array))
        {
            Runtime.ArrayNull();
        }

        return BinarySearch(array, 0, array.Length, value, checkRange: false);
    }

    public static int BinarySearch<T>(T[] array, int index, int length, T value) where T : struct, IComparable<T>
    {
        if (IsNull(array))
        {
            Runtime.ArrayNull();
        }

        return BinarySearch(array, index, length, value, checkRange: true);
    }

    /// <summary>
    /// True for null: the only array whose span has no elements' address (an empty array's has one). A read-only view,
    /// so that a constant table BinarySearch reads stays one.
    /// </summary>
    private static bool IsNull<T>(T[] array) => new ReadOnlySpan<T>(array) == ReadOnlySpan<T>.Empty;

    /// <summary>.NET's checks of an index and a length: neither negative, and together within the array.</summary>
    private static void CheckRange(int arrayLength, int index, int length)
    {
        if (index < 0)
        {
            Runtime.NegativeIndex();
        }

        if (length < 0)
        {
            Runtime.NegativeLength();
        }

        if (arrayLength - index < length)
        {
            Runtime.InvalidOffsetLength();
        }
    }

    /// <summary>The index of <paramref name="value"/> in the sorted elements, or the complement of where it would go.</summary>
    private static int BinarySearch<T>(T[] array, int index, int length, T value, bool checkRange)
        where T : struct, IComparable<T>
    {
        if (checkRange)
        {
            CheckRange(array.Length, index, length);
        }

        var lo = index;
        var hi = index + length - 1;
        while (lo <= hi)
        {
            var i = lo + ((hi - lo) >> 1);
            var element = array[i];
            var order = element.CompareTo(value);
            if (order == 0)
            {
                return i;
            }

            if (order < 0)
            {
                lo = i + 1;
            }
            else
            {
                hi = i - 1;
            }
        }

        return ~lo;
    }

    /// <summary>GenericArraySortHelper.Sort: NaNs to the front, then an introsort of the rest.</summary>
    private static void Sort<T>(Span<T> keys) where T : struct, IComparable<T>
    {
        if (typeof(T) == typeof(float) || typeof(T) == typeof(double))
        {
            keys = keys.Slice(NaNsToFront(keys));
        }

        IntroSort(keys, 2 * (Log2((uint)keys.Length) + 1));
    }

    /// <summary>SortUtils.MoveNansToFront: every NaN swapped to the front, in order; how many there are.</summary>
    private static int NaNsToFront<T>(Span<T> keys) where T : struct
    {
        var left = 0;
#pragma warning disable CS1718 // x != x is how NaN is told apart here, without the library's IsNaN
        if (typeof(T) == typeof(float))
        {
            var values = MemoryMarshal.Cast<T, float>(keys);
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (value != value)
                {
                    (values[left], values[i]) = (values[i], values[left]);
                    left++;
                }
            }
        }
        else
        {
            var values = MemoryMarshal.Cast<T, double>(keys);
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (value != value)
                {
                    (values[left], values[i]) = (values[i], values[left]);
                    left++;
                }
            }
        }
#pragma warning restore CS1718

        return left;
    }

    /// <summary>BitOperations.Log2: the index of the highest bit set (0 for 0).</summary>
    private static int Log2(uint value)
    {
        var log = 0;
        while ((value >>= 1) != 0)
        {
            log++;
        }

        return log;
    }

    private static void IntroSort<T>(Span<T> keys, int depthLimit) where T : struct, IComparable<T>
    {
        var partitionSize = keys.Length;
        while (partitionSize > 1)
        {
            if (partitionSize <= IntrosortSizeThreshold)
            {
                if (partitionSize == 2)
                {
                    SwapIfGreater(keys, 0, 1);
                    return;
                }

                if (partitionSize == 3)
                {
                    SwapIfGreater(keys, 0, 1);
                    SwapIfGreater(keys, 0, 2);
                    SwapIfGreater(keys, 1, 2);
                    return;
                }

                InsertionSort(keys.Slice(0, partitionSize));
                return;
            }

            if (depthLimit == 0)
            {
                HeapSort(keys.Slice(0, partitionSize));
                return;
            }

            depthLimit--;
            var p = PickPivotAndPartition(keys.Slice(0, partitionSize));

            // the pivot is where it belongs already
            IntroSort(keys.Slice(p + 1, partitionSize - (p + 1)), depthLimit);
            partitionSize = p;
        }
    }

    private static int PickPivotAndPartition<T>(Span<T> keys) where T : struct, IComparable<T>
    {
        // the median of the first, middle and last elements, moved just before the last
        var last = keys.Length - 1;
        var middle = last >> 1;
        SwapIfGreater(keys, 0, middle);
        SwapIfGreater(keys, 0, last);
        SwapIfGreater(keys, middle, last);

        var nextToLast = keys.Length - 2;
        var pivot = keys[middle];
        Swap(keys, middle, nextToLast);

        // the left and right ends walk towards each other, swapping, until they meet
        int left = 0, right = nextToLast;
        while (left < right)
        {
            while (left < nextToLast && GreaterThan(pivot, keys[++left]))
            {
            }

            while (right > 0 && LessThan(pivot, keys[--right]))
            {
            }

            if (left >= right)
            {
                break;
            }

            Swap(keys, left, right);
        }

        // the pivot where it belongs
        if (left != nextToLast)
        {
            Swap(keys, left, nextToLast);
        }

        return left;
    }

    private static void HeapSort<T>(Span<T> keys) where T : struct, IComparable<T>
    {
        var n = keys.Length;
        for (var i = n >> 1; i >= 1; i--)
        {
            DownHeap(keys, i, n);
        }

        for (var i = n; i > 1; i--)
        {
            Swap(keys, 0, i - 1);
            DownHeap(keys, 1, i - 1);
        }
    }

    private static void DownHeap<T>(Span<T> keys, int i, int n) where T : struct, IComparable<T>
    {
        var d = keys[i - 1];
        while (i <= n >> 1)
        {
            var child = 2 * i;
            if (child < n && LessThan(keys[child - 1], keys[child]))
            {
                child++;
            }

            if (!LessThan(d, keys[child - 1]))
            {
                break;
            }

            keys[i - 1] = keys[child - 1];
            i = child;
        }

        keys[i - 1] = d;
    }

    private static void InsertionSort<T>(Span<T> keys) where T : struct, IComparable<T>
    {
        for (var i = 0; i < keys.Length - 1; i++)
        {
            var t = keys[i + 1];
            var j = i;
            while (j >= 0 && LessThan(t, keys[j]))
            {
                keys[j + 1] = keys[j];
                j--;
            }

            keys[j + 1] = t;
        }
    }

    private static void SwapIfGreater<T>(Span<T> keys, int i, int j) where T : struct, IComparable<T>
    {
        if (GreaterThan(keys[i], keys[j]))
        {
            Swap(keys, i, j);
        }
    }

    private static void Swap<T>(Span<T> keys, int i, int j) => (keys[i], keys[j]) = (keys[j], keys[i]);

    // .NET compares a float or double with < and > here, which for the values left after the NaNs is CompareTo's order
    // (-0.0 and 0.0 are equal both ways); the narrow whole numbers' CompareTo is a difference, of the right sign
    private static bool LessThan<T>(T left, T right) where T : struct, IComparable<T> => left.CompareTo(right) < 0;

    private static bool GreaterThan<T>(T left, T right) where T : struct, IComparable<T> => left.CompareTo(right) > 0;
}
