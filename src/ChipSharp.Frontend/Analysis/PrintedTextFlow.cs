using System.Collections.Immutable;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Finds the printed text of one method (see <see cref="PrintedText"/>): an abstract interpretation of its IL, block by
/// block until nothing changes, of what each value on the evaluation stack can be as far as text is concerned, and of
/// the parts each DefaultInterpolatedStringHandler local holds so far. Then it records, block by block, what each
/// instruction does for printed text, and its verdict.
/// </summary>
/// <remarks>
/// A value can be a constant (an int for an alignment or an index, a string or null for a literal or a format), the
/// address of a handler local, an array of strings for <c>string.Concat(string[])</c> with the parts of each element,
/// built text with all its parts (<c>ToStringAndClear</c>, <c>string.Concat</c>, <c>ToString</c>), built text that the
/// ways into a block disagree on (<see cref="Mixed"/>), or anything else. Built text may go only where it is printed
/// (Console.Write and Console.WriteLine of a string), into more text (a part of a Concat, of a <c>$"..."</c>, of the
/// array), to a test against null (which it never is: only the way where it is not null is followed), be dropped
/// (<c>pop</c>), or wait in a temporary: a string local written once and read once, as C# makes them in Debug and around
/// a <c>switch</c> expression (in Release C# leaves such a variable out itself). Anywhere else it is kept, which needs
/// the heap. A temporary holds what was stored in it, so a text read from it after the parts were made again (in a
/// loop) is <see cref="Mixed"/> with what it held before, which is rejected. An array of strings that goes anywhere else is a plain
/// array, which <see cref="ArrayEscapeAnalysis"/> judges: the analysis then runs again without it.
/// </remarks>
internal sealed class PrintedTextFlow
{
    /// <summary>The most parts a <c>string.Concat(string[])</c> array may have; a bigger one is a plain array.</summary>
    private const int MostArrayParts = 1024;

    private static readonly Value Unknown = new Other();

    private readonly IlMethod _method;
    private readonly IlMethodBody _body;
    private readonly IReadOnlySet<int> _handlerLocals;

    /// <summary>The string locals written once and read once, through which built text may go (see above).</summary>
    private readonly HashSet<int> _temporaries;
    private readonly IReadOnlyList<IlBlock> _blocks;
    private readonly int[] _blockOf;

    /// <summary>The newarr of string[] whose array is not (only) the parts of a Concat: plain arrays.</summary>
    private readonly HashSet<IlInstruction> _plainArrays = [];

    private readonly Dictionary<IlInstruction, PrintedRole> _roles = [];
    private readonly Dictionary<IlInstruction, Unsupported?> _verdicts = [];
    private readonly Dictionary<IlInstruction, PrintSite> _prints = [];
    private bool _foundPlainArray;
    private bool _record;

    /// <summary>Where control goes after the block followed last when a null test cuts a way off; null otherwise.</summary>
    private int? _onlyTo;

    public PrintedTextFlow(IlMethod method, IlMethodBody body, IReadOnlySet<int> handlerLocals)
    {
        _method = method;
        _body = body;
        _handlerLocals = handlerLocals;
        _temporaries = FindTemporaries(body);
        _blocks = IlBlock.Split(body);
        _blockOf = new int[body.Instructions.Count];
        for (var b = 0; b < _blocks.Count; b++)
        {
            for (var i = _blocks[b].First; i < _blocks[b].End; i++)
            {
                _blockOf[i] = b;
            }
        }
    }

    public IReadOnlyDictionary<IlInstruction, PrintedRole> Roles => _roles;

    public IReadOnlyDictionary<IlInstruction, Unsupported?> Verdicts => _verdicts;

    public IReadOnlyDictionary<IlInstruction, PrintSite> Prints => _prints;

    public PrintedTextFlow Run()
    {
        while (true)
        {
            _foundPlainArray = false;
            var entries = FindEntryStates();
            if (_foundPlainArray)
            {
                continue;
            }

            _roles.Clear();
            _verdicts.Clear();
            _prints.Clear();
            _record = true;
            for (var b = 0; b < _blocks.Count; b++)
            {
                Follow(b, entries[b]!.Clone());
            }

            _record = false;
            if (!_foundPlainArray)
            {
                return this;
            }
        }
    }

    /// <summary>
    /// What can be where each block starts, from following every way into it until nothing changes. A block that
    /// nothing seen so far leads to starts with an empty stack, as in the translation.
    /// </summary>
    private State?[] FindEntryStates()
    {
        var entries = new State?[_blocks.Count];
        var work = new SortedSet<int>();
        while (Array.FindIndex(entries, entry => entry is null) is var start and >= 0)
        {
            entries[start] = new State([], ImmutableDictionary<int, Parts>.Empty, ImmutableDictionary<int, Value>.Empty);
            work.Add(start);
            while (work.Count > 0)
            {
                var b = work.Min;
                work.Remove(b);
                var state = entries[b]!.Clone();
                if (Follow(b, state) is not { } successors)
                {
                    continue;
                }

                foreach (var successor in successors)
                {
                    if (entries[successor] is not { } entry)
                    {
                        entries[successor] = state.Clone();
                        work.Add(successor);
                    }
                    else if (MergeInto(entry, state))
                    {
                        work.Add(successor);
                    }
                }
            }
        }

        return entries;
    }

    /// <summary>Follows block <paramref name="b"/> from <paramref name="state"/>; its successors, or null when its stack runs out.</summary>
    private List<int>? Follow(int b, State state)
    {
        _onlyTo = null;
        try
        {
            for (var i = _blocks[b].First; i < _blocks[b].End; i++)
            {
                Step(_body.Instructions[i], i, state);
                if (!FallsThrough(_body.Instructions[i]))
                {
                    break;
                }
            }
        }
        catch (StackUnderflow)
        {
            return null;
        }

        if (_onlyTo is { } only)
        {
            return [only];
        }

        var block = _blocks[b];
        var successors = IlBlock.Targets(block.Last).Select(target => _blockOf[_body.IndexOf(target)]).ToList();
        if (FallsThrough(block.Last) && block.End < _body.Instructions.Count)
        {
            successors.Add(b + 1);
        }

        return successors;
    }

    private static bool FallsThrough(IlInstruction instruction) => instruction.OpCode is not (IlOpCode.Br
        or IlOpCode.Br_S or IlOpCode.Ret or IlOpCode.Throw or IlOpCode.Rethrow or IlOpCode.Jmp or IlOpCode.Leave
        or IlOpCode.Leave_S or IlOpCode.Endfinally or IlOpCode.Endfilter);

    private void Step(IlInstruction instruction, int index, State state)
    {
        var stack = state.Stack;
        switch (instruction.OpCode)
        {
            case >= IlOpCode.Ldc_I4_M1 and <= IlOpCode.Ldc_I4_8:
                stack.Add(new Number((int)instruction.OpCode - (int)IlOpCode.Ldc_I4_0));
                return;

            case IlOpCode.Ldc_I4_S or IlOpCode.Ldc_I4:
                stack.Add(new Number(((IlInt32Operand)instruction.Operand!).Value));
                return;

            case IlOpCode.Ldstr:
                stack.Add(new Literal(((IlStringOperand)instruction.Operand!).Value));
                return;

            case IlOpCode.Ldnull:
                stack.Add(new Literal(null));
                return;

            case IlOpCode.Ldloca or IlOpCode.Ldloca_S when _handlerLocals.Contains(instruction.LocalIndex!.Value):
                Own(instruction, PrintedRole.Handler);
                stack.Add(new Handler(instruction.LocalIndex.Value));
                return;

            case var _ when instruction.LocalIndex is { } local && _handlerLocals.Contains(local):
                // a handler is used only through its address
                Problem(instruction, Unsupported.BuildingText);
                break;

            case IlOpCode.Stloc or IlOpCode.Stloc_S or (>= IlOpCode.Stloc_0 and <= IlOpCode.Stloc_3)
                when _temporaries.Contains(instruction.LocalIndex!.Value):
                state.Temporaries = state.Temporaries.SetItem(instruction.LocalIndex.Value, Pop(stack));
                return;

            case IlOpCode.Ldloc or IlOpCode.Ldloc_S or (>= IlOpCode.Ldloc_0 and <= IlOpCode.Ldloc_3)
                when _temporaries.Contains(instruction.LocalIndex!.Value):
                stack.Add(state.Temporaries.GetValueOrDefault(instruction.LocalIndex.Value, Unknown));
                return;

            case IlOpCode.Dup:
                var top = Pop(stack);
                stack.Add(top);
                stack.Add(top);
                return;

            case IlOpCode.Pop:
                Consume(instruction, Pop(stack), dropped: true);
                return;

            case IlOpCode.Newarr when instruction.Operand is IlTypeOperand { Type: var type }
                && StackArrays.Primitive(type) == IlType.String && !_plainArrays.Contains(instruction)
                && stack is [.., Number { Constant: >= 0 and <= MostArrayParts } length]:
                Pop(stack);
                Own(instruction, PrintedRole.PartArray);
                stack.Add(new PartArray(instruction, [.. Enumerable.Repeat(Parts.None, length.Constant)]));
                return;

            case IlOpCode.Stelem_Ref when stack is [.., PartArray array, Number element, _]:
                if (element.Constant >= 0 && element.Constant < array.Elements.Length)
                {
                    ArrayPart(instruction, array, element.Constant, stack);
                    OwnLastText(index);
                    return;
                }

                break;

            case IlOpCode.Brtrue or IlOpCode.Brtrue_S or IlOpCode.Brfalse or IlOpCode.Brfalse_S
                when stack is [.., Built]:
                // C#'s ?? "" after the one part of a +: built text is never null, so only one way is taken
                Pop(stack);
                Own(instruction, PrintedRole.NullTest);
                _onlyTo = instruction.OpCode is IlOpCode.Brtrue or IlOpCode.Brtrue_S
                    ? _blockOf[_body.IndexOf(((IlBranchOperand)instruction.Operand!).Target)]
                    : _blockOf[index] + 1;
                return;

            case IlOpCode.Call when instruction.Operand is IlMethodOperand { Method: var callee }
                && Call(instruction, index, callee, state):
                return;

            case IlOpCode.Constrained:
                ConstrainedToString(instruction, index);
                break;
        }

        var (pops, pushes) = IlStackEffects.Of(instruction, _method);
        for (var i = 0; i < pops; i++)
        {
            Consume(instruction, Pop(stack), dropped: false);
        }

        for (var i = 0; i < pushes; i++)
        {
            stack.Add(Unknown);
        }
    }

    /// <summary>A value an instruction takes that is not for printed text: built text there is kept.</summary>
    private void Consume(IlInstruction instruction, Value value, bool dropped)
    {
        switch (value)
        {
            case Built or Mixed when !dropped:
                Problem(instruction, Unsupported.KeepingBuiltText);
                break;

            case Handler:
                Problem(instruction, Unsupported.BuildingText);
                break;

            case PartArray array:
                MarkPlain(array.Newarr);
                break;
        }
    }

    /// <summary>
    /// A call that builds or prints text: of the handler, string.Concat, ToString, Console.Write and WriteLine of built
    /// text. False for any other call, which the caller takes as it takes any instruction.
    /// </summary>
    private bool Call(IlInstruction instruction, int index, IlMethodReference callee, State state)
    {
        var stack = state.Stack;
        if (callee.Definition is not null)
        {
            return false;
        }

        if (Unsupported.IsInterpolatedStringHandler(callee.DeclaringType))
        {
            return callee.HasThis && stack.Count > callee.ParameterTypes.Count
                && stack[^(callee.ParameterTypes.Count + 1)] is Handler handler
                && HandlerCall(instruction, index, callee, handler.Local, state);
        }

        if (PrintedText.IsSystem(callee, "String") && callee is { Name: "Concat", HasThis: false, GenericArguments.Count: 0 })
        {
            return Concat(instruction, index, callee, stack);
        }

        if (PrintedText.IsToString(callee, out var type))
        {
            ToStringCall(instruction, callee, type, stack);
            OwnLastText(index);
            return true;
        }

        if (PrintedText.IsSystem(callee, "Console") && callee is { Name: "Write" or "WriteLine", HasThis: false }
            && callee.ParameterTypes is [IlPrimitiveType { Kind: IlPrimitiveKind.String }] && stack is [.., Built or Mixed])
        {
            if (Pop(stack) is Built built)
            {
                Own(instruction, PrintedRole.Print);
                if (_record)
                {
                    _prints[instruction] = new PrintSite(Joined(built.Parts.Pieces), callee.Name == "WriteLine");
                }
            }
            else
            {
                Problem(instruction, Unsupported.KeepingBuiltText);
            }

            return true;
        }

        return false;
    }

    /// <summary>A call on the handler in local <paramref name="local"/>; false for a method of it that is not supported.</summary>
    private bool HandlerCall(IlInstruction instruction, int index, IlMethodReference callee, int local, State state)
    {
        var stack = state.Stack;
        var parameters = callee.ParameterTypes;
        switch (callee.Name)
        {
            case ".ctor" when parameters.Count == 2 && parameters.All(parameter => parameter == IlType.Int32):
                Pop(stack);
                Pop(stack);
                Pop(stack);
                state.Handlers = state.Handlers.SetItem(local, Parts.None);
                Own(instruction, PrintedRole.Start);
                return true;

            case "AppendLiteral" when parameters is [IlPrimitiveType { Kind: IlPrimitiveKind.String }]:
                var literal = Pop(stack);
                Pop(stack);
                Append(instruction, local, state, TextParts(literal, instruction, 1, 0));
                OwnLastText(index);
                return true;

            case "AppendFormatted" when Hole(callee) is ({ } type, var hasAlignment, var hasFormat):
                var format = hasFormat ? Pop(stack) : new Literal(null);
                var alignment = hasAlignment ? Pop(stack) : new Number(0);
                var value = Pop(stack);
                Pop(stack);
                Append(instruction, local, state, format is Literal { Text: var text } && alignment is Number { Constant: var align }
                    ? HoleParts(type, value, instruction, 1, align, text)
                    : (null, Unsupported.BuildingText));
                OwnLastText(index);
                return true;

            case "ToStringAndClear" when parameters.Count == 0:
                Pop(stack);
                if (state.Handlers.TryGetValue(local, out var parts) && !parts.IsBroken)
                {
                    Own(instruction, PrintedRole.Finish);
                    stack.Add(new Built(instruction, parts));
                }
                else
                {
                    Problem(instruction, Unsupported.BuildingText);
                    stack.Add(Unknown);
                }

                state.Handlers = state.Handlers.Remove(local);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The hole an AppendFormatted takes: its type, and whether an alignment and a format follow it. Null for an
    /// overload that is not supported (of an object, of a span).
    /// </summary>
    private static (IlType Type, bool HasAlignment, bool HasFormat)? Hole(IlMethodReference callee)
    {
        var parameters = callee.ParameterTypes;
        IlType type;
        if (callee.GenericArguments is [var argument] && parameters.Count > 0
            && parameters[0] is IlGenericParameterType { Index: 0, IsMethodParameter: true })
        {
            type = argument;
        }
        else if (callee.GenericArguments.Count == 0 && parameters.Count > 0 && parameters[0] == IlType.String)
        {
            type = IlType.String;
        }
        else
        {
            return null;
        }

        return parameters.Count switch
        {
            1 => (type, false, false),
            2 when parameters[1] == IlType.Int32 => (type, true, false),
            2 when parameters[1] == IlType.String => (type, false, true),
            3 when parameters[1] == IlType.Int32 && parameters[2] == IlType.String => (type, true, true),
            _ => null,
        };
    }

    /// <summary>Adds parts to the text the handler in <paramref name="local"/> builds.</summary>
    private void Append(IlInstruction instruction, int local, State state, (Parts? Parts, Unsupported? Problem) more)
    {
        if (!state.Handlers.TryGetValue(local, out var parts) || parts.IsBroken)
        {
            Problem(instruction, Unsupported.BuildingText);
        }
        else if (more.Problem is { } problem)
        {
            Problem(instruction, problem);
        }
        else
        {
            state.Handlers = state.Handlers.SetItem(local, parts.Then(more.Parts!));
            Own(instruction, PrintedRole.Append);
        }
    }

    /// <summary>
    /// <c>string.Concat</c> of two to four strings, or of an array of them: its parts one after another. False for the
    /// other overloads (of objects, of spans) and for an array that is not only parts.
    /// </summary>
    private bool Concat(IlInstruction instruction, int index, IlMethodReference callee, List<Value> stack)
    {
        var parameters = callee.ParameterTypes;
        var parts = Parts.None;
        Unsupported? problem = null;
        if (parameters.Count is >= 2 and <= 4 && parameters.All(parameter => parameter == IlType.String))
        {
            var values = new Value[parameters.Count];
            for (var i = values.Length - 1; i >= 0; i--)
            {
                values[i] = Pop(stack);
            }

            for (var i = 0; i < values.Length; i++)
            {
                var (more, why) = TextParts(values[i], instruction, i, 0);
                problem ??= why;
                parts = more is null ? parts : parts.Then(more);
            }

            OwnLastText(index);
        }
        else if (parameters is [IlArrayType { ElementType: IlPrimitiveType { Kind: IlPrimitiveKind.String } }]
            && stack is [.., PartArray array])
        {
            Pop(stack);
            parts = array.Elements.Aggregate(parts, (all, element) => all.Then(element));
        }
        else
        {
            return false;
        }

        if (problem is not null)
        {
            Problem(instruction, problem);
            stack.Add(Unknown);
        }
        else
        {
            Own(instruction, PrintedRole.Concat);
            stack.Add(new Built(instruction, parts));
        }

        return true;
    }

    /// <summary><c>x.ToString()</c> or <c>x.ToString(format)</c> of a number or bool: a text of one part.</summary>
    private void ToStringCall(IlInstruction instruction, IlMethodReference callee, IlPrimitiveType type, List<Value> stack)
    {
        var format = callee.ParameterTypes.Count == 1 ? Pop(stack) : new Literal(null);
        var receiver = Pop(stack);
        var (parts, problem) = format is Literal { Text: var text }
            ? HoleParts(type, receiver, instruction, 0, 0, text)
            : (null, Unsupported.BuildingText);
        if (problem is not null)
        {
            Problem(instruction, problem);
            stack.Add(Unknown);
        }
        else
        {
            Own(instruction, PrintedRole.ToString);
            stack.Add(new Built(instruction, parts!));
        }
    }

    /// <summary><c>stelem.ref</c> of a part into the array of a <c>string.Concat(string[])</c>.</summary>
    private void ArrayPart(IlInstruction instruction, PartArray array, int element, List<Value> stack)
    {
        var value = Pop(stack);
        Pop(stack);
        Pop(stack);
        var (parts, problem) = TextParts(value, instruction, 2, 0);
        if (problem is not null)
        {
            Problem(instruction, problem);
        }
        else
        {
            Own(instruction, PrintedRole.ArrayPart);
        }

        // every copy of the array on the stack (dup) is the same array
        var filled = array with { Elements = array.Elements.SetItem(element, parts ?? Parts.None) };
        for (var i = 0; i < stack.Count; i++)
        {
            if (stack[i] is PartArray copy && copy.Newarr == array.Newarr)
            {
                stack[i] = filled;
            }
        }
    }

    /// <summary>
    /// <c>constrained. T</c> before <c>callvirt Object::ToString()</c>: C#'s <c>value.ToString()</c> of an enum or a
    /// struct (of a number, C# calls its ToString directly), which needs what the enum or struct is called.
    /// </summary>
    private void ConstrainedToString(IlInstruction instruction, int index)
    {
        if (instruction.Operand is IlTypeOperand { Type: var type } && index + 1 < _body.Instructions.Count
            && _body.Instructions[index + 1] is
            {
                OpCode: IlOpCode.Callvirt,
                Operand: IlMethodOperand { Method: { Name: "ToString", ParameterTypes.Count: 0, HasThis: true } },
            } call
            && !PrintedText.TryGetPrinted(StackArrays.Primitive(type), out _))
        {
            var problem = ForOtherValue(StackArrays.Primitive(type));
            Problem(instruction, problem);
            Problem(call, problem);
        }
    }

    /// <summary>
    /// The parts of a value of <paramref name="type"/> written as a hole (or as its ToString): one part, of text, a
    /// whole number, a bool, a float or a double; or what is not supported about it.
    /// </summary>
    private (Parts? Parts, Unsupported? Problem) HoleParts(IlType type, Value value, IlInstruction instruction,
        int operand, int alignment, string? format)
    {
        if (!PrintedText.TryGetPrinted(type, out var kind))
        {
            return (null, ForOtherValue(ValueTypes.WithoutModifiers(type)));
        }

        if (kind == IlPrimitiveKind.String)
        {
            return TextParts(value, instruction, operand, alignment);
        }

        if (!PrintedText.TryParseFormat(format, kind, out var parsed))
        {
            return (null, Unsupported.Format(format!));
        }

        if (PrintedText.IsFloat(kind) && alignment != 0)
        {
            return (null, Unsupported.AlignedFloats);
        }

        var primitive = (IlPrimitiveType)ValueTypes.WithoutModifiers(type);
        return (Parts.None.Then([new PrintedPiece(instruction, operand, primitive, null, alignment, parsed)]), null);
    }

    /// <summary>
    /// The parts of text that <paramref name="instruction"/> takes as its operand <paramref name="operand"/>: a
    /// literal as it is (aligned already; null is nothing), built text's parts, or any other string as one part.
    /// </summary>
    private static (Parts? Parts, Unsupported? Problem) TextParts(Value value, IlInstruction instruction, int operand,
        int alignment) => value switch
    {
        Built built when alignment == 0 => (built.Parts, null),
        Built or Mixed => (null, Unsupported.KeepingBuiltText),
        Handler or PartArray => (null, Unsupported.BuildingText),
        Literal literal => PrintedText.Align(literal.Text ?? "", alignment) is { Length: > 0 } text
            ? (Parts.None.Then([new PrintedPiece(instruction, operand, IlType.String, text, 0, default)]), null)
            : (Parts.None, null),
        _ => (Parts.None.Then([new PrintedPiece(instruction, operand, IlType.String, null, alignment, default)]), null),
    };

    /// <summary>What is not supported about printing a value that is not a number, a bool or text.</summary>
    private Unsupported ForOtherValue(IlType type) => type switch
    {
        IlPrimitiveType { Kind: IlPrimitiveKind.Char } => Unsupported.Characters,
        IlPrimitiveType { Kind: IlPrimitiveKind.Object } => Unsupported.ClassesAndObjects,
        IlPrimitiveType { Kind: IlPrimitiveKind.NativeInt or IlPrimitiveKind.NativeUInt } => Unsupported.NativeIntText,
        _ when ValueTypes.Definition(type, _method.Assembly) is { } definition && ValueTypes.IsEnum(definition) =>
            Unsupported.EnumNames,
        _ => Unsupported.TextOf(CSharpNames.Type(type)),
    };

    /// <summary>The parts with each run of literal text joined into one.</summary>
    private static List<PrintedPiece> Joined(IEnumerable<PrintedPiece> pieces)
    {
        var joined = new List<PrintedPiece>();
        foreach (var piece in pieces)
        {
            if (piece.Literal is { } text && joined is [.., { Literal: { } before } last])
            {
                joined[^1] = last with { Literal = before + text };
            }
            else
            {
                joined.Add(piece);
            }
        }

        return joined;
    }

    /// <summary>
    /// Merges what comes into a block another way into <paramref name="entry"/>; true when that changes anything. Two
    /// different texts, or text and something else, are <see cref="Mixed"/>; an array of parts that meets anything but
    /// itself is a plain array.
    /// </summary>
    private bool MergeInto(State entry, State other)
    {
        var changed = false;
        if (entry.Stack.Count == other.Stack.Count)
        {
            for (var i = 0; i < entry.Stack.Count; i++)
            {
                var merged = Merge(entry.Stack[i], other.Stack[i]);
                changed |= !merged.Equals(entry.Stack[i]);
                entry.Stack[i] = merged;
            }
        }

        var handlers = entry.Handlers;
        foreach (var local in entry.Handlers.Keys.Union(other.Handlers.Keys))
        {
            var mine = entry.Handlers.GetValueOrDefault(local);
            var theirs = other.Handlers.GetValueOrDefault(local);
            if (mine is null || theirs is null || !mine.Equals(theirs))
            {
                handlers = handlers.SetItem(local, Parts.Broken);
            }
        }

        changed |= !handlers.SequenceEqual(entry.Handlers);
        entry.Handlers = handlers;

        var temporaries = entry.Temporaries;
        foreach (var local in entry.Temporaries.Keys.Union(other.Temporaries.Keys))
        {
            var merged = Merge(entry.Temporaries.GetValueOrDefault(local, Unknown),
                other.Temporaries.GetValueOrDefault(local, Unknown));
            temporaries = temporaries.SetItem(local, merged);
        }

        changed |= !temporaries.SequenceEqual(entry.Temporaries);
        entry.Temporaries = temporaries;
        return changed;
    }

    private Value Merge(Value a, Value b)
    {
        if (a.Equals(b))
        {
            return a;
        }

        foreach (var array in new[] { a, b }.OfType<PartArray>())
        {
            MarkPlain(array.Newarr);
        }

        return a is Built or Mixed || b is Built or Mixed ? new Mixed() : Unknown;
    }

    /// <summary>The string locals of <paramref name="body"/> that one instruction writes and one reads, and none takes the address of.</summary>
    private static HashSet<int> FindTemporaries(IlMethodBody body)
    {
        var uses = body.Instructions.Where(instruction => instruction.LocalIndex is not null)
            .GroupBy(instruction => instruction.LocalIndex!.Value);
        return
        [
            .. uses.Where(use => use.Key < body.Locals.Count && FixedText.IsText(body.Locals[use.Key].Type)
                    && use.Count(IsStore) == 1 && use.Count(instruction => !IsStore(instruction)
                        && instruction.OpCode is not (IlOpCode.Ldloca or IlOpCode.Ldloca_S)) == 1
                    && use.All(instruction => instruction.OpCode is not (IlOpCode.Ldloca or IlOpCode.Ldloca_S)))
                .Select(use => use.Key),
        ];

        static bool IsStore(IlInstruction instruction) =>
            instruction.OpCode is IlOpCode.Stloc or IlOpCode.Stloc_S or (>= IlOpCode.Stloc_0 and <= IlOpCode.Stloc_3);
    }

    private void MarkPlain(IlInstruction newarr)
    {
        if (_plainArrays.Add(newarr))
        {
            _foundPlainArray = true;
        }
    }

    /// <summary>
    /// For an instruction at <paramref name="index"/> that took its last operand as a literal part or a format: the
    /// <c>ldstr</c> or <c>ldnull</c> right before it in its block, whose value nothing else can take, is
    /// <see cref="PrintedRole.Text"/>.
    /// </summary>
    private void OwnLastText(int index)
    {
        if (index > 0 && _blockOf[index - 1] == _blockOf[index]
            && _body.Instructions[index - 1] is { OpCode: IlOpCode.Ldstr or IlOpCode.Ldnull } text)
        {
            Own(text, PrintedRole.Text);
        }
    }

    private void Own(IlInstruction instruction, PrintedRole role)
    {
        if (_record)
        {
            _roles[instruction] = role;
            _verdicts.TryAdd(instruction, null);
        }
    }

    /// <summary>Records what is not supported at <paramref name="instruction"/> (the first thing found there).</summary>
    private void Problem(IlInstruction instruction, Unsupported unsupported)
    {
        if (_record && _verdicts.GetValueOrDefault(instruction) is null)
        {
            _verdicts[instruction] = unsupported;
        }
    }

    private static Value Pop(List<Value> stack)
    {
        if (stack.Count == 0)
        {
            throw new StackUnderflow();
        }

        var value = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return value;
    }

    /// <summary>What a value on the stack can be, as far as printed text cares.</summary>
    private abstract record Value;

    /// <summary>Anything else: a number, a string, an address, ...</summary>
    private sealed record Other : Value;

    /// <summary>An int constant: an alignment, the size of an array or an index into it.</summary>
    private sealed record Number(int Constant) : Value;

    /// <summary>A string literal (ldstr), or null (ldnull): literal text, or a format.</summary>
    private sealed record Literal(string? Text) : Value;

    /// <summary>The address of the DefaultInterpolatedStringHandler in local <paramref name="Local"/>.</summary>
    private sealed record Handler(int Local) : Value;

    /// <summary>The string[] of a <c>string.Concat(string[])</c>, made by <paramref name="Newarr"/>, with each element's parts.</summary>
    private sealed record PartArray(IlInstruction Newarr, ImmutableArray<Parts> Elements) : Value
    {
        public bool Equals(PartArray? other) =>
            other is not null && Newarr == other.Newarr && Elements.SequenceEqual(other.Elements);

        public override int GetHashCode() => Newarr.GetHashCode();
    }

    /// <summary>Text built by <paramref name="Producer"/> (ToStringAndClear, Concat, ToString), with all its parts.</summary>
    private sealed record Built(IlInstruction Producer, Parts Parts) : Value;

    /// <summary>Built text where ways join with other text, or with something else.</summary>
    private sealed record Mixed : Value;

    /// <summary>The parts of a text so far, compared part by part; broken where the ways into a block disagree.</summary>
    private sealed record Parts(ImmutableArray<PrintedPiece> Pieces)
    {
        public static Parts None { get; } = new(ImmutableArray<PrintedPiece>.Empty);

        public static Parts Broken { get; } = new(default(ImmutableArray<PrintedPiece>));

        public bool IsBroken => Pieces.IsDefault;

        public Parts Then(Parts more) => new(Pieces.AddRange(more.Pieces));

        public Parts Then(IEnumerable<PrintedPiece> more) => new(Pieces.AddRange(more));

        public bool Equals(Parts? other) => other is not null
            && (IsBroken ? other.IsBroken : !other.IsBroken && Pieces.SequenceEqual(other.Pieces));

        public override int GetHashCode() => IsBroken ? -1 : Pieces.Length;
    }

    /// <summary>The stack, the parts each started handler holds so far, and what each temporary holds, at one point.</summary>
    private sealed class State(List<Value> stack, ImmutableDictionary<int, Parts> handlers,
        ImmutableDictionary<int, Value> temporaries)
    {
        public List<Value> Stack { get; } = stack;

        public ImmutableDictionary<int, Parts> Handlers { get; set; } = handlers;

        public ImmutableDictionary<int, Value> Temporaries { get; set; } = temporaries;

        public State Clone() => new([.. Stack], Handlers, Temporaries);
    }

    /// <summary>The evaluation stack had fewer values than an instruction takes: IL that is broken, which the
    /// translation reports; the rest of the block is not followed.</summary>
    private sealed class StackUnderflow : Exception;
}
