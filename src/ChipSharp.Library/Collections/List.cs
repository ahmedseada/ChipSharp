using System.Collections;

namespace ChipSharp.Library.Collections;

/// <summary>
/// ChipSharp's <see cref="System.Collections.Generic.List{T}"/>, .NET 10's own code (System.Private.CoreLib's List.cs):
/// an array of T and how many are used, which grows to twice its size (from 4) when full, so <see cref="Capacity"/> is
/// what .NET's is after the same calls; every check, in .NET's order, throws .NET's exception with its message; a change
/// of the elements makes the enumerators that are out stop with "Collection was modified; enumeration operation may not
/// execute." (a version number, as .NET's), and only those that .NET's makes (setting <see cref="Capacity"/> does not).
/// </summary>
/// <remarks>
/// <para>
/// <c>foreach</c> over a list uses <see cref="Enumerator"/>, a struct, as in .NET; the list as an
/// <c>IEnumerable&lt;T&gt;</c> gives a class that enumerates the same way (.NET boxes the struct; boxing arrives later).
/// <see cref="IndexOf(T)"/>, <see cref="Contains"/> and <see cref="Remove"/> compare with
/// <c>EqualityComparer&lt;T&gt;.Default</c>, and <see cref="Sort()"/> and <see cref="BinarySearch(T)"/> with
/// <c>Comparer&lt;T&gt;.Default</c> (or the comparer given), as .NET does: .NET's introsort (<see cref="ArraySort{T}"/>);
/// a list of floats or doubles without a comparer sorts as <c>Array.Sort</c> of numbers, NaNs first.
/// </para>
/// <para>
/// The methods that take a delegate (<see cref="Find"/>, <see cref="RemoveAll"/>, <see cref="ForEach"/>, <see
/// cref="Sort(Comparison{T})"/>, ...) are here too, in plain C#, and compile where delegates do. The non-generic
/// <c>IList</c> and <c>ICollection</c> (whose members take objects, so boxing), <c>AsReadOnly</c> and the overloads of
/// spans are not here yet.
/// </para>
/// </remarks>
[Substitute(typeof(System.Collections.Generic.List<>))]
public class List<T> : IList<T>, IReadOnlyList<T>
{
    private const int DefaultCapacity = 4;

    /// <summary>The most elements an array may have: .NET's Array.MaxLength.</summary>
    internal const int MaxLength = 0x7FFFFFC7;

    internal T[] _items;
    internal int _size;
    internal int _version;

    public List() => _items = System.Array.Empty<T>();

    public List(int capacity)
    {
        if (capacity < 0)
        {
            ThrowHelper.NegativeArgument(nameof(capacity));
        }

        _items = capacity == 0 ? System.Array.Empty<T>() : new T[capacity];
    }

    public List(IEnumerable<T> collection)
    {
        if (collection == null)
        {
            ThrowHelper.ArgumentNull(nameof(collection));
        }

        if (collection is ICollection<T> c)
        {
            var count = c.Count;
            if (count == 0)
            {
                _items = System.Array.Empty<T>();
            }
            else
            {
                _items = new T[count];
                c.CopyTo(_items, 0);
                _size = count;
            }
        }
        else
        {
            _items = System.Array.Empty<T>();
            using var en = collection!.GetEnumerator();
            while (en.MoveNext())
            {
                Add(en.Current);
            }
        }
    }

    public int Capacity
    {
        get => _items.Length;
        set
        {
            if (value < _size)
            {
                ThrowHelper.ArgumentOutOfRange(nameof(value), "capacity was less than the current size.");
            }

            if (value != _items.Length)
            {
                if (value > 0)
                {
                    var newItems = new T[value];
                    if (_size > 0)
                    {
                        System.Array.Copy(_items, newItems, _size);
                    }

                    _items = newItems;
                }
                else
                {
                    _items = System.Array.Empty<T>();
                }
            }
        }
    }

    public int Count => _size;

    bool ICollection<T>.IsReadOnly => false;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_size)
            {
                ThrowHelper.IndexMustBeLessArgument();
            }

            return _items[index];
        }

        set
        {
            if ((uint)index >= (uint)_size)
            {
                ThrowHelper.IndexMustBeLessArgument();
            }

            _items[index] = value;
            _version++;
        }
    }

    public void Add(T item)
    {
        _version++;
        var array = _items;
        var size = _size;
        if ((uint)size < (uint)array.Length)
        {
            _size = size + 1;
            array[size] = item;
        }
        else
        {
            AddWithResize(item);
        }
    }

    private void AddWithResize(T item)
    {
        var size = _size;
        Grow(size + 1);
        _size = size + 1;
        _items[size] = item;
    }

    public void AddRange(IEnumerable<T> collection)
    {
        if (collection == null)
        {
            ThrowHelper.ArgumentNull(nameof(collection));
        }

        if (collection is ICollection<T> c)
        {
            var count = c.Count;
            if (count > 0)
            {
                if (_items.Length - _size < count)
                {
                    Grow(_size + count);
                }

                c.CopyTo(_items, _size);
                _size += count;
                _version++;
            }
        }
        else
        {
            using var en = collection!.GetEnumerator();
            while (en.MoveNext())
            {
                Add(en.Current);
            }
        }
    }

    public int BinarySearch(int index, int count, T item, IComparer<T>? comparer)
    {
        CheckRange(index, count);
        return ArraySort<T>.BinarySearch(_items, index, count, item, comparer ?? Comparer<T>.Default);
    }

    public int BinarySearch(T item) => BinarySearch(0, Count, item, null);

    public int BinarySearch(T item, IComparer<T>? comparer) => BinarySearch(0, Count, item, comparer);

    public void Clear()
    {
        _version++;
        var size = _size;
        _size = 0;
        if (size > 0)
        {
            // the collector no longer sees what the list held
            System.Array.Clear(_items, 0, size);
        }
    }

    public bool Contains(T item) => _size != 0 && IndexOf(item) >= 0;

    public List<TOutput> ConvertAll<TOutput>(Converter<T, TOutput> converter)
    {
        if (converter == null)
        {
            ThrowHelper.ArgumentNull(nameof(converter));
        }

        var list = new List<TOutput>(_size);
        for (var i = 0; i < _size; i++)
        {
            list._items[i] = converter!(_items[i]);
        }

        list._size = _size;
        return list;
    }

    public void CopyTo(T[] array) => CopyTo(array, 0);

    public void CopyTo(int index, T[] array, int arrayIndex, int count)
    {
        if (_size - index < count)
        {
            ThrowHelper.OffsetLength();
        }

        System.Array.Copy(_items, index, array, arrayIndex, count);
    }

    public void CopyTo(T[] array, int arrayIndex) => System.Array.Copy(_items, 0, array, arrayIndex, _size);

    public int EnsureCapacity(int capacity)
    {
        if (capacity < 0)
        {
            ThrowHelper.NegativeArgument(nameof(capacity));
        }

        if (_items.Length < capacity)
        {
            Grow(capacity);
        }

        return _items.Length;
    }

    /// <summary>Makes room for at least <paramref name="capacity"/> elements: twice the size, at least 4, at most Array.MaxLength.</summary>
    internal void Grow(int capacity)
    {
        var newCapacity = _items.Length == 0 ? DefaultCapacity : 2 * _items.Length;
        if ((uint)newCapacity > MaxLength)
        {
            newCapacity = MaxLength;
        }

        if (newCapacity < capacity)
        {
            newCapacity = capacity;
        }

        Capacity = newCapacity;
    }

    /// <summary>Grows as <see cref="Grow"/> does, leaving room for <paramref name="insertionCount"/> at <paramref name="indexToInsert"/>.</summary>
    private void GrowForInsertion(int indexToInsert, int insertionCount)
    {
        var requiredCapacity = _size + insertionCount;
        var newCapacity = _items.Length == 0 ? DefaultCapacity : 2 * _items.Length;
        if ((uint)newCapacity > MaxLength)
        {
            newCapacity = MaxLength;
        }

        if (newCapacity < requiredCapacity)
        {
            newCapacity = requiredCapacity;
        }

        var newItems = new T[newCapacity];
        if (indexToInsert != 0)
        {
            System.Array.Copy(_items, newItems, indexToInsert);
        }

        if (_size != indexToInsert)
        {
            System.Array.Copy(_items, indexToInsert, newItems, indexToInsert + insertionCount, _size - indexToInsert);
        }

        _items = newItems;
    }

    public bool Exists(Predicate<T> match) => FindIndex(match) != -1;

    public T? Find(Predicate<T> match)
    {
        if (match == null)
        {
            ThrowHelper.ArgumentNull(nameof(match));
        }

        for (var i = 0; i < _size; i++)
        {
            if (match!(_items[i]))
            {
                return _items[i];
            }
        }

        return default;
    }

    public List<T> FindAll(Predicate<T> match)
    {
        if (match == null)
        {
            ThrowHelper.ArgumentNull(nameof(match));
        }

        var list = new List<T>();
        for (var i = 0; i < _size; i++)
        {
            if (match!(_items[i]))
            {
                list.Add(_items[i]);
            }
        }

        return list;
    }

    public int FindIndex(Predicate<T> match) => FindIndex(0, _size, match);

    public int FindIndex(int startIndex, Predicate<T> match) => FindIndex(startIndex, _size - startIndex, match);

    public int FindIndex(int startIndex, int count, Predicate<T> match)
    {
        if ((uint)startIndex > (uint)_size)
        {
            ThrowHelper.ArgumentOutOfRange(nameof(startIndex), IndexMustBeLessOrEqualText);
        }

        if (count < 0 || startIndex > _size - count)
        {
            ThrowHelper.ArgumentOutOfRange(nameof(count), CountText);
        }

        if (match == null)
        {
            ThrowHelper.ArgumentNull(nameof(match));
        }

        var endIndex = startIndex + count;
        for (var i = startIndex; i < endIndex; i++)
        {
            if (match!(_items[i]))
            {
                return i;
            }
        }

        return -1;
    }

    public T? FindLast(Predicate<T> match)
    {
        if (match == null)
        {
            ThrowHelper.ArgumentNull(nameof(match));
        }

        for (var i = _size - 1; i >= 0; i--)
        {
            if (match!(_items[i]))
            {
                return _items[i];
            }
        }

        return default;
    }

    public int FindLastIndex(Predicate<T> match) => FindLastIndex(_size - 1, _size, match);

    public int FindLastIndex(int startIndex, Predicate<T> match) => FindLastIndex(startIndex, startIndex + 1, match);

    public int FindLastIndex(int startIndex, int count, Predicate<T> match)
    {
        if (match == null)
        {
            ThrowHelper.ArgumentNull(nameof(match));
        }

        if (_size == 0)
        {
            if (startIndex != -1)
            {
                ThrowHelper.ArgumentOutOfRange(nameof(startIndex), IndexMustBeLessText);
            }
        }
        else if ((uint)startIndex >= (uint)_size)
        {
            ThrowHelper.ArgumentOutOfRange(nameof(startIndex), IndexMustBeLessText);
        }

        if (count < 0 || startIndex - count + 1 < 0)
        {
            ThrowHelper.ArgumentOutOfRange(nameof(count), CountText);
        }

        var endIndex = startIndex - count;
        for (var i = startIndex; i > endIndex; i--)
        {
            if (match!(_items[i]))
            {
                return i;
            }
        }

        return -1;
    }

    public void ForEach(Action<T> action)
    {
        if (action == null)
        {
            ThrowHelper.ArgumentNull(nameof(action));
        }

        var version = _version;
        for (var i = 0; i < _size; i++)
        {
            if (version != _version)
            {
                break;
            }

            action!(_items[i]);
        }

        if (version != _version)
        {
            ThrowHelper.VersionChanged();
        }
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new ObjectEnumerator(this);

    IEnumerator IEnumerable.GetEnumerator() => new ObjectEnumerator(this);

    public List<T> GetRange(int index, int count)
    {
        CheckRange(index, count);
        var list = new List<T>(count);
        System.Array.Copy(_items, index, list._items, 0, count);
        list._size = count;
        return list;
    }

    public List<T> Slice(int start, int length) => GetRange(start, length);

    public int IndexOf(T item) => IndexOfRange(item, 0, _size);

    public int IndexOf(T item, int index)
    {
        if (index > _size)
        {
            ThrowHelper.IndexMustBeLessOrEqualArgument();
        }

        return IndexOfRange(item, index, _size - index);
    }

    public int IndexOf(T item, int index, int count)
    {
        if (index > _size)
        {
            ThrowHelper.IndexMustBeLessOrEqualArgument();
        }

        if (count < 0 || index > _size - count)
        {
            ThrowHelper.ArgumentOutOfRange(nameof(count), CountText);
        }

        return IndexOfRange(item, index, count);
    }

    /// <summary>
    /// Array.IndexOf's search of <paramref name="count"/> elements from <paramref name="index"/> (a negative index is
    /// Array.IndexOf's to reject: "startIndex"), with EqualityComparer&lt;T&gt;.Default.
    /// </summary>
    private int IndexOfRange(T item, int index, int count)
    {
        if (index < 0)
        {
            ThrowHelper.ArgumentOutOfRange("startIndex", IndexMustBeLessOrEqualText);
        }

        var comparer = EqualityComparer<T>.Default;
        var end = index + count;
        for (var i = index; i < end; i++)
        {
            if (comparer.Equals(_items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)_size)
        {
            ThrowHelper.ArgumentOutOfRange(nameof(index), "Index must be within the bounds of the List.");
        }

        if (_size == _items.Length)
        {
            GrowForInsertion(index, 1);
        }
        else if (index < _size)
        {
            System.Array.Copy(_items, index, _items, index + 1, _size - index);
        }

        _items[index] = item;
        _size++;
        _version++;
    }

    public void InsertRange(int index, IEnumerable<T> collection)
    {
        if (collection == null)
        {
            ThrowHelper.ArgumentNull(nameof(collection));
        }

        if ((uint)index > (uint)_size)
        {
            ThrowHelper.IndexMustBeLessOrEqualArgument();
        }

        if (collection is ICollection<T> c)
        {
            var count = c.Count;
            if (count > 0)
            {
                if (_items.Length - _size < count)
                {
                    GrowForInsertion(index, count);
                }
                else if (index < _size)
                {
                    System.Array.Copy(_items, index, _items, index + count, _size - index);
                }

                // inserting the list into itself: its two halves, now apart, into the room made
                if ((object)this == c)
                {
                    System.Array.Copy(_items, 0, _items, index, index);
                    System.Array.Copy(_items, index + count, _items, index * 2, _size - index);
                }
                else
                {
                    c.CopyTo(_items, index);
                }

                _size += count;
                _version++;
            }
        }
        else
        {
            using var en = collection!.GetEnumerator();
            while (en.MoveNext())
            {
                Insert(index++, en.Current);
            }
        }
    }

    public int LastIndexOf(T item) => _size == 0 ? -1 : LastIndexOf(item, _size - 1, _size);

    public int LastIndexOf(T item, int index)
    {
        if (index >= _size)
        {
            ThrowHelper.IndexMustBeLessArgument();
        }

        return LastIndexOf(item, index, index + 1);
    }

    public int LastIndexOf(T item, int index, int count)
    {
        if (Count != 0 && index < 0)
        {
            ThrowHelper.NegativeArgument(nameof(index));
        }

        if (Count != 0 && count < 0)
        {
            ThrowHelper.NegativeArgument(nameof(count));
        }

        if (_size == 0)
        {
            return -1;
        }

        if (index >= _size)
        {
            ThrowHelper.ArgumentOutOfRange(nameof(index), BiggerThanCollectionText);
        }

        if (count > index + 1)
        {
            ThrowHelper.ArgumentOutOfRange(nameof(count), BiggerThanCollectionText);
        }

        var comparer = EqualityComparer<T>.Default;
        var end = index - count;
        for (var i = index; i > end; i--)
        {
            if (comparer.Equals(_items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    public bool Remove(T item)
    {
        var index = IndexOf(item);
        if (index >= 0)
        {
            RemoveAt(index);
            return true;
        }

        return false;
    }

    public int RemoveAll(Predicate<T> match)
    {
        if (match == null)
        {
            ThrowHelper.ArgumentNull(nameof(match));
        }

        // the first element to remove
        var freeIndex = 0;
        while (freeIndex < _size && !match!(_items[freeIndex]))
        {
            freeIndex++;
        }

        if (freeIndex >= _size)
        {
            return 0;
        }

        var current = freeIndex + 1;
        while (current < _size)
        {
            // the next element to keep
            while (current < _size && match!(_items[current]))
            {
                current++;
            }

            if (current < _size)
            {
                _items[freeIndex++] = _items[current++];
            }
        }

        System.Array.Clear(_items, freeIndex, _size - freeIndex);
        var result = _size - freeIndex;
        _size = freeIndex;
        _version++;
        return result;
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_size)
        {
            ThrowHelper.IndexMustBeLessArgument();
        }

        _size--;
        if (index < _size)
        {
            System.Array.Copy(_items, index + 1, _items, index, _size - index);
        }

        _items[_size] = default!;
        _version++;
    }

    public void RemoveRange(int index, int count)
    {
        CheckRange(index, count);
        if (count > 0)
        {
            _size -= count;
            if (index < _size)
            {
                System.Array.Copy(_items, index + count, _items, index, _size - index);
            }

            _version++;
            System.Array.Clear(_items, _size, count);
        }
    }

    public void Reverse() => Reverse(0, Count);

    public void Reverse(int index, int count)
    {
        CheckRange(index, count);
        if (count > 1)
        {
            System.Array.Reverse(_items, index, count);
        }

        _version++;
    }

    public void Sort() => Sort(0, Count, null);

    public void Sort(IComparer<T>? comparer) => Sort(0, Count, comparer);

    public void Sort(int index, int count, IComparer<T>? comparer)
    {
        CheckRange(index, count);
        if (count > 1)
        {
            ArraySort<T>.Sort(_items, index, count, comparer);
        }

        _version++;
    }

    public void Sort(Comparison<T> comparison)
    {
        if (comparison == null)
        {
            ThrowHelper.ArgumentNull(nameof(comparison));
        }

        if (_size > 1)
        {
            ArraySort<T>.Sort(_items, 0, _size, comparison!);
        }

        _version++;
    }

    public T[] ToArray()
    {
        if (_size == 0)
        {
            return System.Array.Empty<T>();
        }

        var array = new T[_size];
        System.Array.Copy(_items, array, _size);
        return array;
    }

    public void TrimExcess()
    {
        var threshold = (int)(_items.Length * 0.9);
        if (_size < threshold)
        {
            Capacity = _size;
        }
    }

    public bool TrueForAll(Predicate<T> match)
    {
        if (match == null)
        {
            ThrowHelper.ArgumentNull(nameof(match));
        }

        for (var i = 0; i < _size; i++)
        {
            if (!match!(_items[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The checks of an index and a count within the list: neither negative, together within it.</summary>
    private void CheckRange(int index, int count)
    {
        if (index < 0)
        {
            ThrowHelper.NegativeArgument(nameof(index));
        }

        if (count < 0)
        {
            ThrowHelper.NegativeArgument(nameof(count));
        }

        if (_size - index < count)
        {
            ThrowHelper.OffsetLength();
        }
    }

    private const string IndexMustBeLessText = ThrowHelper.IndexMustBeLess;
    private const string IndexMustBeLessOrEqualText = ThrowHelper.IndexMustBeLessOrEqual;
    private const string CountText =
        "Count must be positive and count must refer to a location within the string/array/collection.";
    private const string BiggerThanCollectionText = "Larger than collection size.";

    /// <summary>.NET's List&lt;T&gt;.Enumerator: the list, where it is, and the list's version when it started.</summary>
    [Substitute(typeof(System.Collections.Generic.List<>.Enumerator))]
    public struct Enumerator : IEnumerator<T>
    {
        private readonly List<T> _list;
        private readonly int _version;
        private int _index;
        private T? _current;

        internal Enumerator(List<T> list)
        {
            _list = list;
            _index = 0;
            _version = list._version;
            _current = default;
        }

        public readonly T Current => _current!;

        readonly object? IEnumerator.Current
        {
            get
            {
                if (_index == 0 || _index == _list._size + 1)
                {
                    ThrowHelper.EnumerationCannotHappen();
                }

                return Current;
            }
        }

        public readonly void Dispose()
        {
        }

        public bool MoveNext()
        {
            var localList = _list;
            if (_version == localList._version && (uint)_index < (uint)localList._size)
            {
                _current = localList._items[_index];
                _index++;
                return true;
            }

            return MoveNextRare();
        }

        private bool MoveNextRare()
        {
            if (_version != _list._version)
            {
                ThrowHelper.VersionChanged();
            }

            _index = _list._size + 1;
            _current = default;
            return false;
        }

        void IEnumerator.Reset()
        {
            if (_version != _list._version)
            {
                ThrowHelper.VersionChanged();
            }

            _index = 0;
            _current = default;
        }
    }

    /// <summary>
    /// The enumerator of the list as an <c>IEnumerable&lt;T&gt;</c>: <see cref="Enumerator"/>'s code, in a class, for what
    /// .NET's boxed struct is.
    /// </summary>
    private sealed class ObjectEnumerator : IEnumerator<T>
    {
        private readonly List<T> _list;
        private readonly int _version;
        private int _index;
        private T? _current;

        public ObjectEnumerator(List<T> list)
        {
            _list = list;
            _version = list._version;
        }

        public T Current => _current!;

        object? IEnumerator.Current
        {
            get
            {
                if (_index == 0 || _index == _list._size + 1)
                {
                    ThrowHelper.EnumerationCannotHappen();
                }

                return Current;
            }
        }

        public void Dispose()
        {
        }

        public bool MoveNext()
        {
            var localList = _list;
            if (_version == localList._version && (uint)_index < (uint)localList._size)
            {
                _current = localList._items[_index];
                _index++;
                return true;
            }

            if (_version != _list._version)
            {
                ThrowHelper.VersionChanged();
            }

            _index = _list._size + 1;
            _current = default;
            return false;
        }

        public void Reset()
        {
            if (_version != _list._version)
            {
                ThrowHelper.VersionChanged();
            }

            _index = 0;
            _current = default;
        }
    }
}
