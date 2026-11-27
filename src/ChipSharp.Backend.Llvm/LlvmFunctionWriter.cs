using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// Writes one defined function as an LLVM <c>define</c>. Its local names are stable: parameters are <c>%p.name</c>,
/// slots <c>%s.name</c>, temporaries <c>%t0</c>, <c>%t1</c>, ... in block order, and blocks keep their TIR names
/// (made LLVM-safe like symbols). The first block, <c>allocas</c>, holds the slots and jumps to the TIR entry block.
/// A <c>nullcheck</c> ends an LLVM block in the middle of a TIR block: it branches to <c>null_reference</c>, one block at
/// the end of the function that calls the runtime's <see cref="LlvmHeap.NullReferenceSymbol"/>, or goes on in a block
/// of its own, <c>not_null</c>, <c>not_null_2</c>, ...
/// </summary>
internal sealed class LlvmFunctionWriter
{
    private readonly TirFunction _function;
    private readonly LlvmTypes _types;
    private readonly IReadOnlyDictionary<TirFunction, string> _symbols;
    private readonly IReadOnlyDictionary<TirGlobal, string> _globals;
    private readonly IReadOnlyDictionary<TirTypeDescriptor, string> _typeDescriptors;
    private readonly StringBuilder _text;

    /// <summary>The declarations of the LLVM intrinsics the function calls, which the module writes once.</summary>
    private readonly ISet<string> _declarations;

    /// <summary>Every local name used so far; labels and values share one namespace in an LLVM function.</summary>
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

    /// <summary>How each parameter and temporary is written as an operand, e.g. "%p.n", "%t3" or "42".</summary>
    private readonly Dictionary<TirValue, string> _values = [];

    private readonly Dictionary<TirSlot, string> _slots = [];
    private readonly Dictionary<TirBlock, string> _labels = [];
    private int _nextTemporary;

    /// <summary>The label of the block that stops the program with NullReferenceException; null until a nullcheck needs it.</summary>
    private string? _nullReference;

    public LlvmFunctionWriter(TirFunction function, LlvmTypes types, IReadOnlyDictionary<TirFunction, string> symbols,
        IReadOnlyDictionary<TirGlobal, string> globals, IReadOnlyDictionary<TirTypeDescriptor, string> typeDescriptors,
        StringBuilder text, ISet<string> declarations)
    {
        _function = function;
        _types = types;
        _symbols = symbols;
        _globals = globals;
        _typeDescriptors = typeDescriptors;
        _text = text;
        _declarations = declarations;
    }

    public void Write()
    {
        var allocas = Unique("allocas");
        foreach (var parameter in _function.Parameters)
        {
            _values[parameter] = "%" + Unique("p." + LlvmText.Name(parameter.Name));
        }

        foreach (var slot in _function.Slots)
        {
            _slots[slot] = "%" + Unique("s." + LlvmText.Name(slot.Name));
        }

        foreach (var block in _function.Blocks)
        {
            _labels[block] = Unique(LlvmText.Name(block.Name));
        }

        // internal: only chipsharp_entry and the exported functions are called from outside, so LLVM may inline and
        // drop the rest. nounwind: nothing unwinds yet, and without it ARM targets would want the C++ unwinder linked in.
        var parameters = string.Join(", ", _function.Parameters.Select(Typed));
        var linkage = LlvmExports.Linkage(_function);
        _text.Append(CultureInfo.InvariantCulture,
            $"define {linkage}{_types.Of(_function.ReturnType)} @{_symbols[_function]}({parameters}) nounwind {{\n");

        // LLVM's entry block cannot be a jump target, and TIR's may be (a loop at the very start of a method),
        // so the slots get a block of their own. Each starts at zero, like the locals of IL's ".locals init".
        _text.Append(allocas).Append(":\n");
        foreach (var slot in _function.Slots)
        {
            Line($"{_slots[slot]} = alloca {_types.Of(slot.Type)}, align {_types.Alignment(slot.Type)}");
        }

        foreach (var slot in _function.Slots)
        {
            Line($"store {_types.Of(slot.Type)} {LlvmText.Zero(slot.Type)}, {Address(slot)}");
        }

        Line($"br {Label(_function.EntryBlock!)}");

        foreach (var block in _function.Blocks)
        {
            _text.Append('\n').Append(_labels[block]).Append(":\n");
            foreach (var instruction in block.Instructions)
            {
                Write(instruction);
            }
        }

        if (_nullReference is not null)
        {
            _text.Append('\n').Append(_nullReference).Append(":\n");
            Line($"call void @{LlvmHeap.NullReferenceSymbol}()");
            Line("unreachable");
        }

        _text.Append("}\n");
    }

    private void Write(TirInstruction instruction)
    {
        switch (instruction)
        {
            case TirConst constant:
                // LLVM writes constants inline as operands, so a constant needs no instruction of its own
                _values[constant] = LlvmText.Constant(constant);
                break;

            case TirBinary binary:
                // TODO: IL throws DivideByZeroException on x / 0 and x % 0, and OverflowException on
                // int.MinValue / -1 (and long.MinValue / -1, and the same with %); LLVM's sdiv, udiv, srem and
                // urem make all of these undefined behaviour. Add the checks once ChipSharp has exceptions.
                // Shift amounts need no check: C# compilers mask them below the operand width.
                var arithmetic = LlvmOpcodes.Binary(binary.Op, binary.Type.IsFloat);
                Define(binary, $"{arithmetic} {Typed(binary.Left)}, {Value(binary.Right)}");
                break;

            case TirCompare compare:
                var comparison = LlvmOpcodes.Compare(compare.Op, compare.Left.Type.IsFloat);
                Define(compare, $"{comparison} {Typed(compare.Left)}, {Value(compare.Right)}");
                break;

            case TirConvert convert:
                Convert(convert);
                break;

            case TirLoad load:
                Define(load, $"load {_types.Of(load.Type)}, {Address(load.Slot)}");
                break;

            case TirStore store:
                Line($"store {Typed(store.Value)}, {Address(store.Slot)}");
                break;

            case TirZero zero:
                // written inline like a constant: 0, false, null or zeroinitializer
                _values[zero] = LlvmText.Zero(zero.Type);
                break;

            case TirSizeOf size:
                _values[size] = SizeConstant(size.OfType);
                break;

            case TirSlotAddress address:
                // the alloca is the slot's address already
                _values[address] = _slots[address.Slot];
                break;

            case TirGlobalAddress address:
                _values[address] = "@" + _globals[address.Global];
                break;

            case TirPointerLoad load:
                // unaligned: align 1, which LLVM reads so that no chip faults (byte by byte on a Cortex-M0+)
                Define(load, $"load {Volatile(load.IsVolatile)}{_types.Of(load.Type)}, " +
                    $"{Typed(load.Address)}, align {(load.IsUnaligned ? 1 : _types.Alignment(load.Type))}");
                break;

            case TirPointerStore store:
                Line($"store {Volatile(store.IsVolatile)}{Typed(store.Value)}, {Typed(store.Address)}, " +
                    $"align {(store.IsUnaligned ? 1 : _types.Alignment(store.Value.Type))}");
                break;

            case TirFieldAddress field:
                // no inbounds: TIR does not promise that the address is that of a struct LLVM knows about
                Define(field, $"getelementptr {_types.Of(field.StructType)}, {Typed(field.Address)}, i32 0, " +
                    $"i32 {field.FieldIndex}");
                break;

            case TirElementAddress element:
                Define(element, $"getelementptr {_types.Of(element.ElementType)}, {Typed(element.Address)}, " +
                    $"{Typed(element.Index)}");
                break;

            case TirPtrToInt toInteger:
                Define(toInteger, $"ptrtoint {Typed(toInteger.Address)} to {_types.Of(toInteger.Type)}");
                break;

            case TirIntToPtr toPointer:
                Define(toPointer, $"inttoptr {Typed(toPointer.Value)} to ptr");
                break;

            case TirNewObject allocation:
                _declarations.Add(LlvmHeap.Declaration(LlvmHeap.NewObjectSymbol, _types));
                Define(allocation, $"call ptr @{LlvmHeap.NewObjectSymbol}(ptr {Descriptor(allocation.TypeDescriptor)})");
                break;

            case TirNewArray allocation:
                _declarations.Add(LlvmHeap.Declaration(LlvmHeap.NewArraySymbol, _types));
                Define(allocation, $"call ptr @{LlvmHeap.NewArraySymbol}(ptr {Descriptor(allocation.TypeDescriptor)}, " +
                    $"{Typed(allocation.Length)})");
                break;

            case TirNullCheck check:
                NullCheck(check);
                break;

            case TirArrayLength length:
                var lengthAddress = Temporary();
                Line($"{lengthAddress} = getelementptr i8, {Typed(length.Array)}, i32 {_types.Layout.ArrayLengthOffset}");
                Define(length, $"load i32, ptr {lengthAddress}, align 4");
                break;

            case TirArrayData data:
                Define(data, $"getelementptr i8, {Typed(data.Array)}, i32 {_types.Layout.ArrayDataOffset}");
                break;

            case TirArrayObject arrayObject:
                Define(arrayObject, $"getelementptr i8, {Typed(arrayObject.Data)}, i32 -{_types.Layout.ArrayDataOffset}");
                break;

            case TirTypeOf typeOf:
                Define(typeOf, $"load ptr, {Typed(typeOf.Reference)}, align {_types.Layout.PointerSize}");
                break;

            case TirTypeAddress address:
                _values[address] = Descriptor(address.TypeDescriptor);
                break;

            case TirCall call:
                // TODO: no signext/zeroext on arguments yet. Calls between ChipSharp functions do not need them,
                // and the runtime's functions take i32 and i64 only; a C function taking a bool, i8 or i16 would.
                var arguments = string.Join(", ", call.Arguments.Select(Typed));
                var callText = $"call {_types.Of(call.Type)} @{_symbols[call.Callee]}({arguments})";
                if (call.HasResult)
                {
                    Define(call, callText);
                }
                else
                {
                    Line(callText);
                }

                break;

            case TirCallIndirect call:
                // the address is a defined function's, from a method table: the same convention as a direct call
                var indirectText = $"call {_types.Of(call.Type)} {Value(call.Callee)}" +
                    $"({string.Join(", ", call.Arguments.Select(Typed))})";
                if (call.HasResult)
                {
                    Define(call, indirectText);
                }
                else
                {
                    Line(indirectText);
                }

                break;

            case TirReturn { Value: null }:
                Line("ret void");
                break;

            case TirReturn { Value: { } value }:
                Line($"ret {Typed(value)}");
                break;

            case TirJump jump:
                Line($"br {Label(jump.Target)}");
                break;

            case TirBranch branch:
                Line($"br {Typed(branch.Condition)}, {Label(branch.WhenTrue)}, {Label(branch.WhenFalse)}");
                break;

            default:
                throw new ChipSharpException(
                    $"Function '{_function.Name}': the llvm code generator does not support '{instruction}' yet.");
        }
    }

    /// <summary>
    /// trunc, sext or zext between integers (a bool is always zero-extended: it converts to 0 or 1), sitofp or
    /// uitofp, fpext or fptrunc. A float becomes an integer through llvm.fptosi.sat or llvm.fptoui.sat, which
    /// saturate as TIR (and .NET) do, where fptosi and fptoui would give poison. Converting between two types LLVM
    /// sees as the same (e.g. nint and i64 on a 64-bit target) writes nothing: the result is the value itself.
    /// </summary>
    private void Convert(TirConvert convert)
    {
        var (from, to) = (convert.Value.Type, convert.Type);
        if (_types.Of(from) == _types.Of(to))
        {
            _values[convert] = Value(convert.Value);
            return;
        }

        var (unsigned, widens) = (convert.IsUnsigned, _types.Bits(to) > _types.Bits(from));
        if (from.IsFloat && !to.IsFloat)
        {
            var intrinsic = $"llvm.fpto{(unsigned ? "u" : "s")}i.sat.{_types.Of(to)}.f{_types.Bits(from)}";
            _declarations.Add($"declare {_types.Of(to)} @{intrinsic}({_types.Of(from)})");
            Define(convert, $"call {_types.Of(to)} @{intrinsic}({Typed(convert.Value)})");
            return;
        }

        var opcode = (from.IsFloat, to.IsFloat) switch
        {
            (false, false) when from == TirType.Bool => "zext",
            (false, false) => widens ? unsigned ? "zext" : "sext" : "trunc",
            (false, true) when from == TirType.Bool => "uitofp",
            (false, true) => unsigned ? "uitofp" : "sitofp",
            _ => widens ? "fpext" : "fptrunc",
        };

        Define(convert, $"{opcode} {Typed(convert.Value)} to {_types.Of(to)}");
    }

    /// <summary>Writes "%tN = <paramref name="body"/>" and remembers %tN as the instruction's value.</summary>
    private void Define(TirInstruction instruction, string body)
    {
        var name = Temporary();
        _values[instruction] = name;
        Line($"{name} = {body}");
    }

    /// <summary>The next temporary's name, %tN, for a value that is not a TIR instruction's.</summary>
    private string Temporary() => "%" + Unique("t" + _nextTemporary++.ToString(CultureInfo.InvariantCulture));

    /// <summary>A type descriptor's address as an operand, e.g. <c>@type.Program_Node</c>.</summary>
    private string Descriptor(TirTypeDescriptor type) => "@" + _typeDescriptors[type];

    /// <summary>
    /// nullcheck: a branch to the function's <c>null_reference</c> block when the ref is null, else on into a block of its
    /// own, where the rest of the TIR block goes.
    /// </summary>
    private void NullCheck(TirNullCheck check)
    {
        _nullReference ??= Unique("null_reference");
        _declarations.Add(LlvmHeap.Declaration(LlvmHeap.NullReferenceSymbol, _types));
        var isNull = Temporary();
        var notNull = Unique("not_null");
        Line($"{isNull} = icmp eq {Typed(check.Reference)}, null");
        Line($"br i1 {isNull}, label %{_nullReference}, label %{notNull}");
        _text.Append('\n').Append(notNull).Append(":\n");
    }

    private static string Volatile(bool isVolatile) => isVolatile ? "volatile " : "";

    /// <summary>The size of a type on the target as an i32 constant; sizeof reads its 32 bits as unsigned.</summary>
    private string SizeConstant(TirType type)
    {
        var size = _types.Layout.SizeOf(type);
        return size <= uint.MaxValue
            ? LlvmText.Integer((int)(uint)size)
            : throw new ChipSharpException($"Function '{_function.Name}': {type} takes {size} bytes, more than " +
                "sizeof's 32 bits can count.");
    }

    private string Value(TirValue value) => _values[value];

    private string Typed(TirValue value) => $"{_types.Of(value.Type)} {Value(value)}";

    /// <summary>A slot as the address of a load or store, e.g. "ptr %s.sum, align 4".</summary>
    private string Address(TirSlot slot) => $"ptr {_slots[slot]}, align {_types.Alignment(slot.Type)}";

    private string Label(TirBlock block) => $"label %{_labels[block]}";

    private void Line(string line) => _text.Append("  ").Append(line).Append('\n');

    /// <summary><paramref name="wanted"/>, or with _2, _3, ... after it when that is taken.</summary>
    private string Unique(string wanted)
    {
        var name = wanted;
        for (var n = 2; !_taken.Add(name); n++)
        {
            name = wanted + "_" + n.ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }
}
