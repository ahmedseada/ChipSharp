using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The TIR functions the translation of arrays calls (see <see cref="StackArrays"/>), each added to the module when
/// first needed. TIR has no instruction that puts a struct value together or takes one apart, so these do it
/// through a slot; they are tiny, and LLVM inlines them (and then keeps the struct in registers).
/// <code>
/// function ChipSharp.Array ChipSharp.Array.Make(ptr %data, i32 %length)
/// function ptr ChipSharp.Array.Data(ChipSharp.Array %array)
/// function i32 ChipSharp.Array.Length(ChipSharp.Array %array)
/// function void ChipSharp.Array.CheckIndex(ChipSharp.Array %array, i32 %index)   (also with an nint or i64 index)
/// external function void ChipSharp.Runtime.IndexOutOfRange()
/// </code>
/// CheckIndex compares the index with the length as unsigned numbers, so a negative index is out of range too, and
/// calls the runtime's IndexOutOfRange, which stops the program, when it is out of range.
/// </summary>
internal sealed class ArrayFunctions(TirModule module)
{
    private static readonly TirStructType ArrayType = StackArrays.ArrayType;

    private readonly Dictionary<TirType, TirFunction> _checks = [];
    private TirFunction? _make;
    private TirFunction? _data;
    private TirFunction? _length;
    private TirFunction? _indexOutOfRange;

    /// <summary>An array value from the address of its elements and their number.</summary>
    public TirFunction Make => _make ??= DefineMake();

    /// <summary>The address of an array's elements (null for an empty array).</summary>
    public TirFunction Data => _data ??= DefineField("Data", StackArrays.DataField);

    /// <summary>The number of an array's elements.</summary>
    public TirFunction Length => _length ??= DefineField("Length", StackArrays.LengthField);

    /// <summary>Stops the program unless an index of type <paramref name="indexType"/> (i32, nint or i64) is in range.</summary>
    public TirFunction CheckIndex(TirType indexType)
    {
        if (!_checks.TryGetValue(indexType, out var function))
        {
            function = DefineCheckIndex(indexType);
            _checks.Add(indexType, function);
        }

        return function;
    }

    private TirFunction DefineMake()
    {
        var function = module.AddFunction("ChipSharp.Array.Make", ArrayType, ("data", TirType.Ptr), ("length", TirType.I32));
        var array = function.AddSlot("array", ArrayType);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var address = ir.AddressOf(array);
        ir.Store(ir.FieldAddress(address, ArrayType, StackArrays.DataField), function.Parameters[0]);
        ir.Store(ir.FieldAddress(address, ArrayType, StackArrays.LengthField), function.Parameters[1]);
        ir.Return(ir.Load(array));
        return function;
    }

    private TirFunction DefineField(string name, int field)
    {
        var type = ArrayType.Fields[field].Type;
        var function = module.AddFunction($"ChipSharp.Array.{name}", type, ("array", ArrayType));
        var array = function.AddSlot("array", ArrayType);
        var ir = new TirBuilder(function.AddBlock("entry"));
        ir.Store(array, function.Parameters[0]);
        ir.Return(ir.Load(type, ir.FieldAddress(ir.AddressOf(array), ArrayType, field)));
        return function;
    }

    private TirFunction DefineCheckIndex(TirType indexType)
    {
        var function = module.AddFunction("ChipSharp.Array.CheckIndex", TirType.Void,
            ("array", ArrayType), ("index", indexType));
        var (array, index) = (function.Parameters[0], function.Parameters[1]);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var inRange = function.AddBlock("in_range");
        var outOfRange = function.AddBlock("out_of_range");

        TirValue length = ir.Call(Length, array);
        if (indexType != TirType.I32)
        {
            length = ir.Convert(length, indexType, unsigned: true);
        }

        ir.Branch(ir.Compare(TirCompareOp.LtUnsigned, index, length), inRange, outOfRange);

        ir.Block = inRange;
        ir.Return();

        ir.Block = outOfRange;
        // the methods of System.Array may have declared it already (Array.Clear)
        _indexOutOfRange ??= module.Functions.FirstOrDefault(f => f is
            { Kind: TirFunctionKind.External, Name: StackArrays.IndexOutOfRange })
            ?? module.AddExternalFunction(StackArrays.IndexOutOfRange, TirType.Void);
        ir.Call(_indexOutOfRange);
        ir.Return();
        return function;
    }
}
