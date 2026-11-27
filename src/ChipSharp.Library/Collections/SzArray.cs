using System.Collections;

namespace ChipSharp.Library.Collections;

/// <summary>
/// An array as the interfaces of collections, .NET's SZArrayHelper&lt;T&gt;: a <c>T[]</c> is an <c>IList&lt;T&gt;</c>,
/// <c>ICollection&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c> and
/// <c>IReadOnlyCollection&lt;T&gt;</c>. No object of this class is ever made: ChipSharp gives an array type whose arrays
/// the program uses as objects the interface map this class has, so a call through one of these interfaces on an array
/// runs one of these methods with the array as <c>this</c> (ChipSharp.Frontend's ArrayInterfaces). Each method has the
/// array back with a cast, which an array always passes, and does what .NET's does: a fixed size, so Add, Insert, Remove
/// and RemoveAt throw "Collection was of a fixed size.", and Clear "Collection is read-only."; the indexer checks its
/// index as a list's does (ArgumentOutOfRangeException); the enumerator is .NET's SZGenericArrayEnumerator.
/// </summary>
internal sealed class SzArray<T> : IList<T>, IReadOnlyList<T>
{
    private const string FixedSize = "Collection was of a fixed size.";

    private SzArray()
    {
    }

    /// <summary>The array this is.</summary>
    private T[] Items => (T[])(object)this;

    public int Count => Items.Length;

    public bool IsReadOnly => true;

    public T this[int index]
    {
        get
        {
            var items = Items;
            if ((uint)index >= (uint)items.Length)
            {
                ThrowHelper.IndexMustBeLessArgument();
            }

            return items[index];
        }

        set
        {
            var items = Items;
            if ((uint)index >= (uint)items.Length)
            {
                ThrowHelper.IndexMustBeLessArgument();
            }

            items[index] = value;
        }
    }

    public IEnumerator<T> GetEnumerator() => new ArrayEnumerator<T>(Items);

    IEnumerator IEnumerable.GetEnumerator() => new ArrayEnumerator<T>(Items);

    public void CopyTo(T[] array, int arrayIndex)
    {
        var items = Items;
        System.Array.Copy(items, 0, array, arrayIndex, items.Length);
    }

    public bool Contains(T item) => IndexOf(item) >= 0;

    public int IndexOf(T item)
    {
        var items = Items;
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < items.Length; i++)
        {
            if (comparer.Equals(items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    public void Add(T item) => throw new NotSupportedException(FixedSize);

    public void Clear() => throw new NotSupportedException("Collection is read-only.");

    public void Insert(int index, T item) => throw new NotSupportedException(FixedSize);

    public bool Remove(T item) => throw new NotSupportedException(FixedSize);

    public void RemoveAt(int index) => throw new NotSupportedException(FixedSize);
}

/// <summary>.NET's SZGenericArrayEnumerator&lt;T&gt;: an array's elements, in order, as an <c>IEnumerator&lt;T&gt;</c>.</summary>
internal sealed class ArrayEnumerator<T> : IEnumerator<T>
{
    private readonly T[] _array;
    private int _index;

    public ArrayEnumerator(T[] array)
    {
        _array = array;
        _index = -1;
    }

    public bool MoveNext()
    {
        var index = _index + 1;
        if ((uint)index < (uint)_array.Length)
        {
            _index = index;
            return true;
        }

        _index = _array.Length;
        return false;
    }

    public T Current
    {
        get
        {
            var index = _index;
            if ((uint)index >= (uint)_array.Length)
            {
                throw new InvalidOperationException(index < 0
                    ? "Enumeration has not started. Call MoveNext."
                    : "Enumeration already finished.");
            }

            return _array[index];
        }
    }

    object? IEnumerator.Current => Current;

    public void Reset() => _index = -1;

    public void Dispose()
    {
    }
}
