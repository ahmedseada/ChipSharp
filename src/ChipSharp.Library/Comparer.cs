namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.Collections.Generic.Comparer{T}"/>: <see cref="Default"/>, the comparer sorting and
/// sorted collections order their elements with, which gives .NET's default comparer's results (see
/// <see cref="Equality"/>): a number, a bool, an enum and a type that implements <c>IComparable&lt;T&gt;</c> through its
/// <c>CompareTo(T)</c>, a class that implements only <c>IComparable</c> through <c>CompareTo(object)</c>; null comes
/// before every object. Like .NET's it is an object, and an <see cref="IComparer{T}"/>.
/// </summary>
/// <remarks>A class derived from Comparer, <c>Create</c> and <c>Compare(object, object)</c> are not here yet.</remarks>
[Substitute(typeof(System.Collections.Generic.Comparer<>))]
internal sealed class Comparer<T> : IComparer<T>
{
    private Comparer()
    {
    }

    public static Comparer<T> Default { get; } = new();

    public int Compare(T? x, T? y) => Equality.Compare(x, y);
}
