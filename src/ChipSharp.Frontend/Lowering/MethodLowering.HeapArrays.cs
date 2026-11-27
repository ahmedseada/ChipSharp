using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Arrays on the heap (see <see cref="HeapArrays"/> for the rule and the representation). An array value is the same
/// <c>ChipSharp.Array</c> view whatever the array is, so most array instructions translate as for arrays on the stack;
/// what differs:
/// <list type="bullet">
/// <item>A <c>newarr</c> the analysis puts on the heap (<see cref="MethodArrays.HeapArrays"/>) is <c>newarray</c>, then the
/// view of its elements: <c>Make(arraydata T %r, %length)</c>. A length that is an nint goes through NewLength.</item>
/// <item>Where an array is stored as an object (a static field of an array type that is no static array, a field of an
/// object, an element of an array of references, an <c>object</c>), the view becomes the object, <c>ToObject</c>
/// (<see cref="ObjectFromStack"/>); where one is loaded with an array type, the object becomes a view, <c>FromObject</c>
/// (<see cref="ToStack"/>).</item>
/// <item><c>ldelem.ref</c> and <c>stelem.ref</c> load and store refs; an element that is an array becomes a view. What an
/// array holds is known from where the value came from (a local, a parameter, a field, a call, a newarr): each array
/// value on the evaluation stack keeps its IL type, also through a stack slot.</item>
/// <item>An array that may be null (<see cref="MethodArrays.MayBeNull"/>) is measured with LengthOrNull and indexed after
/// CheckIndexOrNull; null itself is the view of zeros; <c>brtrue</c>, <c>brfalse</c>, <c>ceq</c>, <c>cgt.un</c>,
/// <c>beq</c> and <c>bne.un</c> compare arrays by the address of their elements, which null has none of.</item>
/// <item>Every empty array has an address of its elements: an empty array on the stack a byte of its own,
/// <c>Array.Empty&lt;T&gt;()</c> and an empty static array the program's one empty array object.</item>
/// </list>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>What the analysis says about the method's arrays: set by <see cref="CheckArrays"/>.</summary>
    private MethodArrays _arrays = null!;

    /// <summary>The IL type of each array value on the evaluation stack, by its TIR value.</summary>
    private readonly Dictionary<TirValue, IlType> _arrayTypes = [];

    /// <summary>The IL type of an array value coming into a block in a stack slot; null when the ways in do not agree.</summary>
    private readonly Dictionary<(IlBlock Block, TirSlot Slot), IlType?> _enteringArrayTypes = [];

    /// <summary>The byte of each empty array on the stack (one per newarr), so that two such arrays are not the same.</summary>
    private readonly Dictionary<IlInstruction, TirSlot> _emptySlots = [];

    /// <summary>The element type and number of the newarr instructions of a constant size that make an array on the heap.</summary>
    private readonly Dictionary<IlInstruction, (TirType Element, int Count)> _heapInitialized = [];

    private HeapArrayFunctions HeapFunctions => _program.HeapArrayFunctions;

    /// <summary>True when the instruction being translated takes an array that may be null.</summary>
    private bool TakesNullableArray => _arrays.MayBeNull.Contains(_instruction.Offset);

    /// <summary>The bounds check for an index of type <paramref name="indexType"/>: with the null test when the array may be null.</summary>
    private TirFunction CheckIndexFunction(TirType indexType) =>
        TakesNullableArray ? HeapFunctions.CheckIndexOrNull(indexType) : _program.Arrays.CheckIndex(indexType);

    /// <summary>The length of an array: with the null test when it may be null.</summary>
    private TirFunction LengthFunction => TakesNullableArray ? HeapFunctions.LengthOrNull : _program.Arrays.Length;

    /// <summary>
    /// newarr of an array on the heap: <c>newarray</c> (which stops the program for a negative length, and runs the
    /// collector when the heap is full), then the view of the new object's elements.
    /// </summary>
    private void NewHeapArray()
    {
        var count = PopValue();
        var (element, ilElement) = Element();
        TirValue length = count.Type == TirType.NativeInt && !count.IsConstant
            ? _ir.Call(HeapFunctions.NewLength, Materialize(count))
            : Materialize(Convert(count, TirType.I32, unsigned: false));
        if (count.IsConstant)
        {
            _heapInitialized[_instruction] = (element, (int)count.Constant);
        }

        var arrayType = new IlArrayType(ilElement);
        var made = _ir.NewArray(_program.HeapArrayDescriptor(arrayType, _method.Assembly), length);
        PushArray(_ir.Call(_program.Arrays.Make, _ir.ArrayData(made, element), length), arrayType);
    }

    /// <summary>An empty array on the stack: a byte of its own (zero), and a length of 0.</summary>
    private TirValue EmptyStackArray()
    {
        if (!_emptySlots.TryGetValue(_instruction, out var slot))
        {
            slot = AddSlot($"array_{IlPrinter.Label(_instruction.Offset)}", new TirArrayType(TirType.I8, 1));
            _emptySlots.Add(_instruction, slot);
        }

        return _ir.Call(_program.Arrays.Make, _ir.AddressOf(slot), _ir.Const(TirType.I32, 0));
    }

    /// <summary>The empty array of <c>Array.Empty&lt;T&gt;()</c> and of an empty static array, of IL type <paramref name="arrayType"/>.</summary>
    private TirValue EmptyArray(IlType arrayType) =>
        _ir.Call(HeapFunctions.Empty(_program.HeapArrayDescriptor(arrayType, _method.Assembly)));

    /// <summary>
    /// ldelem.ref and stelem.ref, of an array of references: the element is a ref; one that is an array is loaded as its
    /// view and stored as its object.
    /// </summary>
    private void LowerReferenceElement()
    {
        if (_instruction.OpCode == IlOpCode.Stelem_Ref)
        {
            var value = PopValue();
            _ir.Store(ElementAddress(TirType.Ref), ObjectFromStack(value, "the array element"));
            return;
        }

        var arrayType = _stack.Count >= 2 ? ArrayTypeOf(_stack[^2]) : null;
        var element = arrayType is null ? null : HeapArrays.ElementType(arrayType);
        if (element is null)
        {
            throw Error($"{_instruction.Name} reads an element of an array whose type is not known here");
        }

        Push(ToStack(_ir.Load(TirType.Ref, ElementAddress(TirType.Ref)), element));
    }

    /// <summary>
    /// True for a branch or comparison that the translation of arrays takes: brtrue or brfalse of an array, or a
    /// comparison of an array with an array or null.
    /// </summary>
    private bool IsArrayComparison(IlInstruction instruction) => instruction.OpCode switch
    {
        IlOpCode.Brtrue or IlOpCode.Brtrue_S or IlOpCode.Brfalse or IlOpCode.Brfalse_S =>
            _stack.Count > 0 && _stack[^1].Type == StackArrays.ArrayType,
        IlOpCode.Ceq or IlOpCode.Cgt_Un or IlOpCode.Clt_Un or IlOpCode.Beq or IlOpCode.Beq_S or IlOpCode.Bne_Un
            or IlOpCode.Bne_Un_S => _stack.Count >= 2
            && (_stack[^1].Type == StackArrays.ArrayType || _stack[^2].Type == StackArrays.ArrayType),
        _ => false,
    };

    /// <summary>
    /// brtrue and brfalse of an array (is it not null?), and the comparisons of two arrays, or of an array and null, by
    /// the address of their elements: the same array has the same one, and null has none.
    /// </summary>
    private void LowerArrayComparison()
    {
        if (_instruction.OpCode is IlOpCode.Brtrue or IlOpCode.Brtrue_S or IlOpCode.Brfalse or IlOpCode.Brfalse_S)
        {
            var jumpWhenTrue = _instruction.OpCode is IlOpCode.Brtrue or IlOpCode.Brtrue_S;
            var data = ElementsOf(PopValue());
            Branch(_ir.Compare(jumpWhenTrue ? TirCompareOp.Ne : TirCompareOp.Eq, data, _ir.Zero(TirType.Ptr)));
            return;
        }

        // an array and an object (ReferenceEquals(o, array)): the array's object and the other, as references
        var (second, first) = (PopValue(), PopValue());
        var (left, right) = first.IsObject || second.IsObject
            ? (ObjectFromStack(first, _instruction.Name), ObjectFromStack(second, _instruction.Name))
            : (ElementsOf(first), ElementsOf(second));
        var op = _instruction.OpCode switch
        {
            IlOpCode.Ceq or IlOpCode.Beq or IlOpCode.Beq_S => TirCompareOp.Eq,
            IlOpCode.Bne_Un or IlOpCode.Bne_Un_S => TirCompareOp.Ne,
            IlOpCode.Cgt_Un => TirCompareOp.GtUnsigned,
            _ => TirCompareOp.LtUnsigned,
        };
        var compared = _ir.Compare(op, left, right);
        if (_instruction.OpCode is IlOpCode.Ceq or IlOpCode.Cgt_Un or IlOpCode.Clt_Un)
        {
            Push(StackValue.OfBool(compared));
        }
        else
        {
            Branch(compared);
        }
    }

    /// <summary>The address of an array's elements, to compare: null's is a null ptr.</summary>
    private TirValue ElementsOf(StackValue value) => value switch
    {
        _ when value.Type == TirType.Ptr => Materialize(value), // null (a ptr where IL has an array is null)
        _ when value.Type == StackArrays.ArrayType => _ir.Call(_program.Arrays.Data, Materialize(value)),
        _ => throw Error($"{_instruction.Name} compares an array with {StackName(value.Type)}; an array compares only " +
            "with an array or null, for equality"),
    };

    /// <summary>
    /// For <see cref="ToStack"/>: true for a value of an array type, a view, or an array object loaded from a field (a ref
    /// whose IL type is an array).
    /// </summary>
    private bool IsArrayValue(TirValue value, IlType type) =>
        value.Type == StackArrays.ArrayType
        || (value.Type == TirType.Ref && StackArrays.TryMapType(ValueTypes.WithoutModifiers(type), _method.Assembly, out _));

    /// <summary>An array value of IL type <paramref name="type"/> onto the stack: a view, as it is or made of the object.</summary>
    private StackValue ArrayToStack(TirValue value, IlType type)
    {
        var plain = ValueTypes.WithoutModifiers(type);
        if (value.Type == TirType.Ref)
        {
            var descriptor = _program.HeapArrayDescriptor(plain, _method.Assembly);
            value = _ir.Call(HeapFunctions.FromObject(descriptor), value);
        }

        _arrayTypes[value] = plain;
        return StackValue.Of(value);
    }

    /// <summary>Pushes an array value of IL type <paramref name="type"/>.</summary>
    private void PushArray(TirValue view, IlType type)
    {
        _arrayTypes[view] = type;
        Push(view);
    }

    /// <summary>
    /// For <see cref="FromStack"/>: an array whose IL type is known, going where an array of another type of reference is
    /// wanted (<c>object[] items = grid;</c> for an <c>int[][]</c>), is array covariance, which is not supported.
    /// </summary>
    private void CheckArrayConversion(StackValue value, IlType destination)
    {
        if (value.Type == StackArrays.ArrayType && ArrayTypeOf(value) is { } source
            && HeapArrays.ElementType(source) is { } have && HeapArrays.ElementType(destination) is { } wanted
            && HeapArrays.IsReferenceElement(have, _method.Assembly) && HeapArrays.IsReferenceElement(wanted, _method.Assembly)
            && !ValueTypes.WithoutModifiers(have).Equals(ValueTypes.WithoutModifiers(wanted)))
        {
            throw Error($"{CSharpNames.Type(source)} is used as {CSharpNames.Type(ValueTypes.WithoutModifiers(destination))}, " +
                $"and {Analysis.Unsupported.ArrayCovariance.Heading}");
        }
    }

    /// <summary>For <see cref="ObjectFromStack"/>: an array value stored as an object, its array object (null for null).</summary>
    private TirValue ArrayObject(StackValue value) => _ir.Call(HeapFunctions.ToObject, Materialize(value));

    /// <summary>The IL type of an array value on the stack, when it is known.</summary>
    private IlType? ArrayTypeOf(StackValue value) => value switch
    {
        { Value: { } tir } => _arrayTypes.GetValueOrDefault(tir),
        { Slot: { } slot } => _enteringArrayTypes.GetValueOrDefault((_block, slot)),
        _ => null,
    };

    /// <summary>
    /// For <see cref="Leave"/>: null on the evaluation stack where a block it goes to has an array (C#'s
    /// <c>c ? a : null</c>) is that array's null, the view of zeros, so that every way into the block agrees.
    /// </summary>
    private void NullsAsArrays(IlBlock[] successors)
    {
        for (var depth = 0; depth < _stack.Count; depth++)
        {
            if (_stack[depth].Type == TirType.Ptr && successors.Any(successor =>
                    _arrays.ArraysOnEntry.TryGetValue(successor.Offset, out var depths) && depths.Contains(depth)))
            {
                _stack[depth] = StackValue.Of(_ir.Zero(StackArrays.ArrayType));
            }
        }
    }

    /// <summary>
    /// For <see cref="Leave"/>: the IL type of each array value on the stack, for the blocks it goes to in a stack slot.
    /// When two ways into a block disagree, that block does not know.
    /// </summary>
    private void PassArrayTypes(IEnumerable<IlBlock> successors)
    {
        for (var depth = 0; depth < _stack.Count; depth++)
        {
            if (_stack[depth].Type != StackArrays.ArrayType)
            {
                continue;
            }

            var (slot, type) = (StackSlot(depth, StackArrays.ArrayType), ArrayTypeOf(_stack[depth]));
            foreach (var successor in successors)
            {
                _enteringArrayTypes[(successor, slot)] =
                    _enteringArrayTypes.TryGetValue((successor, slot), out var known) && known != type ? null : type;
            }
        }
    }
}
