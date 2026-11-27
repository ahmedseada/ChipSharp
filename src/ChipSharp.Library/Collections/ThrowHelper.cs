using System.Diagnostics.CodeAnalysis;

namespace ChipSharp.Library.Collections;

/// <summary>
/// The exceptions the collections throw, with .NET 10's messages (System.Private.CoreLib's ThrowHelper and SR): .NET's own
/// exception types, which ChipSharp links to its substitutes (see Exceptions), so that on .NET the collections throw what
/// .NET's throw, and the tests compare them.
/// </summary>
internal static class ThrowHelper
{
    public const string EnumFailedVersion = "Collection was modified; enumeration operation may not execute.";
    public const string EnumOpCantHappen = "Enumeration has either not started or has already finished.";
    public const string NeedNonNegative = "Non-negative number required.";
    public const string InvalidOffsetLength = "Offset and length were out of bounds for the array or count is greater " +
        "than the number of elements from index to the end of the source collection.";
    public const string IndexMustBeLess =
        "Index was out of range. Must be non-negative and less than the size of the collection.";
    public const string IndexMustBeLessOrEqual =
        "Index was out of range. Must be non-negative and less than or equal to the size of the collection.";

    [DoesNotReturn]
    public static void ArgumentNull(string paramName) => throw new ArgumentNullException(paramName);

    [DoesNotReturn]
    public static void ArgumentOutOfRange(string paramName, string message) =>
        throw new ArgumentOutOfRangeException(paramName, message);

    [DoesNotReturn]
    public static void NegativeArgument(string paramName) => ArgumentOutOfRange(paramName, NeedNonNegative);

    /// <summary>ArgumentOutOfRangeException.ThrowIfNegative's: the value in the message and on a line of its own.</summary>
    public static void IfNegative(int value, string paramName)
    {
        if (value < 0)
        {
            var text = value.ToString();
            throw new ArgumentOutOfRangeException(paramName, text,
                paramName + " ('" + text + "') must be a non-negative value.");
        }
    }

    /// <summary>ArgumentOutOfRangeException.ThrowIfLessThan's.</summary>
    public static void IfLess(int value, int other, string paramName)
    {
        if (value < other)
        {
            var text = value.ToString();
            throw new ArgumentOutOfRangeException(paramName, text,
                paramName + " ('" + text + "') must be greater than or equal to '" + other.ToString() + "'.");
        }
    }

    [DoesNotReturn]
    public static void IndexMustBeLessArgument() => ArgumentOutOfRange("index", IndexMustBeLess);

    [DoesNotReturn]
    public static void IndexMustBeLessOrEqualArgument() => ArgumentOutOfRange("index", IndexMustBeLessOrEqual);

    [DoesNotReturn]
    public static void OffsetLength() => throw new ArgumentException(InvalidOffsetLength);

    [DoesNotReturn]
    public static void VersionChanged() => throw new InvalidOperationException(EnumFailedVersion);

    [DoesNotReturn]
    public static void EnumerationCannotHappen() => throw new InvalidOperationException(EnumOpCantHappen);

    /// <summary>Dictionary's indexer: "The given key 'x' was not present in the dictionary."</summary>
    [DoesNotReturn]
    public static void KeyNotFound<TKey>(TKey key) =>
        throw new KeyNotFoundException("The given key '" + Text(key) + "' was not present in the dictionary.");

    /// <summary>Dictionary's Add: "An item with the same key has already been added. Key: x".</summary>
    [DoesNotReturn]
    public static void DuplicateKey<TKey>(TKey key) =>
        throw new ArgumentException("An item with the same key has already been added. Key: " + Text(key));

    /// <summary>A value as string.Format writes it: its ToString, nothing for null.</summary>
    public static string Text<T>(T value) => value == null ? "" : value.ToString() ?? "";
}

/// <summary>
/// .NET's HashHelpers: the sizes of a Dictionary's and a HashSet's tables, primes, which grow to the next prime at least
/// twice as big. The tables are what the collections' capacities are (EnsureCapacity gives them).
/// </summary>
internal static class HashHelpers
{
    /// <summary>The largest prime below Array.MaxLength.</summary>
    public const int MaxPrimeArrayLength = 0x7FFFFFC3;

    public const int HashPrime = 101;

    private static readonly int[] Primes =
    [
        3, 7, 11, 17, 23, 29, 37, 47, 59, 71, 89, 107, 131, 163, 197, 239, 293, 353, 431, 521, 631, 761, 919,
        1103, 1327, 1597, 1931, 2333, 2801, 3371, 4049, 4861, 5839, 7013, 8419, 10103, 12143, 14591,
        17519, 21023, 25229, 30293, 36353, 43627, 52361, 62851, 75431, 90523, 108631, 130363, 156437,
        187751, 225307, 270371, 324449, 389357, 467237, 560689, 672827, 807403, 968897, 1162687, 1395263,
        1674319, 2009191, 2411033, 2893249, 3471899, 4166287, 4999559, 5999471, 7199369,
    ];

    public static bool IsPrime(int candidate)
    {
        if ((candidate & 1) != 0)
        {
            var limit = (int)Math.Sqrt(candidate);
            for (var divisor = 3; divisor <= limit; divisor += 2)
            {
                if (candidate % divisor == 0)
                {
                    return false;
                }
            }

            return true;
        }

        return candidate == 2;
    }

    /// <summary>The smallest of .NET's primes at least <paramref name="min"/>.</summary>
    public static int GetPrime(int min)
    {
        foreach (var prime in Primes)
        {
            if (prime >= min)
            {
                return prime;
            }
        }

        // outside the table: the next prime that is not 1 more than a multiple of HashPrime
        for (var i = min | 1; i < int.MaxValue; i += 2)
        {
            if (IsPrime(i) && (i - 1) % HashPrime != 0)
            {
                return i;
            }
        }

        return min;
    }

    /// <summary>The size a table of <paramref name="oldSize"/> grows to: the prime after twice its size.</summary>
    public static int ExpandPrime(int oldSize)
    {
        var newSize = 2 * oldSize;
        if ((uint)newSize > MaxPrimeArrayLength && MaxPrimeArrayLength > oldSize)
        {
            return MaxPrimeArrayLength;
        }

        return GetPrime(newSize);
    }
}
