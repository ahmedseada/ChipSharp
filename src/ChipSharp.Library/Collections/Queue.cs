using System.Collections;

namespace ChipSharp.Library.Collections;

/// <summary>
/// ChipSharp's <see cref="System.Collections.Generic.Queue{T}"/>, .NET 10's own code (System.Collections' Queue.cs): a
/// ring of an array, its head, its tail and how many it holds, which grows to twice its size (from 4) when full;
/// enumerated from the head, as .NET's; "Queue empty." where .NET says it.
/// </summary>
[Substitute(typeof(System.Collections.Generic.Queue<>))]
public class Queue<T> : IEnumerable<T>, IReadOnlyCollection<T>
{
    private const int DefaultCapacity = 4;

    internal T[] _array;
    internal int _head;
    internal int _tail;
    internal int _size;
    internal int _version;

    public Queue() => _array = System.Array.Empty<T>();

    public Queue(int capacity)
    {
        ThrowHelper.IfNegative(capacity, nameof(capacity));
        _array = new T[capacity];
    }

    public Queue(IEnumerable<T> collection)
    {
        if (collection == null)
        {
            ThrowHelper.ArgumentNull(nameof(collection));
        }

        _array = collection is ICollection<T> c ? new T[c.Count] : System.Array.Empty<T>();
        if (collection is ICollection<T> known)
        {
            known.CopyTo(_array, 0);
            _size = known.Count;
            if (_size != _array.Length)
            {
                _tail = _size;
            }
        }
        else
        {
            foreach (var item in collection!)
            {
                Enqueue(item);
            }
        }
    }

    public int Count => _size;

    /// <summary>How many items the queue has room for before it grows.</summary>
    public int Capacity => _array.Length;

    public void Clear()
    {
        if (_size != 0)
        {
            if (_head < _tail)
            {
                System.Array.Clear(_array, _head, _size);
            }
            else
            {
                System.Array.Clear(_array, _head, _array.Length - _head);
                System.Array.Clear(_array, 0, _tail);
            }

            _size = 0;
        }

        _head = 0;
        _tail = 0;
        _version++;
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        if (array == null)
        {
            ThrowHelper.ArgumentNull(nameof(array));
        }

        if (arrayIndex < 0 || arrayIndex > array!.Length)
        {
            ThrowHelper.ArgumentOutOfRange(nameof(arrayIndex), ThrowHelper.IndexMustBeLessOrEqual);
        }

        var arrayLen = array.Length;
        if (arrayLen - arrayIndex < _size)
        {
            throw new ArgumentException(ThrowHelper.InvalidOffsetLength);
        }

        var numToCopy = _size;
        if (numToCopy == 0)
        {
            return;
        }

        var firstPart = _array.Length - _head < numToCopy ? _array.Length - _head : numToCopy;
        System.Array.Copy(_array, _head, array, arrayIndex, firstPart);
        numToCopy -= firstPart;
        if (numToCopy > 0)
        {
            System.Array.Copy(_array, 0, array, arrayIndex + _array.Length - _head, numToCopy);
        }
    }

    public void Enqueue(T item)
    {
        if (_size == _array.Length)
        {
            Grow(_size + 1);
        }

        _array[_tail] = item;
        MoveNext(ref _tail);
        _size++;
        _version++;
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new BoxedEnumerator<Enumerator, T>(new Enumerator(this));

    IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator<Enumerator, T>(new Enumerator(this));

    public T Dequeue()
    {
        var head = _head;
        var array = _array;
        if (_size == 0)
        {
            ThrowForEmptyQueue();
        }

        var removed = array[head];
        array[head] = default!;
        MoveNext(ref _head);
        _size--;
        _version++;
        return removed;
    }

    public bool TryDequeue(out T result)
    {
        var head = _head;
        var array = _array;
        if (_size == 0)
        {
            result = default!;
            return false;
        }

        result = array[head];
        array[head] = default!;
        MoveNext(ref _head);
        _size--;
        _version++;
        return true;
    }

    public T Peek()
    {
        if (_size == 0)
        {
            ThrowForEmptyQueue();
        }

        return _array[_head];
    }

    public bool TryPeek(out T result)
    {
        if (_size == 0)
        {
            result = default!;
            return false;
        }

        result = _array[_head];
        return true;
    }

    public bool Contains(T item)
    {
        if (_size == 0)
        {
            return false;
        }

        if (_head < _tail)
        {
            return IndexOf(item, _head, _size) >= 0;
        }

        return IndexOf(item, _head, _array.Length - _head) >= 0 || IndexOf(item, 0, _tail) >= 0;
    }

    /// <summary>Array.IndexOf's search, with EqualityComparer&lt;T&gt;.Default.</summary>
    private int IndexOf(T item, int index, int count)
    {
        var comparer = EqualityComparer<T>.Default;
        for (var i = index; i < index + count; i++)
        {
            if (comparer.Equals(_array[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    public T[] ToArray()
    {
        if (_size == 0)
        {
            return System.Array.Empty<T>();
        }

        var arr = new T[_size];
        if (_head < _tail)
        {
            System.Array.Copy(_array, _head, arr, 0, _size);
        }
        else
        {
            System.Array.Copy(_array, _head, arr, 0, _array.Length - _head);
            System.Array.Copy(_array, 0, arr, _array.Length - _head, _tail);
        }

        return arr;
    }

    private void SetCapacity(int capacity)
    {
        var newarray = new T[capacity];
        if (_size > 0)
        {
            if (_head < _tail)
            {
                System.Array.Copy(_array, _head, newarray, 0, _size);
            }
            else
            {
                System.Array.Copy(_array, _head, newarray, 0, _array.Length - _head);
                System.Array.Copy(_array, 0, newarray, _array.Length - _head, _tail);
            }
        }

        _array = newarray;
        _head = 0;
        _tail = _size == capacity ? 0 : _size;
        _version++;
    }

    /// <summary>Moves <paramref name="index"/> one on, round to 0 at the end of the array.</summary>
    private void MoveNext(ref int index)
    {
        var tmp = index + 1;
        if (tmp == _array.Length)
        {
            tmp = 0;
        }

        index = tmp;
    }

    private static void ThrowForEmptyQueue() => throw new InvalidOperationException("Queue empty.");

    public void TrimExcess()
    {
        var threshold = (int)(_array.Length * 0.9);
        if (_size < threshold)
        {
            SetCapacity(_size);
        }
    }

    public void TrimExcess(int capacity)
    {
        ThrowHelper.IfNegative(capacity, nameof(capacity));
        ThrowHelper.IfLess(capacity, _size, nameof(capacity));

        if (capacity == _array.Length)
        {
            return;
        }

        SetCapacity(capacity);
    }

    public int EnsureCapacity(int capacity)
    {
        ThrowHelper.IfNegative(capacity, nameof(capacity));
        if (_array.Length < capacity)
        {
            Grow(capacity);
        }

        return _array.Length;
    }

    private void Grow(int capacity)
    {
        const int GrowFactor = 2;
        const int MinimumGrow = 4;
        var newcapacity = GrowFactor * _array.Length;
        if ((uint)newcapacity > List<T>.MaxLength)
        {
            newcapacity = List<T>.MaxLength;
        }

        if (newcapacity < _array.Length + MinimumGrow)
        {
            newcapacity = _array.Length + MinimumGrow;
        }
        if (newcapacity < capacity)
        {
            newcapacity = capacity;
        }

        SetCapacity(newcapacity);
    }

    /// <summary>.NET's Queue&lt;T&gt;.Enumerator: from the head to the tail.</summary>
    [Substitute(typeof(System.Collections.Generic.Queue<>.Enumerator))]
    public struct Enumerator : IEnumerator<T>
    {
        private readonly Queue<T> _q;
        private readonly int _version;
        private int _index; // -1 = not started, -2 = ended
        private T? _currentElement;

        internal Enumerator(Queue<T> q)
        {
            _q = q;
            _version = q._version;
            _index = -1;
            _currentElement = default;
        }

        public void Dispose()
        {
            _index = -2;
            _currentElement = default;
        }

        public bool MoveNext()
        {
            if (_version != _q._version)
            {
                ThrowHelper.VersionChanged();
            }

            if (_index == -2)
            {
                return false;
            }

            _index++;
            if (_index == _q._size)
            {
                // the end
                _index = -2;
                _currentElement = default;
                return false;
            }

            // the element's place in the ring
            var array = _q._array;
            var capacity = (uint)array.Length;
            var arrayIndex = (uint)(_q._head + _index);
            if (arrayIndex >= capacity)
            {
                arrayIndex -= capacity;
            }

            _currentElement = array[arrayIndex];
            return true;
        }

        public readonly T Current => _currentElement!;

        readonly object? IEnumerator.Current
        {
            get
            {
                if (_index < 0)
                {
                    throw new InvalidOperationException(_index == -1
                        ? "Enumeration has not started. Call MoveNext."
                        : "Enumeration already finished.");
                }

                return _currentElement;
            }
        }

        void IEnumerator.Reset()
        {
            if (_version != _q._version)
            {
                ThrowHelper.VersionChanged();
            }

            _index = -1;
            _currentElement = default;
        }
    }
}
