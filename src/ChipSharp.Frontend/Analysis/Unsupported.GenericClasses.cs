namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// What of generics over classes and the groundwork of collections (see <see cref="Generics.ReferenceArguments"/> and
/// <see cref="Generics.GenericSubstitutes"/>) does not compile yet, in C# words.
/// </summary>
internal sealed partial record Unsupported
{
    /// <summary>The <see cref="Arrival"/> of the collections and what they need: try/finally around an enumerator.</summary>
    private const string WithCollections = "with M3 (collections)";

    /// <summary>
    /// <c>foreach</c> over a collection whose enumerator must be disposed: one that implements IDisposable (every
    /// <c>IEnumerator&lt;T&gt;</c> does, so a foreach over an <c>IEnumerable&lt;T&gt;</c> too), which C# disposes in a
    /// try/finally.
    /// </summary>
    /// <summary>
    /// <c>EqualityComparer&lt;T&gt;.Default</c> or <c>Comparer&lt;T&gt;.Default</c> (a library member that gives an
    /// object), as a heap use: objects need a heap.
    /// </summary>
    public static readonly Unsupported DefaultComparers = HeapUse(Heap(
        "the default comparers EqualityComparer<T>.Default and Comparer<T>.Default (objects)", true,
        "For now, compare with Equals and CompareTo, through a constraint where the type is a T: " +
        "static bool Same<T>(T a, T b) where T : IEquatable<T> => a.Equals(b);"));

    /// <summary>
    /// An array used as an interface (<c>IEnumerable&lt;int&gt; values = numbers;</c>), a heap use: the array is an object
    /// (see <see cref="ArrayInterfaces"/>).
    /// </summary>
    public static readonly Unsupported ArraysAsInterfaces = HeapUse(Heap(
        "arrays used as interfaces (an array as an IEnumerable<T>, ICollection<T>, IList<T> or IReadOnlyList<T>)", true,
        "For now, take the array itself (T[]) or a span (ReadOnlySpan<T>)."));

    public static readonly Unsupported DisposedEnumerators = Coming(
        "foreach over an enumerator that is disposed (one that implements IDisposable, as IEnumerator<T> does, and so a " +
        "foreach over an IEnumerable<T>), which C# does in a try/finally", false,
        "For now, give the collection a GetEnumerator() that returns a struct (or a sealed class) with MoveNext() and " +
        "Current and no Dispose, and loop over the collection itself.", WithCollections);
}
