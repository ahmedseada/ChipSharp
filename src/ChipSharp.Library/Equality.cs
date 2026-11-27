namespace ChipSharp.Library;

/// <summary>
/// How <see cref="EqualityComparer{T}"/> and <see cref="Comparer{T}"/> compare two T's, as .NET's default comparers do,
/// with no boxing. <see cref="AreEqual{T}"/> and <see cref="Compare{T}"/> are what they call; in an instance of their code
/// for a type, ChipSharp calls the one below that .NET's default comparer of that type does what it does
/// (Generics/Specialization.References.cs):
/// <list type="bullet">
/// <item>a type that implements <c>IEquatable&lt;T&gt;</c> (a number, a bool, a struct or class with its own
/// <c>Equals(T)</c>): <see cref="OfEquatable{T}"/>, which calls it, as .NET's GenericEqualityComparer does; an enum
/// compares its numbers;</item>
/// <item>any other class: <see cref="OfObject{T}"/>, its <c>Equals(object)</c> (an override, or object's own: the same
/// object), as .NET's ObjectEqualityComparer does;</item>
/// <item>a type that implements <c>IComparable&lt;T&gt;</c>: <see cref="OfComparable{T}"/>; a class that implements
/// only <c>IComparable</c>: <see cref="OfObjectComparable{T}"/>; any other, as .NET's ObjectComparer (which fails when
/// neither object implements IComparable);</item>
/// <item>the hash code (<see cref="HashOf{T}"/>) of an enum is its number's.</item>
/// </list>
/// Null is equal to null and less than any object, as in .NET. A struct without <c>IEquatable&lt;T&gt;</c> (or
/// <c>IComparable&lt;T&gt;</c>) would be boxed, as .NET boxes it: that arrives with M3.
/// </summary>
internal static class Equality
{
    public static bool AreEqual<T>(T x, T y) => OfObject(x, y);

    public static int Compare<T>(T x, T y) => OfObjects(x, y, false);

    /// <summary>
    /// <see cref="Compare{T}"/> in a sort or a binary search with the default comparer (ArraySort), which wraps what the
    /// comparer throws as .NET's ArraySortHelper does: "Failed to compare two elements in the array.", with the
    /// comparer's ArgumentException inside.
    /// </summary>
    public static int CompareInSort<T>(T x, T y) => OfObjects(x, y, true);

    public static bool OfEquatable<T>(T x, T y) where T : IEquatable<T>
    {
        if (x != null)
        {
            return y != null && x.Equals(y);
        }

        return y == null;
    }

    public static bool OfObject<T>(T x, T y)
    {
        if (x != null)
        {
            return y != null && x.Equals((object)y);
        }

        return y == null;
    }

    public static int OfComparable<T>(T x, T y) where T : IComparable<T>
    {
        if (x != null)
        {
            return y != null ? x.CompareTo(y) : 1;
        }

        return y != null ? -1 : 0;
    }

    /// <summary>A class that implements IComparable, as System.Collections.Comparer compares it: the same object is 0.</summary>
    public static int OfObjectComparable<T>(T x, T y)
    {
        if (x != null)
        {
            return y == null ? 1 : (object)x == (object)y ? 0 : ((IComparable)x).CompareTo(y);
        }

        return y != null ? -1 : 0;
    }

    /// <summary>
    /// What .NET's default comparer of a type that implements neither <c>IComparable&lt;T&gt;</c> nor <c>IComparable</c>
    /// does (ObjectComparer&lt;T&gt;, System.Collections.Comparer's Compare): the same object (or null and null) is 0,
    /// null comes first, else the first of the two that implements IComparable compares them (a derived class may), and
    /// when neither does, ArgumentException: "At least one object must implement IComparable." (in a sort, inside an
    /// InvalidOperationException).
    /// </summary>
    private static int OfObjects<T>(T x, T y, bool inSort)
    {
        if (x == null)
        {
            return y == null ? 0 : -1;
        }

        if (y == null)
        {
            return 1;
        }

        if ((object)x == (object)y)
        {
            return 0;
        }

        if (x is IComparable first)
        {
            return first.CompareTo(y);
        }

        if (y is IComparable second)
        {
            return -second.CompareTo(x);
        }

        const string Message = "At least one object must implement IComparable.";
        if (inSort)
        {
            throw new InvalidOperationException("Failed to compare two elements in the array.",
                new ArgumentException(Message));
        }

        throw new ArgumentException(Message);
    }

    /// <summary>Two strings in order as string's CompareTo has them, null first: Comparer&lt;string&gt;.Default's Compare.</summary>
    public static int CompareText(string? x, string? y) => string.Compare(x, y);

    /// <summary>The hash code of <paramref name="value"/>: its GetHashCode, 0 for null, as .NET's default comparer gives it.</summary>
    public static int HashOf<T>(T value) => value == null ? 0 : value.GetHashCode();

    /// <summary>
    /// Two nullables as .NET's NullableEqualityComparer compares them: both without a value, or both with equal values
    /// (as T's default comparer compares them). What <c>EqualityComparer&lt;int?&gt;.Default.Equals</c> is (see
    /// ChipSharp.Frontend's DefaultComparers).
    /// </summary>
    public static bool OfNullable<T>(T? x, T? y) where T : struct =>
        x.HasValue ? y.HasValue && AreEqual(x.GetValueOrDefault(), y.GetValueOrDefault()) : !y.HasValue;

    /// <summary>The hash code of a nullable: its value's, 0 without one, as Nullable's GetHashCode gives it.</summary>
    public static int HashOfNullable<T>(T? value) where T : struct => value.HasValue ? HashOf(value.GetValueOrDefault()) : 0;
}
