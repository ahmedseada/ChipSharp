using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Arrays on the stack (see <see cref="StackArrays"/> for the rule and the representation). An array value is a
/// <c>ChipSharp.Array</c> on the evaluation stack, in locals and in parameters; <c>newarr</c> zeroes the slot of its
/// elements and makes one with <see cref="ArrayFunctions.Make"/>; every element access checks its index first.
/// <code>
/// %3 = load ChipSharp.Array $V_0                  ldloc.0
/// %4 = load i32 $V_1                              ldloc.1
/// call void ChipSharp.Array.CheckIndex(%3, %4)    ldelem.i4
/// %5 = call ptr ChipSharp.Array.Data(%3)
/// %6 = elementaddr i32 %5, %4
/// %7 = load i32 [%6]
/// </code>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>The slot of the elements of each newarr of at least one element.</summary>
    private readonly Dictionary<IlInstruction, TirSlot> _elementSlots = [];

    /// <summary>
    /// Rejects arrays that break the rule of <see cref="StackArrays"/> (and of <see cref="HeapArrays"/>), with the analysis
    /// the whole-program pass uses (<see cref="ArrayEscapeAnalysis"/>), so the two always agree; what needs a heap is
    /// fine here (the code generator refuses a target without one). The array of strings of a <c>string.Concat</c> of
    /// printed text is no array (see <see cref="PrintedText"/>).
    /// </summary>
    private void CheckArrays()
    {
        if (ArrayEscapeAnalysis.ForReturnType(_method) is { IsHeapUse: false } returned)
        {
            throw LoweringErrors.In(_method, $"it returns {_method.ReturnType}, and {returned.Heading}");
        }

        _arrays = ArrayEscapeAnalysis.Analyze(_method, _program.StaticArrays, _program.HeapArrays);
        var problem = _arrays.Verdicts
            .Where(verdict => verdict.Value is { IsHeapUse: false } && !IsPrintedText(verdict.Key))
            .OrderBy(verdict => verdict.Key.Offset).FirstOrDefault();
        if (problem.Value is { } unsupported)
        {
            throw LoweringErrors.At(_method, problem.Key.Offset, $"{problem.Key.Name}: {unsupported.Heading}");
        }
    }

    /// <summary>
    /// True for the instructions <see cref="LowerArrayInstruction"/> translates. An element's address (ldelema) is a
    /// reference like any other: ldind, stind, ldfld and the rest go through it as through a <c>ref</c>.
    /// </summary>
    private bool IsArrayInstruction(IlInstruction instruction) => instruction.OpCode switch
    {
        IlOpCode.Newarr or IlOpCode.Ldlen or IlOpCode.Ldelema or IlOpCode.Ldelem or IlOpCode.Stelem
            or (>= IlOpCode.Ldelem_I1 and <= IlOpCode.Ldelem_Ref) or (>= IlOpCode.Stelem_I and <= IlOpCode.Stelem_Ref)
            or IlOpCode.Conv_Ovf_I or IlOpCode.Conv_Ovf_I_Un or IlOpCode.Ldtoken => true,
        IlOpCode.Call => instruction.Operand is IlMethodOperand { Method: var method }
            && (StackArrays.IsInitializeArray(method) || StackArrays.IsEmptyArray(method, _method.Assembly, out _)),
        _ => IsArrayComparison(instruction),
    };

    private void LowerArrayInstruction(IlInstruction instruction)
    {
        switch (instruction.OpCode)
        {
            case var _ when IsArrayComparison(instruction):
                LowerArrayComparison();
                break;

            case IlOpCode.Newarr when _arrays.HeapArrays.Contains(instruction.Offset):
                NewHeapArray();
                break;

            case IlOpCode.Newarr:
                NewArray();
                break;

            case IlOpCode.Ldelem_Ref or IlOpCode.Stelem_Ref:
                LowerReferenceElement();
                break;

            case IlOpCode.Ldlen:
                // IL's length is a native unsigned int; C# converts it to int at once (conv.i4)
                var length = _ir.Call(LengthFunction, PopArray());
                Push(Convert(StackValue.Of(length), TirType.NativeInt, unsigned: true));
                break;

            case IlOpCode.Ldelema:
                Push(ElementAddress(Element().Type));
                break;

            case IlOpCode.Ldelem or (>= IlOpCode.Ldelem_I1 and <= IlOpCode.Ldelem_Ref):
                var (type, ilType) = Element();
                Push(ToStack(_ir.Load(type, ElementAddress(type)), ilType));
                break;

            case IlOpCode.Stelem or (>= IlOpCode.Stelem_I and <= IlOpCode.Stelem_Ref):
                (type, ilType) = Element();
                var value = PopValue();
                _ir.Store(ElementAddress(type), FromStack(value, type, ilType, "the array element"));
                break;

            case IlOpCode.Conv_Ovf_I or IlOpCode.Conv_Ovf_I_Un:
                // only ever an index or a constant that fits (the rule of StackArrays): an index stays as it is, and
                // the bounds check, which reads it as unsigned, stops the program for every value conv.ovf.i rejects
                var converted = PopValue();
                Push(converted.IsConstant ? Convert(converted, TirType.NativeInt, unsigned: false) : converted);
                break;

            case IlOpCode.Ldtoken:
                // only ever the data of an array initializer, which the call to InitializeArray after it reads
                Push(StackValue.OfConstant(TirType.NativeInt, 0));
                break;

            case IlOpCode.Call when StackArrays.IsEmptyArray(((IlMethodOperand)instruction.Operand!).Method, _method.Assembly,
                out var emptyOf):
                var emptyType = new IlArrayType(StackArrays.Primitive(emptyOf));
                PushArray(EmptyArray(emptyType), emptyType);
                break;

            default:
                InitializeArray();
                break;
        }
    }

    /// <summary>
    /// newarr of a constant length on the stack: an empty array (no elements, a byte of its own as their address), or
    /// the elements' slot zeroed, as a new array is, every time this runs.
    /// </summary>
    private void NewArray()
    {
        var count = PopValue();
        if (!count.IsConstant || count.Constant is < 0 or > int.MaxValue)
        {
            throw Error("newarr needs a constant number of elements here (an array lives on the stack)");
        }

        var (element, ilElement) = Element();
        if (count.Constant == 0)
        {
            PushArray(EmptyStackArray(), new IlArrayType(ilElement));
            return;
        }

        if (!_elementSlots.TryGetValue(_instruction, out var slot))
        {
            slot = AddSlot($"array_{IlPrinter.Label(_instruction.Offset)}", new TirArrayType(element, (int)count.Constant));
            _elementSlots.Add(_instruction, slot);
        }

        _ir.Store(slot, _ir.Zero(slot.Type));
        PushArray(_ir.Call(_program.Arrays.Make, _ir.AddressOf(slot), _ir.Const(TirType.I32, count.Constant)),
            new IlArrayType(ilElement));
    }

    /// <summary>
    /// <c>newarr T; dup; ldtoken field; call InitializeArray</c>: the elements C# keeps in the field's data, stored one
    /// by one into the new array's slot, or its elements on the heap (the zeros are there already).
    /// </summary>
    private void InitializeArray()
    {
        PopValue(); // ldtoken's stand-in
        var made = PopValue(); // the array, which dup left below it too
        var instructions = _body.Instructions;
        var index = _body.IndexOf(_instruction.Offset);
        if (index < 3 || instructions[index - 1].Operand is not IlFieldOperand { Field.Definition: { } field }
            || instructions[index - 2].OpCode != IlOpCode.Dup || instructions[index - 3].OpCode != IlOpCode.Newarr)
        {
            throw Error("InitializeArray is supported only right after newarr, dup and ldtoken, as C# calls it");
        }

        TirArrayType array;
        TirValue elements;
        if (_elementSlots.TryGetValue(instructions[index - 3], out var slot))
        {
            array = (TirArrayType)slot.Type;
            elements = _ir.AddressOf(slot);
        }
        else if (_heapInitialized.TryGetValue(instructions[index - 3], out var onHeap) && onHeap.Count > 0)
        {
            array = new TirArrayType(onHeap.Element, onHeap.Count);
            elements = _ir.Call(_program.Arrays.Data, Materialize(made));
        }
        else
        {
            return; // an empty array
        }

        var size = array.Element.Kind switch
        {
            TirTypeKind.I8 => 1,
            TirTypeKind.I16 => 2,
            TirTypeKind.I32 or TirTypeKind.F32 => 4,
            TirTypeKind.I64 or TirTypeKind.F64 => 8,
            _ => throw Error($"an array of {array.Element} has no initializer data, as its size depends on the target"),
        };
        var data = _method.Assembly.ReadFieldData(field, array.Count * size)
            ?? throw Error($"the field {field.Name} has no data for {array.Count} elements of {size} bytes");

        for (var i = 0; i < array.Count; i++)
        {
            var bytes = data.AsSpan(i * size, size);
            long value = size switch
            {
                1 => (sbyte)bytes[0],
                2 => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes),
                4 => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes),
                _ => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes),
            };

            // the bits of a float or double: -0.0 is not zero, and is stored
            if (value != 0)
            {
                _ir.Store(_ir.ElementAddress(elements, array.Element, _ir.Const(TirType.I32, i)),
                    array.Element.Kind switch
                    {
                        TirTypeKind.F32 => _ir.Const(TirType.F32, BitConverter.Int32BitsToSingle((int)value)),
                        TirTypeKind.F64 => _ir.Const(TirType.F64, BitConverter.Int64BitsToDouble(value)),
                        _ => _ir.Const(array.Element, value),
                    });
            }
        }
    }

    /// <summary>The element type an element instruction reads or writes: in memory, and as IL names it.</summary>
    private (TirType Type, IlType IlType) Element()
    {
        var ilType = StackArrays.ElementType(_instruction) ?? throw Error($"{_instruction.Name} names no element type");
        return (StorageType(ilType), ilType);
    }

    private TirType StorageType(IlType element) => HeapArrays.TryMapElementType(element, _method.Assembly, out var type)
        ? type
        : throw Error($"an array of {element} is not supported yet (arrays hold bools, whole numbers, floats, the " +
            "structs and enums of the program, arrays and objects)");

    /// <summary>
    /// Pops an index and an array; checks the index, which stops the program when it is outside the array; gives the
    /// element's address.
    /// </summary>
    private TirElementAddress ElementAddress(TirType element)
    {
        var index = PopNumber();
        var array = PopArray();
        if (index.Type.Kind is not (TirTypeKind.I32 or TirTypeKind.NativeInt or TirTypeKind.I64))
        {
            throw Error($"{_instruction.Name} takes an int32 or native int index, not {LoweringErrors.StackTypeName(index.Type)}");
        }

        _ir.Call(CheckIndexFunction(index.Type), array, index);
        return _ir.ElementAddress(_ir.Call(_program.Arrays.Data, array), element, index);
    }

    /// <summary>
    /// Pops an array value: a <c>ChipSharp.Array</c>, or null as the view of zeros (a ptr where IL has an array is null,
    /// also one that waited in a stack slot).
    /// </summary>
    private TirValue PopArray()
    {
        var array = PopValue();
        return array.Type == StackArrays.ArrayType ? Materialize(array)
            : array.Type == TirType.Ptr ? _ir.Zero(StackArrays.ArrayType)
            : throw Error($"{_instruction.Name} needs an array, but the evaluation stack has {StackName(array.Type)}");
    }
}
