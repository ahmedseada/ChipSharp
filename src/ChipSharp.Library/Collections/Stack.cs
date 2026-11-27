using System.Collections;

namespace ChipSharp.Library.Collections;

/// <summary>
/// ChipSharp's <see cref="System.Collections.Generic.Stack{T}"/>, .NET 10's own code (System.Collections' Stack.cs): an
/// array and how many it holds, which grows to twice its size (from 4) when full; enumerated and copied from the top,
/// as .NET's; "Stack empty." where .NET says it.
/// </summary>
[Substitute(typeof(System.Collections.Generic.Stack<>))]
public class Stack<T> : IEnumerable<T>, IReadOnlyCollection<T>
{
    private const int DefaultCapacity = 4;

    /// <summary>Stack's own words for an enumerator whose stack changed.</summary>
    private const string EnumFailedVersion = "Collection was modified after the enumerator was instantiated.";

    internal T[] _array;
    internal int _size;
    internal int _version;

    public Stack() => _array = System.Array.Empty<T>();

    public Stack(int capacity)
    {
        ThrowHelper.IfNegative(capacity, nameof(capacity));
        _array = new T[capacity];
    }

    public Stack(IEnumerable<T> collection)
    {
        if (collection == null)
        {
            ThrowHelper.ArgumentNull(nameof(collection));
        }

        if (collection is ICollection<T> c)
        {
            _array = c.Count == 0 ? System.Array.Empty<T>() : new T[c.Count];
            c.CopyTo(_array, 0);
            _size = c.Count;
        }
        else
        {
            _array = System.Array.Empty<T>();
            foreach (var item in collection!)
            {
                Push(item);
            }
        }
    }

    public int Count => _size;

    /// <summary>How many items the stack has room for before it grows.</summary>
    public int Capacity => _array.Length;

    public void Clear()
    {
        System.Array.Clear(_array, 0, _size);
        _size = 0;
        _version++;
    }

    public bool Contains(T item)
    {
        var comparer = EqualityComparer<T>.Default;
        for (var i = _size - 1; i >= 0; i--)
        {
            if (comparer.Equals(_array[i], item))
            {
                return true;
            }
        }

        return false;
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        if (array == null)
        {
            ThrowHelper.ArgumentNull(nameof(array));
        }

        if (arrayIndex < 0 || arrayIndex > array!.Length)
        {
            var text = arrayIndex.ToString();
            throw new ArgumentOutOfRangeException(nameof(arrayIndex), text, ThrowHelper.IndexMustBeLessOrEqual);
        }

        if (array.Length - arrayIndex < _size)
        {
            throw new ArgumentException(ThrowHelper.InvalidOffsetLength);
        }

        // the top first
        var srcIndex = 0;
        var dstIndex = arrayIndex + _size;
        while (srcIndex < _size)
        {
            array[--dstIndex] = _array[srcIndex++];
        }
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new BoxedEnumerator<Enumerator, T>(new Enumerator(this));

    IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator<Enumerator, T>(new Enumerator(this));

    public void TrimExcess()
    {
        var threshold = (int)(_array.Length * 0.9);
        if (_size < threshold)
        {
            System.Array.Resize(ref _array, _size);
            _version++;
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

        System.Array.Resize(ref _array, capacity);
        _version++;
    }

    public T Peek()
    {
        var size = _size - 1;
        var array = _array;
        if ((uint)size >= (uint)array.Length)
        {
            ThrowForEmptyStack();
        }

        return array[size];
    }

    public bool TryPeek(out T result)
    {
        var size = _size - 1;
        var array = _array;
        if ((uint)size >= (uint)array.Length)
        {
            result = default!;
            return false;
        }

        result = array[size];
        return true;
    }

    public T Pop()
    {
        var size = _size - 1;
        var array = _array;
        if ((uint)size >= (uint)array.Length)
        {
            ThrowForEmptyStack();
        }

        _version++;
        _size = size;
        var item = array[size];
        array[size] = default!;
        return item;
    }

    public bool TryPop(out T result)
    {
        var size = _size - 1;
        var array = _array;
        if ((uint)size >= (uint)array.Length)
        {
            result = default!;
            return false;
        }

        _version++;
        _size = size;
        result = array[size];
        array[size] = default!;
        return true;
    }

    public void Push(T item)
    {
        var size = _size;
        var array = _array;
        if ((uint)size < (uint)array.Length)
        {
            array[size] = item;
            _version++;
            _size = size + 1;
        }
        else
        {
            PushWithResize(item);
        }
    }

    private void PushWithResize(T item)
    {
        Grow(_size + 1);
        _array[_size] = item;
        _version++;
        _size++;
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
        var newcapacity = _array.Length == 0 ? DefaultCapacity : 2 * _array.Length;
        if ((uint)newcapacity > List<T>.MaxLength)
        {
            newcapacity = List<T>.MaxLength;
        }

        if (newcapacity < capacity)
        {
            newcapacity = capacity;
        }

        System.Array.Resize(ref _array, newcapacity);
    }

    public T[] ToArray()
    {
        if (_size == 0)
        {
            return System.Array.Empty<T>();
        }

        var objArray = new T[_size];
        var i = 0;
        while (i < _size)
        {
            objArray[i] = _array[_size - i - 1];
            i++;
        }

        return objArray;
    }

    private static void ThrowForEmptyStack() => throw new InvalidOperationException("Stack empty.");

    /// <summary>.NET's Stack&lt;T&gt;.Enumerator: from the top to the bottom.</summary>
    [Substitute(typeof(System.Collections.Generic.Stack<>.Enumerator))]
    public struct Enumerator : IEnumerator<T>
    {
        private readonly Stack<T> _stack;
        private readonly int _version;
        private int _index; // -2 = not started, -1 = ended
        private T? _currentElement;

        internal Enumerator(Stack<T> stack)
        {
            _stack = stack;
            _version = stack._version;
            _index = -2;
            _currentElement = default;
        }

        public void Dispose() => _index = -1;

        public bool MoveNext()
        {
            bool retval;
            if (_version != _stack._version)
            {
                throw new InvalidOperationException(EnumFailedVersion);
            }

            if (_index == -2)
            {
                // the first call
                _index = _stack._size - 1;
                retval = _index >= 0;
                if (retval)
                {
                    _currentElement = _stack._array[_index];
                }

                return retval;
            }

            if (_index == -1)
            {
                // the end, already
                return false;
            }

            retval = --_index >= 0;
            _currentElement = retval ? _stack._array[_index] : default;
            return retval;
        }

        public readonly T Current => _currentElement!;

        readonly object? IEnumerator.Current
        {
            get
            {
                if (_index < 0)
                {
                    throw new InvalidOperationException(_index == -2
                        ? "Enumeration has not started. Call MoveNext."
                        : "Enumeration already finished.");
                }

                return _currentElement;
            }
        }

        void IEnumerator.Reset()
        {
            if (_version != _stack._version)
            {
                throw new InvalidOperationException(EnumFailedVersion);
            }

            _index = -2;
            _currentElement = default;
        }
    }
}
