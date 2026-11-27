// null is told apart from an array with == null here (spans of strings are not there yet), which the compiler's null
// state does not follow into the runtime functions that stop the program
#nullable disable

namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.Array"/> methods that take a delegate: Exists, Find, FindAll, FindIndex, FindLast,
/// FindLastIndex, ForEach, TrueForAll, ConvertAll and Sort with a Comparison, with .NET 10's results and exceptions, in
/// .NET's order (the array's null check, then the indices', then the delegate's). Each calls its delegate once per element
/// it looks at, in .NET's order. Sort is .NET's ArraySortHelper&lt;T&gt; with a Comparison: the same introsort as Sort's
/// (insertion sort up to 16 elements, a median-of-three quicksort, a heapsort past the depth limit), so elements the
/// comparison calls equal end up where .NET puts them.
/// </summary>
/// <remarks>
/// T is any element type an instance can be made for (numbers, bools, enums, structs, strings); the program's classes as T
/// wait for generics over classes. FindAll gives a new array of what it found (an empty one too, as .NET's is not
/// <c>Array.Empty</c>), grown as .NET's list of them grows.
/// </remarks>
internal static partial class Array
{
    public static bool Exists<T>(T[] array, Predicate<T> match) => FindIndex(array, match) != -1;

    public static T Find<T>(T[] array, Predicate<T> match)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        if (match == null)
        {
            Runtime.MatchNull();
        }

        for (var i = 0; i < array.Length; i++)
        {
            if (match(array[i]))
            {
                return array[i];
            }
        }

        return default;
    }

    public static T[] FindAll<T>(T[] array, Predicate<T> match)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        if (match == null)
        {
            Runtime.MatchNull();
        }

        var found = 0;
        var result = new T[0];
        for (var i = 0; i < array.Length; i++)
        {
            if (match(array[i]))
            {
                if (found == result.Length)
                {
                    var grown = found == 0 ? 4 : found * 2;
                    System.Array.Resize(ref result, grown < array.Length ? grown : array.Length);
                }

                result[found++] = array[i];
            }
        }

        if (found != result.Length)
        {
            System.Array.Resize(ref result, found);
        }

        return result;
    }

    public static int FindIndex<T>(T[] array, Predicate<T> match)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        return FindIndex(array, 0, array.Length, match);
    }

    public static int FindIndex<T>(T[] array, int startIndex, Predicate<T> match)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        return FindIndex(array, startIndex, array.Length - startIndex, match);
    }

    public static int FindIndex<T>(T[] array, int startIndex, int count, Predicate<T> match)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        if (startIndex < 0 || startIndex > array.Length)
        {
            Runtime.StartIndexPastEnd();
        }

        if (count < 0 || startIndex > array.Length - count)
        {
            Runtime.CountOutOfRange();
        }

        if (match == null)
        {
            Runtime.MatchNull();
        }

        var end = startIndex + count;
        for (var i = startIndex; i < end; i++)
        {
            if (match(array[i]))
            {
                return i;
            }
        }

        return -1;
    }

    public static T FindLast<T>(T[] array, Predicate<T> match)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        if (match == null)
        {
            Runtime.MatchNull();
        }

        for (var i = array.Length - 1; i >= 0; i--)
        {
            if (match(array[i]))
            {
                return array[i];
            }
        }

        return default;
    }

    public static int FindLastIndex<T>(T[] array, Predicate<T> match)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        return FindLastIndex(array, array.Length - 1, array.Length, match);
    }

    public static int FindLastIndex<T>(T[] array, int startIndex, Predicate<T> match)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        return FindLastIndex(array, startIndex, startIndex + 1, match);
    }

    public static int FindLastIndex<T>(T[] array, int startIndex, int count, Predicate<T> match)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        if (match == null)
        {
            Runtime.MatchNull();
        }

        // an empty array takes only -1, where every other start must be one of its indices
        if (array.Length == 0 ? startIndex != -1 : startIndex < 0 || startIndex >= array.Length)
        {
            Runtime.StartIndexAtEnd();
        }

        // the second half catches int.MaxValue too: MaxValue - 0 + 1 is negative
        if (count < 0 || startIndex - count + 1 < 0)
        {
            Runtime.CountOutOfRange();
        }

        var end = startIndex - count;
        for (var i = startIndex; i > end; i--)
        {
            if (match(array[i]))
            {
                return i;
            }
        }

        return -1;
    }

    public static void ForEach<T>(T[] array, Action<T> action)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        if (action == null)
        {
            Runtime.ActionNull();
        }

        for (var i = 0; i < array.Length; i++)
        {
            action(array[i]);
        }
    }

    public static bool TrueForAll<T>(T[] array, Predicate<T> match)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        if (match == null)
        {
            Runtime.MatchNull();
        }

        for (var i = 0; i < array.Length; i++)
        {
            if (!match(array[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static TOutput[] ConvertAll<TInput, TOutput>(TInput[] array, Converter<TInput, TOutput> converter)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        if (converter == null)
        {
            Runtime.ConverterNull();
        }

        var converted = new TOutput[array.Length];
        for (var i = 0; i < array.Length; i++)
        {
            converted[i] = converter(array[i]);
        }

        return converted;
    }

    public static void Sort<T>(T[] array, Comparison<T> comparison)
    {
        if (array == null)
        {
            Runtime.ArrayNull();
        }

        if (comparison == null)
        {
            Runtime.ComparisonNull();
        }

        if (array.Length > 1)
        {
            SortWith(array, 0, array.Length, 2 * (Log2((uint)array.Length) + 1), comparison);
        }
    }

    // ArraySortHelper<T>'s introsort with a Comparison, on the elements start to start + length of an array (where .NET
    // has a span of them): an array of strings, which a span cannot hold yet, sorts as one of numbers does

    private static void SortWith<T>(T[] keys, int start, int length, int depthLimit, Comparison<T> comparer)
    {
        var partitionSize = length;
        while (partitionSize > 1)
        {
            if (partitionSize <= IntrosortSizeThreshold)
            {
                if (partitionSize == 2)
                {
                    SwapIfGreaterWith(keys, comparer, start, start + 1);
                    return;
                }

                if (partitionSize == 3)
                {
                    SwapIfGreaterWith(keys, comparer, start, start + 1);
                    SwapIfGreaterWith(keys, comparer, start, start + 2);
                    SwapIfGreaterWith(keys, comparer, start + 1, start + 2);
                    return;
                }

                InsertionSortWith(keys, start, partitionSize, comparer);
                return;
            }

            if (depthLimit == 0)
            {
                HeapSortWith(keys, start, partitionSize, comparer);
                return;
            }

            depthLimit--;
            var p = PartitionWith(keys, start, partitionSize, comparer);

            // the pivot is where it belongs already
            SortWith(keys, start + p + 1, partitionSize - (p + 1), depthLimit, comparer);
            partitionSize = p;
        }
    }

    private static int PartitionWith<T>(T[] keys, int start, int length, Comparison<T> comparer)
    {
        // the median of the first, middle and last elements, moved just before the last
        var hi = length - 1;
        var middle = hi >> 1;
        SwapIfGreaterWith(keys, comparer, start, start + middle);
        SwapIfGreaterWith(keys, comparer, start, start + hi);
        SwapIfGreaterWith(keys, comparer, start + middle, start + hi);

        var pivot = keys[start + middle];
        Exchange(keys, start + middle, start + hi - 1);

        // with a comparison .NET walks without bounds: the median of three stops each walk
        int left = 0, right = hi - 1;
        while (left < right)
        {
            while (comparer(keys[start + ++left], pivot) < 0)
            {
            }

            while (comparer(pivot, keys[start + --right]) < 0)
            {
            }

            if (left >= right)
            {
                break;
            }

            Exchange(keys, start + left, start + right);
        }

        if (left != hi - 1)
        {
            Exchange(keys, start + left, start + hi - 1);
        }

        return left;
    }

    private static void HeapSortWith<T>(T[] keys, int start, int n, Comparison<T> comparer)
    {
        for (var i = n >> 1; i >= 1; i--)
        {
            DownHeapWith(keys, start, i, n, comparer);
        }

        for (var i = n; i > 1; i--)
        {
            Exchange(keys, start, start + i - 1);
            DownHeapWith(keys, start, 1, i - 1, comparer);
        }
    }

    private static void DownHeapWith<T>(T[] keys, int start, int i, int n, Comparison<T> comparer)
    {
        var d = keys[start + i - 1];
        while (i <= n >> 1)
        {
            var child = 2 * i;
            if (child < n && comparer(keys[start + child - 1], keys[start + child]) < 0)
            {
                child++;
            }

            if (!(comparer(d, keys[start + child - 1]) < 0))
            {
                break;
            }

            keys[start + i - 1] = keys[start + child - 1];
            i = child;
        }

        keys[start + i - 1] = d;
    }

    private static void InsertionSortWith<T>(T[] keys, int start, int length, Comparison<T> comparer)
    {
        for (var i = 0; i < length - 1; i++)
        {
            var t = keys[start + i + 1];
            var j = i;
            while (j >= 0 && comparer(t, keys[start + j]) < 0)
            {
                keys[start + j + 1] = keys[start + j];
                j--;
            }

            keys[start + j + 1] = t;
        }
    }

    private static void SwapIfGreaterWith<T>(T[] keys, Comparison<T> comparer, int i, int j)
    {
        if (comparer(keys[i], keys[j]) > 0)
        {
            Exchange(keys, i, j);
        }
    }

    private static void Exchange<T>(T[] keys, int i, int j)
    {
        var t = keys[i];
        keys[i] = keys[j];
        keys[j] = t;
    }
}