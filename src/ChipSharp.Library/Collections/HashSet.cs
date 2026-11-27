using System.Collections;

namespace ChipSharp.Library.Collections;

/// <summary>
/// ChipSharp's <see cref="System.Collections.Generic.HashSet{T}"/>, .NET 10's own code (System.Private.CoreLib's
/// HashSet.cs), laid out as <see cref="Dictionary{TKey, TValue}"/> is: buckets of indexes into the entries, in the order
/// they were added, removed ones reused, the last freed first. So it enumerates in .NET's order whatever the hash codes
/// are, and grows as .NET's (3, 7, 17, 37, ...). Items compare with the comparer given, else
/// <c>EqualityComparer&lt;T&gt;.Default</c>; null is an item like any other.
/// </summary>
[Substitute(typeof(System.Collections.Generic.HashSet<>))]
public class HashSet<T> : ISet<T>, IReadOnlySet<T>, IReadOnlyCollection<T>
{
    private const int StartOfFreeList = -3;

    internal int[]? _buckets;
    internal Entry[]? _entries;
    internal int _count;
    internal int _freeList;
    internal int _freeCount;
    internal int _version;
    private readonly IEqualityComparer<T> _comparer;

    public HashSet()
        : this((IEqualityComparer<T>?)null)
    {
    }

    public HashSet(IEqualityComparer<T>? comparer) => _comparer = comparer ?? EqualityComparer<T>.Default;

    public HashSet(int capacity)
        : this(capacity, null)
    {
    }

    public HashSet(IEnumerable<T> collection)
        : this(collection, null)
    {
    }

    public HashSet(IEnumerable<T> collection, IEqualityComparer<T>? comparer)
        : this(comparer)
    {
        if (collection == null)
        {
            ThrowHelper.ArgumentNull(nameof(collection));
        }

        if (collection is HashSet<T> otherAsHashSet && EqualityComparersAreEqual(this, otherAsHashSet))
        {
            ConstructFrom(otherAsHashSet);
        }
        else
        {
            // a collection's count is how many there are at most
            if (collection is ICollection<T> coll)
            {
                var count = coll.Count;
                if (count > 0)
                {
                    Initialize(count);
                }
            }

            UnionWith(collection!);
            if (_count > 0 && _entries!.Length / _count > ShrinkThreshold)
            {
                TrimExcess();
            }
        }
    }

    public HashSet(int capacity, IEqualityComparer<T>? comparer)
        : this(comparer)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (capacity > 0)
        {
            Initialize(capacity);
        }
    }

    private const int ShrinkThreshold = 3;

    /// <summary>A copy of a set with the same comparer: its entries, in order, with no free one between.</summary>
    private void ConstructFrom(HashSet<T> source)
    {
        if (source.Count == 0)
        {
            return;
        }

        var capacity = source._buckets!.Length;
        var threshold = HashHelpers.ExpandPrime(source.Count + 1);
        if (threshold >= capacity)
        {
            _buckets = (int[])source._buckets.Clone();
            _entries = (Entry[])source._entries!.Clone();
            _freeList = source._freeList;
            _freeCount = source._freeCount;
            _count = source._count;
        }
        else
        {
            Initialize(source.Count);
            var entries = source._entries!;
            for (var i = 0; i < source._count; i++)
            {
                if (entries[i].Next >= -1)
                {
                    AddIfNotPresent(entries[i].Value, out _);
                }
            }
        }
    }

    public IEqualityComparer<T> Comparer => _comparer;

    public int Count => _count - _freeCount;

    /// <summary>How many items the set has room for before it grows.</summary>
    public int Capacity => _entries?.Length ?? 0;

    bool ICollection<T>.IsReadOnly => false;

    void ICollection<T>.Add(T item) => AddIfNotPresent(item, out _);

    public bool Add(T item) => AddIfNotPresent(item, out _);

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

    public bool Contains(T item) => FindItemIndex(item) >= 0;

    /// <summary>The index of the entry of <paramref name="item"/>, or -1.</summary>
    private int FindItemIndex(T item)
    {
        var buckets = _buckets;
        if (buckets == null)
        {
            return -1;
        }

        var entries = _entries!;
        var comparer = _comparer;
        var hashCode = item == null ? 0 : comparer.GetHashCode(item);
        var i = buckets[(uint)hashCode % (uint)buckets.Length] - 1; // the buckets count from 1
        var collisionCount = 0u;
        while (i >= 0)
        {
            if (entries[i].HashCode == hashCode && comparer.Equals(entries[i].Value, item))
            {
                return i;
            }

            i = entries[i].Next;
            collisionCount++;
            if (collisionCount > (uint)entries.Length)
            {
                ConcurrentOperations();
            }
        }

        return -1;
    }

    public bool Remove(T item)
    {
        if (_buckets != null)
        {
            var entries = _entries!;
            var collisionCount = 0u;
            var last = -1;
            var comparer = _comparer;
            var hashCode = item == null ? 0 : comparer.GetHashCode(item);
            var bucket = (int)((uint)hashCode % (uint)_buckets.Length);
            var i = _buckets[bucket] - 1; // the buckets count from 1
            while (i >= 0)
            {
                if (entries[i].HashCode == hashCode && comparer.Equals(entries[i].Value, item))
                {
                    if (last < 0)
                    {
                        _buckets[bucket] = entries[i].Next + 1;
                    }
                    else
                    {
                        entries[last].Next = entries[i].Next;
                    }

                    entries[i].Next = StartOfFreeList - _freeList;
                    entries[i].Value = default!;
                    _freeList = i;
                    _freeCount++;
                    return true;
                }

                last = i;
                i = entries[i].Next;
                collisionCount++;
                if (collisionCount > (uint)entries.Length)
                {
                    ConcurrentOperations();
                }
            }
        }

        return false;
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new BoxedEnumerator<Enumerator, T>(new Enumerator(this));

    IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator<Enumerator, T>(new Enumerator(this));

    public void UnionWith(IEnumerable<T> other)
    {
        if (other == null)
        {
            ThrowHelper.ArgumentNull(nameof(other));
        }

        foreach (var item in other!)
        {
            AddIfNotPresent(item, out _);
        }
    }

    public void IntersectWith(IEnumerable<T> other)
    {
        if (other == null)
        {
            ThrowHelper.ArgumentNull(nameof(other));
        }

        // an empty set, or the set itself, stays as it is
        if (Count == 0 || (object)other! == this)
        {
            return;
        }

        if (other is ICollection<T> otherAsCollection)
        {
            if (otherAsCollection.Count == 0)
            {
                Clear();
                return;
            }

            if (other is HashSet<T> otherAsSet && EqualityComparersAreEqual(this, otherAsSet))
            {
                IntersectWithHashSetWithSameComparer(otherAsSet);
                return;
            }
        }

        IntersectWithEnumerable(other);
    }

    public void ExceptWith(IEnumerable<T> other)
    {
        if (other == null)
        {
            ThrowHelper.ArgumentNull(nameof(other));
        }

        if (Count == 0)
        {
            return;
        }

        if ((object)other! == this)
        {
            Clear();
            return;
        }

        foreach (var element in other)
        {
            Remove(element);
        }
    }

    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        if (other == null)
        {
            ThrowHelper.ArgumentNull(nameof(other));
        }

        if (Count == 0)
        {
            UnionWith(other!);
            return;
        }

        if ((object)other! == this)
        {
            Clear();
            return;
        }

        if (other is HashSet<T> otherAsSet && EqualityComparersAreEqual(this, otherAsSet))
        {
            foreach (var item in otherAsSet)
            {
                if (!Remove(item))
                {
                    AddIfNotPresent(item, out _);
                }
            }

            return;
        }

        SymmetricExceptWithEnumerable(other);
    }

    public bool IsSubsetOf(IEnumerable<T> other)
    {
        if (other == null)
        {
            ThrowHelper.ArgumentNull(nameof(other));
        }

        if (Count == 0 || (object)other! == this)
        {
            return true;
        }

        if (other is HashSet<T> otherAsSet && EqualityComparersAreEqual(this, otherAsSet))
        {
            if (Count > otherAsSet.Count)
            {
                return false;
            }

            return IsSubsetOfHashSetWithSameComparer(otherAsSet);
        }

        var uniqueCount = CheckUniqueAndUnfoundElements(other, returnIfUnfound: false, out var unfoundCount);
        return uniqueCount == Count && unfoundCount >= 0;
    }

    public bool IsProperSubsetOf(IEnumerable<T> other)
    {
        if (other == null)
        {
            ThrowHelper.ArgumentNull(nameof(other));
        }

        if ((object)other! == this)
        {
            return false;
        }

        if (other is ICollection<T> otherAsCollection)
        {
            if (Count == 0)
            {
                return otherAsCollection.Count > 0;
            }

            if (other is HashSet<T> otherAsSet && EqualityComparersAreEqual(this, otherAsSet))
            {
                if (Count >= otherAsSet.Count)
                {
                    return false;
                }

                return IsSubsetOfHashSetWithSameComparer(otherAsSet);
            }
        }

        var uniqueCount = CheckUniqueAndUnfoundElements(other, returnIfUnfound: false, out var unfoundCount);
        return uniqueCount == Count && unfoundCount > 0;
    }

    public bool IsSupersetOf(IEnumerable<T> other)
    {
        if (other == null)
        {
            ThrowHelper.ArgumentNull(nameof(other));
        }

        if ((object)other! == this)
        {
            return true;
        }

        if (other is ICollection<T> otherAsCollection)
        {
            if (otherAsCollection.Count == 0)
            {
                return true;
            }

            if (other is HashSet<T> otherAsSet && EqualityComparersAreEqual(this, otherAsSet) && otherAsSet.Count > Count)
            {
                return false;
            }
        }

        foreach (var element in other)
        {
            if (!Contains(element))
            {
                return false;
            }
        }

        return true;
    }

    public bool IsProperSupersetOf(IEnumerable<T> other)
    {
        if (other == null)
        {
            ThrowHelper.ArgumentNull(nameof(other));
        }

        if (Count == 0 || (object)other! == this)
        {
            return false;
        }

        if (other is ICollection<T> otherAsCollection)
        {
            if (otherAsCollection.Count == 0)
            {
                return true;
            }

            if (other is HashSet<T> otherAsSet && EqualityComparersAreEqual(this, otherAsSet))
            {
                if (otherAsSet.Count >= Count)
                {
                    return false;
                }

                return ContainsAllElements(otherAsSet);
            }
        }

        var uniqueCount = CheckUniqueAndUnfoundElements(other, returnIfUnfound: true, out var unfoundCount);
        return uniqueCount < Count && unfoundCount == 0;
    }

    public bool Overlaps(IEnumerable<T> other)
    {
        if (other == null)
        {
            ThrowHelper.ArgumentNull(nameof(other));
        }

        if (Count == 0)
        {
            return false;
        }

        if ((object)other! == this)
        {
            return true;
        }

        foreach (var element in other)
        {
            if (Contains(element))
            {
                return true;
            }
        }

        return false;
    }

    public bool SetEquals(IEnumerable<T> other)
    {
        if (other == null)
        {
            ThrowHelper.ArgumentNull(nameof(other));
        }

        if ((object)other! == this)
        {
            return true;
        }

        if (other is HashSet<T> otherAsSet && EqualityComparersAreEqual(this, otherAsSet))
        {
            if (Count != otherAsSet.Count)
            {
                return false;
            }

            return ContainsAllElements(otherAsSet);
        }

        if (Count == 0 && other is ICollection<T> otherAsCollection && otherAsCollection.Count > 0)
        {
            return false;
        }

        var uniqueCount = CheckUniqueAndUnfoundElements(other, returnIfUnfound: true, out var unfoundCount);
        return uniqueCount == Count && unfoundCount == 0;
    }

    public void CopyTo(T[] array) => CopyTo(array, 0, Count);

    public void CopyTo(T[] array, int arrayIndex) => CopyTo(array, arrayIndex, Count);

    public void CopyTo(T[] array, int arrayIndex, int count)
    {
        if (array == null)
        {
            ThrowHelper.ArgumentNull(nameof(array));
        }

        ThrowHelper.IfNegative(arrayIndex, nameof(arrayIndex));
        ThrowHelper.IfNegative(count, nameof(count));
        if (arrayIndex > array!.Length || count > array.Length - arrayIndex)
        {
            throw new ArgumentException(
                "Destination array is not long enough to copy all the items in the collection. Check array index and length.");
        }

        var entries = _entries;
        for (var i = 0; i < _count && count != 0; i++)
        {
            if (entries![i].Next >= -1)
            {
                array[arrayIndex++] = entries[i].Value;
                count--;
            }
        }
    }

    public int RemoveWhere(Predicate<T> match)
    {
        if (match == null)
        {
            ThrowHelper.ArgumentNull(nameof(match));
        }

        var entries = _entries;
        var numRemoved = 0;
        for (var i = 0; i < _count; i++)
        {
            if (entries![i].Next >= -1)
            {
                // the item is kept out of the entry, which Remove clears
                var value = entries[i].Value;
                if (match!(value) && Remove(value))
                {
                    numRemoved++;
                }
            }
        }

        return numRemoved;
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
        var entries = _entries!;
        var buckets = _buckets!;
        var count = 0;
        for (var i = 0; i < oldCount; i++)
        {
            var hashCode = oldEntries![i].HashCode;
            if (oldEntries[i].Next >= -1)
            {
                entries[count] = oldEntries[i];
                var bucket = (int)((uint)hashCode % (uint)buckets.Length);
                entries[count].Next = buckets[bucket] - 1;
                buckets[bucket] = count + 1;
                count++;
            }
        }

        _count = count;
        _freeCount = 0;
    }

    public bool TryGetValue(T equalValue, out T actualValue)
    {
        if (_buckets != null)
        {
            var index = FindItemIndex(equalValue);
            if (index >= 0)
            {
                actualValue = _entries![index].Value;
                return true;
            }
        }

        actualValue = default!;
        return false;
    }

    private int Initialize(int capacity)
    {
        var size = HashHelpers.GetPrime(capacity);
        _buckets = new int[size];
        _entries = new Entry[size];
        _freeList = -1;
        _freeCount = 0;
        return size;
    }

    private void Resize(int newSize)
    {
        var entries = new Entry[newSize];
        var count = _count;
        System.Array.Copy(_entries!, entries, count);
        var buckets = new int[newSize];
        for (var i = 0; i < count; i++)
        {
            if (entries[i].Next >= -1)
            {
                var bucket = (int)((uint)entries[i].HashCode % (uint)newSize);
                entries[i].Next = buckets[bucket] - 1;
                buckets[bucket] = i + 1;
            }
        }

        _buckets = buckets;
        _entries = entries;
    }

    /// <summary>Adds <paramref name="value"/> when it is not there; its entry's index either way.</summary>
    private bool AddIfNotPresent(T value, out int location)
    {
        if (_buckets == null)
        {
            Initialize(0);
        }

        var entries = _entries!;
        var comparer = _comparer;
        var hashCode = value == null ? 0 : comparer.GetHashCode(value);
        var bucket = (int)((uint)hashCode % (uint)_buckets!.Length);
        var i = _buckets[bucket] - 1; // the buckets count from 1
        var collisionCount = 0u;
        while (i >= 0)
        {
            if (entries[i].HashCode == hashCode && comparer.Equals(entries[i].Value, value))
            {
                location = i;
                return false;
            }

            i = entries[i].Next;
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
            _freeCount--;
            _freeList = StartOfFreeList - entries[_freeList].Next;
        }
        else
        {
            var count = _count;
            if (count == entries.Length)
            {
                Resize(HashHelpers.ExpandPrime(_count));
                bucket = (int)((uint)hashCode % (uint)_buckets.Length);
            }

            index = count;
            _count = count + 1;
            entries = _entries!;
        }

        entries[index].HashCode = hashCode;
        entries[index].Next = _buckets[bucket] - 1;
        entries[index].Value = value;
        _buckets[bucket] = index + 1;
        _version++;
        location = index;
        return true;
    }

    private bool ContainsAllElements(IEnumerable<T> other)
    {
        foreach (var element in other)
        {
            if (!Contains(element))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsSubsetOfHashSetWithSameComparer(HashSet<T> other)
    {
        foreach (var item in this)
        {
            if (!other.Contains(item))
            {
                return false;
            }
        }

        return true;
    }

    private void IntersectWithHashSetWithSameComparer(HashSet<T> other)
    {
        var entries = _entries;
        for (var i = 0; i < _count; i++)
        {
            if (entries![i].Next >= -1)
            {
                var item = entries[i].Value;
                if (!other.Contains(item))
                {
                    Remove(item);
                }
            }
        }
    }

    /// <summary>Marks the items of the set that <paramref name="other"/> has, then removes the others.</summary>
    private void IntersectWithEnumerable(IEnumerable<T> other)
    {
        var originalCount = _count;
        var marked = new bool[originalCount];
        foreach (var item in other)
        {
            var location = FindItemIndex(item);
            if (location >= 0)
            {
                marked[location] = true;
            }
        }

        for (var i = 0; i < originalCount; i++)
        {
            if (_entries![i].Next >= -1 && !marked[i])
            {
                Remove(_entries[i].Value);
            }
        }
    }

    private void SymmetricExceptWithEnumerable(IEnumerable<T> other)
    {
        var originalCount = _count;
        var itemsToRemove = new bool[originalCount];
        var itemsAddedFromOther = new bool[originalCount];
        foreach (var item in other)
        {
            if (AddIfNotPresent(item, out var location))
            {
                // an item of other's the set did not have, added (maybe where a removed one was)
                if (location < originalCount)
                {
                    itemsAddedFromOther[location] = true;
                }
            }
            else
            {
                // one it has: to remove, unless other added it
                if (location < originalCount && !itemsAddedFromOther[location])
                {
                    itemsToRemove[location] = true;
                }
            }
        }

        for (var i = 0; i < originalCount; i++)
        {
            if (itemsToRemove[i])
            {
                Remove(_entries![i].Value);
            }
        }
    }

    /// <summary>
    /// How many items of the set <paramref name="other"/> has (each once); and how many of its items the set does not
    /// </summary>
    private int CheckUniqueAndUnfoundElements(IEnumerable<T> other, bool returnIfUnfound, out int unfoundCount)
    {
        if (_count == 0)
        {
            var numElementsInOther = 0;
            foreach (var item in other)
            {
                numElementsInOther++;
                break;
            }

            unfoundCount = numElementsInOther;
            return 0;
        }

        var originalCount = _count;
        var marked = new bool[originalCount];
        unfoundCount = 0;
        var uniqueFoundCount = 0;
        foreach (var item in other)
        {
            var location = FindItemIndex(item);
            if (location >= 0)
            {
                if (!marked[location])
                {
                    marked[location] = true;
                    uniqueFoundCount++;
                }
            }
            else
            {
                unfoundCount++;
                if (returnIfUnfound)
                {
                    break;
                }
            }
        }

        return uniqueFoundCount;
    }

    /// <summary>True when the two sets compare their items the same way: with the same comparer.</summary>
    private static bool EqualityComparersAreEqual(HashSet<T> set1, HashSet<T> set2) => set1.Comparer.Equals(set2.Comparer);

    private static void ConcurrentOperations() => throw new InvalidOperationException(
        "Operations that change non-concurrent collections must have exclusive access. A concurrent update was " +
        "performed on this collection and corrupted its state. The collection's state is no longer correct.");

    internal struct Entry
    {
        public int HashCode;

        /// <summary>The next entry of the bucket (0-based), -1 for none; below -1 a free entry, -3 - the next free one.</summary>
        public int Next;
        public T Value;
    }

    /// <summary>.NET's HashSet&lt;T&gt;.Enumerator: the items in the order of the entries.</summary>
    [Substitute(typeof(System.Collections.Generic.HashSet<>.Enumerator))]
    public struct Enumerator : IEnumerator<T>
    {
        private readonly HashSet<T> _hashSet;
        private readonly int _version;
        private int _index;
        private T _current;

        internal Enumerator(HashSet<T> hashSet)
        {
            _hashSet = hashSet;
            _version = hashSet._version;
            _index = 0;
            _current = default!;
        }

        public bool MoveNext()
        {
            if (_version != _hashSet._version)
            {
                ThrowHelper.VersionChanged();
            }

            while ((uint)_index < (uint)_hashSet._count)
            {
                var entries = _hashSet._entries!;
                var i = _index++;
                if (entries[i].Next >= -1)
                {
                    _current = entries[i].Value;
                    return true;
                }
            }

            _index = _hashSet._count + 1;
            _current = default!;
            return false;
        }

        public readonly T Current => _current;

        public readonly void Dispose()
        {
        }

        readonly object? IEnumerator.Current
        {
            get
            {
                if (_index == 0 || _index == _hashSet._count + 1)
                {
                    ThrowHelper.EnumerationCannotHappen();
                }

                return _current;
            }
        }

        void IEnumerator.Reset()
        {
            if (_version != _hashSet._version)
            {
                ThrowHelper.VersionChanged();
            }

            _index = 0;
            _current = default!;
        }
    }
}
