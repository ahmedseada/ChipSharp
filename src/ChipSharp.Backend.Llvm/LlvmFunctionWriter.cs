using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// Writes one defined function as an LLVM <c>define</c>. Its local names are stable: parameters are <c>%p.name</c>,
/// slots <c>%s.name</c>, temporaries <c>%t0</c>, <c>%t1</c>, ... in block order, and blocks keep their TIR names
/// (made LLVM-safe like symbols). The first block, <c>allocas</c>, holds the slots and jumps to the TIR entry block.
/// </summary>
internal sealed class LlvmFunctionWriter
{
    private readonly TirFunction _function;
    private readonly LlvmTypes _types;
    private readonly IReadOnlyDictionary<TirFunction, string> _symbols;
    private readonly IReadOnlyDictionary<TirGlobal, string> _globals;
    private readonly StringBuilder _text;

    /// <summary>Every local name used so far; labels and values share one namespace in an LLVM function.</summary>
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

    /// <summary>How each parameter and temporary is written as an operand, e.g. "%p.n", "%t3" or "42".</summary>
    private readonly Dictionary<TirValue, string> _values = [];

    private readonly Dictionary<TirSlot, string> _slots = [];
    private readonly Dictionary<TirBlock, string> _labels = [];
    private int _nextTemporary;

    public LlvmFunctionWriter(TirFunction function, LlvmTypes types, IReadOnlyDictionary<TirFunction, string> symbols,
        IReadOnlyDictionary<TirGlobal, string> globals, StringBuilder text)
    {
        _function = function;
        _types = types;
        _symbols = symbols;
        _globals = globals;
        _text = text;
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

        // internal: only chipsharp_entry is called from outside, so LLVM may inline and drop the rest.
        // nounwind: nothing unwinds yet, and without it ARM targets would want the C++ unwinder linked in.
        var parameters = string.Join(", ", _function.Parameters.Select(Typed));
        _text.Append(CultureInfo.InvariantCulture,
            $"define internal {_types.Of(_function.ReturnType)} @{_symbols[_function]}({parameters}) nounwind {{\n");

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
                Define(load, $"load {Volatile(load.IsVolatile)}{_types.Of(load.Type)}, " +
                    $"{Typed(load.Address)}, align {_types.Alignment(load.Type)}");
                break;

            case TirPointerStore store:
                Line($"store {Volatile(store.IsVolatile)}{Typed(store.Value)}, {Typed(store.Address)}, " +
                    $"align {_types.Alignment(store.Value.Type)}");
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
    /// uitofp, fptosi or fptoui, fpext or fptrunc. Converting between two types LLVM sees as the same
    /// (e.g. nint and i64 on a 64-bit target) writes nothing: the result is the value itself.
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
        var opcode = (from.IsFloat, to.IsFloat) switch
        {
            (false, false) when from == TirType.Bool => "zext",
            (false, false) => widens ? unsigned ? "zext" : "sext" : "trunc",
            (false, true) when from == TirType.Bool => "uitofp",
            (false, true) => unsigned ? "uitofp" : "sitofp",
            // TODO: a float that does not fit the integer type gives poison in LLVM; .NET saturates
            // (NaN gives 0). Switch to llvm.fptosi.sat / llvm.fptoui.sat when results must match .NET there.
            (true, false) => unsigned ? "fptoui" : "fptosi",
            (true, true) => widens ? "fpext" : "fptrunc",
        };

        Define(convert, $"{opcode} {Typed(convert.Value)} to {_types.Of(to)}");
    }

    /// <summary>Writes "%tN = <paramref name="body"/>" and remembers %tN as the instruction's value.</summary>
    private void Define(TirInstruction instruction, string body)
    {
        var name = "%" + Unique("t" + _nextTemporary++.ToString(CultureInfo.InvariantCulture));
        _values[instruction] = name;
        Line($"{name} = {body}");
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
