using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The TIR functions the translation of arrays on the heap calls (see <see cref="HeapArrays"/>), each added to the module
/// when first needed; like those of <see cref="ArrayFunctions"/>, they are tiny and LLVM inlines them.
/// <code>
/// function ChipSharp.Array ChipSharp.Array.FromObject(ref %array)       (one per element type)
/// function ref ChipSharp.Array.ToObject(ChipSharp.Array %array)
/// function i32 ChipSharp.Array.LengthOrNull(ChipSharp.Array %array)
/// function void ChipSharp.Array.CheckIndexOrNull(ChipSharp.Array %array, i32 %index)   (also with an nint or i64 index)
/// function ChipSharp.Array ChipSharp.Array.Empty<int[]>()          (one per array type)
/// function i32 ChipSharp.Array.NewLength(nint %length)
/// constant ChipSharp.EmptyArray @ChipSharp.Array.Empty<int[]> = object int[]
/// external function void ChipSharp.Runtime.NullReference()
/// external function void ChipSharp.Runtime.Overflow()
/// </code>
/// FromObject turns an array object (a ref, from a field or an element) into an array value, null into the null view;
/// ToObject the other way round, for a view of an array object (the analysis makes sure it is one). LengthOrNull and
/// CheckIndexOrNull are Length and CheckIndex for an array that may be null: null stops the program with .NET's
/// NullReferenceException message, where the bounds check would stop it; the way in range is the same as theirs.
/// </summary>
internal sealed class HeapArrayFunctions(TirModule module, ArrayFunctions arrays)
{
    private static readonly TirStructType ArrayType = StackArrays.ArrayType;

    /// <summary>
    /// An empty array object outside the heap: its header (the address of its array type's descriptor, so that a type
    /// test reads it like any array's), a length of 0, and a word where its elements would start, so that their address
    /// is inside it.
    /// </summary>
    private static readonly TirStructType EmptyType =
        new("ChipSharp.EmptyArray", ("header", TirType.Ptr), ("length", TirType.I32), ("elements", TirType.Ptr));

    private readonly Dictionary<TirType, TirFunction> _fromObject = [];
    private readonly Dictionary<TirType, TirFunction> _checks = [];
    private readonly Dictionary<TirTypeDescriptor, TirFunction> _empty = [];
    private TirFunction? _toObject;
    private TirFunction? _lengthOrNull;
    private TirFunction? _newLength;
    private TirFunction? _nullReference;
    private TirFunction? _overflow;

    /// <summary>The array value of an array object whose elements are <paramref name="arrayType"/>'s (null: the null view).</summary>
    public TirFunction FromObject(TirTypeDescriptor arrayType)
    {
        var element = arrayType.ElementType!;
        if (!_fromObject.TryGetValue(element, out var function))
        {
            function = DefineFromObject(element);
            _fromObject.Add(element, function);
        }

        return function;
    }

    /// <summary>The array object of an array value that is one (the null view: null).</summary>
    public TirFunction ToObject => _toObject ??= DefineToObject();

    /// <summary>The length of an array that may be null.</summary>
    public TirFunction LengthOrNull => _lengthOrNull ??= DefineLengthOrNull();

    /// <summary>
    /// The empty array of <c>Array.Empty&lt;T&gt;()</c> (and of an empty static array) of <paramref name="arrayType"/>:
    /// one array object outside the heap, in flash on a chip, for each T, as .NET has one for each.
    /// </summary>
    public TirFunction Empty(TirTypeDescriptor arrayType)
    {
        if (!_empty.TryGetValue(arrayType, out var function))
        {
            function = DefineEmpty(arrayType);
            _empty.Add(arrayType, function);
        }

        return function;
    }

    /// <summary>The length of a new array given as an nint: as an i32, or the stop of a negative length when it does not fit.</summary>
    public TirFunction NewLength => _newLength ??= DefineNewLength();

    /// <summary>Stops the program unless the index (i32, nint or i64) is in range of an array that is not null.</summary>
    public TirFunction CheckIndexOrNull(TirType indexType)
    {
        if (!_checks.TryGetValue(indexType, out var function))
        {
            function = DefineCheckIndexOrNull(indexType);
            _checks.Add(indexType, function);
        }

        return function;
    }

    private TirFunction NullReference => _nullReference ??= External("ChipSharp.Runtime.NullReference");

    /// <summary>The runtime function <paramref name="name"/> (void, no parameters): the module's, when it has one already.</summary>
    private TirFunction External(string name) =>
        module.Functions.FirstOrDefault(function => function is { Kind: TirFunctionKind.External } && function.Name == name)
        ?? module.AddExternalFunction(name, TirType.Void);

    private TirFunction DefineFromObject(TirType element)
    {
        var function = module.AddFunction("ChipSharp.Array.FromObject", ArrayType, ("array", TirType.Ref));
        var array = function.Parameters[0];
        var ir = new TirBuilder(function.AddBlock("entry"));
        var isNull = function.AddBlock("null");
        var notNull = function.AddBlock("not_null");
        ir.Branch(ir.Compare(TirCompareOp.Eq, array, ir.Zero(TirType.Ref)), isNull, notNull);

        ir.Block = isNull;
        ir.Return(ir.Zero(ArrayType));

        ir.Block = notNull;
        ir.Return(ir.Call(arrays.Make, ir.ArrayData(array, element), ir.ArrayLength(array)));
        return function;
    }

    private TirFunction DefineToObject()
    {
        var function = module.AddFunction("ChipSharp.Array.ToObject", TirType.Ref, ("array", ArrayType));
        var ir = new TirBuilder(function.AddBlock("entry"));
        var isNull = function.AddBlock("null");
        var notNull = function.AddBlock("not_null");
        var data = ir.Call(arrays.Data, function.Parameters[0]);
        ir.Branch(ir.Compare(TirCompareOp.Eq, data, ir.Zero(TirType.Ptr)), isNull, notNull);

        ir.Block = isNull;
        ir.Return(ir.Zero(TirType.Ref));

        ir.Block = notNull;
        ir.Return(ir.ArrayObject(ir.Call(arrays.Data, function.Parameters[0])));
        return function;
    }

    private TirFunction DefineLengthOrNull()
    {
        var function = module.AddFunction("ChipSharp.Array.LengthOrNull", TirType.I32, ("array", ArrayType));
        var array = function.Parameters[0];
        var ir = new TirBuilder(function.AddBlock("entry"));
        var isNull = function.AddBlock("null");
        var notNull = function.AddBlock("not_null");
        ir.Branch(ir.Compare(TirCompareOp.Eq, ir.Call(arrays.Data, array), ir.Zero(TirType.Ptr)), isNull, notNull);

        ir.Block = isNull;
        ir.Call(NullReference);
        ir.Return(ir.Const(TirType.I32, 0));

        ir.Block = notNull;
        ir.Return(ir.Call(arrays.Length, array));
        return function;
    }

    private TirFunction DefineCheckIndexOrNull(TirType indexType)
    {
        var function = module.AddFunction("ChipSharp.Array.CheckIndexOrNull", TirType.Void,
            ("array", ArrayType), ("index", indexType));
        var (array, index) = (function.Parameters[0], function.Parameters[1]);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var inRange = function.AddBlock("in_range");
        var notInRange = function.AddBlock("not_in_range");
        var isNull = function.AddBlock("null");
        var outOfRange = function.AddBlock("out_of_range");

        // null has no elements, so its every index is out of range: only then is it told apart, and an index out of
        // range of an array that is not null goes on to CheckIndex, which stops the program
        TirValue length = ir.Call(arrays.Length, array);
        if (indexType != TirType.I32)
        {
            length = ir.Convert(length, indexType, unsigned: true);
        }

        ir.Branch(ir.Compare(TirCompareOp.LtUnsigned, index, length), inRange, notInRange);

        ir.Block = inRange;
        ir.Return();

        ir.Block = notInRange;
        ir.Branch(ir.Compare(TirCompareOp.Eq, ir.Call(arrays.Data, array), ir.Zero(TirType.Ptr)), isNull, outOfRange);

        ir.Block = isNull;
        ir.Call(NullReference);
        ir.Return();

        ir.Block = outOfRange;
        ir.Call(arrays.CheckIndex(indexType), array, index);
        ir.Return();
        return function;
    }

    private TirFunction DefineEmpty(TirTypeDescriptor arrayType)
    {
        var name = $"ChipSharp.Array.Empty<{arrayType.Name}>";
        var global = module.AddObject(name, EmptyType, arrayType);
        var function = module.AddFunction(name, ArrayType);
        var ir = new TirBuilder(function.AddBlock("entry"));
        ir.Return(ir.Call(arrays.Make, ir.FieldAddress(ir.AddressOf(global), EmptyType, 2), ir.Const(TirType.I32, 0)));
        return function;
    }

    private TirFunction DefineNewLength()
    {
        var function = module.AddFunction("ChipSharp.Array.NewLength", TirType.I32, ("length", TirType.NativeInt));
        var length = function.Parameters[0];
        var ir = new TirBuilder(function.AddBlock("entry"));
        var fits = function.AddBlock("fits");
        var tooLong = function.AddBlock("too_long");

        // a negative length is too long read as unsigned
        var most = ir.Convert(ir.Const(TirType.I32, int.MaxValue), TirType.NativeInt, unsigned: true);
        ir.Branch(ir.Compare(TirCompareOp.LeUnsigned, length, most), fits, tooLong);

        ir.Block = fits;
        ir.Return(ir.Convert(length, TirType.I32));

        ir.Block = tooLong;
        ir.Call(_overflow ??= External("ChipSharp.Runtime.Overflow"));
        ir.Return(ir.Const(TirType.I32, 0));
        return function;
    }
}
