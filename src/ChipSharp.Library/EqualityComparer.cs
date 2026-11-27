namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.Collections.Generic.EqualityComparer{T}"/>: <see cref="Default"/>, the comparer
/// collections compare their elements and keys with, which gives .NET's default comparer's results for every T, with
/// no boxing (see <see cref="Equality"/>): a number, a bool, an enum and a struct or class that implements
/// <c>IEquatable&lt;T&gt;</c> through its <c>Equals(T)</c>, any other class through its <c>Equals(object)</c>; null
/// equals only null. It is an object, as .NET's is: it needs a heap, and it is an
/// <see cref="IEqualityComparer{T}"/>, which a collection may keep and call.
/// </summary>
/// <remarks>
/// The hash code of a value is its GetHashCode (a number's, a struct's own), of an object its GetHashCode (an override,
/// or object's own, which differs from .NET's numbers but stays the same for the object), of null 0. A class derived from
/// EqualityComparer, <c>Create</c> and the overloads of object are not here yet.
/// </remarks>
[Substitute(typeof(System.Collections.Generic.EqualityComparer<>))]
internal sealed class EqualityComparer<T> : IEqualityComparer<T>
{
    private EqualityComparer()
    {
    }

    public static EqualityComparer<T> Default { get; } = new();

    public bool Equals(T? x, T? y) => Equality.AreEqual(x, y);

    public int GetHashCode(T obj) => Equality.HashOf(obj);
}
