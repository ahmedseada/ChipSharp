using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The methods of System.Array ChipSharp compiles itself (see <see cref="ArrayMethods"/>): each call is a call of one
/// of <see cref="ArrayMethodFunctions"/>, with the arrays as their views, the element size or type from the array (on
/// the evaluation stack, for the methods that take an <c>Array</c>) or from T, and the value made fit for an element.
/// <code>
/// %4 = load ChipSharp.Array $V_0                      ldloc.0
/// %5 = load ChipSharp.Array $V_1                      ldloc.1
/// %6 = sizeof i32                                     ldc.i4.3
/// call void ChipSharp.Array.Copy(%4, 0, %5, 0, 3, %6) call void System.Array::Copy(Array, Array, int32)
/// </code>
/// <list type="bullet">
/// <item><c>Array.Resize(ref x, n)</c> takes the address of a local or a parameter (which hold a view) or of a field
/// (which holds the array object): it reads what is there, and writes back the array Resize gives.</item>
/// <item><c>(int[])values.Clone()</c>: the clone is an array of the type it is cast to, so the castclass right after it
/// is nothing more.</item>
/// </list>
/// </summary>
internal sealed partial class MethodLowering
{
    private ArrayMethodFunctions ArrayMethodFunctions => _program.ArrayMethodFunctions;

    /// <summary>
    /// True for the instructions <see cref="LowerArrayMethod"/> translates: a call or callvirt of a method of System.Array
    /// ChipSharp compiles itself, and the castclass of a clone.
    /// </summary>
    private bool IsArrayMethodInstruction(IlInstruction instruction) => instruction.OpCode switch
    {
        IlOpCode.Call or IlOpCode.Callvirt => instruction.Operand is IlMethodOperand { Method: var method }
            && ArrayMethods.Call(method) is { Operation: not ArrayOperation.WithDelegate },
        IlOpCode.Castclass => ArrayMethods.IsCloneCast(_body, _body.IndexOf(instruction.Offset)),
        _ => false,
    };

    private void LowerArrayMethod(IlInstruction instruction)
    {
        if (instruction.OpCode == IlOpCode.Castclass)
        {
            CloneCast(instruction);
            return;
        }

        var call = ArrayMethods.Call(((IlMethodOperand)instruction.Operand!).Method)!;
        var f = ArrayMethodFunctions;
        var zero = _ir.Const(TirType.I32, 0);
        switch (call.Operation)
        {
            case ArrayOperation.Copy or ArrayOperation.CopyLong:
            {
                var length = PopInt(call.Operation == ArrayOperation.CopyLong ? "length" : null);
                var (destination, to) = PopArrayArgument();
                var (source, from) = PopArrayArgument();
                _ir.Call(f.Copy, source, zero, destination, zero, length, ElementSize(from, to));
                break;
            }

            case ArrayOperation.CopyRange or ArrayOperation.CopyRangeLong:
            {
                // the long overload checks sourceIndex, destinationIndex and length in that order
                var wide = call.Operation == ArrayOperation.CopyRangeLong;
                var (length, destinationIndex) = (PopValue(), PopValue());
                var (destination, to) = PopArrayArgument();
                var sourceIndex = PopValue();
                var (source, from) = PopArrayArgument();
                var (si, di, n) = (Int(sourceIndex, wide ? "sourceIndex" : null),
                    Int(destinationIndex, wide ? "destinationIndex" : null), Int(length, wide ? "length" : null));
                _ir.Call(f.Copy, source, si, destination, di, n, ElementSize(from, to));
                break;
            }

            case ArrayOperation.CopyTo or ArrayOperation.CopyToLong:
            {
                var index = PopValue();
                var (destination, to) = PopArrayArgument();
                var (array, from) = PopArrayArgument();
                var wide = call.Operation == ArrayOperation.CopyToLong
                    ? FromStack(index, TirType.I64, IlType.Int64, "the index")
                    : _ir.Convert(Int(index, null), TirType.I64);
                _ir.Call(f.CopyTo, array, destination, wide, ElementSize(from, to));
                break;
            }

            case ArrayOperation.Clear:
            {
                var (array, type) = PopArrayArgument();
                _ir.Call(f.Clear, array, ElementSize(type));
                break;
            }

            case ArrayOperation.ClearRange:
            {
                var length = PopInt(null);
                var index = PopInt(null);
                var (array, type) = PopArrayArgument();
                _ir.Call(f.ClearRange, array, index, length, ElementSize(type));
                break;
            }

            case ArrayOperation.Fill or ArrayOperation.FillRange:
            {
                var range = call.Operation == ArrayOperation.FillRange;
                var count = range ? PopInt(null) : null;
                var start = range ? PopInt(null) : null;
                var (element, value) = PopElement(call.Element!);
                var (array, _) = PopArrayArgument();
                if (range)
                {
                    _ir.Call(f.FillRange(element), array, value, start!, count!);
                }
                else
                {
                    _ir.Call(f.Fill(element), array, value);
                }

                break;
            }

            case ArrayOperation.IndexOf or ArrayOperation.IndexOfFrom or ArrayOperation.IndexOfRange
                or ArrayOperation.LastIndexOf or ArrayOperation.LastIndexOfFrom or ArrayOperation.LastIndexOfRange:
            {
                var arguments = call.Operation is ArrayOperation.IndexOf or ArrayOperation.LastIndexOf ? 2
                    : call.Operation is ArrayOperation.IndexOfFrom or ArrayOperation.LastIndexOfFrom ? 3
                    : 4;
                var rest = new TirValue[arguments - 2];
                for (var i = rest.Length - 1; i >= 0; i--)
                {
                    rest[i] = PopInt(null);
                }

                var (element, value) = PopElement(call.Element!);
                var (array, _) = PopArrayArgument();
                var last = call.Operation is ArrayOperation.LastIndexOf or ArrayOperation.LastIndexOfFrom
                    or ArrayOperation.LastIndexOfRange;
                var function = last ? f.LastIndexOf(element, arguments) : f.IndexOf(element, arguments);
                Push(_ir.Call(function, [array, value, .. rest]));
                break;
            }

            case ArrayOperation.Reverse or ArrayOperation.ReverseRange:
            {
                var range = call.Operation == ArrayOperation.ReverseRange;
                var length = range ? PopInt(null) : null;
                var index = range ? PopInt(null) : null;
                var (array, _) = PopArrayArgument();
                var element = StorageType(call.Element!);
                _ir.Call(f.Reverse(element, range), range ? new[] { array, index!, length! } : new[] { array });
                break;
            }

            case ArrayOperation.Resize:
                Resize(call.Element!);
                break;

            case ArrayOperation.Clone:
            {
                var (array, type) = PopArrayArgument();
                var cast = ArrayMethods.CastType(_body.Instructions[_body.IndexOf(_instruction.Offset) + 1]);
                var arrayType = (type == IlType.Void ? null : type) ?? cast
                    ?? throw Error("array.Clone() of an array whose type is not known here");
                var clone = _ir.Call(f.Clone(_program.HeapArrayDescriptor(arrayType, _method.Assembly)), array);
                PushArray(clone, arrayType);
                break;
            }

            default:
                throw Error($"{call.Operation} of System.Array is not supported here");
        }
    }

    /// <summary>
    /// The castclass right after <c>array.Clone()</c>: the clone is an array of its own type already, which must be the
    /// type it is cast to (array covariance is not supported).
    /// </summary>
    private void CloneCast(IlInstruction castclass)
    {
        var cast = ArrayMethods.CastType(castclass);
        if (_stack.Count == 0 || _stack[^1].Type != StackArrays.ArrayType || cast is null
            || ArrayTypeOf(_stack[^1]) is not { } type
            || !ValueTypes.WithoutModifiers(type).Equals(ValueTypes.WithoutModifiers(cast)))
        {
            throw Error($"castclass of array.Clone() casts it to another type than its own; {Analysis.Unsupported.CloneWithoutCast.Heading}");
        }
    }

    /// <summary>
    /// <c>Array.Resize(ref x, newSize)</c> of an array of <paramref name="element"/>: what x holds goes to Resize, and
    /// what Resize gives goes back into x, as a view in a local's or parameter's slot, as the array object in a field.
    /// </summary>
    private void Resize(IlType element)
    {
        var newSize = PopInt(null);
        var reference = PopValue();
        var arrayType = new IlArrayType(element);
        var descriptor = _program.HeapArrayDescriptor(arrayType, _method.Assembly);
        var resize = ArrayMethodFunctions.Resize(descriptor);
        switch (reference.Value)
        {
            case TirSlotAddress { Slot.Type: var type } address when type == StackArrays.ArrayType:
                _ir.Store(address, _ir.Call(resize, _ir.Load(StackArrays.ArrayType, address), newSize));
                break;

            case TirGlobalAddress { Global.Type: var type } address when type == TirType.Ref:
                ResizeObject(address);
                break;

            case TirFieldAddress { Field.Type: var type } address when type == TirType.Ref:
                ResizeObject(address);
                break;

            default:
                throw Error($"Array.Resize takes the address of a local, a parameter or a field here; " +
                    $"{Analysis.Unsupported.ResizeReference.Heading}");
        }

        void ResizeObject(TirValue address)
        {
            var view = _ir.Call(HeapFunctions.FromObject(descriptor), _ir.Load(TirType.Ref, address));
            _ir.Store(address, _ir.Call(HeapFunctions.ToObject, _ir.Call(resize, view, newSize)));
        }
    }

    /// <summary>
    /// Pops an array argument: its view (null, ldnull's, as the view of zeros) and its IL type, when that is known; the
    /// type of null is <see cref="IlType.Void"/>, which any array's size will do for.
    /// </summary>
    private (TirValue View, IlType? Type) PopArrayArgument()
    {
        var value = PopValue();
        return value.Type == StackArrays.ArrayType ? (Materialize(value), ArrayTypeOf(value))
            : value.Type == TirType.Ptr ? (_ir.Zero(StackArrays.ArrayType), IlType.Void)
            : throw Error($"{_instruction.Name} needs an array, but the evaluation stack has {StackName(value.Type)}");
    }

    /// <summary>Pops the value of an element of <paramref name="element"/> (T), as an element holds it.</summary>
    private (TirType Storage, TirValue Value) PopElement(IlType element)
    {
        var storage = StorageType(element);
        return (storage, FromStack(PopValue(), storage, element, "the value"));
    }

    /// <summary>Pops an int argument, or a long one that becomes an int after .NET's check (see <see cref="Int"/>).</summary>
    private TirValue PopInt(string? longParameter) => Int(PopValue(), longParameter);

    /// <summary>
    /// An int argument; or, when <paramref name="longParameter"/> names the parameter of a long overload, the long as the
    /// int .NET makes it (a long that is no int stops the program, for that parameter).
    /// </summary>
    private TirValue Int(StackValue value, string? longParameter) => longParameter is null
        ? FromStack(value, TirType.I32, IlType.Int32, "the argument")
        : _ir.Call(ArrayMethodFunctions.Narrow(longParameter), FromStack(value, TirType.I64, IlType.Int64, "the argument"));

    /// <summary>
    /// The size of an element of the arrays a copy or Clear takes, of the IL types <see cref="PopArrayArgument"/> gives
    /// (void for null, which the function stops at before it uses the size). Two arrays of different elements are not
    /// supported, and an array whose type is not known here (it came by ways that do not agree) is not either.
    /// </summary>
    private TirValue ElementSize(params IlType?[] arrays)
    {
        if (arrays.Any(type => type is null))
        {
            throw Error($"{_instruction.Name} takes an array whose element type is not known here: keep it in a " +
                "variable of its type first");
        }

        var elements = arrays.Where(type => type != IlType.Void).Select(type => ValueTypes.WithoutModifiers(
            HeapArrays.ElementType(type!)!)).Distinct().ToList();
        if (elements.Count > 1)
        {
            throw Error($"{string.Join(" and ", arrays.Select(type => CSharpNames.Type(type!)))}: " +
                Analysis.Unsupported.ArrayCopyTypes.Heading);
        }

        return elements.Count == 1 ? _ir.SizeOf(StorageType(elements[0])) : _ir.Const(TirType.I32, 1);
    }
}
