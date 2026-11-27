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
/// only <c>IComparable</c>: <see cref="OfObjectComparable{T}"/>;</item>
/// <item>the hash code (<see cref="HashOf{T}"/>) of an enum is its number's.</item>
/// </list>
/// Null is equal to null and less than any object, as in .NET. A struct without <c>IEquatable&lt;T&gt;</c> (or
/// <c>IComparable&lt;T&gt;</c>) would be boxed, as .NET boxes it: that arrives with M3.
/// </summary>
internal static class Equality
{
    public static bool AreEqual<T>(T x, T y) => OfObject(x, y);

    public static int Compare<T>(T x, T y) => OfObjectComparable(x, y);

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

    public static int OfObjectComparable<T>(T x, T y)
    {
        if (x != null)
        {
            return y != null ? ((IComparable)x).CompareTo(y) : 1;
        }

        return y != null ? -1 : 0;
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
