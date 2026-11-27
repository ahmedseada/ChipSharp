using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The parts of printed and kept text that are objects, structs and nullables (see Analysis/PrintedTextFlow.Objects.cs):
/// <list type="bullet">
/// <item>An object waits in a ref slot; it is written with its ToString as a <c>callvirt</c> of it runs it (one method, the
/// method table's, a string's), nothing for null, as Console.Write(object) writes it.</item>
/// <item>A struct waits in a slot of its own type; its ToString (<c>ChipSharp.Value.ToString&lt;T&gt;</c>) runs on that
/// slot's address, with no box.</item>
/// <item>A nullable waits in a slot of its struct: when it has a value, the value is written as a part of the value's type
/// would be; else nothing, or as many spaces as its alignment asks for.</item>
/// </list>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>The type an object, struct or nullable part waits in; null for the other parts.</summary>
    private TirType? ObjectPartType(PrintedPiece piece) =>
        piece.Nullable is { } nullable ? MapType(nullable, "a nullable in text")
        : piece.Value is { } value ? value.ValueType
        : piece.Formatted is { } formatted ? ValueTypes.StructType(formatted)
        : piece.Object is not null ? TirType.Ref
        : null;

    /// <summary>
    /// The arguments of a formatted value's Print and Text (see <see cref="FormattedValues"/>): the value's address, its
    /// format as a span of constant chars (empty for none), its alignment, and whether it is its ToString.
    /// </summary>
    private TirValue[] FormattedArguments(PrintedPiece piece)
    {
        var format = piece.TextFormat is { Length: > 0 } text
            ? _ir.Call(_program.Arrays.Make, _ir.AddressOf(_program.CharData(text)), _ir.Const(TirType.I32, text.Length))
            : _ir.Call(_program.Arrays.Make, _ir.Zero(TirType.Ptr), _ir.Const(TirType.I32, 0));
        return [_ir.AddressOf(PartSlot(piece)), format, _ir.Const(TirType.I32, piece.Alignment), _ir.Const(piece.ViaToString)];
    }

    /// <summary>Writes an object, struct or nullable part; false for any other part, which <see cref="WritePart"/> writes.</summary>
    private bool WriteObjectPart(PrintedPiece piece)
    {
        if (piece.Formatted is { } formatted)
        {
            _ir.Call(_program.Function(DispatchTarget.Of(FormattedValues.Print(formatted)!)), FormattedArguments(piece));
            return true;
        }

        if (piece.Nullable is { } nullableType)
        {
            var nullable = (TirStructType)MapType(nullableType, "a nullable in text");
            var inner = InnerPiece(piece);
            var (some, none, join) = BranchOnValue(nullable, PartSlot(piece));

            _ir.Block = some;
            var address = _ir.AddressOf(PartSlot(piece));
            _ir.Store(PartSlot(inner), _ir.Load(nullable.Fields[Nullables.ValueField].Type,
                _ir.FieldAddress(address, nullable, Nullables.ValueField)));
            WritePart(inner, endLine: false);
            _ir.Jump(join);

            _ir.Block = none;
            if (piece.Alignment != 0)
            {
                _ir.Call(_program.TextFunctions.WriteAligned, Text(""), _ir.Const(TirType.I32, piece.Alignment));
            }

            _ir.Jump(join);
            _ir.Block = join;
            return true;
        }

        if (ObjectText(piece) is not { } text)
        {
            return false;
        }

        if (piece.Alignment != 0)
        {
            _ir.Call(_program.TextFunctions.WriteAligned, text, _ir.Const(TirType.I32, piece.Alignment));
        }
        else
        {
            _ir.Call(_program.Runtime("WriteString"), text);
        }

        return true;
    }

    /// <summary>The text of an object, struct or nullable part, as a string for kept text; null for any other part.</summary>
    private TirValue? ObjectPartText(PrintedPiece piece)
    {
        if (piece.Formatted is { } formatted)
        {
            return _ir.Call(_program.Function(DispatchTarget.Of(FormattedValues.Text(formatted)!)), FormattedArguments(piece));
        }

        if (piece.Nullable is { } nullableType)
        {
            var nullable = (TirStructType)MapType(nullableType, "a nullable in text");
            var inner = InnerPiece(piece);
            var result = AddSlot($"text_{IlPrinter.Label(piece.Source.Offset)}", TirType.Ref);
            var (some, none, join) = BranchOnValue(nullable, PartSlot(piece));

            _ir.Block = some;
            var address = _ir.AddressOf(PartSlot(piece));
            _ir.Store(PartSlot(inner), _ir.Load(nullable.Fields[Nullables.ValueField].Type,
                _ir.FieldAddress(address, nullable, Nullables.ValueField)));
            _ir.Store(result, PartText(inner));
            _ir.Jump(join);

            _ir.Block = none;
            _ir.Store(result, Aligned(Text(""), piece.Alignment));
            _ir.Jump(join);

            _ir.Block = join;
            return _ir.Load(result);
        }

        return ObjectText(piece) is { } text ? Aligned(text, piece.Alignment) : null;
    }

    /// <summary>
    /// The text of an object part (its ToString, "" for null) or a struct part (its ToString on the value), not aligned;
    /// null for any other part.
    /// </summary>
    private TirValue? ObjectText(PrintedPiece piece)
    {
        if (piece.Value is { } boxed)
        {
            return _ir.Call(_program.Boxes.ToStringOf(boxed), _ir.AddressOf(PartSlot(piece)));
        }

        if (piece.Object is not { } receiver)
        {
            return null;
        }

        var slot = Boxing.ObjectSlot("ToString");
        var targets = _program.VirtualTables.Dispatch.Targets(slot, receiver);
        var called = targets.Count switch
        {
            0 => null,
            1 => _program.Function(targets[0]),
            _ => _program.ObjectFunctions.VirtualObjectCall(slot.Name, SlotIndex(slot)),
        };
        var toString = StringMethods.MayBeString(receiver) || FixedText.IsText(receiver)
            ? _program.StringObjects.OrString(slot.Name, called)
            : called;
        var value = _ir.Load(PartSlot(piece));
        return toString is null ? Text("") : _ir.Call(_program.Boxes.TextOrEmpty(toString), value);
    }

    /// <summary><paramref name="text"/> aligned as a hole with <paramref name="alignment"/>, a string made for it; as it is for 0.</summary>
    private TirValue Aligned(TirValue text, int alignment) => alignment == 0
        ? text
        : _ir.Call(_program.RuntimeFunction("StringAligned", TirType.Ref, TirType.Ref, TirType.I32), text,
            _ir.Const(TirType.I32, alignment));

    /// <summary>The part of a nullable's value: the same piece, of the value's type, in a slot of its own.</summary>
    private static PrintedPiece InnerPiece(PrintedPiece piece) => piece with { Nullable = null, Operand = piece.Operand + 1_000_000 };

    /// <summary>
    /// Ends the current block with a branch on whether the nullable in <paramref name="slot"/> has a value: to the first
    /// block when it has, the second when not; both go on in the third. What the evaluation stack holds waits in its stack
    /// slots meanwhile, as a value of one block cannot be used in another.
    /// </summary>
    private (TirBlock Some, TirBlock None, TirBlock Join) BranchOnValue(TirStructType nullable, TirSlot slot)
    {
        for (var depth = 0; depth < _stack.Count; depth++)
        {
            if (_stack[depth] is { Value: not null } or { Condition: not null })
            {
                var stackSlot = StackSlot(depth, _stack[depth].Type);
                _ir.Store(stackSlot, Materialize(_stack[depth]));
                _stack[depth] = StackValue.InSlot(stackSlot);
            }
        }

        var label = IlPrinter.Label(_instruction.Offset);
        var n = 1;
        while (_function.Blocks.Any(block => block.Name == $"{label}_value{n}"))
        {
            n++;
        }

        var (some, none, join) = (_function.AddBlock($"{label}_value{n}"), _function.AddBlock($"{label}_no_value{n}"),
            _function.AddBlock($"{label}_text{n}"));
        var hasValue = _ir.Load(TirType.Bool, _ir.FieldAddress(_ir.AddressOf(slot), nullable, Nullables.HasValueField));
        _ir.Branch(hasValue, some, none);
        StartObjectsOfBlock(); // no value of the block before is known any more
        return (some, none, join);
    }
}
