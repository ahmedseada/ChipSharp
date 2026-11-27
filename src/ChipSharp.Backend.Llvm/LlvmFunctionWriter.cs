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
/// <para>
/// On an AVR (see <see cref="LlvmFlash"/>) an address of flash data is written twice: as the program's value (bit 15
/// set) and as a <c>ptr addrspace(1)</c> in flash, which the loads that read it take; a load through an address that
/// may be either calls the module's reader for its type.
/// </para>
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

    /// <summary>
    /// How each address of flash data (on an AVR) is written as a flash address, a <c>ptr addrspace(1)</c>, e.g.
    /// "%t4" or "addrspacecast (ptr @Program_Digits to ptr addrspace(1))".
    /// </summary>
    private readonly Dictionary<TirValue, string> _flash = [];

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

    /// <summary>True when the function checks the stack where it starts (see <see cref="LlvmStackChecks"/>).</summary>
    public bool ChecksStack { get; init; }

    /// <summary>The line that clears the callee-saved registers before a call that collects (<see cref="LlvmCollectCalls"/>).</summary>
    public string? ForgetRegisters { get; init; }

    /// <summary>Where constant data is on the target: in a flash of its own on an AVR; null as on a target without.</summary>
    public LlvmFlash? Flash { get; init; }

    /// <summary>
    /// True when the function gets a byte on the stack, so that LLVM gives it a frame pointer: on an AVR, for a call that
    /// passes arguments on the stack (see <see cref="LlvmAvrCalls"/>).
    /// </summary>
    public bool HasFrame { get; init; }

    /// <summary>True on an AVR, whose C int has 16 bits: a call to the C library's memset is LLVM's llvm.memset there.</summary>
    public bool IsAvr { get; init; }

    /// <summary>True when LLVM must not inline the function: on an AVR, a large one with a loop (see <see cref="LlvmAvrCalls"/>).</summary>
    public bool StaysOutOfLine { get; init; }

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
            $"define {linkage}{_types.Of(_function.ReturnType)} @{_symbols[_function]}({parameters}) nounwind" +
            $"{(StaysOutOfLine ? " noinline" : "")}{Flash?.FunctionAttributes} {{\n");

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

        if (HasFrame)
        {
            foreach (var line in LlvmAvrCalls.FrameLines("%" + Unique("avr.frame")))
            {
                Line(line);
            }
        }

        string? stackOverflow = null;
        if (ChecksStack)
        {
            stackOverflow = Unique("stack_overflow");
            foreach (var line in LlvmStackChecks.Check("%" + Unique("stack.pointer"), "%" + Unique("stack.low"),
                stackOverflow, _labels[_function.EntryBlock!]))
            {
                Line(line);
            }

            foreach (var declaration in LlvmStackChecks.Declarations)
            {
                _declarations.Add(declaration);
            }
        }
        else
        {
            Line($"br {Label(_function.EntryBlock!)}");
        }

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

        if (stackOverflow is not null)
        {
            _text.Append('\n').Append(stackOverflow).Append(":\n");
            Line($"call void @{LlvmStackChecks.OverflowSymbol}()");
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
                // IL throws DivideByZeroException on x / 0 and x % 0, and OverflowException on int.MinValue / -1
                // (and long.MinValue / -1, and the same with %); LLVM's sdiv, udiv, srem and urem make all of these
                // undefined behaviour, so the translation checks the divisor first (ChipSharp.Frontend's
                // DivisionFunctions) and a division reaches here only when it is safe.
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
                var global = _globals[address.Global];
                if (Memory(address) == TirMemory.Constants)
                {
                    _flash[address] = LlvmFlash.FlashAddress(global);
                    _values[address] = Flash!.Tagged("@" + global);
                }
                else
                {
                    _values[address] = "@" + global;
                }

                break;

            case TirPointerLoad load:
                // unaligned: align 1, which LLVM reads so that no chip faults (byte by byte on a Cortex-M0+)
                Define(load, Load(load.Type, load.Address, Value(load.Address), FlashValue(load.Address),
                    load.IsUnaligned ? 1 : _types.Alignment(load.Type), load.IsVolatile));
                break;

            case TirPointerStore store:
                Line($"store {Volatile(store.IsVolatile)}{Typed(store.Value)}, {Typed(store.Address)}, " +
                    $"align {(store.IsUnaligned ? 1 : _types.Alignment(store.Value.Type))}");
                break;

            case TirFieldAddress field:
                // no inbounds: TIR does not promise that the address is that of a struct LLVM knows about
                Define(field, $"getelementptr {_types.Of(field.StructType)}, {Typed(field.Address)}, i32 0, " +
                    $"i32 {field.FieldIndex}");
                DefineInFlash(field, field.Address, address => $"getelementptr {_types.Of(field.StructType)}, " +
                    $"ptr addrspace(1) {address}, i32 0, i32 {field.FieldIndex}");
                break;

            case TirElementAddress element:
                Define(element, $"getelementptr {_types.Of(element.ElementType)}, {Typed(element.Address)}, " +
                    $"{Typed(element.Index)}");
                DefineInFlash(element, element.Address, address => $"getelementptr {_types.Of(element.ElementType)}, " +
                    $"ptr addrspace(1) {address}, {Typed(element.Index)}");
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
                var lengthOffset = _types.Layout.ArrayLengthOffset;
                var lengthAddress = Temporary();
                Line($"{lengthAddress} = getelementptr i8, {Typed(length.Array)}, i32 {lengthOffset}");
                string? lengthInFlash = null;
                if (Memory(length.Array) == TirMemory.Constants)
                {
                    lengthInFlash = Temporary();
                    Line($"{lengthInFlash} = getelementptr i8, ptr addrspace(1) {_flash[length.Array]}, i32 {lengthOffset}");
                }

                Define(length, Load(TirType.I32, length.Array, lengthAddress, lengthInFlash,
                    _types.Alignment(TirType.I32), isVolatile: false));
                break;

            case TirArrayData data:
                Define(data, $"getelementptr i8, {Typed(data.Array)}, i32 {_types.Layout.ArrayDataOffset}");
                DefineInFlash(data, data.Array,
                    address => $"getelementptr i8, ptr addrspace(1) {address}, i32 {_types.Layout.ArrayDataOffset}");
                break;

            case TirArrayObject arrayObject:
                Define(arrayObject, $"getelementptr i8, {Typed(arrayObject.Data)}, i32 -{_types.Layout.ArrayDataOffset}");
                DefineInFlash(arrayObject, arrayObject.Data,
                    address => $"getelementptr i8, ptr addrspace(1) {address}, i32 -{_types.Layout.ArrayDataOffset}");
                break;

            case TirTypeOf typeOf:
                // the header: the descriptor's address, as the program has it
                Define(typeOf, Load(TirType.Ptr, typeOf.Reference, Value(typeOf.Reference), FlashValue(typeOf.Reference),
                    _types.Alignment(TirType.Ptr), isVolatile: false));
                break;

            case TirTypeAddress address:
                _values[address] = Descriptor(address.TypeDescriptor);
                if (Memory(address) == TirMemory.Constants)
                {
                    _flash[address] = LlvmFlash.FlashAddress(_typeDescriptors[address.TypeDescriptor]);
                }

                break;

            case TirFunctionAddress address:
                // a function's symbol is its address (a ptr addrspace(1) on an AVR, the same bits as a ptr)
                _values[address] = _types.FunctionAddress(_symbols[address.Function]);
                break;

            case TirCall call when IsAvr && call.Callee.Kind == TirFunctionKind.External
                && _symbols[call.Callee] == "memset" && call.Arguments.Count == 3:
                // the C library's memset takes its value as a C int, which has 16 bits on an AVR, not TIR's i32: LLVM's
                // own llvm.memset calls it the AVR's way (or sets small blocks itself), and gives back nothing, so the
                // call's result is the destination, as memset's is
                var memsetValue = Temporary();
                Line($"{memsetValue} = trunc {Typed(call.Arguments[1])} to i8");
                var memset = $"llvm.memset.p0.{_types.Of(call.Arguments[2].Type)}";
                _declarations.Add($"declare void @{memset}(ptr, i8, {_types.Of(call.Arguments[2].Type)}, i1 immarg)");
                Line($"call void @{memset}({Typed(call.Arguments[0])}, i8 {memsetValue}, {Typed(call.Arguments[2])}, " +
                    "i1 false)");
                if (call.HasResult)
                {
                    _values[call] = Value(call.Arguments[0]);
                }

                break;

            case TirCall call:
                // TODO: no signext/zeroext on arguments yet. Calls between ChipSharp functions do not need them,
                // and the runtime's functions take i32 and i64 only; a C function taking a bool, i8 or i16 would.
                var arguments = string.Join(", ", call.Arguments.Select(Typed));
                var callText = $"call {_types.Of(call.Type)} @{_symbols[call.Callee]}({arguments})";
                if (ForgetRegisters is { } forget && call.Callee.Kind == TirFunctionKind.External
                    && LlvmCollectCalls.Collects(_symbols[call.Callee]))
                {
                    Line(forget);
                }

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
                // the address is a defined function's, from a method table: the same convention as a direct call; on an
                // AVR, the call goes through flash's address space, where the functions are
                var callee = Value(call.Callee);
                if (_types.ProgramAddressSpace != 0)
                {
                    var function = Temporary();
                    Line($"{function} = addrspacecast ptr {callee} to {_types.FunctionPointer}");
                    callee = function;
                }

                var indirectText = $"call {_types.Of(call.Type)} {callee}" +
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

    /// <summary>
    /// A type descriptor's address as an operand, e.g. <c>@type.Program_Node</c> (with bit 15 set on an AVR, where the
    /// descriptor is in flash).
    /// </summary>
    private string Descriptor(TirTypeDescriptor type) =>
        Flash is { } flash ? flash.Address(_typeDescriptors[type], flash.InFlash(type)) : "@" + _typeDescriptors[type];

    /// <summary>Where <paramref name="address"/> points, for a load through it: see <see cref="LlvmFlash.MemoryOf"/>.</summary>
    private TirMemory Memory(TirValue address) => Flash?.MemoryOf(address) ?? TirMemory.Variables;

    /// <summary>The flash address of <paramref name="address"/> when it points to flash data; null when it does not.</summary>
    private string? FlashValue(TirValue address) => Memory(address) == TirMemory.Constants ? _flash[address] : null;

    /// <summary>
    /// For an address of flash data, its flash address too: <paramref name="body"/> (a getelementptr on a ptr
    /// addrspace(1)) of the flash address of <paramref name="from"/>, the address it is made from.
    /// </summary>
    private void DefineInFlash(TirValue address, TirValue from, Func<string, string> body)
    {
        if (Memory(address) == TirMemory.Constants)
        {
            var name = Temporary();
            _flash[address] = name;
            Line($"{name} = {body(_flash[from])}");
        }
    }

    /// <summary>
    /// What reads a <paramref name="type"/> at <paramref name="address"/>: <c>load i32, ptr %t3, align 4</c>; on an AVR,
    /// from flash (<c>load i32, ptr addrspace(1) %t4, align 1</c>) for flash data, and through the module's reader for
    /// an address that may be either (<c>call i32 @chipsharp.read.i32(ptr %t3)</c>); as on any target for RAM.
    /// </summary>
    /// <param name="type">What to read.</param>
    /// <param name="address">The TIR value that says where it points (the address, or the object a field is in).</param>
    /// <param name="value">The address read, as the program has it.</param>
    /// <param name="inFlash">The address read in flash, when it points to flash data.</param>
    /// <param name="align">The alignment the load may count on.</param>
    /// <param name="isVolatile">True for a read that must happen exactly as written.</param>
    private string Load(TirType type, TirValue address, string value, string? inFlash, int align, bool isVolatile)
    {
        var typeText = _types.Of(type);
        return Memory(address) switch
        {
            TirMemory.Constants => $"load {Volatile(isVolatile)}{typeText}, ptr addrspace(1) {inFlash}, align {align}",
            TirMemory.Unknown when Flash!.MayBeFlash(address) => $"call {typeText} @{Flash.Read(type, isVolatile)}(ptr {value})",
            _ => $"load {Volatile(isVolatile)}{typeText}, ptr {value}, align {align}",
        };
    }

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
