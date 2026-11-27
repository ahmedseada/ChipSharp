using System.Collections;

namespace ChipSharp.Library.Collections;

/// <summary>
/// ChipSharp's <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>, .NET 10's own code (System.Private.
/// CoreLib's Dictionary.cs): buckets of indexes into an array of entries, in the order they were added; a removed entry
/// goes on a list of free ones, which the next added ones take, the last freed first. So a dictionary enumerates its
/// pairs in .NET's order whatever the keys' hash codes are (a string's is FNV-1a here, .NET's differs from run to run),
/// after removals too, and grows as .NET's does, to the prime after twice its size (3, 7, 17, 37, ...). Keys compare
/// with the comparer given, else <c>EqualityComparer&lt;TKey&gt;.Default</c>; every check throws .NET's exception with
/// its message ("The given key 'x' was not present in the dictionary.", "An item with the same key has already been
/// added. Key: x"); adding a key while an enumerator is out makes it stop ("Collection was modified; ..."), setting the
/// value of a key that is there, Remove and Clear do not, as in .NET.
/// </summary>
/// <remarks>
/// The non-generic IDictionary and ICollection (objects, so boxing) and the serialization members are not here.
/// </remarks>
[Substitute(typeof(System.Collections.Generic.Dictionary<,>))]
public class Dictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    private const int StartOfFreeList = -3;
    private const string ArrayPlusOffTooSmall =
        "Destination array is not long enough to copy all the items in the collection. Check array index and length.";

    internal int[]? _buckets;
    internal Entry[]? _entries;
    internal int _count;
    internal int _freeList;
    internal int _freeCount;
    internal int _version;
    private readonly IEqualityComparer<TKey> _comparer;
    private KeyCollection? _keys;
    private ValueCollection? _values;

    public Dictionary()
        : this(0, null)
    {
    }

    public Dictionary(int capacity)
        : this(capacity, null)
    {
    }

    public Dictionary(IEqualityComparer<TKey>? comparer)
        : this(0, comparer)
    {
    }

    public Dictionary(int capacity, IEqualityComparer<TKey>? comparer)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (capacity > 0)
        {
            Initialize(capacity);
        }

        _comparer = comparer ?? EqualityComparer<TKey>.Default;
    }

    public Dictionary(IDictionary<TKey, TValue> dictionary)
        : this(dictionary, null)
    {
    }

    public Dictionary(IDictionary<TKey, TValue> dictionary, IEqualityComparer<TKey>? comparer)
        : this(dictionary?.Count ?? 0, comparer)
    {
        if (dictionary == null)
        {
            ThrowHelper.ArgumentNull(nameof(dictionary));
        }

        AddRange(dictionary!);
    }

    public Dictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection)
        : this(collection, null)
    {
    }

    public Dictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey>? comparer)
        : this((collection as ICollection<KeyValuePair<TKey, TValue>>)?.Count ?? 0, comparer)
    {
        if (collection == null)
        {
            ThrowHelper.ArgumentNull(nameof(collection));
        }

        AddRange(collection!);
    }

    private void AddRange(IEnumerable<KeyValuePair<TKey, TValue>> enumerable)
    {
        foreach (var pair in enumerable)
        {
            Add(pair.Key, pair.Value);
        }
    }

    public IEqualityComparer<TKey> Comparer => _comparer;

    public int Count => _count - _freeCount;

    /// <summary>How many entries the dictionary has room for before it grows.</summary>
    public int Capacity => _entries?.Length ?? 0;

    public KeyCollection Keys => _keys ??= new KeyCollection(this);

    ICollection<TKey> IDictionary<TKey, TValue>.Keys => Keys;

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

    public ValueCollection Values => _values ??= new ValueCollection(this);

    ICollection<TValue> IDictionary<TKey, TValue>.Values => Values;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

    public TValue this[TKey key]
    {
        get
        {
            var i = FindEntry(key);
            if (i >= 0)
            {
                return _entries![i].value;
            }

            ThrowHelper.KeyNotFound(key);
            return default!;
        }

        set => TryInsert(key, value, InsertionBehavior.OverwriteExisting);
    }

    public void Add(TKey key, TValue value) => TryInsert(key, value, InsertionBehavior.ThrowOnExisting);

    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> keyValuePair) =>
        Add(keyValuePair.Key, keyValuePair.Value);

    bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> keyValuePair)
    {
        var i = FindEntry(keyValuePair.Key);
        return i >= 0 && EqualityComparer<TValue>.Default.Equals(_entries![i].value, keyValuePair.Value);
    }

    bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> keyValuePair)
    {
        var i = FindEntry(keyValuePair.Key);
        if (i >= 0 && EqualityComparer<TValue>.Default.Equals(_entries![i].value, keyValuePair.Value))
        {
            Remove(keyValuePair.Key);
            return true;
        }

        return false;
    }

    public void Clear()
    {
        var count = _count;
        if (count > 0)
        {
            System.Array.Clear(_buckets!);
            _count = 0;
            _freeList = -1;
            _freeCount = 0;
            System.Array.Clear(_entries!, 0, count);
        }
    }

    public bool ContainsKey(TKey key) => FindEntry(key) >= 0;

    public bool ContainsValue(TValue value)
    {
        var entries = _entries;
        var comparer = EqualityComparer<TValue>.Default;
        for (var i = 0; i < _count; i++)
        {
            if (entries![i].next >= -1 && comparer.Equals(entries[i].value, value))
            {
                return true;
            }
        }

        return false;
    }

    private void CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
    {
        if (array == null)
        {
            ThrowHelper.ArgumentNull(nameof(array));
        }

        if ((uint)index > (uint)array!.Length)
        {
            ThrowHelper.NegativeArgument(nameof(index));
        }

        if (array.Length - index < Count)
        {
            throw new ArgumentException(ArrayPlusOffTooSmall);
        }

        var count = _count;
        var entries = _entries;
        for (var i = 0; i < count; i++)
        {
            if (entries![i].next >= -1)
            {
                array[index++] = new KeyValuePair<TKey, TValue>(entries[i].key, entries[i].value);
            }
        }
    }

    void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int index) =>
        CopyTo(array, index);

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() =>
        new BoxedEnumerator<Enumerator, KeyValuePair<TKey, TValue>>(new Enumerator(this));

    IEnumerator IEnumerable.GetEnumerator() =>
        new BoxedEnumerator<Enumerator, KeyValuePair<TKey, TValue>>(new Enumerator(this));

    /// <summary>The index of the entry of <paramref name="key"/>, or -1.</summary>
    private int FindEntry(TKey key)
    {
        if (key == null)
        {
            ThrowHelper.ArgumentNull(nameof(key));
        }

        var buckets = _buckets;
        if (buckets == null)
        {
            return -1;
        }

        var entries = _entries!;
        var comparer = _comparer;
        var hashCode = (uint)comparer.GetHashCode(key!);
        var i = buckets[hashCode % (uint)buckets.Length] - 1; // the buckets count from 1
        var collisionCount = 0u;
        while ((uint)i < (uint)entries.Length)
        {
            if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key!))
            {
                return i;
            }

            i = entries[i].next;
            collisionCount++;
            if (collisionCount > (uint)entries.Length)
            {
                ConcurrentOperations();
            }
        }

        return -1;
    }

    private int Initialize(int capacity)
    {
        var size = HashHelpers.GetPrime(capacity);
        _freeList = -1;
        _buckets = new int[size];
        _entries = new Entry[size];
        return size;
    }

    private bool TryInsert(TKey key, TValue value, InsertionBehavior behavior)
    {
        if (key == null)
        {
            ThrowHelper.ArgumentNull(nameof(key));
        }

        if (_buckets == null)
        {
            Initialize(0);
        }

        var entries = _entries!;
        var comparer = _comparer;
        var hashCode = (uint)comparer.GetHashCode(key!);
        var collisionCount = 0u;
        var bucket = (int)(hashCode % (uint)_buckets!.Length);
        var i = _buckets[bucket] - 1; // the buckets count from 1
        while ((uint)i < (uint)entries.Length)
        {
            if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key!))
            {
                if (behavior == InsertionBehavior.OverwriteExisting)
                {
                    entries[i].value = value;
                    return true;
                }

                if (behavior == InsertionBehavior.ThrowOnExisting)
                {
                    ThrowHelper.DuplicateKey(key!);
                }

                return false;
            }

            i = entries[i].next;
            collisionCount++;
            if (collisionCount > (uint)entries.Length)
            {
                ConcurrentOperations();
            }
        }

        int index;
        if (_freeCount > 0)
        {
            index = _freeList;
            _freeList = StartOfFreeList - entries[_freeList].next;
            _freeCount--;
        }
        else
        {
            var count = _count;
            if (count == entries.Length)
            {
                Resize(HashHelpers.ExpandPrime(_count));
                bucket = (int)(hashCode % (uint)_buckets.Length);
            }

            index = count;
            _count = count + 1;
            entries = _entries!;
        }

        entries[index].hashCode = hashCode;
        entries[index].next = _buckets[bucket] - 1;
        entries[index].key = key!;
        entries[index].value = value;
        _buckets[bucket] = index + 1;
        _version++;
        return true;
    }

    private void Resize(int newSize)
    {
        var entries = new Entry[newSize];
        var count = _count;
        System.Array.Copy(_entries!, entries, count);
        var buckets = new int[newSize];
        for (var i = 0; i < count; i++)
        {
            if (entries[i].next >= -1)
            {
                var bucket = (int)(entries[i].hashCode % (uint)newSize);
                entries[i].next = buckets[bucket] - 1;
                buckets[bucket] = i + 1;
            }
        }

        _buckets = buckets;
        _entries = entries;
    }

    public bool Remove(TKey key) => Remove(key, out _);

    public bool Remove(TKey key, out TValue value)
    {
        if (key == null)
        {
            ThrowHelper.ArgumentNull(nameof(key));
        }

        if (_buckets != null)
        {
            var entries = _entries!;
            var comparer = _comparer;
            var collisionCount = 0u;
            var hashCode = (uint)comparer.GetHashCode(key!);
            var bucket = (int)(hashCode % (uint)_buckets.Length);
            var last = -1;
            var i = _buckets[bucket] - 1; // the buckets count from 1
            while (i >= 0)
            {
                if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key!))
                {
                    if (last < 0)
                    {
                        _buckets[bucket] = entries[i].next + 1;
                    }
                    else
                    {
                        entries[last].next = entries[i].next;
                    }

                    value = entries[i].value;
                    entries[i].next = StartOfFreeList - _freeList;
                    entries[i].key = default!;
                    entries[i].value = default!;
                    _freeList = i;
                    _freeCount++;
                    return true;
                }

                last = i;
                i = entries[i].next;
                collisionCount++;
                if (collisionCount > (uint)entries.Length)
                {
                    ConcurrentOperations();
                }
            }
        }

        value = default!;
        return false;
    }

    public bool TryAdd(TKey key, TValue value) => TryInsert(key, value, InsertionBehavior.None);

    public bool TryGetValue(TKey key, out TValue value)
    {
        var i = FindEntry(key);
        if (i >= 0)
        {
            value = _entries![i].value;
            return true;
        }

        value = default!;
        return false;
    }

    public int EnsureCapacity(int capacity)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        var currentCapacity = _entries == null ? 0 : _entries.Length;
        if (currentCapacity >= capacity)
        {
            return currentCapacity;
        }

        _version++;
        if (_buckets == null)
        {
            return Initialize(capacity);
        }

        var newSize = HashHelpers.GetPrime(capacity);
        Resize(newSize);
        return newSize;
    }

    public void TrimExcess() => TrimExcess(Count);

    public void TrimExcess(int capacity)
    {
        if (capacity < Count)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        var newSize = HashHelpers.GetPrime(capacity);
        var oldEntries = _entries;
        var currentCapacity = oldEntries == null ? 0 : oldEntries.Length;
        if (newSize >= currentCapacity)
        {
            return;
        }

        var oldCount = _count;
        _version++;
        Initialize(newSize);
        CopyEntries(oldEntries!, oldCount);
    }

    /// <summary>The entries in use of <paramref name="entries"/>, in order, into the new ones, with no free one between.</summary>
    private void CopyEntries(Entry[] entries, int count)
    {
        var newEntries = _entries!;
        var buckets = _buckets!;
        var newCount = 0;
        for (var i = 0; i < count; i++)
        {
            var hashCode = entries[i].hashCode;
            if (entries[i].next >= -1)
            {
                newEntries[newCount] = entries[i];
                var bucket = (int)(hashCode % (uint)buckets.Length);
                newEntries[newCount].next = buckets[bucket] - 1;
                buckets[bucket] = newCount + 1;
                newCount++;
            }
        }

        _count = newCount;
        _freeCount = 0;
    }

    private static void ConcurrentOperations() => throw new InvalidOperationException(
        "Operations that change non-concurrent collections must have exclusive access. A concurrent update was " +
        "performed on this collection and corrupted its state. The collection's state is no longer correct.");


    internal struct Entry
    {
        public uint hashCode;

        /// <summary>The next entry of the bucket (0-based), -1 for none; below -1 a free entry, -3 - the next free one.</summary>
        public int next;
        public TKey key;
        public TValue value;
    }

    /// <summary>.NET's Dictionary&lt;TKey, TValue&gt;.Enumerator: the pairs in the order of the entries.</summary>
    [Substitute(typeof(System.Collections.Generic.Dictionary<,>.Enumerator))]
    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private readonly Dictionary<TKey, TValue> _dictionary;
        private readonly int _version;
        private int _index;
        private KeyValuePair<TKey, TValue> _current;

        internal Enumerator(Dictionary<TKey, TValue> dictionary)
        {
            _dictionary = dictionary;
            _version = dictionary._version;
            _index = 0;
            _current = default;
        }

        public readonly KeyValuePair<TKey, TValue> Current => _current;

        readonly object IEnumerator.Current
        {
            get
            {
                if (_index == 0 || _index == _dictionary._count + 1)
                {
                    ThrowHelper.EnumerationCannotHappen();
                }

                return _current;
            }
        }

        public bool MoveNext()
        {
            if (_version != _dictionary._version)
            {
                ThrowHelper.VersionChanged();
            }

            // the index is 1 past the entry Current is
            while ((uint)_index < (uint)_dictionary._count)
            {
                var entries = _dictionary._entries!;
                var i = _index++;
                if (entries[i].next >= -1)
                {
                    _current = new KeyValuePair<TKey, TValue>(entries[i].key, entries[i].value);
                    return true;
                }
            }

            _index = _dictionary._count + 1;
            _current = default;
            return false;
        }

        void IEnumerator.Reset()
        {
            if (_version != _dictionary._version)
            {
                ThrowHelper.VersionChanged();
            }

            _index = 0;
            _current = default;
        }

        public readonly void Dispose()
        {
        }
    }

    /// <summary>.NET's KeyCollection: the keys, in the order of the entries.</summary>
    [Substitute(typeof(System.Collections.Generic.Dictionary<,>.KeyCollection))]
    public sealed class KeyCollection : ICollection<TKey>, IReadOnlyCollection<TKey>
    {
        private readonly Dictionary<TKey, TValue> _dictionary;

        public KeyCollection(Dictionary<TKey, TValue> dictionary)
        {
            if (dictionary == null)
            {
                ThrowHelper.ArgumentNull(nameof(dictionary));
            }

            _dictionary = dictionary!;
        }

        public int Count => _dictionary.Count;

        bool ICollection<TKey>.IsReadOnly => true;

        public Enumerator GetEnumerator() => new(_dictionary);

        IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => new BoxedEnumerator<Enumerator, TKey>(new Enumerator(_dictionary));

        IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator<Enumerator, TKey>(new Enumerator(_dictionary));

        public void CopyTo(TKey[] array, int index)
        {
            if (array == null)
            {
                ThrowHelper.ArgumentNull(nameof(array));
            }

            if (index < 0 || index > array!.Length)
            {
                ThrowHelper.NegativeArgument(nameof(index));
            }

            if (array.Length - index < _dictionary.Count)
            {
                throw new ArgumentException(ArrayPlusOffTooSmall);
            }

            var count = _dictionary._count;
            var entries = _dictionary._entries;
            for (var i = 0; i < count; i++)
            {
                if (entries![i].next >= -1)
                {
                    array[index++] = entries[i].key;
                }
            }
        }

        public bool Contains(TKey item) => _dictionary.ContainsKey(item);

        void ICollection<TKey>.Add(TKey item) => throw new NotSupportedException(KeyMutation);

        void ICollection<TKey>.Clear() => throw new NotSupportedException(KeyMutation);

        bool ICollection<TKey>.Remove(TKey item) => throw new NotSupportedException(KeyMutation);

        private const string KeyMutation = "Mutating a key collection derived from a dictionary is not allowed.";

        [Substitute(typeof(System.Collections.Generic.Dictionary<,>.KeyCollection.Enumerator))]
        public struct Enumerator : IEnumerator<TKey>
        {
            private readonly Dictionary<TKey, TValue> _dictionary;
            private readonly int _version;
            private int _index;
            private TKey? _currentKey;

            internal Enumerator(Dictionary<TKey, TValue> dictionary)
            {
                _dictionary = dictionary;
                _version = dictionary._version;
                _index = 0;
                _currentKey = default;
            }

            public readonly TKey Current => _currentKey!;

            readonly object? IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || _index == _dictionary._count + 1)
                    {
                        ThrowHelper.EnumerationCannotHappen();
                    }

                    return _currentKey;
                }
            }

            public readonly void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (_version != _dictionary._version)
                {
                    ThrowHelper.VersionChanged();
                }

                while ((uint)_index < (uint)_dictionary._count)
                {
                    var entries = _dictionary._entries!;
                    var i = _index++;
                    if (entries[i].next >= -1)
                    {
                        _currentKey = entries[i].key;
                        return true;
                    }
                }

                _index = _dictionary._count + 1;
                _currentKey = default;
                return false;
            }

            void IEnumerator.Reset()
            {
                if (_version != _dictionary._version)
                {
                    ThrowHelper.VersionChanged();
                }

                _index = 0;
                _currentKey = default;
            }
        }
    }

    /// <summary>.NET's ValueCollection: the values, in the order of the entries.</summary>
    [Substitute(typeof(System.Collections.Generic.Dictionary<,>.ValueCollection))]
    public sealed class ValueCollection : ICollection<TValue>, IReadOnlyCollection<TValue>
    {
        private readonly Dictionary<TKey, TValue> _dictionary;

        public ValueCollection(Dictionary<TKey, TValue> dictionary)
        {
            if (dictionary == null)
            {
                ThrowHelper.ArgumentNull(nameof(dictionary));
            }

            _dictionary = dictionary!;
        }

        public int Count => _dictionary.Count;

        bool ICollection<TValue>.IsReadOnly => true;

        public Enumerator GetEnumerator() => new(_dictionary);

        IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() =>
            new BoxedEnumerator<Enumerator, TValue>(new Enumerator(_dictionary));

        IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator<Enumerator, TValue>(new Enumerator(_dictionary));

        public void CopyTo(TValue[] array, int index)
        {
            if (array == null)
            {
                ThrowHelper.ArgumentNull(nameof(array));
            }

            if ((uint)index > array!.Length)
            {
                ThrowHelper.NegativeArgument(nameof(index));
            }

            if (array.Length - index < _dictionary.Count)
            {
                throw new ArgumentException(ArrayPlusOffTooSmall);
            }

            var count = _dictionary._count;
            var entries = _dictionary._entries;
            for (var i = 0; i < count; i++)
            {
                if (entries![i].next >= -1)
                {
                    array[index++] = entries[i].value;
                }
            }
        }

        bool ICollection<TValue>.Contains(TValue item) => _dictionary.ContainsValue(item);

        void ICollection<TValue>.Add(TValue item) => throw new NotSupportedException(ValueMutation);

        void ICollection<TValue>.Clear() => throw new NotSupportedException(ValueMutation);

        bool ICollection<TValue>.Remove(TValue item) => throw new NotSupportedException(ValueMutation);

        private const string ValueMutation = "Mutating a value collection derived from a dictionary is not allowed.";

        [Substitute(typeof(System.Collections.Generic.Dictionary<,>.ValueCollection.Enumerator))]
        public struct Enumerator : IEnumerator<TValue>
        {
            private readonly Dictionary<TKey, TValue> _dictionary;
            private readonly int _version;
            private int _index;
            private TValue? _currentValue;

            internal Enumerator(Dictionary<TKey, TValue> dictionary)
            {
                _dictionary = dictionary;
                _version = dictionary._version;
                _index = 0;
                _currentValue = default;
            }

            public readonly TValue Current => _currentValue!;

            readonly object? IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || _index == _dictionary._count + 1)
                    {
                        ThrowHelper.EnumerationCannotHappen();
                    }

                    return _currentValue;
                }
            }

            public readonly void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (_version != _dictionary._version)
                {
                    ThrowHelper.VersionChanged();
                }

                while ((uint)_index < (uint)_dictionary._count)
                {
                    var entries = _dictionary._entries!;
                    var i = _index++;
                    if (entries[i].next >= -1)
                    {
                        _currentValue = entries[i].value;
                        return true;
                    }
                }

                _index = _dictionary._count + 1;
                _currentValue = default;
                return false;
            }

            void IEnumerator.Reset()
            {
                if (_version != _dictionary._version)
                {
                    ThrowHelper.VersionChanged();
                }

                _index = 0;
                _currentValue = default;
            }
        }
    }
}

/// <summary>What Dictionary's TryInsert does with a key that is there already.</summary>
internal enum InsertionBehavior : byte
{
    None = 0,
    OverwriteExisting = 1,
    ThrowOnExisting = 2,
}

/// <summary>ChipSharp's <see cref="System.Collections.Generic.CollectionExtensions"/>: GetValueOrDefault, TryAdd and Remove of a dictionary.</summary>
[Substitute(typeof(System.Collections.Generic.CollectionExtensions))]
public static class CollectionExtensions
{
    public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key) =>
        dictionary.GetValueOrDefault(key, default!);

    public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key,
        TValue defaultValue)
    {
        if (dictionary == null)
        {
            ThrowHelper.ArgumentNull(nameof(dictionary));
        }

        return dictionary!.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public static bool TryAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue value)
    {
        if (dictionary == null)
        {
            ThrowHelper.ArgumentNull(nameof(dictionary));
        }

        if (!dictionary!.ContainsKey(key))
        {
            dictionary.Add(key, value);
            return true;
        }

        return false;
    }

    public static bool Remove<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, out TValue value)
    {
        if (dictionary == null)
        {
            ThrowHelper.ArgumentNull(nameof(dictionary));
        }

        if (dictionary!.TryGetValue(key, out value!))
        {
            dictionary.Remove(key);
            return true;
        }

        value = default!;
        return false;
    }
}
