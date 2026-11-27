using ChipSharp.Abstractions;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>Compiles C# with Roslyn and runs the whole-program pass on it; the assembly is called Test.dll in messages.</summary>
internal static class WholeProgram
{
    /// <summary>What building text (concatenation, interpolation, ToString, the methods of string) is called.</summary>
    public const string BuildingFeature = "building text ($\"...\", +, ToString, string.Format and the other methods of string)";

    /// <summary>The heading of a building-text problem: its feature and when it arrives.</summary>
    public const string Building = $"{BuildingFeature} arrives with M3 (the heap)";

    /// <summary>The hint of a building-text problem.</summary>
    public const string BuildingHint = "For now, print the parts one after another with Console.Write.";

    /// <summary>
    /// The lines a summary ends with, before how to see every place, when the program calls a library method ChipSharp
    /// does not have.
    /// </summary>
    public static readonly string LibraryCalls = """
        ChipSharp can call these library methods (--details lists each one with its parameters):
          Console.Write and Console.WriteLine of (nothing), text, char, int, uint, long, ulong, bool, float, double
          Math and MathF: Abs, Min, Max, Sqrt
          Array: Copy, ConstrainedCopy, CopyTo, Clear, Fill, IndexOf, LastIndexOf, Reverse, Resize, Clone, Sort, BinarySearch, Exists, Find, FindAll, FindIndex, FindLast, FindLastIndex, ForEach, TrueForAll, ... (21 in all)
          MemoryExtensions: IndexOf, LastIndexOf, Contains, SequenceEqual, StartsWith, EndsWith, Reverse, AsSpan
          TimeSpan: FromTicks, FromDays, FromHours, FromMinutes, FromSeconds, FromMilliseconds, FromMicroseconds, Add, Subtract, Negate, Multiply, Divide, Duration, Compare, CompareTo, Equals, its constructors, properties and operators
          ArgumentException: ThrowIfNullOrEmpty, ThrowIfNullOrWhiteSpace, its constructors and properties
          ArgumentNullException: ThrowIfNull, its constructors
          ArgumentOutOfRangeException and ObjectDisposedException: its constructors and properties
          Exception: GetBaseException, ToString, its constructors and properties
          SystemException, ApplicationException, InvalidOperationException, NotSupportedException, NotImplementedException, IndexOutOfRangeException, FormatException, NullReferenceException, InvalidCastException, TimeoutException, ArithmeticException, OverflowException and DivideByZeroException: its constructors
          Span<T>: Slice, CopyTo, TryCopyTo, Fill, Clear, its constructors, properties and operators
          ReadOnlySpan<T>: Slice, CopyTo, TryCopyTo, its constructors, properties and operators
          String: Equals, CompareOrdinal, Compare, CompareTo, IsNullOrEmpty, IsNullOrWhiteSpace, IndexOf, LastIndexOf, Contains, StartsWith, EndsWith, ToString, Substring, Replace, Trim, TrimStart, TrimEnd, PadLeft, PadRight, ToUpper, ... (29 in all), its constructors, properties and operators
          ReadOnlySpan<char> and Span<char>: ToString
          Char.IsDigit, Char.IsLetter, Char.IsLetterOrDigit, Char.IsWhiteSpace, Char.IsUpper, Char.IsLower, Char.ToUpper, Char.ToLower, Char.ToUpperInvariant, Char.ToLowerInvariant and Char.ToString of char
          SByte, Byte, Int16, UInt16, Int32, UInt32, Int64 and UInt64: Parse, TryParse
          BinaryPrimitives: ReverseEndianness, ReadInt16BigEndian, ReadInt16LittleEndian, ReadUInt16BigEndian, ReadUInt16LittleEndian, ReadInt32BigEndian, ReadInt32LittleEndian, ReadUInt32BigEndian, ReadUInt32LittleEndian, ReadInt64BigEndian, ReadInt64LittleEndian, ReadUInt64BigEndian, ReadUInt64LittleEndian, ReadIntPtrBigEndian, ReadIntPtrLittleEndian, ReadUIntPtrBigEndian, ReadUIntPtrLittleEndian, ReadSingleBigEndian, ReadSingleLittleEndian, ReadDoubleBigEndian, ... (73 in all)
          Comparer<T>: Compare, its properties
          EqualityComparer<T>: Equals, GetHashCode, its properties
          KeyNotFoundException: its constructors
          KeyValuePair<TKey, TValue>: ToString, Deconstruct, its constructors and properties
          KeyValuePair.Create of (TKey, TValue)
          Dictionary<TKey, TValue>: Add, Clear, ContainsKey, ContainsValue, GetEnumerator, Remove, TryAdd, TryGetValue, EnsureCapacity, TrimExcess, its constructors and properties
          CollectionExtensions: GetValueOrDefault, TryAdd, Remove
          HashSet<T>: Add, Clear, Contains, Remove, GetEnumerator, UnionWith, IntersectWith, ExceptWith, SymmetricExceptWith, IsSubsetOf, IsProperSubsetOf, IsSupersetOf, IsProperSupersetOf, Overlaps, SetEquals, CopyTo, RemoveWhere, EnsureCapacity, TrimExcess, TryGetValue, its constructors and properties
          List<T>: Add, AddRange, BinarySearch, Clear, Contains, ConvertAll, CopyTo, EnsureCapacity, Exists, Find, FindAll, FindIndex, FindLast, FindLastIndex, ForEach, GetEnumerator, GetRange, Slice, IndexOf, Insert, ... (31 in all), its constructors and properties
          Queue<T>: Clear, CopyTo, Enqueue, GetEnumerator, Dequeue, TryDequeue, Peek, TryPeek, Contains, ToArray, TrimExcess, EnsureCapacity, its constructors and properties
          Stack<T>: Clear, Contains, CopyTo, GetEnumerator, TrimExcess, Peek, TryPeek, Pop, TryPop, Push, EnsureCapacity, ToArray, its constructors and properties
          CollectionsMarshal: AsSpan, SetCount
          MemoryMarshal: Read, Write, TryRead, TryWrite, AsBytes, Cast, CreateSpan, CreateReadOnlySpan
          KeyCollection: GetEnumerator, CopyTo, Contains, its constructors and properties
          ValueCollection: GetEnumerator, CopyTo, its constructors and properties
          ChipSharp.Hardware: Pin, Timing, Registers (see the README)
        """.ReplaceLineEndings("\n");

    /// <summary>The lines the details end with when the program calls a library method ChipSharp does not have.</summary>
    public static readonly string LibraryCallDetails = """
        ChipSharp can call these library methods:
          System.Console: Write(string), Write(char), Write(int), Write(uint), Write(long), Write(ulong), Write(bool), Write(float), Write(double), WriteLine(), WriteLine(string), WriteLine(char), WriteLine(int), WriteLine(uint), WriteLine(long), WriteLine(ulong), WriteLine(bool), WriteLine(float), WriteLine(double)
          System.Math: Abs(double), Abs(float), Min(double, double), Min(float, float), Max(double, double), Max(float, float), Sqrt(double)
          System.MathF: Abs(float), Min(float, float), Max(float, float), Sqrt(float)
          System.Array: Copy(System.Array, System.Array, int), Copy(System.Array, System.Array, long), Copy(System.Array, int, System.Array, int, int), Copy(System.Array, long, System.Array, long, long), ConstrainedCopy(System.Array, int, System.Array, int, int), CopyTo(System.Array, int), CopyTo(System.Array, long), Clear(System.Array), Clear(System.Array, int, int), Fill(T[], T), Fill(T[], T, int, int), IndexOf(T[], T), IndexOf(T[], T, int), IndexOf(T[], T, int, int), LastIndexOf(T[], T), LastIndexOf(T[], T, int), LastIndexOf(T[], T, int, int), Reverse(T[]), Reverse(T[], int, int), Resize(ref T[], int), Clone(), Sort(T[]), Sort(T[], int, int), BinarySearch(T[], T), BinarySearch(T[], int, int, T), Exists(T[], System.Predicate<T>), Find(T[], System.Predicate<T>), FindAll(T[], System.Predicate<T>), FindIndex(T[], System.Predicate<T>), FindIndex(T[], int, System.Predicate<T>), FindIndex(T[], int, int, System.Predicate<T>), FindLast(T[], System.Predicate<T>), FindLastIndex(T[], System.Predicate<T>), FindLastIndex(T[], int, System.Predicate<T>), FindLastIndex(T[], int, int, System.Predicate<T>), ForEach(T[], System.Action<T>), TrueForAll(T[], System.Predicate<T>), ConvertAll(TInput[], System.Converter<TInput, TOutput>), Sort(T[], System.Comparison<T>)
          System.Buffers.Binary.BinaryPrimitives: ReverseEndianness(sbyte), ReverseEndianness(byte), ReverseEndianness(short), ReverseEndianness(ushort), ReverseEndianness(int), ReverseEndianness(uint), ReverseEndianness(long), ReverseEndianness(ulong), ReverseEndianness(nint), ReverseEndianness(nuint), ReadInt16BigEndian(System.ReadOnlySpan<byte>), ReadInt16LittleEndian(System.ReadOnlySpan<byte>), ReadUInt16BigEndian(System.ReadOnlySpan<byte>), ReadUInt16LittleEndian(System.ReadOnlySpan<byte>), ReadInt32BigEndian(System.ReadOnlySpan<byte>), ReadInt32LittleEndian(System.ReadOnlySpan<byte>), ReadUInt32BigEndian(System.ReadOnlySpan<byte>), ReadUInt32LittleEndian(System.ReadOnlySpan<byte>), ReadInt64BigEndian(System.ReadOnlySpan<byte>), ReadInt64LittleEndian(System.ReadOnlySpan<byte>), ReadUInt64BigEndian(System.ReadOnlySpan<byte>), ReadUInt64LittleEndian(System.ReadOnlySpan<byte>), ReadIntPtrBigEndian(System.ReadOnlySpan<byte>), ReadIntPtrLittleEndian(System.ReadOnlySpan<byte>), ReadUIntPtrBigEndian(System.ReadOnlySpan<byte>), ReadUIntPtrLittleEndian(System.ReadOnlySpan<byte>), ReadSingleBigEndian(System.ReadOnlySpan<byte>), ReadSingleLittleEndian(System.ReadOnlySpan<byte>), ReadDoubleBigEndian(System.ReadOnlySpan<byte>), ReadDoubleLittleEndian(System.ReadOnlySpan<byte>), TryReadInt16BigEndian(System.ReadOnlySpan<byte>, ref short), TryReadInt16LittleEndian(System.ReadOnlySpan<byte>, ref short), TryReadUInt16BigEndian(System.ReadOnlySpan<byte>, ref ushort), TryReadUInt16LittleEndian(System.ReadOnlySpan<byte>, ref ushort), TryReadInt32BigEndian(System.ReadOnlySpan<byte>, ref int), TryReadInt32LittleEndian(System.ReadOnlySpan<byte>, ref int), TryReadUInt32BigEndian(System.ReadOnlySpan<byte>, ref uint), TryReadUInt32LittleEndian(System.ReadOnlySpan<byte>, ref uint), TryReadInt64BigEndian(System.ReadOnlySpan<byte>, ref long), TryReadInt64LittleEndian(System.ReadOnlySpan<byte>, ref long), TryReadUInt64BigEndian(System.ReadOnlySpan<byte>, ref ulong), TryReadUInt64LittleEndian(System.ReadOnlySpan<byte>, ref ulong), TryReadSingleBigEndian(System.ReadOnlySpan<byte>, ref float), TryReadSingleLittleEndian(System.ReadOnlySpan<byte>, ref float), TryReadDoubleBigEndian(System.ReadOnlySpan<byte>, ref double), TryReadDoubleLittleEndian(System.ReadOnlySpan<byte>, ref double), WriteInt16BigEndian(System.Span<byte>, short), WriteInt16LittleEndian(System.Span<byte>, short), WriteUInt16BigEndian(System.Span<byte>, ushort), WriteUInt16LittleEndian(System.Span<byte>, ushort), WriteInt32BigEndian(System.Span<byte>, int), WriteInt32LittleEndian(System.Span<byte>, int), WriteUInt32BigEndian(System.Span<byte>, uint), WriteUInt32LittleEndian(System.Span<byte>, uint), WriteInt64BigEndian(System.Span<byte>, long), WriteInt64LittleEndian(System.Span<byte>, long), WriteUInt64BigEndian(System.Span<byte>, ulong), WriteUInt64LittleEndian(System.Span<byte>, ulong), WriteIntPtrBigEndian(System.Span<byte>, nint), WriteIntPtrLittleEndian(System.Span<byte>, nint), WriteUIntPtrBigEndian(System.Span<byte>, nuint), WriteUIntPtrLittleEndian(System.Span<byte>, nuint), WriteSingleBigEndian(System.Span<byte>, float), WriteSingleLittleEndian(System.Span<byte>, float), WriteDoubleBigEndian(System.Span<byte>, double), WriteDoubleLittleEndian(System.Span<byte>, double), TryWriteInt16BigEndian(System.Span<byte>, short), TryWriteInt16LittleEndian(System.Span<byte>, short), TryWriteUInt16BigEndian(System.Span<byte>, ushort), TryWriteUInt16LittleEndian(System.Span<byte>, ushort), TryWriteInt32BigEndian(System.Span<byte>, int), TryWriteInt32LittleEndian(System.Span<byte>, int), TryWriteUInt32BigEndian(System.Span<byte>, uint), TryWriteUInt32LittleEndian(System.Span<byte>, uint), TryWriteInt64BigEndian(System.Span<byte>, long), TryWriteInt64LittleEndian(System.Span<byte>, long), TryWriteUInt64BigEndian(System.Span<byte>, ulong), TryWriteUInt64LittleEndian(System.Span<byte>, ulong), TryWriteSingleBigEndian(System.Span<byte>, float), TryWriteSingleLittleEndian(System.Span<byte>, float), TryWriteDoubleBigEndian(System.Span<byte>, double), TryWriteDoubleLittleEndian(System.Span<byte>, double)
          System.Collections.Generic.Comparer<T>: Default, Compare(T, T)
          System.Collections.Generic.EqualityComparer<T>: Default, Equals(T, T), GetHashCode(T)
          System.MemoryExtensions: IndexOf(System.Span<T>, T), IndexOf(System.ReadOnlySpan<T>, T), IndexOf(System.Span<T>, System.ReadOnlySpan<T>), IndexOf(System.ReadOnlySpan<T>, System.ReadOnlySpan<T>), LastIndexOf(System.Span<T>, T), LastIndexOf(System.ReadOnlySpan<T>, T), LastIndexOf(System.Span<T>, System.ReadOnlySpan<T>), LastIndexOf(System.ReadOnlySpan<T>, System.ReadOnlySpan<T>), Contains(System.Span<T>, T), Contains(System.ReadOnlySpan<T>, T), SequenceEqual(System.Span<T>, System.ReadOnlySpan<T>), SequenceEqual(System.ReadOnlySpan<T>, System.ReadOnlySpan<T>), StartsWith(System.Span<T>, System.ReadOnlySpan<T>), StartsWith(System.ReadOnlySpan<T>, System.ReadOnlySpan<T>), StartsWith(System.ReadOnlySpan<T>, T), EndsWith(System.Span<T>, System.ReadOnlySpan<T>), EndsWith(System.ReadOnlySpan<T>, System.ReadOnlySpan<T>), EndsWith(System.ReadOnlySpan<T>, T), Reverse(System.Span<T>), AsSpan(T[]), AsSpan(T[], int), AsSpan(T[], int, int), AsSpan(string), AsSpan(string, int), AsSpan(string, int, int)
          System.TimeSpan: Zero, MaxValue, MinValue, new TimeSpan(long), new TimeSpan(int, int, int), new TimeSpan(int, int, int, int), new TimeSpan(int, int, int, int, int), new TimeSpan(int, int, int, int, int, int), Ticks, Days, Hours, Minutes, Seconds, Milliseconds, Microseconds, Nanoseconds, TotalDays, TotalHours, TotalMinutes, TotalSeconds, TotalMilliseconds, TotalMicroseconds, TotalNanoseconds, FromTicks(long), FromDays(int), FromHours(int), FromMinutes(long), FromSeconds(long), FromMilliseconds(long), FromMicroseconds(long), FromDays(double), FromHours(double), FromMinutes(double), FromSeconds(double), FromMilliseconds(double), FromMicroseconds(double), Add(System.TimeSpan), Subtract(System.TimeSpan), Negate(), Multiply(double), Divide(double), Divide(System.TimeSpan), Duration(), Compare(System.TimeSpan, System.TimeSpan), CompareTo(System.TimeSpan), Equals(System.TimeSpan), Equals(System.TimeSpan, System.TimeSpan), operator -(System.TimeSpan), operator -(System.TimeSpan, System.TimeSpan), operator +(System.TimeSpan), operator +(System.TimeSpan, System.TimeSpan), operator *(System.TimeSpan, double), operator *(double, System.TimeSpan), operator /(System.TimeSpan, double), operator /(System.TimeSpan, System.TimeSpan), operator ==(System.TimeSpan, System.TimeSpan), operator !=(System.TimeSpan, System.TimeSpan), operator <(System.TimeSpan, System.TimeSpan), operator <=(System.TimeSpan, System.TimeSpan), operator >(System.TimeSpan, System.TimeSpan), operator >=(System.TimeSpan, System.TimeSpan)
          System.ArgumentException: new ArgumentException(), new ArgumentException(string), new ArgumentException(string, System.Exception), new ArgumentException(string, string, System.Exception), new ArgumentException(string, string), Message, ParamName, ThrowIfNullOrEmpty(string, string), ThrowIfNullOrWhiteSpace(string, string)
          System.ArgumentNullException: new ArgumentNullException(), new ArgumentNullException(string), new ArgumentNullException(string, System.Exception), new ArgumentNullException(string, string), ThrowIfNull(object, string)
          System.ArgumentOutOfRangeException: new ArgumentOutOfRangeException(), new ArgumentOutOfRangeException(string), new ArgumentOutOfRangeException(string, string), new ArgumentOutOfRangeException(string, System.Exception), new ArgumentOutOfRangeException(string, object, string), ActualValue, Message
          System.Exception: new Exception(), new Exception(string), new Exception(string, System.Exception), Message, InnerException, StackTrace, GetBaseException(), ToString()
          System.SystemException: new SystemException(), new SystemException(string), new SystemException(string, System.Exception)
          System.ApplicationException: new ApplicationException(), new ApplicationException(string), new ApplicationException(string, System.Exception)
          System.InvalidOperationException: new InvalidOperationException(), new InvalidOperationException(string), new InvalidOperationException(string, System.Exception)
          System.ObjectDisposedException: new ObjectDisposedException(string), new ObjectDisposedException(string, string), new ObjectDisposedException(string, System.Exception), ObjectName, Message
          System.NotSupportedException: new NotSupportedException(), new NotSupportedException(string), new NotSupportedException(string, System.Exception)
          System.NotImplementedException: new NotImplementedException(), new NotImplementedException(string), new NotImplementedException(string, System.Exception)
          System.IndexOutOfRangeException: new IndexOutOfRangeException(), new IndexOutOfRangeException(string), new IndexOutOfRangeException(string, System.Exception)
          System.Collections.Generic.KeyNotFoundException: new KeyNotFoundException(), new KeyNotFoundException(string), new KeyNotFoundException(string, System.Exception)
          System.FormatException: new FormatException(), new FormatException(string), new FormatException(string, System.Exception)
          System.NullReferenceException: new NullReferenceException(), new NullReferenceException(string), new NullReferenceException(string, System.Exception)
          System.InvalidCastException: new InvalidCastException(), new InvalidCastException(string), new InvalidCastException(string, System.Exception)
          System.TimeoutException: new TimeoutException(), new TimeoutException(string), new TimeoutException(string, System.Exception)
          System.ArithmeticException: new ArithmeticException(), new ArithmeticException(string), new ArithmeticException(string, System.Exception)
          System.OverflowException: new OverflowException(), new OverflowException(string), new OverflowException(string, System.Exception)
          System.DivideByZeroException: new DivideByZeroException(), new DivideByZeroException(string), new DivideByZeroException(string, System.Exception)
          System.Collections.Generic.KeyValuePair<TKey, TValue>: new KeyValuePair(TKey, TValue), Key, Value, ToString(), Deconstruct(ref TKey, ref TValue)
          System.Collections.Generic.KeyValuePair: Create(TKey, TValue)
          System.Runtime.InteropServices.CollectionsMarshal: AsSpan(System.Collections.Generic.List<T>), SetCount(System.Collections.Generic.List<T>, int)
          System.Collections.Generic.Dictionary<TKey, TValue>: new Dictionary(), new Dictionary(int), new Dictionary(System.Collections.Generic.IEqualityComparer<TKey>), new Dictionary(int, System.Collections.Generic.IEqualityComparer<TKey>), new Dictionary(System.Collections.Generic.IDictionary<TKey, TValue>), new Dictionary(System.Collections.Generic.IDictionary<TKey, TValue>, System.Collections.Generic.IEqualityComparer<TKey>), new Dictionary(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<TKey, TValue>>), new Dictionary(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<TKey, TValue>>, System.Collections.Generic.IEqualityComparer<TKey>), Comparer, Count, Capacity, Keys, Values, Item, Add(TKey, TValue), Clear(), ContainsKey(TKey), ContainsValue(TValue), GetEnumerator(), Remove(TKey), Remove(TKey, ref TValue), TryAdd(TKey, TValue), TryGetValue(TKey, ref TValue), EnsureCapacity(int), TrimExcess(), TrimExcess(int)
          System.Collections.Generic.CollectionExtensions: GetValueOrDefault(System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>, TKey), GetValueOrDefault(System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>, TKey, TValue), TryAdd(System.Collections.Generic.IDictionary<TKey, TValue>, TKey, TValue), Remove(System.Collections.Generic.IDictionary<TKey, TValue>, TKey, ref TValue)
          System.Collections.Generic.HashSet<T>: new HashSet(), new HashSet(System.Collections.Generic.IEqualityComparer<T>), new HashSet(int), new HashSet(System.Collections.Generic.IEnumerable<T>), new HashSet(System.Collections.Generic.IEnumerable<T>, System.Collections.Generic.IEqualityComparer<T>), new HashSet(int, System.Collections.Generic.IEqualityComparer<T>), Comparer, Count, Capacity, Add(T), Clear(), Contains(T), Remove(T), GetEnumerator(), UnionWith(System.Collections.Generic.IEnumerable<T>), IntersectWith(System.Collections.Generic.IEnumerable<T>), ExceptWith(System.Collections.Generic.IEnumerable<T>), SymmetricExceptWith(System.Collections.Generic.IEnumerable<T>), IsSubsetOf(System.Collections.Generic.IEnumerable<T>), IsProperSubsetOf(System.Collections.Generic.IEnumerable<T>), IsSupersetOf(System.Collections.Generic.IEnumerable<T>), IsProperSupersetOf(System.Collections.Generic.IEnumerable<T>), Overlaps(System.Collections.Generic.IEnumerable<T>), SetEquals(System.Collections.Generic.IEnumerable<T>), CopyTo(T[]), CopyTo(T[], int), CopyTo(T[], int, int), RemoveWhere(System.Predicate<T>), EnsureCapacity(int), TrimExcess(), TrimExcess(int), TryGetValue(T, ref T)
          System.Collections.Generic.List<T>: new List(), new List(int), new List(System.Collections.Generic.IEnumerable<T>), Capacity, Count, Item, Add(T), AddRange(System.Collections.Generic.IEnumerable<T>), BinarySearch(int, int, T, System.Collections.Generic.IComparer<T>), BinarySearch(T), BinarySearch(T, System.Collections.Generic.IComparer<T>), Clear(), Contains(T), ConvertAll(System.Converter<T, TOutput>), CopyTo(T[]), CopyTo(int, T[], int, int), CopyTo(T[], int), EnsureCapacity(int), Exists(System.Predicate<T>), Find(System.Predicate<T>), FindAll(System.Predicate<T>), FindIndex(System.Predicate<T>), FindIndex(int, System.Predicate<T>), FindIndex(int, int, System.Predicate<T>), FindLast(System.Predicate<T>), FindLastIndex(System.Predicate<T>), FindLastIndex(int, System.Predicate<T>), FindLastIndex(int, int, System.Predicate<T>), ForEach(System.Action<T>), GetEnumerator(), GetRange(int, int), Slice(int, int), IndexOf(T), IndexOf(T, int), IndexOf(T, int, int), Insert(int, T), InsertRange(int, System.Collections.Generic.IEnumerable<T>), LastIndexOf(T), LastIndexOf(T, int), LastIndexOf(T, int, int), Remove(T), RemoveAll(System.Predicate<T>), RemoveAt(int), RemoveRange(int, int), Reverse(), Reverse(int, int), Sort(), Sort(System.Collections.Generic.IComparer<T>), Sort(int, int, System.Collections.Generic.IComparer<T>), Sort(System.Comparison<T>), ToArray(), TrimExcess(), TrueForAll(System.Predicate<T>)
          System.Collections.Generic.Queue<T>: new Queue(), new Queue(int), new Queue(System.Collections.Generic.IEnumerable<T>), Count, Capacity, Clear(), CopyTo(T[], int), Enqueue(T), GetEnumerator(), Dequeue(), TryDequeue(ref T), Peek(), TryPeek(ref T), Contains(T), ToArray(), TrimExcess(), TrimExcess(int), EnsureCapacity(int)
          System.Collections.Generic.Stack<T>: new Stack(), new Stack(int), new Stack(System.Collections.Generic.IEnumerable<T>), Count, Capacity, Clear(), Contains(T), CopyTo(T[], int), GetEnumerator(), TrimExcess(), TrimExcess(int), Peek(), TryPeek(ref T), Pop(), TryPop(ref T), Push(T), EnsureCapacity(int), ToArray()
          System.Collections.Generic.Dictionary<TKey, TValue>.KeyCollection: new KeyCollection(System.Collections.Generic.Dictionary<TKey, TValue>), Count, GetEnumerator(), CopyTo(TKey[], int), Contains(TKey)
          System.Collections.Generic.Dictionary<TKey, TValue>.ValueCollection: new ValueCollection(System.Collections.Generic.Dictionary<TKey, TValue>), Count, GetEnumerator(), CopyTo(TValue[], int)
          System.Span<T>: new Span(T[]), new Span(T[], int, int), Length, IsEmpty, Empty, Slice(int), Slice(int, int), CopyTo(System.Span<T>), TryCopyTo(System.Span<T>), Fill(T), Clear(), operator ==(System.Span<T>, System.Span<T>)
          System.ReadOnlySpan<T>: new ReadOnlySpan(T[]), new ReadOnlySpan(T[], int, int), Length, IsEmpty, Empty, Slice(int), Slice(int, int), CopyTo(System.Span<T>), TryCopyTo(System.Span<T>), operator ==(System.ReadOnlySpan<T>, System.ReadOnlySpan<T>)
          System.Runtime.InteropServices.MemoryMarshal: Read(System.ReadOnlySpan<byte>), Write(System.Span<byte>, in T), TryRead(System.ReadOnlySpan<byte>, out T), TryWrite(System.Span<byte>, in T), AsBytes(System.Span<T>), Cast(System.Span<TFrom>), CreateSpan(ref T, int), CreateReadOnlySpan(ref T, int)
          System.String: Length, this[int], operator ==(string, string), operator !=(string, string), Equals(string, string), Equals(string), Equals(object), CompareOrdinal(string, string), Compare(string, string), CompareTo(string), IsNullOrEmpty(string), IsNullOrWhiteSpace(string), IndexOf(char), IndexOf(char, int), IndexOf(char, int, int), IndexOf(string), IndexOf(string, int), LastIndexOf(char), LastIndexOf(char, int), LastIndexOf(string), Contains(string), Contains(char), StartsWith(string), StartsWith(char), EndsWith(string), EndsWith(char), ToString(), Substring(int), Substring(int, int), Replace(char, char), Replace(string, string), Trim(), TrimStart(), TrimEnd(), Trim(char), TrimStart(char), TrimEnd(char), PadLeft(int), PadLeft(int, char), PadRight(int), PadRight(int, char), ToUpper(), ToLower(), ToUpperInvariant(), ToLowerInvariant(), Insert(int, string), Remove(int), Remove(int, int), ToCharArray(), Split(char, StringSplitOptions), Join(string, string[]), Concat(string[]), new String(char[], int, int), new String(char, int), new String(char[]), new String(ReadOnlySpan<char>)
          System.ReadOnlySpan<char>: ToString()
          System.Span<char>: ToString()
          System.Char: IsDigit(char), IsLetter(char), IsLetterOrDigit(char), IsWhiteSpace(char), IsUpper(char), IsLower(char), ToUpper(char), ToLower(char), ToUpperInvariant(char), ToLowerInvariant(char), ToString(char)
          System.SByte: Parse(string), TryParse(string, out sbyte)
          System.Byte: Parse(string), TryParse(string, out byte)
          System.Int16: Parse(string), TryParse(string, out short)
          System.UInt16: Parse(string), TryParse(string, out ushort)
          System.Int32: Parse(string), TryParse(string, out int)
          System.UInt32: Parse(string), TryParse(string, out uint)
          System.Int64: Parse(string), TryParse(string, out long)
          System.UInt64: Parse(string), TryParse(string, out ulong)
          ChipSharp.Hardware.Pin: Output(int), Input(int), Write(int, bool), Toggle(int), Read(int), Watch(int), WatchRising(int), WatchFalling(int), Unwatch(int)
          ChipSharp.Hardware.Timing: DelayMilliseconds(int), DelayMicroseconds(int), EveryMilliseconds(int), EveryMicroseconds(int), AfterMilliseconds(int), AfterMicroseconds(int), Stop(), Delay(System.TimeSpan)
          ChipSharp.Hardware.Registers: Read(uint), Write(uint, uint)
        """.ReplaceLineEndings("\n");

    public static IlProgram Analyze(string source) => WholeProgramPass.Analyze(Load(source));

    public static AnalysisResult Check(string source) => WholeProgramPass.Check(Load(source));

    /// <summary>The display names of the program's methods, in the pass's order.</summary>
    public static string[] MethodNames(string source) => [.. Analyze(source).Methods.Select(m => m.DisplayName)];

    public static ChipSharpException Error(string source) => Assert.Throws<ChipSharpException>(() => Analyze(source));

    /// <summary>The problems one per place, as the details list them (without the heading and the indent).</summary>
    public static string[] Problems(string source)
    {
        var analysis = Check(source);
        Assert.False(analysis.IsSupported);
        Assert.Equal(analysis.Problems.Count, analysis.Groups.Sum(group => group.Problems.Count));
        return [.. analysis.Problems.Select(problem => problem.Line)];
    }

    private static IlAssembly Load(string source) => IlAssembly.Load(CSharpCompiler.Compile(source), "Test.dll");
}
