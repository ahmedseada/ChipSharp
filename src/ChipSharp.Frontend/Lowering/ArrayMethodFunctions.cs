using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The TIR functions the methods of System.Array call (see <see cref="ArrayMethods"/>), each added to the module when
/// first needed: once for every element size where only the bytes matter (a copy, Clear), once per element type where
/// the elements do (Fill, IndexOf, Reverse), once per array type where a new array is made (Resize, Clone). Each checks
/// its arguments as .NET does, in .NET's order, and stops the program with .NET's message where .NET throws.
/// <code>
/// function void ChipSharp.Array.Copy(ChipSharp.Array %source, i32 %sourceIndex, ChipSharp.Array %destination,
///     i32 %destinationIndex, i32 %length, i32 %size)
/// function i32 ChipSharp.Array.Narrow.length(i64 %value)          (and sourceIndex, destinationIndex, index)
/// function void ChipSharp.Array.CopyTo(ChipSharp.Array %array, ChipSharp.Array %destination, i64 %index, i32 %size)
/// function void ChipSharp.Array.Clear(ChipSharp.Array %array, i32 %size)   (and ClearRange, with an index and length)
/// function void ChipSharp.Array.Fill&lt;i32&gt;(ChipSharp.Array %array, i32 %value)   (and FillRange)
/// function i32 ChipSharp.Array.IndexOf&lt;i32&gt;(ChipSharp.Array %array, i32 %value, i32 %startIndex, i32 %count)
/// function void ChipSharp.Array.Reverse&lt;i32&gt;(ChipSharp.Array %array, i32 %index, i32 %length)
/// function ChipSharp.Array ChipSharp.Array.Resize&lt;int[]&gt;(ChipSharp.Array %array, i32 %newSize)   (and Clone)
/// external function ptr memmove(ptr, ptr, nint)                   (and memset)
/// external function void ChipSharp.Runtime.SourceArrayNull()       (and the other failures)
/// </code>
/// The elements move with the C library's memmove (an overlapping copy within one array comes out as .NET's does) and
/// are cleared with memset, which every runtime has. Nothing here allocates but Resize and Clone, which make the new
/// array before they copy into it: so no collection can come while the references of an array are being copied,
/// cleared or moved (the collector runs only when an allocation needs it, and never in an interrupt handler).
/// </summary>
internal sealed class ArrayMethodFunctions(TirModule module, ArrayFunctions arrays)
{
    public const string ArrayNull = "ChipSharp.Runtime.ArrayNull";
    public const string SourceArrayNull = "ChipSharp.Runtime.SourceArrayNull";
    public const string DestinationArrayNull = "ChipSharp.Runtime.DestinationArrayNull";
    public const string SourceArrayTooShort = "ChipSharp.Runtime.SourceArrayTooShort";
    public const string DestinationArrayTooShort = "ChipSharp.Runtime.DestinationArrayTooShort";
    public const string LengthBelowZero = "ChipSharp.Runtime.LengthBelowZero";
    public const string SourceIndexBelowZero = "ChipSharp.Runtime.SourceIndexBelowZero";
    public const string DestinationIndexBelowZero = "ChipSharp.Runtime.DestinationIndexBelowZero";
    public const string StartIndexPastEnd = "ChipSharp.Runtime.StartIndexPastEnd";
    public const string StartIndexAtEnd = "ChipSharp.Runtime.StartIndexAtEnd";
    public const string CountOutOfRange = "ChipSharp.Runtime.CountOutOfRange";
    public const string NegativeIndex = "ChipSharp.Runtime.NegativeIndex";
    public const string NegativeLength = "ChipSharp.Runtime.NegativeLength";
    public const string NegativeNewSize = "ChipSharp.Runtime.NegativeNewSize";
    public const string InvalidOffsetLength = "ChipSharp.Runtime.InvalidOffsetLength";
    public const string NullReference = "ChipSharp.Runtime.NullReference";

    /// <summary>The parameters of the long overloads that a value too big for an int stops: ArgumentOutOfRangeException.</summary>
    public static IReadOnlyList<string> HugeParameters { get; } = ["sourceIndex", "destinationIndex", "length", "index"];

    /// <summary>
    /// Every failure these functions call, for the runtimes' tests: each runtime's failures file defines them all (the
    /// ...BelowZero ones take the value, an int).
    /// </summary>
    public static IReadOnlyList<string> Failures { get; } =
    [
        ArrayNull, SourceArrayNull, DestinationArrayNull, SourceArrayTooShort, DestinationArrayTooShort,
        LengthBelowZero, SourceIndexBelowZero, DestinationIndexBelowZero, StartIndexPastEnd, StartIndexAtEnd,
        CountOutOfRange, NegativeIndex, NegativeLength, NegativeNewSize, InvalidOffsetLength,
        .. HugeParameters.Select(Huge),
    ];

    private static readonly TirStructType ArrayType = StackArrays.ArrayType;

    private readonly Dictionary<(string Name, object? Key), TirFunction> _functions = [];

    /// <summary>
    /// <c>(source, sourceIndex, destination, destinationIndex, length, size)</c>: Array.Copy of elements of
    /// <c>size</c> bytes, after .NET's checks in .NET's order (null source, null destination, a negative length, a
    /// negative source index, a source too short, a negative destination index, a destination too short); the 3-argument
    /// Copy is this with both indexes 0, ConstrainedCopy the same.
    /// </summary>
    public TirFunction Copy => Get("Copy", null, () =>
    {
        var (function, ir) = Define("ChipSharp.Array.Copy", TirType.Void, ("source", ArrayType), ("sourceIndex", TirType.I32),
            ("destination", ArrayType), ("destinationIndex", TirType.I32), ("length", TirType.I32), ("size", TirType.I32));
        var p = function.Parameters;
        var (source, sourceIndex, destination, destinationIndex, length, size) = (p[0], p[1], p[2], p[3], p[4], p[5]);
        Check(ir, NotNull(ir, source), SourceArrayNull);
        Check(ir, NotNull(ir, destination), DestinationArrayNull);
        Check(ir, NotNegative(ir, length), LengthBelowZero, length);
        Check(ir, NotNegative(ir, sourceIndex), SourceIndexBelowZero, sourceIndex);
        Check(ir, Within(ir, sourceIndex, length, source), SourceArrayTooShort);
        Check(ir, NotNegative(ir, destinationIndex), DestinationIndexBelowZero, destinationIndex);
        Check(ir, Within(ir, destinationIndex, length, destination), DestinationArrayTooShort);
        ir.Call(Memmove, Element(ir, destination, destinationIndex, size), Element(ir, source, sourceIndex, size),
            Bytes(ir, length, size));
        ir.Return();
        return function;
    });

    /// <summary>
    /// <c>(value)</c>: a long argument of a long overload (Copy's, CopyTo's) as the int .NET makes it, after .NET's check
    /// that it is one: <c>Arrays larger than 2GB are not supported.</c> for <paramref name="parameter"/>.
    /// </summary>
    public TirFunction Narrow(string parameter) => Get("Narrow", parameter, () =>
    {
        var (function, ir) = Define($"ChipSharp.Array.Narrow.{parameter}", TirType.I32, ("value", TirType.I64));
        var value = function.Parameters[0];
        Check(ir, ir.Compare(TirCompareOp.Eq, ir.Convert(ir.Convert(value, TirType.I32), TirType.I64), value),
            Huge(parameter));
        ir.Return(ir.Convert(value, TirType.I32));
        return function;
    });

    /// <summary>
    /// <c>(array, destination, index, size)</c>: <c>array.CopyTo(destination, index)</c>, Array.Copy of every element
    /// to index on. The call of a method of null stops the program first (NullReferenceException), then an index of the
    /// long overload that is no int.
    /// </summary>
    public TirFunction CopyTo => Get("CopyTo", null, () =>
    {
        var (function, ir) = Define("ChipSharp.Array.CopyTo", TirType.Void, ("array", ArrayType),
            ("destination", ArrayType), ("index", TirType.I64), ("size", TirType.I32));
        var (array, destination, index, size) =
            (function.Parameters[0], function.Parameters[1], function.Parameters[2], function.Parameters[3]);
        Check(ir, NotNull(ir, array), NullReference);
        var narrowed = ir.Call(Narrow("index"), index);
        ir.Call(Copy, array, ir.Const(TirType.I32, 0), destination, narrowed, Length(ir, array), size);
        ir.Return();
        return function;
    });

    /// <summary><c>(array, size)</c>: Array.Clear(array), every element zero (null, default).</summary>
    public TirFunction Clear => Get("Clear", null, () =>
    {
        var (function, ir) = Define("ChipSharp.Array.Clear", TirType.Void, ("array", ArrayType), ("size", TirType.I32));
        var (array, size) = (function.Parameters[0], function.Parameters[1]);
        Check(ir, NotNull(ir, array), ArrayNull);
        ir.Call(Memset, Data(ir, array), ir.Const(TirType.I32, 0), Bytes(ir, Length(ir, array), size));
        ir.Return();
        return function;
    });

    /// <summary>
    /// <c>(array, index, length, size)</c>: Array.Clear(array, index, length); an index or length outside the array is
    /// .NET's IndexOutOfRangeException.
    /// </summary>
    public TirFunction ClearRange => Get("ClearRange", null, () =>
    {
        var (function, ir) = Define("ChipSharp.Array.ClearRange", TirType.Void, ("array", ArrayType), ("index", TirType.I32),
            ("length", TirType.I32), ("size", TirType.I32));
        var (array, index, length, size) =
            (function.Parameters[0], function.Parameters[1], function.Parameters[2], function.Parameters[3]);
        Check(ir, NotNull(ir, array), ArrayNull);
        Check(ir, NotNegative(ir, index), StackArrays.IndexOutOfRange);
        Check(ir, NotNegative(ir, length), StackArrays.IndexOutOfRange);
        Check(ir, Within(ir, index, length, array), StackArrays.IndexOutOfRange);
        ir.Call(Memset, Element(ir, array, index, size), ir.Const(TirType.I32, 0), Bytes(ir, length, size));
        ir.Return();
        return function;
    });

    /// <summary><c>(array, value)</c>: Array.Fill(array, value), every element the value.</summary>
    public TirFunction Fill(TirType element) => Get("Fill", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Array.Fill<{element}>", TirType.Void, ("array", ArrayType), ("value", element));
        var (array, value) = (function.Parameters[0], function.Parameters[1]);
        Check(ir, NotNull(ir, array), ArrayNull);
        Loop(ir, ir.Const(TirType.I32, 0), Length(ir, array), (ir, i) => ir.Store(Element(ir, array, element, i()), value));
        ir.Return();
        return function;
    });

    /// <summary>
    /// <c>(array, value, startIndex, count)</c>: Array.Fill(array, value, startIndex, count), after .NET's checks (read
    /// as unsigned: a start past the end, a count past it).
    /// </summary>
    public TirFunction FillRange(TirType element) => Get("FillRange", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Array.FillRange<{element}>", TirType.Void, ("array", ArrayType),
            ("value", element), ("startIndex", TirType.I32), ("count", TirType.I32));
        var (array, value, start, count) =
            (function.Parameters[0], function.Parameters[1], function.Parameters[2], function.Parameters[3]);
        Check(ir, NotNull(ir, array), ArrayNull);
        CheckStartAndCount(ir, array, start, count);
        Loop(ir, start, ir.Binary(TirBinaryOp.Add, start, count),
            (ir, i) => ir.Store(Element(ir, array, element, i()), value));
        ir.Return();
        return function;
    });

    /// <summary>
    /// <c>(array, value[, startIndex[, count]])</c>: Array.IndexOf with <paramref name="arguments"/> arguments (2, 3 or
    /// 4), after .NET's checks: the index of the first element equal to the value from startIndex on, or -1.
    /// </summary>
    public TirFunction IndexOf(TirType element, int arguments) => Get($"IndexOf{arguments}", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Array.IndexOf<{element}>", TirType.I32,
            [("array", ArrayType), ("value", element), .. Optional(arguments - 2, ("startIndex", TirType.I32),
                ("count", TirType.I32))]);
        var (array, value) = (function.Parameters[0], function.Parameters[1]);
        Check(ir, NotNull(ir, array), ArrayNull);
        if (arguments > 2)
        {
            CheckStartAndCount(ir, array, function.Parameters[2], arguments > 3 ? function.Parameters[3] : null);
        }

        TirValue start = arguments > 2 ? function.Parameters[2] : ir.Const(TirType.I32, 0);
        TirValue count = arguments > 3 ? function.Parameters[3] : ir.Binary(TirBinaryOp.Sub, Length(ir, array), start);
        ir.Return(ir.Call(Search(element), array, value, start, count));
        return function;
    });

    /// <summary>
    /// <c>(array, value[, startIndex[, count]])</c>: Array.LastIndexOf with <paramref name="arguments"/> arguments,
    /// after .NET's checks: the index of the last element equal to the value from startIndex back, count elements, or
    /// -1. Without a count it is startIndex + 1 (0 for an empty array); without a start, the last element. An empty
    /// array takes a startIndex of -1 or 0 and a count of 0, as .NET does.
    /// </summary>
    public TirFunction LastIndexOf(TirType element, int arguments) => Get($"LastIndexOf{arguments}", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Array.LastIndexOf<{element}>", TirType.I32,
            [("array", ArrayType), ("value", element), .. Optional(arguments - 2, ("startIndex", TirType.I32),
                ("count", TirType.I32))]);
        var (array, value) = (function.Parameters[0], function.Parameters[1]);
        Check(ir, NotNull(ir, array), ArrayNull);

        // worked out again in each block: the start (the last element, without one) and the count
        TirValue Start() => arguments > 2 ? function.Parameters[2]
            : ir.Binary(TirBinaryOp.Sub, Length(ir, array), ir.Const(TirType.I32, 1));
        TirValue Count() => arguments > 3 ? function.Parameters[3]
            : arguments > 2 ? ir.Binary(TirBinaryOp.Add, Start(), ir.Const(TirType.I32, 1))
            : Length(ir, array);
        TirValue Zero() => ir.Const(TirType.I32, 0);

        var empty = function.AddBlock("empty");
        var notEmpty = function.AddBlock("not_empty");
        ir.Branch(ir.Compare(TirCompareOp.Eq, Length(ir, array), Zero()), empty, notEmpty);

        // an empty array: a start of -1 or 0 finds nothing, with a count of 0 (which the overloads without one give)
        ir.Block = empty;
        var startOk = ir.Binary(TirBinaryOp.Or,
            ir.Convert(ir.Compare(TirCompareOp.Eq, Start(), ir.Const(TirType.I32, -1)), TirType.I32),
            ir.Convert(ir.Compare(TirCompareOp.Eq, Start(), Zero()), TirType.I32));
        Check(ir, ir.Compare(TirCompareOp.Ne, startOk, Zero()), StartIndexAtEnd);
        if (arguments > 3)
        {
            Check(ir, ir.Compare(TirCompareOp.Eq, function.Parameters[3], Zero()), CountOutOfRange);
        }

        ir.Return(ir.Const(TirType.I32, -1));

        // from the start back to element 0, or count elements
        ir.Block = notEmpty;
        Check(ir, ir.Compare(TirCompareOp.LtUnsigned, Start(), Length(ir, array)), StartIndexAtEnd);
        Check(ir, NotNegative(ir, Count()), CountOutOfRange);
        var first = ir.Binary(TirBinaryOp.Add, ir.Binary(TirBinaryOp.Sub, Start(), Count()), ir.Const(TirType.I32, 1));
        Check(ir, NotNegative(ir, first), CountOutOfRange);
        ir.Return(ir.Call(SearchLast(element), array, value, Start(), Count()));
        return function;
    });

    /// <summary>
    /// <c>(array[, index, length])</c>: Array.Reverse, of the whole array or of length elements from index, after .NET's
    /// checks (a negative index, a negative length, both past the end).
    /// </summary>
    public TirFunction Reverse(TirType element, bool range) => Get(range ? "ReverseRange" : "Reverse", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Array.{(range ? "ReverseRange" : "Reverse")}<{element}>", TirType.Void,
            [("array", ArrayType), .. Optional(range ? 2 : 0, ("index", TirType.I32), ("length", TirType.I32))]);
        var array = function.Parameters[0];
        Check(ir, NotNull(ir, array), ArrayNull);
        if (range)
        {
            var (start, count) = (function.Parameters[1], function.Parameters[2]);
            Check(ir, NotNegative(ir, start), NegativeIndex);
            Check(ir, NotNegative(ir, count), NegativeLength);
            Check(ir, ir.Compare(TirCompareOp.Ge, ir.Binary(TirBinaryOp.Sub, Length(ir, array), start), count),
                InvalidOffsetLength);
        }

        TirValue index = range ? function.Parameters[1] : ir.Const(TirType.I32, 0);
        TirValue length = range ? function.Parameters[2] : Length(ir, array);

        // i from the first element up, j from the last down, until they meet
        var j = function.AddSlot("j", TirType.I32);
        ir.Store(j, ir.Binary(TirBinaryOp.Sub, ir.Binary(TirBinaryOp.Add, index, length), ir.Const(TirType.I32, 1)));
        var half = ir.Binary(TirBinaryOp.Add, index, ir.Binary(TirBinaryOp.Div, length, ir.Const(TirType.I32, 2)));
        Loop(ir, index, half, (ir, i) =>
        {
            var last = ir.Load(j);
            var (a, b) = (Element(ir, array, element, i()), Element(ir, array, element, last));
            var first = ir.Load(element, a);
            ir.Store(a, ir.Load(element, b));
            ir.Store(b, first);
            ir.Store(j, ir.Binary(TirBinaryOp.Sub, last, ir.Const(TirType.I32, 1)));
        });
        ir.Return();
        return function;
    });

    /// <summary>
    /// <c>(array, newSize)</c>: Array.Resize of an array of type <paramref name="descriptor"/>: the array itself when it
    /// has newSize elements already, else a new one with as many of its elements as fit (null: a new array of newSize);
    /// a negative newSize stops the program first.
    /// </summary>
    public TirFunction Resize(TirTypeDescriptor descriptor) => Get("Resize", descriptor, () =>
    {
        var element = descriptor.ElementType!;
        var (function, ir) = Define($"ChipSharp.Array.Resize<{descriptor.Name}>", ArrayType, ("array", ArrayType),
            ("newSize", TirType.I32));
        var (array, newSize) = (function.Parameters[0], function.Parameters[1]);
        Check(ir, NotNegative(ir, newSize), NegativeNewSize);
        var same = function.AddBlock("same");
        var other = function.AddBlock("other");
        var both = ir.Binary(TirBinaryOp.And, ir.Convert(NotNull(ir, array), TirType.I32),
            ir.Convert(ir.Compare(TirCompareOp.Eq, Length(ir, array), newSize), TirType.I32));
        ir.Branch(ir.Compare(TirCompareOp.Ne, both, ir.Const(TirType.I32, 0)), same, other);

        ir.Block = same;
        ir.Return(array);

        // null's length is 0, so it gives a new array of zeros
        ir.Block = other;
        var made = ir.NewArray(descriptor, newSize);
        var data = ir.ArrayData(made, element);

        // as many of the elements as fit: the smaller of the two lengths, both not negative (null's is 0)
        var length = Length(ir, array);
        var shorter = ir.Convert(ir.Compare(TirCompareOp.Lt, newSize, length), TirType.I32);
        var kept = ir.Binary(TirBinaryOp.Add, length, ir.Binary(TirBinaryOp.And,
            ir.Binary(TirBinaryOp.Sub, newSize, length), ir.Binary(TirBinaryOp.Sub, ir.Const(TirType.I32, 0), shorter)));
        ir.Call(Memmove, data, Data(ir, array), Bytes(ir, kept, ir.SizeOf(element)));
        ir.Return(ir.Call(arrays.Make, data, newSize));
        return function;
    });

    /// <summary>
    /// <c>(array)</c>: <c>array.Clone()</c> of an array of type <paramref name="descriptor"/>, a new array with the same
    /// elements; the call of a method of null stops the program (NullReferenceException).
    /// </summary>
    public TirFunction Clone(TirTypeDescriptor descriptor) => Get("Clone", descriptor, () =>
    {
        var element = descriptor.ElementType!;
        var (function, ir) = Define($"ChipSharp.Array.Clone<{descriptor.Name}>", ArrayType, ("array", ArrayType));
        var array = function.Parameters[0];
        Check(ir, NotNull(ir, array), NullReference);
        var length = Length(ir, array);
        var made = ir.NewArray(descriptor, length);
        var data = ir.ArrayData(made, element);
        ir.Call(Memmove, data, Data(ir, array), Bytes(ir, length, ir.SizeOf(element)));
        ir.Return(ir.Call(arrays.Make, data, length));
        return function;
    });

    /// <summary>The runtime function a long argument too big for an int calls, e.g. ChipSharp.Runtime.HugeSourceIndex.</summary>
    private static string Huge(string parameter) =>
        $"ChipSharp.Runtime.Huge{char.ToUpperInvariant(parameter[0])}{parameter[1..]}";

    /// <summary>
    /// <c>(array, value, startIndex, count)</c>: the index of the first of the count elements from startIndex on that is
    /// equal to the value (see <see cref="Equal"/>), or -1; the arguments are checked already.
    /// </summary>
    private TirFunction Search(TirType element) => Get("Search", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Array.Search<{element}>", TirType.I32, ("array", ArrayType),
            ("value", element), ("startIndex", TirType.I32), ("count", TirType.I32));
        var p = function.Parameters;
        var (array, value, start, count) = (p[0], p[1], p[2], p[3]);
        Loop(ir, start, ir.Binary(TirBinaryOp.Add, start, count), (ir, i) =>
        {
            var found = function.AddBlock($"found{function.Blocks.Count}");
            var next = function.AddBlock($"next{function.Blocks.Count}");
            ir.Branch(Equal(ir, ir.Load(element, Element(ir, array, element, i())), value), found, next);
            ir.Block = found;
            ir.Return(i());
            ir.Block = next;
        });
        ir.Return(ir.Const(TirType.I32, -1));
        return function;
    });

    /// <summary>
    /// <c>(array, value, startIndex, count)</c>: the index of the last element equal to the value from startIndex back
    /// (count elements), or -1; the arguments are checked already.
    /// </summary>
    private TirFunction SearchLast(TirType element) => Get("SearchLast", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Array.SearchLast<{element}>", TirType.I32, ("array", ArrayType),
            ("value", element), ("startIndex", TirType.I32), ("count", TirType.I32));
        var p = function.Parameters;
        var (array, value, start, count) = (p[0], p[1], p[2], p[3]);

        // i counts the elements looked at; the element is startIndex - i
        Loop(ir, ir.Const(TirType.I32, 0), count, (ir, i) =>
        {
            var found = function.AddBlock($"found{function.Blocks.Count}");
            var next = function.AddBlock($"next{function.Blocks.Count}");
            var index = ir.Binary(TirBinaryOp.Sub, start, i());
            ir.Branch(Equal(ir, ir.Load(element, Element(ir, array, element, index)), value), found, next);
            ir.Block = found;
            ir.Return(ir.Binary(TirBinaryOp.Sub, start, i()));
            ir.Block = next;
        });
        ir.Return(ir.Const(TirType.I32, -1));
        return function;
    });

    /// <summary>
    /// True when two elements are equal as .NET's EqualityComparer says: whole numbers, bools (0 or 1), enums and
    /// references by value; floats as their Equals, where NaN equals NaN (and -0.0 equals 0.0).
    /// </summary>
    private static TirValue Equal(TirBuilder ir, TirValue a, TirValue b)
    {
        var equal = ir.Compare(TirCompareOp.Eq, a, b);
        if (!a.Type.IsFloat)
        {
            return equal;
        }

        // NaN is the only value not equal to itself
        TirValue AsInt(TirValue condition) => ir.Convert(condition, TirType.I32);
        var aIsNaN = ir.Compare(TirCompareOp.Ne, AsInt(ir.Compare(TirCompareOp.Eq, a, a)), ir.Const(TirType.I32, 1));
        var bIsNaN = ir.Compare(TirCompareOp.Ne, AsInt(ir.Compare(TirCompareOp.Eq, b, b)), ir.Const(TirType.I32, 1));
        var either = ir.Binary(TirBinaryOp.Or, AsInt(equal), ir.Binary(TirBinaryOp.And, AsInt(aIsNaN), AsInt(bIsNaN)));
        return ir.Compare(TirCompareOp.Ne, either, ir.Const(TirType.I32, 0));
    }

    /// <summary>
    /// .NET's checks of a start and a count (Fill, IndexOf), as unsigned numbers: the start at most the length, the count
    /// at most what is left after it.
    /// </summary>
    private void CheckStartAndCount(TirBuilder ir, TirValue array, TirValue start, TirValue? count)
    {
        Check(ir, ir.Compare(TirCompareOp.LeUnsigned, start, Length(ir, array)), StartIndexPastEnd);
        if (count is not null)
        {
            Check(ir, ir.Compare(TirCompareOp.LeUnsigned, count, ir.Binary(TirBinaryOp.Sub, Length(ir, array), start)),
                CountOutOfRange);
        }
    }

    /// <summary>
    /// A loop over i from <paramref name="from"/> up to (not including) <paramref name="to"/>, by 1, with
    /// <paramref name="body"/> in it, which loads i where it needs it (it may go on in a block of its own); the builder
    /// goes on after the loop.
    /// </summary>
    private static void Loop(TirBuilder ir, TirValue from, TirValue to, Action<TirBuilder, Func<TirValue>> body)
    {
        var function = ir.Function;
        var index = function.AddSlot($"i{function.Blocks.Count}", TirType.I32);
        var end = function.AddSlot($"end{function.Blocks.Count}", TirType.I32);
        var test = function.AddBlock($"test{function.Blocks.Count}");
        var loop = function.AddBlock($"loop{function.Blocks.Count}");
        var done = function.AddBlock($"done{function.Blocks.Count}");
        ir.Store(index, from);
        ir.Store(end, to);
        ir.Jump(test);

        ir.Block = test;
        ir.Branch(ir.Compare(TirCompareOp.Lt, ir.Load(index), ir.Load(end)), loop, done);

        ir.Block = loop;
        body(ir, () => ir.Load(index));
        ir.Store(index, ir.Binary(TirBinaryOp.Add, ir.Load(index), ir.Const(TirType.I32, 1)));
        ir.Jump(test);

        ir.Block = done;
    }

    /// <summary>The first <paramref name="count"/> of the parameters an overload may add.</summary>
    private static IEnumerable<(string Name, TirType Type)> Optional(int count, params (string, TirType)[] parameters) =>
        parameters.Take(count);

    private TirFunction Memmove => External("memmove", TirType.Ptr, ("destination", TirType.Ptr), ("source", TirType.Ptr),
        ("count", TirType.NativeInt));

    private TirFunction Memset => External("memset", TirType.Ptr, ("destination", TirType.Ptr), ("value", TirType.I32),
        ("count", TirType.NativeInt));

    private TirValue Data(TirBuilder ir, TirValue array) => ir.Call(arrays.Data, array);

    private TirValue Length(TirBuilder ir, TirValue array) => ir.Call(arrays.Length, array);

    /// <summary>True when the array is not null: its elements have an address (every array's have, an empty one's too).</summary>
    private TirValue NotNull(TirBuilder ir, TirValue array) =>
        ir.Compare(TirCompareOp.Ne, Data(ir, array), ir.Zero(TirType.Ptr));

    private static TirValue NotNegative(TirBuilder ir, TirValue value) =>
        ir.Compare(TirCompareOp.Ge, value, ir.Const(TirType.I32, 0));

    /// <summary>True when index + length (both not negative) is at most the array's length, worked out in 64 bits.</summary>
    private TirValue Within(TirBuilder ir, TirValue index, TirValue length, TirValue array) =>
        ir.Compare(TirCompareOp.LeUnsigned, ir.Binary(TirBinaryOp.Add, Wide(ir, index), Wide(ir, length)),
            Wide(ir, Length(ir, array)));

    /// <summary>The address of element <paramref name="index"/> of <paramref name="size"/> bytes.</summary>
    private TirValue Element(TirBuilder ir, TirValue array, TirValue index, TirValue size) =>
        ir.ElementAddress(Data(ir, array), TirType.I8, ir.Binary(TirBinaryOp.Mul, Native(ir, index), Native(ir, size)));

    private TirValue Element(TirBuilder ir, TirValue array, TirType element, TirValue index) =>
        ir.ElementAddress(Data(ir, array), element, index);

    /// <summary>The bytes <paramref name="count"/> elements of <paramref name="size"/> bytes take, as an nint.</summary>
    private static TirValue Bytes(TirBuilder ir, TirValue count, TirValue size) =>
        ir.Binary(TirBinaryOp.Mul, Native(ir, count), Native(ir, size));

    private static TirValue Native(TirBuilder ir, TirValue value) => ir.Convert(value, TirType.NativeInt, unsigned: true);

    private static TirValue Wide(TirBuilder ir, TirValue value) => ir.Convert(value, TirType.I64, unsigned: true);

    /// <summary>
    /// Goes on in a new block when <paramref name="ok"/>, else calls the runtime's <paramref name="failure"/> (with
    /// <paramref name="value"/>, for one that says it), which stops the program, then returns zero, which it never
    /// reaches.
    /// </summary>
    private void Check(TirBuilder ir, TirValue ok, string failure, TirValue? value = null)
    {
        var function = ir.Function;
        var fine = function.AddBlock($"ok{function.Blocks.Count}");
        var fail = function.AddBlock($"fail{function.Blocks.Count}");
        ir.Branch(ok, fine, fail);

        ir.Block = fail;
        if (value is null)
        {
            ir.Call(External(failure, TirType.Void));
        }
        else
        {
            ir.Call(External(failure, TirType.Void, ("value", TirType.I32)), value);
        }

        if (function.ReturnType == TirType.Void)
        {
            ir.Return();
        }
        else
        {
            ir.Return(ir.Zero(function.ReturnType));
        }

        ir.Block = fine;
    }

    private (TirFunction Function, TirBuilder Builder) Define(string name, TirType returns,
        params IEnumerable<(string Name, TirType Type)> parameters)
    {
        var function = module.AddFunction(name, returns, parameters);
        return (function, new TirBuilder(function.AddBlock("entry")));
    }

    private TirFunction Get(string name, object? key, Func<TirFunction> define)
    {
        if (!_functions.TryGetValue((name, key), out var function))
        {
            function = define();
            _functions.Add((name, key), function);
        }

        return function;
    }

    /// <summary>The external function <paramref name="name"/>: the module's, when it has one already (a failure the
    /// arrays or a library substitute declared, memmove of the spans).</summary>
    private TirFunction External(string name, TirType returns, params (string Name, TirType Type)[] parameters) =>
        module.Functions.FirstOrDefault(function => function.Kind == TirFunctionKind.External && function.Name == name)
        ?? module.AddExternalFunction(name, returns, parameters);
}
