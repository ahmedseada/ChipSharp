namespace ChipSharp.Library.Collections;

/// <summary>
/// The sorting and binary search of the collections (List&lt;T&gt;.Sort, BinarySearch): .NET's ArraySortHelper&lt;T&gt;
/// (System.Private.CoreLib's ArraySortHelper.cs), its introsort (insertion sort for 16 elements or fewer, a
/// median-of-three quicksort, a heapsort past the depth limit) over a comparer, the default one when none is given.
/// </summary>
/// <remarks>
/// .NET sorts a T that implements IComparable&lt;T&gt; without a comparer with its GenericArraySortHelper, the same
/// introsort through CompareTo, which makes the same moves for every value (a null pivot aside, which only lists of
/// objects with nulls among them have); for floats and doubles it first moves the NaNs to the front, which
/// <see cref="Array"/>'s Sort of numbers does, so a List of floats or doubles sorts with that.
/// </remarks>
internal static class ArraySort<T>
{
    private const int IntrosortSizeThreshold = 16;

    public static void Sort(T[] keys, int index, int length, IComparer<T>? comparer)
    {
        if (comparer == null || comparer == Comparer<T>.Default)
        {
            // numbers with NaNs: GenericArraySortHelper's NaN pre-pass, which Array.Sort of numbers has
            if (typeof(T) == typeof(double))
            {
                Array.Sort((double[])(object)keys, index, length);
                return;
            }

            if (typeof(T) == typeof(float))
            {
                Array.Sort((float[])(object)keys, index, length);
                return;
            }
        }

        var by = comparer ?? Comparer<T>.Default;
        IntrospectiveSort(new Span<T>(keys, index, length), by, null);
    }

    public static void Sort(T[] keys, int index, int length, Comparison<T> comparison) =>
        IntrospectiveSort(new Span<T>(keys, index, length), null, comparison);

    public static int BinarySearch(T[] array, int index, int length, T value, IComparer<T> comparer)
    {
        var lo = index;
        var hi = index + length - 1;
        while (lo <= hi)
        {
            var i = lo + ((hi - lo) >> 1);
            var order = comparer.Compare(array[i], value);
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

    /// <summary>Sorts with <paramref name="comparer"/>, or else <paramref name="comparison"/>.</summary>
    private static void IntrospectiveSort(Span<T> keys, IComparer<T>? comparer, Comparison<T>? comparison)
    {
        if (keys.Length > 1)
        {
            IntroSort(keys, 2 * (Log2((uint)keys.Length) + 1), comparer, comparison);
        }
    }

    private static int Compare(T x, T y, IComparer<T>? comparer, Comparison<T>? comparison) =>
        comparer != null ? comparer.Compare(x, y) : comparison!(x, y);

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

    private static void IntroSort(Span<T> keys, int depthLimit, IComparer<T>? comparer, Comparison<T>? comparison)
    {
        var partitionSize = keys.Length;
        while (partitionSize > 1)
        {
            if (partitionSize <= IntrosortSizeThreshold)
            {
                if (partitionSize == 2)
                {
                    SwapIfGreater(keys, comparer, comparison, 0, 1);
                    return;
                }

                if (partitionSize == 3)
                {
                    SwapIfGreater(keys, comparer, comparison, 0, 1);
                    SwapIfGreater(keys, comparer, comparison, 0, 2);
                    SwapIfGreater(keys, comparer, comparison, 1, 2);
                    return;
                }

                InsertionSort(keys.Slice(0, partitionSize), comparer, comparison);
                return;
            }

            if (depthLimit == 0)
            {
                HeapSort(keys.Slice(0, partitionSize), comparer, comparison);
                return;
            }

            depthLimit--;
            var p = PickPivotAndPartition(keys.Slice(0, partitionSize), comparer, comparison);

            // the pivot is where it belongs already
            IntroSort(keys.Slice(p + 1, partitionSize - (p + 1)), depthLimit, comparer, comparison);
            partitionSize = p;
        }
    }

    private static void SwapIfGreater(Span<T> keys, IComparer<T>? comparer, Comparison<T>? comparison, int i, int j)
    {
        if (Compare(keys[i], keys[j], comparer, comparison) > 0)
        {
            (keys[i], keys[j]) = (keys[j], keys[i]);
        }
    }

    private static void Swap(Span<T> keys, int i, int j) => (keys[i], keys[j]) = (keys[j], keys[i]);

    private static int PickPivotAndPartition(Span<T> keys, IComparer<T>? comparer, Comparison<T>? comparison)
    {
        // the median of the first, middle and last elements, moved just before the last
        var hi = keys.Length - 1;
        var middle = hi >> 1;
        SwapIfGreater(keys, comparer, comparison, 0, middle);
        SwapIfGreater(keys, comparer, comparison, 0, hi);
        SwapIfGreater(keys, comparer, comparison, middle, hi);

        var pivot = keys[middle];
        Swap(keys, middle, hi - 1);

        // the left and right ends walk towards each other, swapping, until they meet
        int left = 0, right = hi - 1;
        while (left < right)
        {
            while (Compare(keys[++left], pivot, comparer, comparison) < 0)
            {
            }

            while (Compare(pivot, keys[--right], comparer, comparison) < 0)
            {
            }

            if (left >= right)
            {
                break;
            }

            Swap(keys, left, right);
        }

        // the pivot where it belongs
        if (left != hi - 1)
        {
            Swap(keys, left, hi - 1);
        }

        return left;
    }

    private static void HeapSort(Span<T> keys, IComparer<T>? comparer, Comparison<T>? comparison)
    {
        var n = keys.Length;
        for (var i = n >> 1; i >= 1; i--)
        {
            DownHeap(keys, i, n, comparer, comparison);
        }

        for (var i = n; i > 1; i--)
        {
            Swap(keys, 0, i - 1);
            DownHeap(keys, 1, i - 1, comparer, comparison);
        }
    }

    private static void DownHeap(Span<T> keys, int i, int n, IComparer<T>? comparer, Comparison<T>? comparison)
    {
        var d = keys[i - 1];
        while (i <= n >> 1)
        {
            var child = 2 * i;
            if (child < n && Compare(keys[child - 1], keys[child], comparer, comparison) < 0)
            {
                child++;
            }

            if (!(Compare(d, keys[child - 1], comparer, comparison) < 0))
            {
                break;
            }

            keys[i - 1] = keys[child - 1];
            i = child;
        }

        keys[i - 1] = d;
    }

    private static void InsertionSort(Span<T> keys, IComparer<T>? comparer, Comparison<T>? comparison)
    {
        for (var i = 0; i < keys.Length - 1; i++)
        {
            var t = keys[i + 1];
            var j = i;
            while (j >= 0 && Compare(t, keys[j], comparer, comparison) < 0)
            {
                keys[j + 1] = keys[j];
                j--;
            }

            keys[j + 1] = t;
        }
    }
}
