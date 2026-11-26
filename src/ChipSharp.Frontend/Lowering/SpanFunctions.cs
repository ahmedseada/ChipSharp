using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The TIR functions the translation of spans calls (see <see cref="Spans"/>), each added to the module when first
/// needed, once per element type where the elements matter. Each checks what .NET checks and stops the program with
/// .NET's message where .NET throws; they are small, and LLVM inlines them.
/// <code>
/// function ChipSharp.Array ChipSharp.Span.Range&lt;i32&gt;(ChipSharp.Array %span, i32 %start, i32 %length)
/// function ChipSharp.Array ChipSharp.Span.From&lt;i32&gt;(ChipSharp.Array %span, i32 %start)
/// function ChipSharp.Array ChipSharp.Span.FromPointer(ptr %data, i32 %length)
/// function void ChipSharp.Span.CopyTo&lt;i32&gt;(ChipSharp.Array %source, ChipSharp.Array %destination)
/// function i32 ChipSharp.Span.Read&lt;i32&gt;(ChipSharp.Array %bytes)            (load unaligned i32)
/// ...
/// external function ptr memmove(ptr, ptr, nint)                               (and memset, memcpy)
/// external function void ChipSharp.Runtime.ArgumentOutOfRange()               (and the other failures)
/// </code>
/// A span's elements are copied and cleared with the C library's memmove and memset, which every runtime has (LLVM
/// calls them for copies of its own). The bounds check of an element is the array's
/// (<see cref="ArrayFunctions.CheckIndex"/>): .NET's IndexOutOfRangeException, the same for both.
/// </summary>
internal sealed class SpanFunctions(TirModule module, ArrayFunctions arrays)
{
    private static readonly TirStructType SpanType = StackArrays.ArrayType;

    private readonly Dictionary<(string Name, TirType? Type), TirFunction> _functions = [];
    private readonly Dictionary<string, TirFunction> _externals = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>(span, start, length)</c>: elements start to start + length, after .NET's check (as unsigned 64-bit numbers,
    /// start + length must not pass the length): <c>new Span&lt;T&gt;(array, start, length)</c>, <c>Slice(start, length)</c>.
    /// </summary>
    public TirFunction Range(TirType element) => Get("Range", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Span.Range<{element}>", SpanType,
            ("span", SpanType), ("start", TirType.I32), ("length", TirType.I32));
        var (span, start, length) = (function.Parameters[0], function.Parameters[1], function.Parameters[2]);
        var end = ir.Binary(TirBinaryOp.Add, Wide(ir, start), Wide(ir, length));
        var fits = ir.Compare(TirCompareOp.LeUnsigned, end, Wide(ir, ir.Call(arrays.Length, span)));
        Check(ir, fits, Spans.ArgumentOutOfRange, SpanType);
        var data = ir.ElementAddress(ir.Call(arrays.Data, span), element, start);
        ir.Return(ir.Call(arrays.Make, data, length));
        return function;
    });

    /// <summary><c>(span, start)</c>: the elements from start on, after .NET's check: <c>Slice(start)</c>, <c>array.AsSpan(start)</c>.</summary>
    public TirFunction From(TirType element) => Get("From", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Span.From<{element}>", SpanType, ("span", SpanType), ("start", TirType.I32));
        var (span, start) = (function.Parameters[0], function.Parameters[1]);
        Check(ir, ir.Compare(TirCompareOp.LeUnsigned, start, ir.Call(arrays.Length, span)), Spans.ArgumentOutOfRange,
            SpanType);
        var data = ir.ElementAddress(ir.Call(arrays.Data, span), element, start);
        ir.Return(ir.Call(arrays.Make, data, ir.Binary(TirBinaryOp.Sub, ir.Call(arrays.Length, span), start)));
        return function;
    });

    /// <summary><c>(data, length)</c>: <c>new Span&lt;T&gt;(void*, length)</c>, whose length must not be negative.</summary>
    public TirFunction FromPointer => Get("FromPointer", null, () =>
    {
        var (function, ir) = Define("ChipSharp.Span.FromPointer", SpanType, ("data", TirType.Ptr), ("length", TirType.I32));
        var length = function.Parameters[1];
        Check(ir, ir.Compare(TirCompareOp.Ge, length, ir.Const(TirType.I32, 0)), Spans.ArgumentOutOfRange, SpanType);
        ir.Return(ir.Call(arrays.Make, function.Parameters[0], length));
        return function;
    });

    /// <summary>
    /// <c>(source, destination)</c>: copies the source's elements to the start of the destination, as memmove does (the
    /// two may overlap); .NET's ArgumentException when the destination is shorter.
    /// </summary>
    public TirFunction CopyTo(TirType element) => Get("CopyTo", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Span.CopyTo<{element}>", TirType.Void,
            ("source", SpanType), ("destination", SpanType));
        var (source, destination) = (function.Parameters[0], function.Parameters[1]);
        Check(ir, Fits(ir, source, destination), Spans.DestinationTooShort, TirType.Void);
        Move(ir, destination, source, element);
        ir.Return();
        return function;
    });

    /// <summary><c>(source, destination)</c>: CopyTo when the destination is long enough, and whether it was.</summary>
    public TirFunction TryCopyTo(TirType element) => Get("TryCopyTo", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Span.TryCopyTo<{element}>", TirType.Bool,
            ("source", SpanType), ("destination", SpanType));
        var (source, destination) = (function.Parameters[0], function.Parameters[1]);
        var copy = function.AddBlock("copy");
        var tooShort = function.AddBlock("too_short");
        ir.Branch(Fits(ir, source, destination), copy, tooShort);

        ir.Block = copy;
        Move(ir, destination, source, element);
        ir.Return(ir.Const(true));

        ir.Block = tooShort;
        ir.Return(ir.Const(false));
        return function;
    });

    /// <summary><c>(span, value)</c>: every element is the value.</summary>
    public TirFunction Fill(TirType element) => Get("Fill", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Span.Fill<{element}>", TirType.Void, ("span", SpanType), ("value", element));
        var (span, value) = (function.Parameters[0], function.Parameters[1]);
        var index = function.AddSlot("index", TirType.I32);
        var test = function.AddBlock("test");
        var body = function.AddBlock("body");
        var done = function.AddBlock("done");
        ir.Store(index, ir.Const(TirType.I32, 0));
        ir.Jump(test);

        ir.Block = test;
        ir.Branch(ir.Compare(TirCompareOp.Lt, ir.Load(index), ir.Call(arrays.Length, span)), body, done);

        ir.Block = body;
        var i = ir.Load(index);
        ir.Store(ir.ElementAddress(ir.Call(arrays.Data, span), element, i), value);
        ir.Store(index, ir.Binary(TirBinaryOp.Add, i, ir.Const(TirType.I32, 1)));
        ir.Jump(test);

        ir.Block = done;
        ir.Return();
        return function;
    });

    /// <summary><c>(span)</c>: every element is zero (default), as memset makes it.</summary>
    public TirFunction Clear(TirType element) => Get("Clear", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Span.Clear<{element}>", TirType.Void, ("span", SpanType));
        var span = function.Parameters[0];
        var bytes = Bytes(ir, ir.Call(arrays.Length, span), element);
        ir.Call(Memset, ir.Call(arrays.Data, span), ir.Const(TirType.I32, 0), bytes);
        ir.Return();
        return function;
    });

    /// <summary><c>(a, b)</c>: <c>==</c> of two spans, true when they are the same elements of the same memory.</summary>
    public TirFunction Equal => Get("Equal", null, () =>
    {
        var (function, ir) = Define("ChipSharp.Span.Equal", TirType.Bool, ("a", SpanType), ("b", SpanType));
        var (a, b) = (function.Parameters[0], function.Parameters[1]);
        var sameData = ir.Compare(TirCompareOp.Eq, ir.Call(arrays.Data, a), ir.Call(arrays.Data, b));
        var sameLength = ir.Compare(TirCompareOp.Eq, ir.Call(arrays.Length, a), ir.Call(arrays.Length, b));
        ir.Return(ir.Compare(TirCompareOp.Eq,
            ir.Binary(TirBinaryOp.And, ir.Convert(sameData, TirType.I32), ir.Convert(sameLength, TirType.I32)),
            ir.Const(TirType.I32, 1)));
        return function;
    });

    /// <summary>
    /// <c>(bytes)</c>: MemoryMarshal.Read, a value of <paramref name="storage"/> from the first bytes, read unaligned;
    /// .NET's ArgumentOutOfRangeException (length) when there are fewer.
    /// </summary>
    public TirFunction Read(TirType storage) => Get("Read", storage, () =>
    {
        var (function, ir) = Define($"ChipSharp.Span.Read<{storage}>", storage, ("bytes", SpanType));
        var bytes = function.Parameters[0];
        Check(ir, Fits(ir, bytes, storage), Spans.LengthOutOfRange, storage);
        ir.Return(ir.LoadUnaligned(storage, ir.Call(arrays.Data, bytes)));
        return function;
    });

    /// <summary>
    /// <c>(bytes, value)</c>: MemoryMarshal.Write, the value that <c>value</c> (an address) refers to, a
    /// <paramref name="type"/> (stored as <paramref name="storage"/>), into the first bytes, written unaligned.
    /// </summary>
    public TirFunction Write(TirType type, TirType storage) => Get("Write", type, () =>
    {
        var (function, ir) = Define($"ChipSharp.Span.Write<{type}>", TirType.Void, ("bytes", SpanType), ("value", TirType.Ptr));
        var bytes = function.Parameters[0];
        Check(ir, Fits(ir, bytes, storage), Spans.LengthOutOfRange, TirType.Void);
        ir.StoreUnaligned(ir.Call(arrays.Data, bytes), Stored(ir, ir.Load(type, function.Parameters[1]), storage));
        ir.Return();
        return function;
    });

    /// <summary><c>(bytes, value)</c>: MemoryMarshal.TryRead into what <c>value</c> refers to (default when too short).</summary>
    public TirFunction TryRead(TirType type, TirType storage) => Get("TryRead", type, () =>
    {
        var (function, ir) = Define($"ChipSharp.Span.TryRead<{type}>", TirType.Bool, ("bytes", SpanType), ("value", TirType.Ptr));
        var (bytes, value) = (function.Parameters[0], function.Parameters[1]);
        var read = function.AddBlock("read");
        var tooShort = function.AddBlock("too_short");
        ir.Branch(Fits(ir, bytes, storage), read, tooShort);

        ir.Block = read;
        TirValue loaded = ir.LoadUnaligned(storage, ir.Call(arrays.Data, bytes));
        if (type == TirType.Bool)
        {
            loaded = ir.Compare(TirCompareOp.Ne, loaded, ir.Const(TirType.I8, 0));
        }

        ir.Store(value, loaded);
        ir.Return(ir.Const(true));

        ir.Block = tooShort;
        ir.Store(value, ir.Zero(type));
        ir.Return(ir.Const(false));
        return function;
    });

    /// <summary><c>(bytes, value)</c>: MemoryMarshal.TryWrite of what <c>value</c> refers to, and whether it fit.</summary>
    public TirFunction TryWrite(TirType type, TirType storage) => Get("TryWrite", type, () =>
    {
        var (function, ir) = Define($"ChipSharp.Span.TryWrite<{type}>", TirType.Bool, ("bytes", SpanType), ("value", TirType.Ptr));
        var bytes = function.Parameters[0];
        var write = function.AddBlock("write");
        var tooShort = function.AddBlock("too_short");
        ir.Branch(Fits(ir, bytes, storage), write, tooShort);

        ir.Block = write;
        ir.StoreUnaligned(ir.Call(arrays.Data, bytes), Stored(ir, ir.Load(type, function.Parameters[1]), storage));
        ir.Return(ir.Const(true));

        ir.Block = tooShort;
        ir.Return(ir.Const(false));
        return function;
    });

    /// <summary>
    /// <c>(span)</c>: MemoryMarshal.AsBytes, the same memory as bytes; .NET's OverflowException when there are more
    /// bytes than an int counts.
    /// </summary>
    public TirFunction AsBytes(TirType element) => Get("AsBytes", element, () =>
    {
        var (function, ir) = Define($"ChipSharp.Span.AsBytes<{element}>", SpanType, ("span", SpanType));
        var span = function.Parameters[0];
        TirValue Bytes() => ir.Binary(TirBinaryOp.Mul, Wide(ir, ir.Call(arrays.Length, span)), Wide(ir, ir.SizeOf(element)));
        Check(ir, ir.Compare(TirCompareOp.LeUnsigned, Bytes(), ir.Const(TirType.I64, int.MaxValue)), Spans.Overflow, SpanType);
        ir.Return(ir.Call(arrays.Make, ir.Call(arrays.Data, span), ir.Convert(Bytes(), TirType.I32)));
        return function;
    });

    /// <summary>
    /// <c>(span, fromSize, toSize, alignment)</c>: MemoryMarshal.Cast, the same memory as elements of toSize bytes, as
    /// many as fit (.NET's OverflowException when that is more than an int counts). An alignment above 1 is checked: a
    /// span that does not start at a multiple of it stops the program with .NET's DataMisalignedException, which only a
    /// target without unaligned access asks for (see <c>"unalignedAccess"</c> in the target files).
    /// </summary>
    public TirFunction Cast => Get("Cast", null, () =>
    {
        var (function, ir) = Define("ChipSharp.Span.Cast", SpanType, ("span", SpanType), ("fromSize", TirType.I32),
            ("toSize", TirType.I32), ("alignment", TirType.I32));
        var (span, fromSize, toSize, alignment) =
            (function.Parameters[0], function.Parameters[1], function.Parameters[2], function.Parameters[3]);
        TirValue Length() => ir.Binary(TirBinaryOp.DivUnsigned,
            ir.Binary(TirBinaryOp.Mul, Wide(ir, ir.Call(arrays.Length, span)), Wide(ir, fromSize)), Wide(ir, toSize));
        Check(ir, ir.Compare(TirCompareOp.LeUnsigned, Length(), ir.Const(TirType.I64, int.MaxValue)), Spans.Overflow,
            SpanType);

        var mask = ir.Binary(TirBinaryOp.Sub, ir.Convert(alignment, TirType.NativeInt, unsigned: true),
            ir.Const(TirType.NativeInt, 1));
        var offset = ir.Binary(TirBinaryOp.And, ir.PtrToInt(ir.Call(arrays.Data, span), TirType.NativeInt), mask);
        Check(ir, ir.Compare(TirCompareOp.Eq, offset, ir.Const(TirType.NativeInt, 0)), Spans.DataMisaligned, SpanType);
        ir.Return(ir.Call(arrays.Make, ir.Call(arrays.Data, span), ir.Convert(Length(), TirType.I32)));
        return function;
    });

    /// <summary><c>(destination, source, bytes)</c>: IL's cpblk, as the C library's memcpy copies.</summary>
    public TirFunction Memcpy => External("memcpy", TirType.Ptr, ("destination", TirType.Ptr), ("source", TirType.Ptr),
        ("count", TirType.NativeInt));

    private TirFunction Memmove => External("memmove", TirType.Ptr, ("destination", TirType.Ptr), ("source", TirType.Ptr),
        ("count", TirType.NativeInt));

    private TirFunction Memset => External("memset", TirType.Ptr, ("destination", TirType.Ptr), ("value", TirType.I32),
        ("count", TirType.NativeInt));

    /// <summary>A value of <paramref name="storage"/>'s type to write: a bool is a byte, 0 or 1.</summary>
    private static TirValue Stored(TirBuilder ir, TirValue value, TirType storage) =>
        value.Type == TirType.Bool && storage == TirType.I8 ? ir.Convert(value, TirType.I8) : value;

    /// <summary>True when the span has at least as many bytes as <paramref name="type"/> takes.</summary>
    private TirValue Fits(TirBuilder ir, TirValue bytes, TirType type) =>
        ir.Compare(TirCompareOp.LeUnsigned, ir.SizeOf(type), ir.Call(arrays.Length, bytes));

    /// <summary>True when <paramref name="destination"/> has at least as many elements as <paramref name="source"/>.</summary>
    private TirValue Fits(TirBuilder ir, TirValue source, TirValue destination) => ir.Compare(TirCompareOp.LeUnsigned,
        ir.Call(arrays.Length, source), ir.Call(arrays.Length, destination));

    /// <summary>Copies the elements of <paramref name="source"/> to the start of <paramref name="destination"/>'s.</summary>
    private void Move(TirBuilder ir, TirValue destination, TirValue source, TirType element) =>
        ir.Call(Memmove, ir.Call(arrays.Data, destination), ir.Call(arrays.Data, source),
            Bytes(ir, ir.Call(arrays.Length, source), element));

    /// <summary>The bytes <paramref name="length"/> elements take, as an nint.</summary>
    private static TirValue Bytes(TirBuilder ir, TirValue length, TirType element) => ir.Binary(TirBinaryOp.Mul,
        ir.Convert(length, TirType.NativeInt, unsigned: true),
        ir.Convert(ir.SizeOf(element), TirType.NativeInt, unsigned: true));

    /// <summary>An i32 as an unsigned i64, so that sums and products of lengths cannot overflow.</summary>
    private static TirValue Wide(TirBuilder ir, TirValue value) => ir.Convert(value, TirType.I64, unsigned: true);

    /// <summary>
    /// Goes on in a new block when <paramref name="ok"/>, else calls the runtime's <paramref name="failure"/>, which
    /// stops the program (then returns zero of <paramref name="returns"/>, which it never reaches).
    /// </summary>
    private void Check(TirBuilder ir, TirValue ok, string failure, TirType returns)
    {
        var function = ir.Function;
        var fine = function.AddBlock($"ok{function.Blocks.Count}");
        var fail = function.AddBlock($"fail{function.Blocks.Count}");
        ir.Branch(ok, fine, fail);

        ir.Block = fail;
        ir.Call(External(failure, TirType.Void));
        if (returns == TirType.Void)
        {
            ir.Return();
        }
        else
        {
            ir.Return(ir.Zero(returns));
        }

        ir.Block = fine;
    }

    private (TirFunction Function, TirBuilder Builder) Define(string name, TirType returns,
        params (string Name, TirType Type)[] parameters)
    {
        var function = module.AddFunction(name, returns, parameters);
        return (function, new TirBuilder(function.AddBlock("entry")));
    }

    private TirFunction Get(string name, TirType? type, Func<TirFunction> define)
    {
        if (!_functions.TryGetValue((name, type), out var function))
        {
            function = define();
            _functions.Add((name, type), function);
        }

        return function;
    }

    private TirFunction External(string name, TirType returns, params (string Name, TirType Type)[] parameters)
    {
        if (!_externals.TryGetValue(name, out var function))
        {
            // a failure of the runtime may be there already, made by an array (IndexOutOfRange) or a library call
            function = module.Functions.FirstOrDefault(f => f.Kind == TirFunctionKind.External && f.Name == name)
                ?? module.AddExternalFunction(name, returns, parameters);
            _externals.Add(name, function);
        }

        return function;
    }
}
