namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.Text.StringBuilder"/>: text built in place, with .NET 10's results and, where .NET
/// throws, its exceptions' messages. It is an object on the heap, and so is its buffer of chars (UTF-16, as .NET's), one
/// array that grows as the text does; <see cref="ToString()"/> makes a string of it (1 byte per char when every char is
/// below U+0100, see ChipSharp.Frontend's Strings). The text of a number, a bool or an object is its <c>ToString()</c>,
/// as .NET appends it; <c>AppendLine</c> ends a line with "\r\n", as <c>Console.WriteLine</c> does on every target.
/// </summary>
/// <remarks>
/// .NET keeps a builder that has grown as a list of chunks; this one is a single buffer, which grows as .NET's capacity
/// does when text is appended (by the text that does not fit, at least as much as the builder holds, up to 8000 chars at
/// a time), so <see cref="Capacity"/> is .NET's for a builder made and grown that way and set with <c>Capacity</c> and
/// <c>EnsureCapacity</c>, and for <c>Insert</c> and a longer <c>Replace</c> of one that has not grown (which put a chunk
/// before the last one in .NET: the capacity grows by what is inserted). Where .NET drops chunks or works in one that is
/// not its last (<c>Clear</c>, a shorter <c>Length</c> or a <c>Remove</c> of a builder that has grown, an
/// <c>Insert</c> into its first chunks), its capacity may come out otherwise; the text never does. C#'s <c>sb.Append($"...")</c> and <c>sb.AppendFormat("...", ...)</c> with a literal
/// format are built where they are called (ChipSharp.Frontend's PrintedText), appending each part. Not here: the
/// overloads of pointers, of <c>ReadOnlyMemory&lt;char&gt;</c>, of <c>decimal</c> and of an <c>IFormatProvider</c>,
/// <c>AppendJoin</c> of an <c>IEnumerable&lt;T&gt;</c> and of a <c>params</c> span (<c>sb.AppendJoin(", ", a, b)</c>:
/// give an array), <c>GetChunks</c>.
/// </remarks>
[Substitute(typeof(System.Text.StringBuilder))]
internal sealed class StringBuilder
{
    /// <summary>.NET's StringBuilder.DefaultCapacity.</summary>
    private const int DefaultCapacity = 16;

    /// <summary>.NET's StringBuilder.MaxChunkSize: the most a builder grows by at once.</summary>
    private const int MaxChunkSize = 8000;

    private const string LessOrEqual = "Index was out of range. Must be non-negative and less than or equal to the size of the collection.";
    private const string Less = "Index was out of range. Must be non-negative and less than the size of the collection.";
    private const string IndexLength = "Index and length must refer to a location within the string.";
    private const string SmallCapacity = "capacity was less than the current size.";
    private const string CapacityPastMaximum = "Capacity exceeds maximum capacity.";
    private const string LengthPastCapacity = "The length cannot be greater than the capacity.";
    private const string NewLine = "\r\n";

    private char[] _chars;
    private int _length;
    private readonly int _maxCapacity;

    public StringBuilder()
    {
        _chars = new char[DefaultCapacity];
        _maxCapacity = int.MaxValue;
    }

    public StringBuilder(int capacity)
        : this(capacity, int.MaxValue)
    {
    }

    public StringBuilder(int capacity, int maxCapacity)
    {
        if (capacity > maxCapacity)
        {
            Runtime.ArgumentOutOfRangeOf("capacity", CapacityPastMaximum);
        }

        if (maxCapacity <= 0)
        {
            Runtime.ArgumentNotPositive("maxCapacity", maxCapacity);
        }

        if (capacity < 0)
        {
            Runtime.ArgumentNegative("capacity", capacity);
        }

        _chars = new char[capacity == 0 ? Min(DefaultCapacity, maxCapacity) : capacity];
        _maxCapacity = maxCapacity;
    }

    public StringBuilder(string? value)
        : this(value, DefaultCapacity)
    {
    }

    public StringBuilder(string? value, int capacity)
        : this(value, 0, value == null ? 0 : value.Length, capacity)
    {
    }

    public StringBuilder(string? value, int startIndex, int length, int capacity)
    {
        if (capacity < 0)
        {
            Runtime.ArgumentNegative("capacity", capacity);
        }

        if (length < 0)
        {
            Runtime.ArgumentNegative("length", length);
        }

        if (startIndex < 0)
        {
            Runtime.ArgumentNegative("startIndex", startIndex);
        }

        var text = value ?? "";
        if (startIndex > text.Length - length)
        {
            Runtime.ArgumentOutOfRangeOf("length", IndexLength);
        }

        _maxCapacity = int.MaxValue;
        _chars = new char[Max(capacity == 0 ? DefaultCapacity : capacity, length)];
        for (var i = 0; i < length; i++)
        {
            _chars[i] = text[startIndex + i];
        }

        _length = length;
    }

    public int Length
    {
        get => _length;
        set
        {
            if (value < 0)
            {
                Runtime.ArgumentNegative("value", value);
            }

            if (value > _maxCapacity)
            {
                Runtime.ArgumentOutOfRangeOf("value", SmallCapacity);
            }

            if (value > _length)
            {
                Append('\0', value - _length);
            }
            else
            {
                _length = value;
            }
        }
    }

    public int Capacity
    {
        get => _chars.Length;
        set
        {
            if (value < 0)
            {
                Runtime.ArgumentNegative("value", value);
            }

            if (value > _maxCapacity)
            {
                Runtime.ArgumentOutOfRangeOf("value", CapacityPastMaximum);
            }

            if (value < _length)
            {
                Runtime.ArgumentOutOfRangeOf("value", SmallCapacity);
            }

            if (value != _chars.Length)
            {
                Resize(value);
            }
        }
    }

    public int MaxCapacity => _maxCapacity;

    [System.Runtime.CompilerServices.IndexerName("Chars")]
    public char this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_length)
            {
                Runtime.IndexOutOfRange();
            }

            return _chars[index];
        }

        set
        {
            if ((uint)index >= (uint)_length)
            {
                Runtime.ArgumentOutOfRangeOf("index", Less);
            }

            _chars[index] = value;
        }
    }

    public int EnsureCapacity(int capacity)
    {
        if (capacity < 0)
        {
            Runtime.ArgumentNegative("capacity", capacity);
        }

        if (_chars.Length < capacity)
        {
            Capacity = capacity;
        }

        return _chars.Length;
    }

    public StringBuilder Clear()
    {
        _length = 0;
        return this;
    }

    public override string ToString() => new string(_chars, 0, _length);

    public string ToString(int startIndex, int length)
    {
        if (startIndex < 0)
        {
            Runtime.ArgumentNegative("startIndex", startIndex);
        }

        if (startIndex > _length)
        {
            Runtime.ArgumentOutOfRangeOf("startIndex", "startIndex cannot be larger than length of string.");
        }

        if (length < 0)
        {
            Runtime.ArgumentNegative("length", length);
        }

        if (startIndex > _length - length)
        {
            Runtime.ArgumentOutOfRangeOf("length", IndexLength);
        }

        return new string(_chars, startIndex, length);
    }

    /// <summary>True when <paramref name="sb"/> holds the same text (.NET compares neither capacity).</summary>
    public bool Equals(StringBuilder? sb)
    {
        if (sb == null || sb._length != _length)
        {
            return false;
        }

        for (var i = 0; i < _length; i++)
        {
            if (sb._chars[i] != _chars[i])
            {
                return false;
            }
        }

        return true;
    }

    public bool Equals(ReadOnlySpan<char> span)
    {
        if (span.Length != _length)
        {
            return false;
        }

        for (var i = 0; i < _length; i++)
        {
            if (span[i] != _chars[i])
            {
                return false;
            }
        }

        return true;
    }

    public void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count)
    {
        if (destination == null)
        {
            Runtime.ArgumentNullOf("destination");
        }

        if (destinationIndex < 0)
        {
            Runtime.ArgumentNegative("destinationIndex", destinationIndex);
        }

        if (destinationIndex > destination!.Length - count)
        {
            Runtime.ArgumentInvalid("Either offset did not refer to a position in the string, or there is an " +
                "insufficient length of destination character array.", null);
        }

        CopyTo(sourceIndex, new Span<char>(destination, destinationIndex, destination.Length - destinationIndex), count);
    }

    public void CopyTo(int sourceIndex, Span<char> destination, int count)
    {
        if (count < 0)
        {
            Runtime.ArgumentNegative("count", count);
        }

        if ((uint)sourceIndex > (uint)_length)
        {
            Runtime.ArgumentOutOfRangeOf("sourceIndex", LessOrEqual);
        }

        if (sourceIndex > _length - count)
        {
            Runtime.ArgumentInvalid("Source string was not long enough. Check sourceIndex and count.", null);
        }

        new ReadOnlySpan<char>(_chars, sourceIndex, count).CopyTo(destination);
    }

    // ---- Append ----------------------------------------------------------------------------------------------------

    public StringBuilder Append(char value)
    {
        if (_length == _chars.Length)
        {
            // (past the maximum capacity, .NET's message of a chunk that cannot be added)
            if (_length == _maxCapacity)
            {
                Runtime.ArgumentOutOfRangeOf("requiredLength", SmallCapacity);
            }

            Grow(1, "repeatCount");
        }

        _chars[_length++] = value;
        return this;
    }

    public StringBuilder Append(char value, int repeatCount)
    {
        if (repeatCount < 0)
        {
            Runtime.ArgumentNegative("repeatCount", repeatCount);
        }

        Room(repeatCount, "repeatCount");
        for (var i = 0; i < repeatCount; i++)
        {
            _chars[_length++] = value;
        }

        return this;
    }

    public StringBuilder Append(string? value)
    {
        if (value != null)
        {
            Room(value.Length, "valueCount");
            for (var i = 0; i < value.Length; i++)
            {
                _chars[_length++] = value[i];
            }
        }

        return this;
    }

    public StringBuilder Append(string? value, int startIndex, int count)
    {
        if (startIndex < 0)
        {
            Runtime.ArgumentNegative("startIndex", startIndex);
        }

        if (count < 0)
        {
            Runtime.ArgumentNegative("count", count);
        }

        if (value == null)
        {
            if (startIndex != 0 || count != 0)
            {
                Runtime.ArgumentNullOf("value");
            }

            return this;
        }

        if (count == 0)
        {
            return this;
        }

        if (startIndex > value.Length - count)
        {
            Runtime.ArgumentOutOfRangeOf("startIndex", LessOrEqual);
        }

        Room(count, "valueCount");
        for (var i = 0; i < count; i++)
        {
            _chars[_length++] = value[startIndex + i];
        }

        return this;
    }

    public StringBuilder Append(char[]? value)
    {
        if (value != null)
        {
            AppendChars(value, 0, value.Length);
        }

        return this;
    }

    public StringBuilder Append(char[]? value, int startIndex, int charCount)
    {
        if (startIndex < 0)
        {
            Runtime.ArgumentNegative("startIndex", startIndex);
        }

        if (charCount < 0)
        {
            Runtime.ArgumentNegative("charCount", charCount);
        }

        if (value == null)
        {
            if (startIndex != 0 || charCount != 0)
            {
                Runtime.ArgumentNullOf("value");
            }

            return this;
        }

        if (charCount > value.Length - startIndex)
        {
            Runtime.ArgumentOutOfRangeOf("charCount", LessOrEqual);
        }

        AppendChars(value, startIndex, charCount);
        return this;
    }

    public StringBuilder Append(ReadOnlySpan<char> value)
    {
        Room(value.Length, "valueCount");
        for (var i = 0; i < value.Length; i++)
        {
            _chars[_length++] = value[i];
        }

        return this;
    }

    public StringBuilder Append(StringBuilder? value)
    {
        if (value != null)
        {
            AppendBuilder(value, 0, value._length);
        }

        return this;
    }

    public StringBuilder Append(StringBuilder? value, int startIndex, int count)
    {
        if (startIndex < 0)
        {
            Runtime.ArgumentNegative("startIndex", startIndex);
        }

        if (count < 0)
        {
            Runtime.ArgumentNegative("count", count);
        }

        if (value == null)
        {
            if (startIndex != 0 || count != 0)
            {
                Runtime.ArgumentNullOf("value");
            }

            return this;
        }

        if (count == 0)
        {
            return this;
        }

        if (count > value._length - startIndex)
        {
            Runtime.ArgumentOutOfRangeOf("startIndex", LessOrEqual);
        }

        AppendBuilder(value, startIndex, count);
        return this;
    }

    public StringBuilder Append(bool value) => Append(value ? "True" : "False");

    public StringBuilder Append(sbyte value) => Append(value.ToString());

    public StringBuilder Append(byte value) => Append(value.ToString());

    public StringBuilder Append(short value) => Append(value.ToString());

    public StringBuilder Append(ushort value) => Append(value.ToString());

    public StringBuilder Append(int value) => Append(value.ToString());

    public StringBuilder Append(uint value) => Append(value.ToString());

    public StringBuilder Append(long value) => Append(value.ToString());

    public StringBuilder Append(ulong value) => Append(value.ToString());

    public StringBuilder Append(float value) => Append(value.ToString());

    public StringBuilder Append(double value) => Append(value.ToString());

    public StringBuilder Append(object? value) => value == null ? this : Append(value.ToString());

    public StringBuilder AppendLine() => Append(NewLine);

    public StringBuilder AppendLine(string? value)
    {
        Append(value);
        return Append(NewLine);
    }

    public StringBuilder AppendJoin(string? separator, params string?[] values)
    {
        if (values == null)
        {
            Runtime.ArgumentNullOf("values");
        }

        for (var i = 0; i < values!.Length; i++)
        {
            if (i > 0)
            {
                Append(separator);
            }

            Append(values[i]);
        }

        return this;
    }

    public StringBuilder AppendJoin(string? separator, params object?[] values)
    {
        if (values == null)
        {
            Runtime.ArgumentNullOf("values");
        }

        for (var i = 0; i < values!.Length; i++)
        {
            if (i > 0)
            {
                Append(separator);
            }

            Append(values[i]);
        }

        return this;
    }

    public StringBuilder AppendJoin(char separator, params string?[] values)
    {
        if (values == null)
        {
            Runtime.ArgumentNullOf("values");
        }

        for (var i = 0; i < values!.Length; i++)
        {
            if (i > 0)
            {
                Append(separator);
            }

            Append(values[i]);
        }

        return this;
    }

    public StringBuilder AppendJoin(char separator, params object?[] values)
    {
        if (values == null)
        {
            Runtime.ArgumentNullOf("values");
        }

        for (var i = 0; i < values!.Length; i++)
        {
            if (i > 0)
            {
                Append(separator);
            }

            Append(values[i]);
        }

        return this;
    }

    // ---- Insert, Remove, Replace -------------------------------------------------------------------------------------

    public StringBuilder Insert(int index, string? value)
    {
        if ((uint)index > (uint)_length)
        {
            Runtime.ArgumentOutOfRangeOf("index", LessOrEqual);
        }

        if (value != null && value.Length > 0)
        {
            Open(index, value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                _chars[index + i] = value[i];
            }
        }

        return this;
    }

    public StringBuilder Insert(int index, string? value, int count)
    {
        if (count < 0)
        {
            Runtime.ArgumentNegative("count", count);
        }

        if ((uint)index > (uint)_length)
        {
            Runtime.ArgumentOutOfRangeOf("index", LessOrEqual);
        }

        if (value == null || value.Length == 0 || count == 0)
        {
            return this;
        }

        if ((long)value.Length * count > _maxCapacity - _length)
        {
            Runtime.OutOfMemory();
        }

        Open(index, value.Length * count);
        for (var copy = 0; copy < count; copy++)
        {
            for (var i = 0; i < value.Length; i++)
            {
                _chars[index++] = value[i];
            }
        }

        return this;
    }

    public StringBuilder Insert(int index, char value)
    {
        if ((uint)index > (uint)_length)
        {
            Runtime.ArgumentOutOfRangeOf("index", LessOrEqual);
        }

        Open(index, 1);
        _chars[index] = value;
        return this;
    }

    public StringBuilder Insert(int index, char[]? value)
    {
        if ((uint)index > (uint)_length)
        {
            Runtime.ArgumentOutOfRangeOf("index", LessOrEqual);
        }

        if (value != null)
        {
            InsertChars(index, value, 0, value.Length);
        }

        return this;
    }

    public StringBuilder Insert(int index, char[]? value, int startIndex, int charCount)
    {
        if ((uint)index > (uint)_length)
        {
            Runtime.ArgumentOutOfRangeOf("index", LessOrEqual);
        }

        if (value == null)
        {
            if (startIndex != 0 || charCount != 0)
            {
                Runtime.ArgumentNullOf("value");
            }

            return this;
        }

        if (startIndex < 0)
        {
            Runtime.ArgumentNegative("startIndex", startIndex);
        }

        if (charCount < 0)
        {
            Runtime.ArgumentNegative("charCount", charCount);
        }

        if (startIndex > value.Length - charCount)
        {
            Runtime.ArgumentOutOfRangeOf("startIndex", LessOrEqual);
        }

        InsertChars(index, value, startIndex, charCount);
        return this;
    }

    public StringBuilder Insert(int index, ReadOnlySpan<char> value)
    {
        if ((uint)index > (uint)_length)
        {
            Runtime.ArgumentOutOfRangeOf("index", LessOrEqual);
        }

        if (value.Length > 0)
        {
            Open(index, value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                _chars[index + i] = value[i];
            }
        }

        return this;
    }

    public StringBuilder Insert(int index, bool value) => Insert(index, value ? "True" : "False", 1);

    public StringBuilder Insert(int index, sbyte value) => Insert(index, value.ToString(), 1);

    public StringBuilder Insert(int index, byte value) => Insert(index, value.ToString(), 1);

    public StringBuilder Insert(int index, short value) => Insert(index, value.ToString(), 1);

    public StringBuilder Insert(int index, ushort value) => Insert(index, value.ToString(), 1);

    public StringBuilder Insert(int index, int value) => Insert(index, value.ToString(), 1);

    public StringBuilder Insert(int index, uint value) => Insert(index, value.ToString(), 1);

    public StringBuilder Insert(int index, long value) => Insert(index, value.ToString(), 1);

    public StringBuilder Insert(int index, ulong value) => Insert(index, value.ToString(), 1);

    public StringBuilder Insert(int index, float value) => Insert(index, value.ToString(), 1);

    public StringBuilder Insert(int index, double value) => Insert(index, value.ToString(), 1);

    public StringBuilder Insert(int index, object? value) => value == null ? this : Insert(index, value.ToString(), 1);

    public StringBuilder Remove(int startIndex, int length)
    {
        if (length < 0)
        {
            Runtime.ArgumentNegative("length", length);
        }

        if (startIndex < 0)
        {
            Runtime.ArgumentNegative("startIndex", startIndex);
        }

        if (length > _length - startIndex)
        {
            Runtime.ArgumentOutOfRangeOf("length", LessOrEqual);
        }

        if (length > 0)
        {
            System.Array.Copy(_chars, startIndex + length, _chars, startIndex, _length - startIndex - length);
            _length -= length;
        }

        return this;
    }

    public StringBuilder Replace(char oldChar, char newChar) => Replace(oldChar, newChar, 0, _length);

    public StringBuilder Replace(char oldChar, char newChar, int startIndex, int count)
    {
        CheckRange(startIndex, count);
        for (var i = startIndex; i < startIndex + count; i++)
        {
            if (_chars[i] == oldChar)
            {
                _chars[i] = newChar;
            }
        }

        return this;
    }

    public StringBuilder Replace(string oldValue, string? newValue) => Replace(oldValue, newValue, 0, _length);

    public StringBuilder Replace(string oldValue, string? newValue, int startIndex, int count)
    {
        if (oldValue == null)
        {
            Runtime.ArgumentNullOf("oldValue");
        }

        if (oldValue!.Length == 0)
        {
            Runtime.ArgumentInvalid("The value cannot be an empty string.", "oldValue");
        }

        CheckRange(startIndex, count);

        // the matches in the range, left to right, none overlapping another
        var end = startIndex + count;
        var matches = 0;
        for (var i = startIndex; i <= end - oldValue.Length;)
        {
            if (At(i, oldValue))
            {
                matches++;
                i += oldValue.Length;
            }
            else
            {
                i++;
            }
        }

        if (matches == 0)
        {
            return this;
        }

        // longer text: .NET makes room for all of it at once, in a chunk of its own before the rest
        var replacement = newValue ?? "";
        var delta = (long)(replacement.Length - oldValue.Length) * matches;
        if (delta > _maxCapacity - _length)
        {
            Runtime.ArgumentOutOfRangeOf("requiredLength", SmallCapacity);
        }

        var chars = delta > 0 ? new char[_chars.Length + (int)delta] : _chars;
        if (delta > 0)
        {
            System.Array.Copy(_chars, chars, startIndex);
        }

        // copied from left to right: in place, the text never moves to the right of where it is read
        var to = startIndex;
        for (var from = startIndex; from < _length;)
        {
            if (from <= end - oldValue.Length && At(from, oldValue))
            {
                for (var j = 0; j < replacement.Length; j++)
                {
                    chars[to++] = replacement[j];
                }

                from += oldValue.Length;
            }
            else
            {
                chars[to++] = _chars[from++];
            }
        }

        _chars = chars;
        _length = to;
        return this;
    }

    // ---- The buffer --------------------------------------------------------------------------------------------------

    /// <summary>.NET's checks of a range of Replace: its start within the text, its count not negative nor past the end.</summary>
    private void CheckRange(int startIndex, int count)
    {
        if ((uint)startIndex > (uint)_length)
        {
            Runtime.ArgumentOutOfRangeOf("startIndex", LessOrEqual);
        }

        if (count < 0 || startIndex > _length - count)
        {
            Runtime.ArgumentOutOfRangeOf("count", LessOrEqual);
        }
    }

    /// <summary>True when <paramref name="value"/> is at <paramref name="index"/> of the text.</summary>
    private bool At(int index, string value)
    {
        for (var j = 0; j < value.Length; j++)
        {
            if (_chars[index + j] != value[j])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Appends <paramref name="count"/> chars of <paramref name="chars"/> from <paramref name="start"/>.</summary>
    private void AppendChars(char[] chars, int start, int count)
    {
        Room(count, "valueCount");
        System.Array.Copy(chars, start, _chars, _length, count);
        _length += count;
    }

    /// <summary>
    /// Appends <paramref name="count"/> chars of the text of <paramref name="value"/> from <paramref name="start"/>, as
    /// .NET does: past the maximum capacity it names Capacity; of the builder itself, it copies them before it grows.
    /// </summary>
    private void AppendBuilder(StringBuilder value, int start, int count)
    {
        if (count > _maxCapacity - _length)
        {
            Runtime.ArgumentOutOfRangeOf("Capacity", CapacityPastMaximum);
        }

        var chars = value._chars;
        Room(count, "valueCount");
        System.Array.Copy(chars, start, _chars, _length, count);
        _length += count;
    }

    /// <summary>Inserts <paramref name="count"/> chars of <paramref name="chars"/> from <paramref name="start"/>.</summary>
    private void InsertChars(int index, char[] chars, int start, int count)
    {
        if (count > 0)
        {
            Open(index, count);
            System.Array.Copy(chars, start, _chars, index, count);
        }
    }

    /// <summary>
    /// Makes room for <paramref name="count"/> more chars at the end: grows the buffer when they do not fit, or stops
    /// the program with .NET's message (naming <paramref name="parameter"/>) when they would pass the maximum capacity.
    /// </summary>
    private void Room(int count, string parameter)
    {
        if (count > _chars.Length - _length)
        {
            Grow(count, parameter);
        }
    }

    /// <summary>
    /// Grows the buffer for <paramref name="count"/> chars more than it holds, as .NET's capacity grows when it adds a
    /// chunk: by what does not fit, and at least by its capacity so far (at most 8000).
    /// </summary>
    private void Grow(int count, string parameter)
    {
        if (count > _maxCapacity - _length)
        {
            Runtime.ArgumentOutOfRangeOf(parameter, LengthPastCapacity);
        }

        var capacity = _chars.Length;
        var missing = count - (capacity - _length);
        Resize(capacity + Max(missing, Min(capacity, MaxChunkSize)));
    }

    /// <summary>
    /// Opens a gap of <paramref name="count"/> chars at <paramref name="index"/>, moving the chars after it along. Like
    /// .NET's, a builder with room enough and at most 32 chars moves them in place; any other grows by what is inserted,
    /// as .NET's capacity does when it puts a chunk for them before its last one.
    /// </summary>
    private void Open(int index, int count)
    {
        if (count > _maxCapacity - _length)
        {
            Runtime.ArgumentOutOfRangeOf("requiredLength", SmallCapacity);
        }

        if (_length > DefaultCapacity * 2 || count > _chars.Length - _length)
        {
            Resize(_chars.Length + count);
        }

        System.Array.Copy(_chars, index, _chars, index + count, _length - index);
        _length += count;
    }

    private static int Max(int a, int b) => a > b ? a : b;

    private static int Min(int a, int b) => a < b ? a : b;

    /// <summary>A new buffer of <paramref name="capacity"/> chars, which starts with the text.</summary>
    private void Resize(int capacity)
    {
        var chars = new char[capacity];
        System.Array.Copy(_chars, chars, _length);
        _chars = chars;
    }
}
