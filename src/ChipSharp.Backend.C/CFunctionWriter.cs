using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>
/// Writes one defined function as C. Parameters are <c>p_name</c>, slots <c>s_name</c> (locals declared first, each
/// starting at zero), temporaries <c>tN</c>, numbered as <see cref="TirPrinter"/> numbers them (<c>t3</c> is the TIR's
/// <c>%3</c>), each declared where it is made, with its type, in the block (a C compound statement) that makes it.
/// Blocks follow one another in reverse postorder (see <see cref="ReversePostorder"/>: a loop starts with its test); a jump or branch to a block other than the next is a <c>goto</c> to its
/// label (its TIR name), one to the next falls through.
/// <para>
/// <b>Arithmetic</b> is C#'s whatever the compiler's int: integers are unsigned, so add, sub and mul wrap around, and
/// those of 8 and 16 bits (which C promotes to int, maybe 16 bits) multiply and shift left as <c>unsigned</c>, which
/// cannot overflow into undefined behaviour; a signed operation (div, rem, shr, the signed comparisons) casts its
/// operands to the signed type of the width, which the file checks is two's complement with an arithmetic shift
/// right. Shift amounts are masked below the width, as C# masks them (TIR's are below it already: this makes it C).
/// Divisions are C's, which round toward zero like C#'s; the TIR checks the divisor for 0 and -1 before (ChipSharp.Frontend's
/// DivisionFunctions), so C's undefined cases never run. A float made an integer goes through a helper that saturates
/// as .NET does (see <see cref="CNeeds.FloatToInteger"/>); C#'s % on floats is fmod.
/// </para>
/// <para>
/// <b>Memory</b>: an address the TIR shows the place of (a slot's or a global's, a field or an element of one, or of a
/// struct or a row of elements at an address) is written as that place, <c>s_p.X</c>, <c>Program_digits.e[t4]</c>,
/// <c>((struct Program_Node *)t2)-&gt;next</c>, and a load or store of its type reads or writes it; any other address
/// is a <c>uint8_t *</c>, and a load casts it to the type it reads: <c>*(uint32_t *)t5</c>. A load or store that may
/// not be aligned copies byte by byte, unless nothing on the target is aligned; a volatile one goes through a
/// <c>volatile</c> pointer.
/// </para>
/// </summary>
internal sealed class CFunctionWriter
{
    /// <summary>How tightly an expression holds together: an operand of a stronger operator needs parentheses.</summary>
    private const int Postfix = 0;

    private const int Unary = 1;
    private const int Binary = 2;

    private readonly TirFunction _function;
    private readonly CTypes _types;
    private readonly CSymbols _symbols;
    private readonly CNeeds _needs;

    /// <summary>Every name the function may not take: the file's, then its own as it takes them.</summary>
    private readonly HashSet<string> _names;

    private readonly HashSet<string> _labelNames = new(StringComparer.Ordinal);
    private readonly Dictionary<TirValue, Expression> _values = [];
    private readonly Dictionary<TirValue, Place> _places = [];
    private readonly Dictionary<TirValue, TirValue> _aliases = [];
    private readonly Dictionary<TirSlot, string> _slots = [];
    private readonly Dictionary<TirBlock, string> _labels = [];
    private readonly Dictionary<TirInstruction, int> _numbers = [];

    /// <summary>Where each instruction is in its block.</summary>
    private readonly Dictionary<TirInstruction, int> _positions = [];
    private readonly HashSet<TirInstruction> _live = [];

    /// <summary>The instructions whose value a written instruction uses.</summary>
    private readonly HashSet<TirInstruction> _used = [];

    /// <summary>How many written instructions use each instruction's value.</summary>
    private readonly Dictionary<TirInstruction, int> _uses = [];

    /// <summary>The blocks the function reaches (see <see cref="CLiveness.Blocks"/>), in the order they are written (<see cref="ReversePostorder"/>).</summary>
    private List<TirBlock> _blocks = [];
    private List<string> _lines = [];
    private bool _declares;

    public CFunctionWriter(TirFunction function, CTypes types, CSymbols symbols, CNeeds needs)
    {
        _function = function;
        _types = types;
        _symbols = symbols;
        _needs = needs;
        _names = new HashSet<string>(symbols.Taken, StringComparer.Ordinal);
    }

    /// <summary>True when the function is marked <see cref="CCodeGenerator.ReentrantMacro"/> (see <see cref="CCallGraph"/>).</summary>
    public bool IsReentrant { get; init; }

    /// <summary>The statement that clears the callee-saved registers before a call that collects; null for none.</summary>
    public string? ForgetRegisters { get; init; }

    /// <summary>
    /// The functions of the file that give their result through a pointer (see <see cref="ResultName"/>): on SDCC's STM8
    /// every one that returns a struct or an array, and one with a large frame that returns 3 or 4 bytes.
    /// </summary>
    public IReadOnlySet<TirFunction> PointerReturns { get; init; } = new HashSet<TirFunction>();

    /// <summary>
    /// The first parameter of a function that gives its result through a pointer: where the caller wants it. SDCC 4.6
    /// gets the address of a struct parameter wrong in a function that returns a struct (it forgets the hidden pointer
    /// it passes the result's place in), and its STM8 port cannot return 3 or 4 bytes from a function whose frame its
    /// 8-bit offsets do not reach; with a pointer of its own, the result is a store like any other.
    /// </summary>
    public const string ResultName = "cs_result";

    /// <summary>The limit the function checks the stack against when it starts (see <see cref="CStackChecks"/>); null for none.</summary>
    public CStackChecks.Limit? StackLimit { get; init; }

    /// <summary>
    /// Whether every function that returns a struct gives it through a pointer ("c.structReturn": "pointer"): how a call
    /// through an address calls one.
    /// </summary>
    public bool StructReturnPointer { get; init; }

    /// <summary>The stores a call wrote its result to itself, through the pointer: they are not written again.</summary>
    private readonly HashSet<TirInstruction> _done = [];

    /// <summary>The C names of a function's parameters: <c>p_</c> and the TIR name, made unique among the file's names.</summary>
    public static IReadOnlyList<string> ParameterNames(TirFunction function, ISet<string> fileNames)
    {
        var names = new HashSet<string>(fileNames, StringComparer.Ordinal);
        return [.. function.Parameters.Select(parameter => CTypes.Unique(names, "p_" + CText.Symbol(parameter.Name)))];
    }

    /// <summary>
    /// The head of a function: <c>static uint32_t Program_Square(uint32_t p_n)</c>, with <c>static</c> unless it is
    /// exported or external, and <see cref="CCodeGenerator.ReentrantMacro"/> after it when it is reentrant.
    /// </summary>
    public static string Signature(TirFunction function, CTypes types, string symbol, IReadOnlyList<string> names,
        bool reentrant, bool resultPointer = false)
    {
        List<string> list = [.. function.Parameters.Select((parameter, i) => types.Declare(parameter.Type, names[i]))];
        if (resultPointer)
        {
            list.Insert(0, CTypes.Declare(types.Of(function.ReturnType) + " *", ResultName));
        }

        var parameters = list.Count == 0 ? "void" : string.Join(", ", list);
        var linkage = function.Kind == TirFunctionKind.Defined && function.ExportName is null ? "static " : "";
        return linkage + CTypes.Declare(resultPointer ? "void" : types.Of(function.ReturnType), $"{symbol}({parameters})") +
            (reentrant ? " " + CCodeGenerator.ReentrantMacro : "");
    }

    public void Write(StringBuilder text)
    {
        var parameterNames = ParameterNames(_function, _symbols.Taken);
        for (var i = 0; i < _function.Parameters.Count; i++)
        {
            _names.Add(parameterNames[i]);
            _values[_function.Parameters[i]] = new Expression(parameterNames[i], Postfix);
        }

        foreach (var slot in _function.Slots)
        {
            _slots[slot] = CTypes.Unique(_names, "s_" + CText.Symbol(slot.Name));
        }

        foreach (var instruction in _function.Blocks.SelectMany(block => block.Instructions).Where(i => i.HasResult))
        {
            _numbers[instruction] = _numbers.Count;
        }

        foreach (var block in _function.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                _positions[block.Instructions[i]] = i;
            }
        }

        var liveness = CLiveness.Find(_function);
        _live.UnionWith(liveness.Live);
        _used.UnionWith(liveness.Used);
        foreach (var (instruction, count) in liveness.Uses)
        {
            _uses[instruction] = count;
        }

        _blocks = ReversePostorder(liveness.Blocks);

        var gotos = Gotos();
        foreach (var block in _blocks.Where(gotos.Contains))
        {
            _labels[block] = CTypes.Unique(_labelNames, Label(block.Name));
        }

        text.Append(Signature(_function, _types, _symbols.Functions[_function], parameterNames, IsReentrant,
            PointerReturns.Contains(_function))).Append("\n{\n");
        var usedSlots = UsedSlots();
        var storedFirst = StoredFirst();
        foreach (var slot in _function.Slots.Where(usedSlots.Contains))
        {
            // every slot starts at zero, like the locals of IL's ".locals init", but one the function stores to before
            // anything reads it or takes its address (a parameter's copy): SDCC 4.6 puts such a local where the
            // parameter it is a copy of is, and so its zero would be written over the parameter before the copy
            text.Append("    ").Append(_types.Declare(slot.Type, _slots[slot])).Append(storedFirst.Contains(slot)
                ? ";\n"
                : slot.Type.IsAggregate ? $" = {CTypes.ZeroInitializer(slot.Type)};\n" : $" = {Zero(slot.Type).Text};\n");
        }

        if (StackLimit is { } limit)
        {
            foreach (var line in CStackChecks.Check(_function, _types, limit, CTypes.Unique(_names, CStackChecks.FrameName)))
            {
                text.Append("    ").Append(line).Append('\n');
            }
        }

        var blocks = _blocks;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var next = i + 1 < blocks.Count ? blocks[i + 1] : null;
            _lines = [];
            _declares = false;
            foreach (var instruction in block.Instructions)
            {
                Write(instruction, next);
            }

            if (i > 0 || usedSlots.Count > 0 || StackLimit is not null)
            {
                text.Append('\n');
            }

            if (_labels.TryGetValue(block, out var label))
            {
                text.Append(label).Append(":\n");
            }
            else if (i > 0)
            {
                text.Append("    /* ").Append(CText.Comment(block.Name)).Append(" */\n");
            }

            if (_declares)
            {
                text.Append("    {\n");
                foreach (var line in _lines)
                {
                    text.Append("        ").Append(line).Append('\n');
                }

                text.Append("    }\n");
            }
            else if (_lines.Count == 0 && label is not null)
            {
                text.Append("    ;\n");
            }
            else
            {
                foreach (var line in _lines)
                {
                    text.Append("    ").Append(line).Append('\n');
                }
            }
        }

        text.Append("}\n");
    }

    private void Write(TirInstruction instruction, TirBlock? next)
    {
        // what nothing written uses is not computed: neither is what only it uses
        if (!_live.Contains(instruction))
        {
            return;
        }

        switch (instruction)
        {
            case TirConst:
                break; // written where it is used, as a constant

            case TirZero zero when !zero.Type.IsAggregate:
                _values[zero] = Zero(zero.Type);
                break;

            case TirZero zero:
                if (_live.Contains(zero))
                {
                    Declare(zero, CTypes.ZeroInitializer(zero.Type));
                }

                break;

            case TirSizeOf size:
                var bytes = _types.Layout.SizeOf(size.OfType);
                _values[size] = bytes <= uint.MaxValue
                    ? UnsignedLiteral(TirType.I32, (ulong)bytes, typed: false)
                    : throw new ChipSharpException($"Function '{_function.Name}': {size.OfType} takes {bytes} bytes, " +
                        "more than sizeof's 32 bits can count.");
                break;

            case TirSlotAddress address:
                _places[address] = new Place(_slots[address.Slot], address.Slot.Type);
                break;

            case TirGlobalAddress { Global: { ObjectType: not null } global } address:
                _values[address] = new Expression($"(uint8_t *)&{_symbols.Globals[global]}", Unary);
                break;

            case TirGlobalAddress address:
                _places[address] = new Place(_symbols.Globals[address.Global], address.Global.Type);
                break;

            case TirTypeAddress address:
                _values[address] = new Expression($"(uint8_t *)&{_symbols.Types[address.TypeDescriptor]}", Unary);
                break;

            case TirFunctionAddress address:
                _values[address] = new Expression($"(uint8_t *)&{_symbols.Functions[address.Function]}", Unary);
                break;

            case TirFieldAddress field:
                _places[field] = new Place(FieldPlace(field), field.Field!.Type);
                break;

            case TirElementAddress element:
                _places[element] = new Place(ElementPlace(element.Address, element.ElementType, element.Index),
                    element.ElementType);
                break;

            case TirArrayData data:
                _values[data] = new Expression(
                    $"{Operand(data.Array, Unary)} + {_types.Layout.ArrayDataOffset.ToString(CultureInfo.InvariantCulture)}",
                    Binary);
                break;

            case TirArrayObject arrayObject:
                _values[arrayObject] = new Expression(
                    $"{Operand(arrayObject.Data, Unary)} - {_types.Layout.ArrayDataOffset.ToString(CultureInfo.InvariantCulture)}",
                    Binary);
                break;

            case TirConvert convert when _types.Of(convert.Value.Type) == _types.Of(convert.Type):
                // nint and the integer of the pointer's width, or a type to itself: the same C type, nothing to do
                _aliases[convert] = convert.Value;
                break;

            case TirBinary binary:
                Define(binary, BinaryText(binary));
                break;

            case TirCompare compare:
                Define(compare, CompareText(compare));
                break;

            case TirConvert convert:
                Define(convert, ConvertText(convert));
                break;

            case TirLoad load when Forwarded(load):
                _values[load] = new Expression(_slots[load.Slot], Postfix);
                break;

            case TirLoad load:
                Define(load, _slots[load.Slot]);
                break;

            case TirStore store when !_live.Contains(store) || _done.Contains(store):
                break; // to a slot nothing reads, or which the call before wrote its result to

            case TirStore store:
                Line($"{_slots[store.Slot]} = {Value(store.Value).Text};");
                break;

            case TirPointerLoad load when !load.IsVolatile && !load.IsUnaligned && Forwarded(load):
                _values[load] = new Expression(Access(load.Address, load.Type, isVolatile: false), Unary);
                break;

            case TirPointerLoad load when load.IsUnaligned && !load.IsVolatile && _types.Layout.AlignmentOf(load.Type) > 1:
                if (_live.Contains(load))
                {
                    var name = Temporary(load);
                    Line(_types.Declare(load.Type, name) + ";", declares: true);
                    Line($"{_needs.Copy()}((uint8_t *)&{name}, {Value(load.Address).Text}, " +
                        $"{_types.Layout.SizeOf(load.Type).ToString(CultureInfo.InvariantCulture)});");
                }

                break;

            case TirPointerLoad load when _live.Contains(load):
                Define(load, Access(load.Address, load.Type, load.IsVolatile));
                break;

            case TirPointerLoad load:
                if (load.IsVolatile)
                {
                    Line($"(void){Access(load.Address, load.Type, isVolatile: true)};");
                }

                break;

            case TirPointerStore store when store.IsUnaligned && !store.IsVolatile
                && _types.Layout.AlignmentOf(store.Value.Type) > 1:
                var copy = CTypes.Unique(_names, "v");
                Line($"{_types.Declare(store.Value.Type, copy)} = {Value(store.Value).Text};", declares: true);
                Line($"{_needs.Copy()}({Value(store.Address).Text}, (uint8_t *)&{copy}, " +
                    $"{_types.Layout.SizeOf(store.Value.Type).ToString(CultureInfo.InvariantCulture)});");
                break;

            case TirPointerStore store:
                Line($"{Access(store.Address, store.Value.Type, store.IsVolatile)} = {Value(store.Value).Text};");
                break;

            case TirPtrToInt toInteger:
                var integer = _types.Bits(toInteger.Type) == _types.PointerBits
                    ? $"({_types.Of(toInteger.Type)}){Operand(toInteger.Address, Unary)}"
                    : $"({_types.Of(toInteger.Type)})({_types.PointerInteger}){Operand(toInteger.Address, Unary)}";
                Define(toInteger, integer);
                break;

            case TirIntToPtr toPointer:
                var widened = _types.Bits(toPointer.Value.Type) == _types.PointerBits
                    ? Operand(toPointer.Value, Unary, typed: true)
                    : $"({_types.PointerInteger}){Operand(toPointer.Value, Unary, typed: true)}";
                Define(toPointer, $"(uint8_t *){widened}");
                break;

            case TirNewObject allocation:
                Call(allocation, $"{_needs.Runtime(CHeap.NewObjectSymbol)}(" +
                    $"(uint8_t *)&{_symbols.Types[allocation.TypeDescriptor]})");
                break;

            case TirNewArray allocation:
                Call(allocation, $"{_needs.Runtime(CHeap.NewArraySymbol)}(" +
                    $"(uint8_t *)&{_symbols.Types[allocation.TypeDescriptor]}, " +
                    $"(int32_t){Operand(allocation.Length, Unary)})");
                break;

            case TirNullCheck check:
                Line($"if ({Operand(check.Reference, Binary - 1)} == 0)");
                Line($"    {_needs.Runtime(CHeap.NullReferenceSymbol)}();");
                break;

            case TirArrayLength length:
                Define(length, $"*(uint32_t *)({Operand(length.Array, Unary)} + " +
                    $"{_types.Layout.ArrayLengthOffset.ToString(CultureInfo.InvariantCulture)})");
                break;

            case TirTypeOf typeOf:
                Define(typeOf, $"*(uint8_t **){Operand(typeOf.Reference, Unary)}");
                break;

            case TirCall call when call.Callee.Kind == TirFunctionKind.External
                && CNeeds.IsLibrary(_symbols.Functions[call.Callee]):
                LibraryCall(call);
                break;

            case TirCall call:
                if (ForgetRegisters is { } forget && call.Callee.Kind == TirFunctionKind.External
                    && CCodeGenerator.Collects(_symbols.Functions[call.Callee]))
                {
                    Line(forget);
                }

                if (PointerReturns.Contains(call.Callee))
                {
                    CallWithResultPointer(call, _symbols.Functions[call.Callee], call.Arguments);
                    break;
                }

                Call(call, $"{_symbols.Functions[call.Callee]}({Arguments(call.Arguments)})");
                break;

            case TirCallIndirect call when call.Type.IsAggregate && StructReturnPointer:
                var pointerType = _needs.FunctionType("void",
                    [_types.Of(call.Type) + " *", .. call.Arguments.Select(argument => _types.Of(argument.Type))]);
                CallWithResultPointer(call, $"(({pointerType}){Operand(call.Callee, Unary)})", call.Arguments);
                break;

            case TirCallIndirect call:
                var functionType = _needs.FunctionType(_types.Of(call.Type),
                    [.. call.Arguments.Select(argument => _types.Of(argument.Type))]);
                Call(call, $"(({functionType}){Operand(call.Callee, Unary)})({Arguments(call.Arguments)})");
                break;

            case TirReturn { Value: null }:
                Line("return;");
                break;

            case TirReturn { Value: { } value } when PointerReturns.Contains(_function):
                if (!_done.Contains(Resolve(value)))
                {
                    Line($"*{ResultName} = {Value(value).Text};");
                }

                Line("return;");
                break;

            case TirReturn { Value: { } value }:
                Line($"return {Value(value).Text};");
                break;

            case TirJump jump:
                if (jump.Target != next)
                {
                    Line($"goto {_labels[jump.Target]};");
                }

                break;

            case TirBranch branch:
                Branch(branch, next);
                break;

            default:
                throw new ChipSharpException(
                    $"Function '{_function.Name}': the c code generator does not support '{instruction}' yet.");
        }
    }

    /// <summary>
    /// <c>if (t3) goto then;</c>, or the opposite test when the true block is the next one, or both gotos when neither
    /// is; nothing when both are the next.
    /// </summary>
    private void Branch(TirBranch branch, TirBlock? next)
    {
        if ((CLiveness.Taken(branch) ?? (branch.WhenTrue == branch.WhenFalse ? branch.WhenTrue : null)) is { } only)
        {
            if (only != next)
            {
                Line($"goto {_labels[only]};");
            }

            return;
        }

        var condition = Value(branch.Condition);

        if (branch.WhenTrue == next)
        {
            Line($"if (!{Paren(condition, Unary)}) goto {_labels[branch.WhenFalse]};");
            return;
        }

        Line($"if ({condition.Text}) goto {_labels[branch.WhenTrue]};");
        if (branch.WhenFalse != next)
        {
            Line($"goto {_labels[branch.WhenFalse]};");
        }
    }

    /// <summary>
    /// The blocks the function reaches, in reverse postorder from its entry: each block before the blocks it goes to,
    /// but along a loop's way back. C# (and so IL and TIR) puts a loop's test after its body, entered by a jump down to
    /// it (<c>goto test; body: ...; test: if (i &lt; n) goto body;</c>), and SDCC 4.6 gets the lifetimes of the
    /// variables in such a loop wrong on the STM8: it gives a variable the loop still needs (the counter of the loop
    /// around it, a local read after the loop) the stack place of one the loop sets, and the program counts forever or
    /// reads another's value. In this order a loop starts with its test (<c>test: if (!(i &lt; n)) goto out; body:
    /// ...; goto test;</c>), the shape of a C <c>for</c>, which SDCC compiles right. A branch's false way is taken
    /// first, so the true way comes right after it, as in TIR.
    /// </summary>
    private List<TirBlock> ReversePostorder(IReadOnlySet<TirBlock> reached)
    {
        var order = new List<TirBlock>();
        if (_function.EntryBlock is not { } entry || !reached.Contains(entry))
        {
            return order;
        }

        var visited = new HashSet<TirBlock> { entry };
        var stack = new Stack<(TirBlock Block, int Next)>();
        stack.Push((entry, 0));
        while (stack.TryPop(out var top))
        {
            var successors = Successors(top.Block);
            if (top.Next < successors.Count)
            {
                stack.Push((top.Block, top.Next + 1));
                var successor = successors[top.Next];
                if (reached.Contains(successor) && visited.Add(successor))
                {
                    stack.Push((successor, 0));
                }

                continue;
            }

            order.Add(top.Block);
        }

        order.Reverse();
        return order;
    }

    /// <summary>Where a block goes: a jump's target, a branch's false way then its true way (one only on a constant).</summary>
    private static IReadOnlyList<TirBlock> Successors(TirBlock block) => block.Terminator switch
    {
        TirBranch branch when (CLiveness.Taken(branch)
            ?? (branch.WhenTrue == branch.WhenFalse ? branch.WhenTrue : null)) is { } only => [only],
        TirBranch branch => [branch.WhenFalse, branch.WhenTrue],
        TirJump jump => [jump.Target],
        _ => [],
    };

    /// <summary>The blocks some jump or branch goes to with a goto: every target that is not the block right after.</summary>
    private HashSet<TirBlock> Gotos()
    {
        var targets = new HashSet<TirBlock>();
        var blocks = _blocks;
        for (var i = 0; i < blocks.Count; i++)
        {
            var next = i + 1 < blocks.Count ? blocks[i + 1] : null;
            switch (blocks[i].Terminator)
            {
                case TirJump jump when jump.Target != next:
                    targets.Add(jump.Target);
                    break;

                case TirBranch branch when (CLiveness.Taken(branch)
                    ?? (branch.WhenTrue == branch.WhenFalse ? branch.WhenTrue : null)) is { } only:
                    if (only != next)
                    {
                        targets.Add(only);
                    }

                    break;

                case TirBranch branch:
                    if (branch.WhenTrue != next)
                    {
                        targets.Add(branch.WhenTrue);
                    }

                    if (branch.WhenFalse != next)
                    {
                        targets.Add(branch.WhenFalse);
                    }

                    break;
            }
        }

        return targets;
    }

    /// <summary>
    /// True for a struct or array value (a slot's, memory's, a call's) that only the instruction right after it uses, to
    /// store or return, or a slot's that a call later in the block takes as an argument, with nothing in between that
    /// writes memory or calls: it is written there, <c>s_temp = s_p;</c>, <c>Program_Sum(s_span)</c>,
    /// rather than copied into a temporary first, which SDCC keeps in RAM of its own (scarce on a Padauk; on an STM8 a
    /// frame its 8-bit offsets do not reach). Nothing runs between them that could change what it reads.
    /// </summary>
    private bool Forwarded(TirInstruction instruction)
    {
        if (!instruction.Type.IsAggregate || _uses.GetValueOrDefault(instruction) != 1)
        {
            return false;
        }

        var instructions = instruction.Block!.Instructions;
        var at = _positions[instruction];
        if (at + 1 < instructions.Count && instructions[at + 1] switch
            {
                TirStore store => store.Value == instruction,
                // memory to memory: C's struct assignment wants the two not to overlap but exactly, which a temporary keeps
                TirPointerStore store => store.Value == instruction && !store.IsUnaligned && instruction is not TirPointerLoad,
                // return f(...) of a struct: the call writes it where this function's caller wants it (see ResultName)
                TirReturn ret => ret.Value == instruction && (instruction is not (TirCall or TirCallIndirect)
                    || PointerReturns.Contains(_function)),
                _ => false,
            })
        {
            return true;
        }

        // a slot's alone: SDCC 4.6 passes *(struct S *)p as an argument wrong on the STM8 (the struct of other bytes)
        if (instruction is not TirLoad)
        {
            return false;
        }

        for (var i = at + 1; i < instructions.Count; i++)
        {
            var next = instructions[i];
            if (next.Operands.Contains(instruction))
            {
                return next is TirCall or TirCallIndirect;
            }

            if (!Reads(next))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>True for an instruction that only computes a value: it writes no memory and calls nothing.</summary>
    private static bool Reads(TirInstruction instruction) => instruction is TirConst or TirZero or TirSizeOf
        or TirSlotAddress or TirGlobalAddress or TirTypeAddress or TirFunctionAddress or TirFieldAddress
        or TirElementAddress or TirArrayData or TirArrayObject or TirConvert or TirBinary or TirCompare or TirLoad
        or TirPointerLoad { IsVolatile: false } or TirPtrToInt or TirIntToPtr or TirArrayLength or TirTypeOf;

    /// <summary>
    /// The slots the function's first block stores to (in code that is written) before it reads them or takes their
    /// address, and before a call that could read one whose address it took: their zero is never read.
    /// </summary>
    private HashSet<TirSlot> StoredFirst()
    {
        var stored = new HashSet<TirSlot>();
        var seen = new HashSet<TirSlot>();
        foreach (var instruction in _blocks.Count > 0 ? _blocks[0].Instructions : [])
        {
            if (!_live.Contains(instruction))
            {
                continue;
            }

            var slot = instruction switch
            {
                TirStore store => store.Slot,
                TirLoad load => load.Slot,
                TirSlotAddress address => address.Slot,
                _ => null,
            };
            if (slot is not null && seen.Add(slot) && instruction is TirStore)
            {
                stored.Add(slot);
            }

            if (instruction is TirCall or TirCallIndirect && seen.Count > stored.Count)
            {
                break;
            }
        }

        return stored;
    }

    /// <summary>The slots a written instruction loads, stores or takes the address of.</summary>
    private HashSet<TirSlot> UsedSlots()
    {
        var used = new HashSet<TirSlot>();
        foreach (var instruction in _function.Blocks.SelectMany(block => block.Instructions).Where(_live.Contains))
        {
            switch (instruction)
            {
                case TirLoad load:
                    used.Add(load.Slot);
                    break;
                case TirStore store:
                    used.Add(store.Slot);
                    break;
                case TirSlotAddress address:
                    used.Add(address.Slot);
                    break;
            }
        }

        return used;
    }

    // ---- arithmetic ----

    private string BinaryText(TirBinary binary)
    {
        var (left, right) = (binary.Left, binary.Right);
        var typed = left is TirConst && right is TirConst;
        if (binary.Type.IsFloat)
        {
            var (l, r) = (Operand(left, Binary - 1, typed), Operand(right, Binary - 1, typed));
            return binary.Op switch
            {
                TirBinaryOp.Add => $"{l} + {r}",
                TirBinaryOp.Sub => $"{l} - {r}",
                TirBinaryOp.Mul => $"{l} * {r}",
                TirBinaryOp.Div => $"{l} / {r}",
                TirBinaryOp.Rem => $"{_needs.Library(binary.Type == TirType.F32 ? "fmodf" : "fmod")}(" +
                    $"{Value(left).Text}, {Value(right).Text})",
                _ => throw Unsupported($"{binary.Op} on floats"),
            };
        }

        var bits = _types.Bits(binary.Type);
        var promoted = bits < 32; // C makes these an int, which may have 16 bits: unsigned keeps it defined
        string L() => Operand(left, Binary - 1, typed);
        string R() => Operand(right, Binary - 1, typed);
        string Wide(TirValue value) => promoted ? $"(unsigned){Operand(value, Unary)}" : Operand(value, Binary - 1, typed);
        string Amount() => right is TirConst amount
            ? CText.Integer((long)(CText.Unsigned(amount, bits) & (ulong)(bits - 1)))
            : $"({Operand(right, Binary - 1)} & {(bits - 1).ToString(CultureInfo.InvariantCulture)})";
        var text = binary.Op switch
        {
            TirBinaryOp.Add => $"{L()} + {R()}",
            TirBinaryOp.Sub => $"{L()} - {R()}",
            TirBinaryOp.Mul => $"{Wide(left)} * {Wide(right)}",
            TirBinaryOp.Div => $"{Signed(left)} / {Signed(right)}",
            TirBinaryOp.DivUnsigned => $"{L()} / {R()}",
            TirBinaryOp.Rem => $"{Signed(left)} % {Signed(right)}",
            TirBinaryOp.RemUnsigned => $"{L()} % {R()}",
            TirBinaryOp.And or TirBinaryOp.Or or TirBinaryOp.Xor when bits == 64 =>
                $"{_needs.Bitwise64(binary.Op switch { TirBinaryOp.And => '&', TirBinaryOp.Or => '|', _ => '^' })}(" +
                $"{Value(left, typed).Text}, {Value(right, typed).Text})",
            TirBinaryOp.And => $"{L()} & {R()}",
            TirBinaryOp.Or => $"{L()} | {R()}",
            TirBinaryOp.Xor => $"{L()} ^ {R()}",
            TirBinaryOp.Shl => $"{(promoted ? Wide(left) : Operand(left, Binary - 1, typed: true))} << {Amount()}",
            TirBinaryOp.Shr => $"{Signed(left, typed: true)} >> {Amount()}",
            TirBinaryOp.ShrUnsigned => $"{Operand(left, Binary - 1, typed: true)} >> {Amount()}",
            _ => throw Unsupported(binary.Op.ToString()),
        };

        // an int (maybe of 16 bits) whose value may not fit, or a signed result: the cast says its low bits are wanted
        // (SDCC warns of an implicit conversion of a constant that does not fit otherwise)
        var signed = binary.Op is TirBinaryOp.Div or TirBinaryOp.Rem or TirBinaryOp.Shr;
        return promoted || signed ? $"({_types.Of(binary.Type)})({text})" : text;
    }

    private string CompareText(TirCompare compare)
    {
        var (left, right) = (compare.Left, compare.Right);
        var typed = left is TirConst && right is TirConst;
        string L() => Operand(left, Binary - 1, typed);
        string R() => Operand(right, Binary - 1, typed);
        if (left.Type.IsFloat)
        {
            // C's comparisons of floats are ordered (false when either side is NaN), but for !=; the unsigned variants
            // are unordered (also true when either side is NaN), the opposite of the opposite comparison
            return compare.Op switch
            {
                TirCompareOp.Eq => $"{L()} == {R()}",
                TirCompareOp.Ne => $"{L()} != {R()}",
                TirCompareOp.Lt => $"{L()} < {R()}",
                TirCompareOp.Le => $"{L()} <= {R()}",
                TirCompareOp.Gt => $"{L()} > {R()}",
                TirCompareOp.Ge => $"{L()} >= {R()}",
                TirCompareOp.LtUnsigned => $"!({L()} >= {R()})",
                TirCompareOp.LeUnsigned => $"!({L()} > {R()})",
                TirCompareOp.GtUnsigned => $"!({L()} <= {R()})",
                TirCompareOp.GeUnsigned => $"!({L()} < {R()})",
                _ => throw Unsupported($"{compare.Op} on floats"),
            };
        }

        if (left.Type.IsAddress && compare.Op is not (TirCompareOp.Eq or TirCompareOp.Ne))
        {
            // C orders only addresses within one object: compare them as numbers
            var (l, r) = ($"({_types.PointerInteger}){Operand(left, Unary)}", $"({_types.PointerInteger}){Operand(right, Unary)}");
            return compare.Op switch
            {
                TirCompareOp.LtUnsigned => $"{l} < {r}",
                TirCompareOp.LeUnsigned => $"{l} <= {r}",
                TirCompareOp.GtUnsigned => $"{l} > {r}",
                TirCompareOp.GeUnsigned => $"{l} >= {r}",
                _ => throw Unsupported($"{compare.Op} on addresses"),
            };
        }

        return compare.Op switch
        {
            TirCompareOp.Eq => $"{L()} == {R()}",
            TirCompareOp.Ne => $"{L()} != {R()}",
            TirCompareOp.Lt => $"{Signed(left)} < {Signed(right)}",
            TirCompareOp.Le => $"{Signed(left)} <= {Signed(right)}",
            TirCompareOp.Gt => $"{Signed(left)} > {Signed(right)}",
            TirCompareOp.Ge => $"{Signed(left)} >= {Signed(right)}",
            TirCompareOp.LtUnsigned => $"{L()} < {R()}",
            TirCompareOp.LeUnsigned => $"{L()} <= {R()}",
            TirCompareOp.GtUnsigned => $"{L()} > {R()}",
            TirCompareOp.GeUnsigned => $"{L()} >= {R()}",
            _ => throw Unsupported(compare.Op.ToString()),
        };
    }

    /// <summary>
    /// A conversion between two C types: a narrower integer keeps the low bits, a wider one is sign-extended (through
    /// the signed type of the narrower width) or zero-extended, a bool converts to 0 or 1; integers and floats convert as C
    /// converts them (rounded to nearest), but a float to an integer, through the helper that saturates as .NET does.
    /// </summary>
    private string ConvertText(TirConvert convert)
    {
        var (from, to, value) = (convert.Value.Type, convert.Type, convert.Value);
        var target = _types.Of(to);
        if (from.IsFloat && !to.IsFloat)
        {
            return $"{_needs.FloatToInteger(from, to, convert.IsUnsigned)}({Value(value).Text})";
        }

        if (from.IsFloat)
        {
            return $"({target}){Operand(value, Unary)}";
        }

        var signed = !convert.IsUnsigned && from != TirType.Bool;
        if (to.IsFloat)
        {
            return signed ? $"({target}){Signed(value)}" : $"({target}){Operand(value, Unary, typed: true)}";
        }

        var widens = _types.Bits(to) > _types.Bits(from);
        return widens && signed ? $"({target}){Signed(value)}" : $"({target}){Operand(value, Unary, typed: true)}";
    }

    /// <summary>
    /// An integer operand as its signed type: <c>(int32_t)t3</c>, or a constant's signed value, <c>-5</c> (an int, which
    /// C converts to the other operand's type), cast too when <paramref name="typed"/>: the left operand of a shift,
    /// whose type is the result's, <c>(int64_t)-7 &gt;&gt; 62</c>.
    /// </summary>
    private string Signed(TirValue value, bool typed = false)
    {
        if (Resolve(value) is TirConst constant)
        {
            var number = constant.IntegerValue;
            return !typed && number is >= -32767 and <= 32767
                ? CText.Integer(number)
                : $"({_types.Signed(value.Type)}){CText.SignedInteger(number)}";
        }

        return $"({_types.Signed(value.Type)}){Operand(value, Unary)}";
    }

    // ---- memory ----

    /// <summary>The field of a struct at an address: <c>s_p.X</c> when the address is a struct's place, else <c>((struct S *)t2)-&gt;X</c>.</summary>
    private string FieldPlace(TirFieldAddress field)
    {
        var member = _types.Field(field.StructType, field.FieldIndex);
        var structType = _types.Of(field.StructType);
        if (_places.TryGetValue(Resolve(field.Address), out var place))
        {
            return _types.Of(place.Type) == structType
                ? $"{place.LValue}.{member}"
                : $"(({structType} *)&{place.LValue})->{member}";
        }

        return $"(({structType} *){Operand(field.Address, Unary)})->{member}";
    }

    /// <summary>
    /// Element <paramref name="index"/> of a row of <paramref name="elementType"/> at an address: <c>s_a.e[t4]</c> in an
    /// array's place, <c>(&amp;s_x)[t4]</c> from an element's place, else <c>((uint32_t *)t2)[t4]</c>.
    /// </summary>
    private string ElementPlace(TirValue address, TirType elementType, TirValue index)
    {
        var element = _types.Of(elementType);
        var at = Index(index);
        if (_places.TryGetValue(Resolve(address), out var place))
        {
            if (place.Type is TirArrayType array && _types.Of(array.Element) == element)
            {
                return $"{place.LValue}.{CTypes.ElementsMember}[{at}]";
            }

            if (_types.Of(place.Type) == element)
            {
                return at == "0" ? place.LValue : $"(&{place.LValue})[{at}]";
            }

            return $"(({PointerType(element, isVolatile: false)})&{place.LValue})[{at}]";
        }

        // an address is a uint8_t * already: a byte needs no cast
        return element == "uint8_t"
            ? $"{Operand(address, Postfix)}[{at}]"
            : $"(({PointerType(element, isVolatile: false)}){Operand(address, Unary)})[{at}]";
    }

    /// <summary>
    /// An index, read as signed, of at most the pointer's width (C takes any integer as an index; a wider one could not
    /// address more): <c>(int32_t)t4</c>, or a constant's value.
    /// </summary>
    private string Index(TirValue index)
    {
        if (Resolve(index) is TirConst constant)
        {
            return CText.SignedInteger(constant.IntegerValue);
        }

        var bits = Math.Min(_types.Bits(index.Type), _types.PointerBits);
        return $"(int{bits.ToString(CultureInfo.InvariantCulture)}_t){Operand(index, Unary)}";
    }

    /// <summary>
    /// The memory a load or store of <paramref name="type"/> at <paramref name="address"/> reads or writes, as an lvalue:
    /// the place itself when it has the type, else through a pointer of the type, <c>*(uint32_t *)t5</c>; volatile
    /// through a volatile one.
    /// </summary>
    private string Access(TirValue address, TirType type, bool isVolatile)
    {
        var cType = _types.Of(type);
        if (_places.TryGetValue(Resolve(address), out var place))
        {
            return !isVolatile && _types.Of(place.Type) == cType
                ? place.LValue
                : $"*({PointerType(cType, isVolatile)})&{place.LValue}";
        }

        return cType == "uint8_t" && !isVolatile
            ? $"*{Operand(address, Unary)}"
            : $"*({PointerType(cType, isVolatile)}){Operand(address, Unary)}";
    }

    /// <summary>A pointer to <paramref name="cType"/>: <c>uint32_t *</c>, <c>volatile uint32_t *</c>, <c>uint8_t *volatile *</c>.</summary>
    private static string PointerType(string cType, bool isVolatile) => cType.EndsWith('*')
        ? cType + (isVolatile ? "volatile *" : "*")
        : (isVolatile ? "volatile " : "") + cType + " *";

    // ---- calls ----

    /// <summary>A call to the C library's memset, memcpy or memmove, with the types C declares them with (int, size_t).</summary>
    private void LibraryCall(TirCall call)
    {
        var symbol = _symbols.Functions[call.Callee];
        var arguments = call.Arguments;
        if (arguments.Count != 3 || !arguments[0].Type.IsAddress || !arguments[2].Type.IsInteger
            || (symbol == "memset" ? !arguments[1].Type.IsInteger : !arguments[1].Type.IsAddress))
        {
            throw new ChipSharpException($"Function '{_function.Name}' calls {symbol} with ({string.Join(", ",
                arguments.Select(argument => argument.Type))}); the C library's takes an address, " +
                $"{(symbol == "memset" ? "a byte" : "an address")} and a size.");
        }

        var middle = symbol == "memset" ? $"(int)(uint8_t){Operand(arguments[1], Unary)}" : Value(arguments[1]).Text;
        Call(call, $"{_needs.Library(symbol)}({Value(arguments[0]).Text}, {middle}, (size_t){Operand(arguments[2], Unary)})");
    }

    private string Arguments(IReadOnlyList<TirValue> arguments) => string.Join(", ", arguments.Select(a => Value(a).Text));

    /// <summary>
    /// A call of a function that gives its result through a pointer (<see cref="ResultName"/>): to the slot the next
    /// instruction stores it in, or to this function's own result when it returns it, else to a temporary.
    /// </summary>
    private void CallWithResultPointer(TirInstruction call, string callee, IReadOnlyList<TirValue> arguments)
    {
        var next = call.Block!.Instructions[_positions[call] + 1];
        string destination;
        if (Forwarded(call) && next is TirStore store)
        {
            destination = $"&{_slots[store.Slot]}";
            _done.Add(store);
        }
        else if (Forwarded(call) && next is TirReturn && PointerReturns.Contains(_function))
        {
            destination = ResultName;
            _done.Add(call);
        }
        else
        {
            var name = Temporary(call);
            Line(_types.Declare(call.Type, name) + ";", declares: true);
            destination = "&" + name;
        }

        var list = arguments.Count == 0 ? destination : destination + ", " + Arguments(arguments);
        Line($"{callee}({list});");
    }

    /// <summary>A call: its result into a temporary when something uses it, else the call alone.</summary>
    private void Call(TirInstruction call, string text)
    {
        if (call.HasResult && Forwarded(call))
        {
            _values[call] = new Expression(text, Postfix);
        }
        else if (call.HasResult && _used.Contains(call))
        {
            Define(call, text);
        }
        else
        {
            Line(text + ";");
        }
    }

    // ---- values ----

    /// <summary>Declares the temporary of <paramref name="instruction"/>, when its value is used, as <paramref name="value"/>.</summary>
    private void Define(TirInstruction instruction, string value)
    {
        if (!_live.Contains(instruction))
        {
            return;
        }

        var name = Temporary(instruction);
        Line($"{_types.Declare(instruction.Type, name)} = {value};", declares: true);
        _values[instruction] = new Expression(name, Postfix);
    }

    /// <summary>The name of an instruction's temporary: t and its TIR number.</summary>
    private string Temporary(TirInstruction instruction)
    {
        var name = CTypes.Unique(_names, "t" + _numbers[instruction].ToString(CultureInfo.InvariantCulture));
        _values[instruction] = new Expression(name, Postfix);
        return name;
    }

    /// <summary>The value a conversion that changes nothing stands for, or the value itself.</summary>
    private TirValue Resolve(TirValue value)
    {
        while (value is TirInstruction instruction && _aliases.TryGetValue(instruction, out var original))
        {
            value = original;
        }

        return value;
    }

    /// <summary>
    /// A value as an expression of its C type: a parameter or temporary by name, a constant written out (see
    /// <see cref="Literal"/>), an address of a place as <c>(uint8_t *)&amp;place</c>.
    /// </summary>
    private Expression Value(TirValue value, bool typed = false)
    {
        var resolved = Resolve(value);
        if (resolved is TirConst constant)
        {
            return Literal(constant, value.Type, typed);
        }

        if (_places.TryGetValue(resolved, out var place))
        {
            return new Expression($"(uint8_t *)&{place.LValue}", Unary);
        }

        return _values.TryGetValue(resolved, out var expression)
            ? expression
            : throw new ChipSharpException($"Function '{_function.Name}': a value is used before it is made " +
                $"({TirPrinter.Print((TirInstruction)resolved)}).");
    }

    /// <summary>The value as an operand of an operator that binds as tightly as <paramref name="level"/>: in parentheses when it binds less.</summary>
    private string Operand(TirValue value, int level, bool typed = false) => Paren(Value(value, typed), level);

    private static string Paren(Expression expression, int level) =>
        expression.Level <= level ? expression.Text : $"({expression.Text})";

    /// <summary>
    /// A constant of <paramref name="type"/>: a bool as 1 or 0; an integer whose unsigned value is at most 32767 as that
    /// number (an int, which C converts without change wherever it meets the operand's type), any other, and every
    /// integer when <paramref name="typed"/> (two constants meet, or one is shifted), as its value cast to the type,
    /// <c>(uint32_t)-5</c>, so that its C value is the unsigned value of its bits; a float exactly (see <see cref="CText.Float"/>).
    /// </summary>
    private Expression Literal(TirConst constant, TirType type, bool typed)
    {
        if (type.IsFloat)
        {
            var value = type == TirType.F32 ? (float)constant.FloatValue : constant.FloatValue;
            var text = CText.Float(value, type == TirType.F32);
            return new Expression(text, text.StartsWith('-') ? Unary : Postfix);
        }

        if (type == TirType.Bool)
        {
            return new Expression(constant.IntegerValue != 0 ? "1" : "0", Postfix);
        }

        return UnsignedLiteral(type, CText.Unsigned(constant, _types.Bits(type)), typed, constant.IntegerValue);
    }

    /// <summary>
    /// A constant as a global's initializer (a constant expression, converted to the global's type): what an operand
    /// is (see <see cref="Literal"/>), never cast for a small number.
    /// </summary>
    public static string Constant(TirConst constant, CTypes types)
    {
        if (constant.Type.IsFloat)
        {
            return CText.Float(constant.Type == TirType.F32 ? (float)constant.FloatValue : constant.FloatValue,
                constant.Type == TirType.F32);
        }

        if (constant.Type == TirType.Bool)
        {
            return constant.IntegerValue != 0 ? "1" : "0";
        }

        var value = CText.Unsigned(constant, types.Bits(constant.Type));
        return value <= 32767
            ? value.ToString(CultureInfo.InvariantCulture)
            : $"({types.Of(constant.Type)}){CText.SignedInteger(constant.IntegerValue)}";
    }

    private Expression UnsignedLiteral(TirType type, ulong value, bool typed, long? signedValue = null)
    {
        if (!typed && value <= 32767)
        {
            return new Expression(value.ToString(CultureInfo.InvariantCulture), Postfix);
        }

        var number = signedValue is { } signed ? CText.SignedInteger(signed) : value.ToString(CultureInfo.InvariantCulture);
        return new Expression($"({_types.Of(type)}){number}", Unary);
    }

    /// <summary>The value of a type with every byte zero, for a scalar: 0, 0.0f, a null address.</summary>
    private static Expression Zero(TirType type) => type.Kind switch
    {
        TirTypeKind.F32 => new Expression("0.0f", Postfix),
        TirTypeKind.F64 => new Expression("0.0", Postfix),
        TirTypeKind.Ptr or TirTypeKind.Ref => new Expression("(uint8_t *)0", Unary),
        _ => new Expression("0", Postfix),
    };

    private void Declare(TirInstruction instruction, string initializer)
    {
        var name = Temporary(instruction);
        Line($"{_types.Declare(instruction.Type, name)} = {initializer};", declares: true);
    }

    private void Line(string line, bool declares = false)
    {
        _lines.Add(line);
        _declares |= declares;
    }

    /// <summary>A block's label: its TIR name made a C name, never a keyword.</summary>
    private static string Label(string name)
    {
        var label = CText.Symbol(name);
        return label.Length == 0 || label[0] == '_' || CText.IsReserved(label) || IsKeywordLabel(label)
            ? "b_" + label
            : label;
    }

    private static bool IsKeywordLabel(string label) => label is "default" or "case";

    private ChipSharpException Unsupported(string what) =>
        new($"Function '{_function.Name}': the c code generator does not support {what} yet.");

    private readonly record struct Expression(string Text, int Level);

    private readonly record struct Place(string LValue, TirType Type);
}
