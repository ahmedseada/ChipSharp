namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.MemoryExtensions"/> for spans of values: searching and comparing elements, and
/// reversing them, with .NET 10's results. An element is compared with its <c>Equals(T)</c>, as .NET compares the
/// elements of a type it cannot compare bit by bit: a float NaN is equal to NaN, and -0.0 to 0.0 (see
/// <see cref="Numbers"/>); a struct with its own Equals.
/// </summary>
/// <remarks>
/// The array's <c>AsSpan</c> and Span's own members are ChipSharp's (see ChipSharp.Frontend's Spans); C# calls the
/// ReadOnlySpan overloads below for a Span too, after making it a ReadOnlySpan. The overloads that take an
/// IEqualityComparer, and those of char and text, are not here yet. These are plain static methods, not extension
/// methods, so that this library's own C# never calls them in place of .NET's.
/// </remarks>
[Substitute(typeof(System.MemoryExtensions))]
internal static class MemoryExtensions
{
    public static int IndexOf<T>(Span<T> span, T value) where T : IEquatable<T>? => IndexOf((ReadOnlySpan<T>)span, value);

    /// <summary>The index of the first element equal to <paramref name="value"/>, or -1.</summary>
    public static int IndexOf<T>(ReadOnlySpan<T> span, T value) where T : IEquatable<T>?
    {
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i]!.Equals(value))
            {
                return i;
            }
        }

        return -1;
    }

    public static int IndexOf<T>(Span<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>? =>
        IndexOf((ReadOnlySpan<T>)span, value);

    /// <summary>Where the elements of <paramref name="value"/> first come one after another, or -1; 0 for none.</summary>
    public static int IndexOf<T>(ReadOnlySpan<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>?
    {
        for (var i = 0; i <= span.Length - value.Length; i++)
        {
            if (SequenceEqual(span.Slice(i, value.Length), value))
            {
                return i;
            }
        }

        return -1;
    }

    public static int LastIndexOf<T>(Span<T> span, T value) where T : IEquatable<T>? =>
        LastIndexOf((ReadOnlySpan<T>)span, value);

    /// <summary>The index of the last element equal to <paramref name="value"/>, or -1.</summary>
    public static int LastIndexOf<T>(ReadOnlySpan<T> span, T value) where T : IEquatable<T>?
    {
        for (var i = span.Length - 1; i >= 0; i--)
        {
            if (span[i]!.Equals(value))
            {
                return i;
            }
        }

        return -1;
    }

    public static int LastIndexOf<T>(Span<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>? =>
        LastIndexOf((ReadOnlySpan<T>)span, value);

    /// <summary>
    /// Where the elements of <paramref name="value"/> last come one after another, or -1; for none, the span's length
    /// (.NET finds nothing at the end).
    /// </summary>
    public static int LastIndexOf<T>(ReadOnlySpan<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>?
    {
        if (value.Length == 0)
        {
            return span.Length;
        }

        for (var i = span.Length - value.Length; i >= 0; i--)
        {
            if (SequenceEqual(span.Slice(i, value.Length), value))
            {
                return i;
            }
        }

        return -1;
    }

    public static bool Contains<T>(Span<T> span, T value) where T : IEquatable<T>? =>
        IndexOf((ReadOnlySpan<T>)span, value) >= 0;

    public static bool Contains<T>(ReadOnlySpan<T> span, T value) where T : IEquatable<T>? => IndexOf(span, value) >= 0;

    public static bool SequenceEqual<T>(Span<T> span, ReadOnlySpan<T> other) where T : IEquatable<T>? =>
        SequenceEqual((ReadOnlySpan<T>)span, other);

    /// <summary>True when the two have as many elements, each equal to the other's.</summary>
    public static bool SequenceEqual<T>(ReadOnlySpan<T> span, ReadOnlySpan<T> other) where T : IEquatable<T>?
    {
        if (span.Length != other.Length)
        {
            return false;
        }

        for (var i = 0; i < span.Length; i++)
        {
            if (!span[i]!.Equals(other[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool StartsWith<T>(Span<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>? =>
        StartsWith((ReadOnlySpan<T>)span, value);

    public static bool StartsWith<T>(ReadOnlySpan<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>? =>
        value.Length <= span.Length && SequenceEqual(span.Slice(0, value.Length), value);

    public static bool StartsWith<T>(ReadOnlySpan<T> span, T value) where T : IEquatable<T>? =>
        span.Length != 0 && span[0]!.Equals(value);

    public static bool EndsWith<T>(Span<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>? =>
        EndsWith((ReadOnlySpan<T>)span, value);

    public static bool EndsWith<T>(ReadOnlySpan<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>? =>
        value.Length <= span.Length && SequenceEqual(span.Slice(span.Length - value.Length), value);

    public static bool EndsWith<T>(ReadOnlySpan<T> span, T value) where T : IEquatable<T>? =>
        span.Length != 0 && span[span.Length - 1]!.Equals(value);

    /// <summary>Reverses the order of the elements, in place.</summary>
    public static void Reverse<T>(Span<T> span)
    {
        for (int i = 0, j = span.Length - 1; i < j; i++, j--)
        {
            (span[i], span[j]) = (span[j], span[i]);
        }
    }
}
