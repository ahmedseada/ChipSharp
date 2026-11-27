using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Spans (see <see cref="Spans"/> for the representation): a span value is a <c>ChipSharp.Array</c>, so an array
/// becomes a span as it is; the members ChipSharp compiles itself call <see cref="SpanFunctions"/>, or are a few
/// instructions where that is all they are. A member called on a span (the indexer, Length, Slice, CopyTo, ...) gets
/// its address as <c>this</c> and loads it first.
/// <code>
/// %3 = load ChipSharp.Array [%2]                          ldloca.s V_1; ldloc.2
/// call void ChipSharp.Array.CheckIndex(%3, %4)            call instance !0&amp; Span`1&lt;int32&gt;::get_Item(int32)
/// %5 = call ptr ChipSharp.Array.Data(%3)
/// %6 = elementaddr i32 %5, %4
/// </code>
/// <list type="bullet">
/// <item><c>stackalloc T[n]</c> (n a constant): a slot of <c>[n x T]</c>, one per <c>localloc</c>, zeroed each time it
/// runs, like the elements of an array on the stack; its initializer stores into it, or copies constant data in with
/// memcpy (IL's <c>cpblk</c>).</item>
/// <item>Constant data (<c>static ReadOnlySpan&lt;byte&gt; X =&gt; new byte[] { ... }</c>, C#'s <c>ldsflda</c> of the
/// data and <c>new ReadOnlySpan&lt;T&gt;(void*, int)</c>, or <c>RuntimeHelpers.CreateSpan&lt;T&gt;</c>): a constant global
/// with the elements (<see cref="ProgramFunctions.SpanData"/>), read where it is, in flash on a chip.</item>
/// <item>MemoryMarshal.Read and Write read and write unaligned (<c>load unaligned</c>), so they work at any offset.</item>
/// </list>
/// </summary>
internal sealed partial class MethodLowering
{
    private readonly Dictionary<IlInstruction, TirSlot> _stackallocSlots = [];
    private IReadOnlyList<Stackalloc>? _stackallocs;
    private IReadOnlyList<DataUse>? _dataUses;

    /// <summary>The stackallocs of the method that make spans.</summary>
    private IReadOnlyList<Stackalloc> Stackallocs => _stackallocs ??= Spans.FindStackallocs(_body);

    /// <summary>The uses of constant data through ldsflda in the method.</summary>
    private IReadOnlyList<DataUse> DataUses => _dataUses ??= Spans.FindDataUses(_body);

    /// <summary>
    /// Rejects spans that would outlive an array or stackalloc of the method, and the other span problems, with the
    /// verdicts the whole-program pass uses (<see cref="WholeProgramPass.ForSpans"/>), so the two always agree; a span
    /// that puts its array on the heap (a heap use, see <see cref="HeapArrays"/>) is fine here.
    /// </summary>
    private void CheckSpans()
    {
        // (printed text's own instructions are its to judge: the params span of a format)
        var problem = WholeProgramPass.ForSpans(_method)
            .Where(verdict => verdict.Value is { IsHeapUse: false } && !IsPrintedText(verdict.Key))
            .OrderBy(verdict => verdict.Key.Offset).FirstOrDefault();
        if (problem.Value is { } unsupported)
        {
            throw LoweringErrors.At(_method, problem.Key.Offset, $"{problem.Key.Name}: {unsupported.Heading}");
        }
    }

    /// <summary>
    /// True for the instructions <see cref="LowerSpanInstruction"/> translates: a call or newobj of a member ChipSharp
    /// compiles itself, a localloc, the instructions of a stackalloc's size and initializer that are not plain IL
    /// (sizeof, mul.ovf.un, cpblk, unaligned., and add on its address), and ldsflda of constant data.
    /// </summary>
    private bool IsSpanInstruction(IlInstruction instruction)
    {
        switch (instruction.OpCode)
        {
            case IlOpCode.Call or IlOpCode.Newobj:
                return instruction.Operand is IlMethodOperand { Method: var method } && Spans.Call(method) is not null;

            case IlOpCode.Localloc:
                return true;

            case IlOpCode.Sizeof or IlOpCode.Mul_Ovf_Un:
                var index = _body.IndexOf(instruction.Offset);
                return Stackallocs.Any(allocation => index >= allocation.Start && index < allocation.Localloc);

            case IlOpCode.Cpblk or IlOpCode.Unaligned or IlOpCode.Add:
                index = _body.IndexOf(instruction.Offset);
                return Stackallocs.Any(allocation => allocation.Contains(index));

            case IlOpCode.Ldsflda:
                index = _body.IndexOf(instruction.Offset);
                return DataUses.Any(use => use.Index == index);

            default:
                return false;
        }
    }

    private void LowerSpanInstruction(IlInstruction instruction)
    {
        switch (instruction.OpCode)
        {
            case IlOpCode.Localloc:
                Stackalloc();
                break;

            case IlOpCode.Sizeof:
                // the size of a struct stackalloc makes room for: the slot has the struct's own type
                Push(StackValue.OfConstant(TirType.I32, 0));
                break;

            case IlOpCode.Mul_Ovf_Un:
                PopValue();
                PopValue();
                Push(StackValue.OfConstant(TirType.NativeInt, 0));
                break;

            case IlOpCode.Unaligned:
                break; // memcpy copies at any alignment

            case IlOpCode.Cpblk:
                var size = PopNumber();
                var source = PopAddress();
                var destination = PopAddress();
                _ir.Call(_program.Spans.Memcpy, destination, source, _ir.Convert(size, TirType.NativeInt, unsigned: true));
                break;

            case IlOpCode.Add:
                PointerAdd();
                break;

            case IlOpCode.Ldsflda:
                ConstantData();
                break;

            default:
                LowerSpanCall(Spans.Call(((IlMethodOperand)instruction.Operand!).Method)!,
                    instruction.OpCode == IlOpCode.Newobj);
                break;
        }
    }

    /// <summary>
    /// localloc of a stackalloc that makes a span: the address of element 0 of its slot, zeroed, as .NET zeroes it
    /// (C#'s locals init); the size in bytes it pops is the slot's.
    /// </summary>
    private void Stackalloc()
    {
        var index = _body.IndexOf(_instruction.Offset);
        var allocation = Stackallocs.FirstOrDefault(found => found.Localloc == index)
            ?? throw Error("localloc is supported only as C# makes stackalloc into a span (Span<int> s = stackalloc " +
                "int[4];), not into a pointer");
        if (allocation.Count is not { } count || count < 0)
        {
            throw Error("stackalloc needs a constant number of elements here (it lives on the stack)");
        }

        PopValue();
        var element = StorageType(allocation.Element);
        if (!_stackallocSlots.TryGetValue(_instruction, out var slot))
        {
            slot = AddSlot($"stackalloc_{IlPrinter.Label(_instruction.Offset)}", new TirArrayType(element, Math.Max(count, 1)));
            _stackallocSlots.Add(_instruction, slot);
        }

        _ir.Store(slot, _ir.Zero(slot.Type));
        Push(_ir.ElementAddress(_ir.AddressOf(slot), element, _ir.Const(TirType.I32, 0)));
    }

    /// <summary>
    /// add in a stackalloc's initializer: the address of the element at that many bytes from the start, which C#'s
    /// initializer stores the next element through (any other add, of numbers, as usual).
    /// </summary>
    private void PointerAdd()
    {
        if (_stack.Count < 2 || _stack[^2].Type != TirType.Ptr)
        {
            var (left, right) = PopOperands();
            Push(_ir.Binary(TirBinaryOp.Add, left, right));
            return;
        }

        var offset = PopValue();
        var address = PopAddress();
        var index = _body.IndexOf(_instruction.Offset);
        var element = StorageType(Stackallocs.First(allocation => allocation.Contains(index)).Element);
        var size = Spans.DataElementSize(element);
        if (!offset.IsConstant || size == 0 || offset.Constant % size != 0)
        {
            throw Error("add on the address of a stackalloc is supported only as C#'s initializer adds the offset of " +
                "an element of a number type");
        }

        Push(_ir.ElementAddress(address, element, _ir.Const(TirType.I32, offset.Constant / size)));
    }

    /// <summary>ldsflda of constant data: the address of its constant global (elements of the span's type, or bytes to copy).</summary>
    private void ConstantData()
    {
        var index = _body.IndexOf(_instruction.Offset);
        var use = DataUses.First(found => found.Index == index);
        TirType? element = use.Element is { } type ? StorageType(type) : null;
        var global = _program.SpanData(use.Field, element, use.Count)
            ?? throw Error($"the field {use.Field.Name} has no data for {use.Count} elements");
        Push(_ir.AddressOf(global));
    }

    /// <summary>A call or newobj of a member ChipSharp compiles itself (see <see cref="SpanOperation"/>).</summary>
    private void LowerSpanCall(SpanCall call, bool isNew)
    {
        var spans = _program.Spans;
        var arrays = _program.Arrays;

        // a constructor C# calls on a span's address (var s = new Span<int>(array, 1, 2)) stores the span there
        var onAddress = !isNew && ((IlMethodOperand)_instruction.Operand!).Method.Name == ".ctor";
        switch (call.Operation)
        {
            case SpanOperation.FromArray:
                Construct(onAddress, PopSpan());
                break;

            case SpanOperation.FromArrayStart or SpanOperation.Slice:
                var start = PopNumber();
                var span = call.Operation == SpanOperation.Slice ? LoadThis() : PopSpan();
                Push(_ir.Call(spans.From(StorageType(call.Element)), span, start));
                break;

            case SpanOperation.FromArrayRange or SpanOperation.SliceRange:
                var length = PopNumber();
                start = PopNumber();
                var element = StorageType(call.Element);
                if (call.Operation == SpanOperation.SliceRange)
                {
                    Push(_ir.Call(spans.Range(element), LoadThis(), start, length));
                }
                else
                {
                    var range = _ir.Call(spans.Range(element), PopSpan(), start, length);
                    Construct(onAddress, range);
                }

                break;

            case SpanOperation.FromPointer:
                length = PopNumber();
                var data = PopAddress();
                Construct(onAddress, _ir.Call(spans.FromPointer, data, length));
                break;

            case SpanOperation.FromReference:
                Construct(onAddress, _ir.Call(arrays.Make, PopAddress(), _ir.Const(TirType.I32, 1)));
                break;

            case SpanOperation.FromReferenceAndLength:
                length = PopNumber();
                Push(_ir.Call(arrays.Make, PopAddress(), length));
                break;

            case SpanOperation.ToReadOnly:
                Push(PopSpan());
                break;

            case SpanOperation.Empty:
                Push(_ir.Zero(StackArrays.ArrayType));
                break;

            case SpanOperation.Item:
                var index = PopNumber();
                span = LoadThis();
                _ir.Call(arrays.CheckIndex(index.Type), span, index);
                Push(_ir.ElementAddress(_ir.Call(arrays.Data, span), StorageType(call.Element), index));
                break;

            case SpanOperation.Length:
                Push(_ir.Call(arrays.Length, LoadThis()));
                break;

            case SpanOperation.IsEmpty:
                Push(StackValue.OfBool(_ir.Compare(TirCompareOp.Eq, _ir.Call(arrays.Length, LoadThis()),
                    _ir.Const(TirType.I32, 0))));
                break;

            case SpanOperation.CopyTo:
                var destination = PopSpan();
                _ir.Call(spans.CopyTo(StorageType(call.Element)), LoadThis(), destination);
                break;

            case SpanOperation.TryCopyTo:
                destination = PopSpan();
                Push(StackValue.OfBool(_ir.Call(spans.TryCopyTo(StorageType(call.Element)), LoadThis(), destination)));
                break;

            case SpanOperation.Fill:
                element = StorageType(call.Element);
                var value = FromStack(PopValue(), element, call.Element, "the value of Fill");
                _ir.Call(spans.Fill(element), LoadThis(), value);
                break;

            case SpanOperation.Clear:
                _ir.Call(spans.Clear(StorageType(call.Element)), LoadThis());
                break;

            case SpanOperation.Equal or SpanOperation.NotEqual:
                var right = PopSpan();
                var equal = _ir.Call(spans.Equal, PopSpan(), right);
                Push(StackValue.OfBool(call.Operation == SpanOperation.Equal
                    ? equal
                    : _ir.Compare(TirCompareOp.Eq, equal, _ir.Const(false))));
                break;

            case SpanOperation.Data:
                Push(DataSpan(StorageType(call.Element)));
                break;

            case SpanOperation.Read:
                var storage = StorageType(call.Element);
                Push(ToStack(_ir.Call(spans.Read(storage), PopSpan()), call.Element));
                break;

            case SpanOperation.Write or SpanOperation.TryRead or SpanOperation.TryWrite:
                var (type, stored) = (ValueType(call.Element), StorageType(call.Element));
                var address = PopAddress();
                var bytes = PopSpan();
                if (call.Operation == SpanOperation.Write)
                {
                    _ir.Call(spans.Write(type, stored), bytes, address);
                }
                else
                {
                    var function = call.Operation == SpanOperation.TryRead
                        ? spans.TryRead(type, stored)
                        : spans.TryWrite(type, stored);
                    Push(StackValue.OfBool(_ir.Call(function, bytes, address)));
                }

                break;

            case SpanOperation.AsBytes:
                Push(_ir.Call(spans.AsBytes(StorageType(call.Element)), PopSpan()));
                break;

            case SpanOperation.Cast:
                var (from, to) = (StorageType(call.Element), StorageType(call.Target!));
                Push(_ir.Call(spans.Cast, PopSpan(), _ir.SizeOf(from), _ir.SizeOf(to), CastAlignment(from, to)));
                break;

            default:
                throw Error($"{CSharpNames.Method(((IlMethodOperand)_instruction.Operand!).Method)} is not supported yet " +
                    (call.Operation == SpanOperation.ToArray
                        ? "(a new array needs the heap, which arrives with M3; copy into an array with CopyTo)"
                        : "(it builds text, which needs the heap)"));
        }
    }

    /// <summary>
    /// The span a constructor (or a conversion) made: stored at the span's address for a constructor called on it,
    /// <paramref name="onAddress"/>; else pushed.
    /// </summary>
    private void Construct(bool onAddress, TirValue span)
    {
        if (onAddress)
        {
            _ir.Store(PopAddress(), span);
        }
        else
        {
            Push(span);
        }
    }

    /// <summary>The span a member is called on: its address is <c>this</c>.</summary>
    private TirValue LoadThis() => _ir.Load(StackArrays.ArrayType, PopAddress());

    /// <summary>Pops a span or an array (the same value); C#'s null as an array is an empty span, as in .NET.</summary>
    private TirValue PopSpan()
    {
        var value = PopValue();
        if (value.IsNull)
        {
            return _ir.Zero(StackArrays.ArrayType);
        }

        return value.Type == StackArrays.ArrayType
            ? Materialize(value)
            : throw Error($"{_instruction.Name} needs a span or an array, but the evaluation stack has {StackName(value.Type)}");
    }

    /// <summary>
    /// RuntimeHelpers.CreateSpan&lt;T&gt;(field), after the ldtoken of the field (whose stand-in it pops): a span of its
    /// constant global, with as many elements as its data has.
    /// </summary>
    private TirValue DataSpan(TirType element)
    {
        PopValue();
        var index = _body.IndexOf(_instruction.Offset);
        if (index < 1 || _body.Instructions[index - 1].Operand is not IlFieldOperand { Field.Definition: { } field }
            || Spans.DataSize(field) is not { } size || Spans.DataElementSize(element) is not (> 0 and var each))
        {
            throw Error("RuntimeHelpers.CreateSpan is supported only right after the ldtoken of constant data, as C# calls it");
        }

        var count = size / each;
        var global = _program.SpanData(field, element, count)
            ?? throw Error($"the field {field.Name} has no data for {count} elements");
        return _ir.Call(_program.Arrays.Make, _ir.AddressOf(global), _ir.Const(TirType.I32, count));
    }

    /// <summary>The TIR type a value of <paramref name="type"/> has (a bool is a bool, not the byte it is in memory).</summary>
    private TirType ValueType(IlType type) => ValueTypes.TryMapType(type, _method.Assembly, out var tirType)
        ? tirType
        : StorageType(type);

    /// <summary>
    /// The alignment MemoryMarshal.Cast checks a span's start against: 1 (none) on a target that reads values at any
    /// address, or when <paramref name="to"/> needs no more alignment than <paramref name="from"/>; else what
    /// <paramref name="to"/> needs (the pointer size when that depends on it).
    /// </summary>
    private TirValue CastAlignment(TirType from, TirType to)
    {
        var (narrow, wide) = (new TirLayout(4), new TirLayout(8));
        if (_program.UnalignedAccess
            || (narrow.AlignmentOf(to) <= narrow.AlignmentOf(from) && wide.AlignmentOf(to) <= wide.AlignmentOf(from)))
        {
            return _ir.Const(TirType.I32, 1);
        }

        return narrow.AlignmentOf(to) == wide.AlignmentOf(to)
            ? _ir.Const(TirType.I32, narrow.AlignmentOf(to))
            : _ir.SizeOf(TirType.NativeInt);
    }
}
