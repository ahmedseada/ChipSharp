using System.Collections;

namespace ChipSharp.Library.Collections;

/// <summary>
/// A collection's struct enumerator as an object: what <c>IEnumerable&lt;T&gt;.GetEnumerator()</c> of a Dictionary,
/// HashSet, Queue or Stack gives, where .NET boxes the struct (boxing arrives later). It holds the struct and calls it,
/// so it enumerates exactly as the struct does, and as .NET's boxed one does.
/// </summary>
internal sealed class BoxedEnumerator<TEnumerator, T> : IEnumerator<T>
    where TEnumerator : struct, IEnumerator<T>
{
    private TEnumerator _enumerator;

    public BoxedEnumerator(TEnumerator enumerator) => _enumerator = enumerator;

    public T Current => _enumerator.Current;

    object? IEnumerator.Current => CurrentOf(ref _enumerator);

    public bool MoveNext() => _enumerator.MoveNext();

    public void Dispose() => _enumerator.Dispose();

    void IEnumerator.Reset() => ResetOf(ref _enumerator);

    private static object? CurrentOf<TE>(ref TE enumerator) where TE : IEnumerator => enumerator.Current;

    private static void ResetOf<TE>(ref TE enumerator) where TE : IEnumerator => enumerator.Reset();
}
