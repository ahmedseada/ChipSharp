using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Printed text (see <see cref="PrintedText"/> for what it is and <see cref="PrintedTextFlow"/> for the plan): text
/// built and printed straight away, with no string ever made.
/// <list type="bullet">
/// <item>Each instruction that hands a hole (or any part that is not a literal) to the text keeps its value in a slot
/// of its own, <c>$part_IL_xxxx</c> (and <c>_n</c> after it for operand n of a <c>string.Concat</c>), as the right
/// type for the runtime's Console.Write function: i32 for bools, chars and the whole numbers up to 32 bits, i64, f32,
/// f64, ref for text. A ToString reads the value through the address it is called on.</item>
/// <item>What stands for the text being built on the evaluation stack (the handler's address, the array of a
/// <c>string.Concat</c>, the built text) is a null ptr that nothing reads, so it may go through a stack slot where a
/// part comes in on several ways; so is a literal that a part or a format takes at once, which the print writes from
/// its own record.</item>
/// <item>The Console.Write or Console.WriteLine of the text writes the literals and the slots one after another, then
/// the end of the line: the last part with the WriteLine of its type when it is written as it is. A part with an
/// alignment or a format is written by <see cref="PrintedTextFunctions"/>.</item>
/// <item>A null test on built text (C#'s <c>?? ""</c>) jumps to where it is not null.</item>
/// </list>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>
    /// What stands for text being built on the evaluation stack: a pending null ptr, written as TIR only where it goes
    /// through a stack slot.
    /// </summary>
    private static readonly StackValue Placeholder = StackValue.OfConstant(TirType.Ptr, 0);

    private readonly Dictionary<(IlInstruction Source, int Operand), TirSlot> _partSlots = [];
    private PrintedText? _printedText;

    private PrintedText Printed => _printedText ??= PrintedText.Of(_method);

    /// <summary>
    /// Rejects printed text that needs the heap or what is not supported yet, with the analysis the whole-program pass
    /// uses, so the two always agree.
    /// </summary>
    private void CheckPrintedText()
    {
        // a heap use (kept text) is the whole-program pass's to judge for the target
        var problem = Printed.Verdicts.Where(verdict => verdict.Value is { IsHeapUse: false })
            .OrderBy(verdict => verdict.Key.Offset).FirstOrDefault();
        if (problem.Value is { } unsupported)
        {
            throw LoweringErrors.At(_method, problem.Key.Offset, $"{problem.Key.Name}: {unsupported.Heading}");
        }
    }

    /// <summary>True for an instruction of printed text, which <see cref="LowerPrintedText"/> translates.</summary>
    private bool IsPrintedText(IlInstruction instruction) => Printed.Owns(instruction);

    /// <summary>
    /// The TIR type of a local's slot. A DefaultInterpolatedStringHandler local is never read or written, only its
    /// address taken: a ptr slot that stays unused.
    /// </summary>
    private TirType LocalSlotType(IlLocal local) => Printed.HandlerLocals.Contains(local.Index)
        || Printed.InlineArrayLocals.Contains(local.Index)
        ? TirType.Ptr
        : LoweringErrors.MapType(_method, local.Type, $"local V_{local.Index} has type");

    /// <summary>Translates an instruction of printed text; false for any other instruction.</summary>
    private bool LowerPrintedText(IlInstruction instruction)
    {
        if (!Printed.Roles.TryGetValue(instruction, out var role))
        {
            return false;
        }

        if (role == PrintedRole.NullTest)
        {
            PopValue();
            var (target, next) = (Target(), Next());
            Leave(target, next);
            _ir.Jump(instruction.OpCode is IlOpCode.Brtrue or IlOpCode.Brtrue_S ? target.Tir : next.Tir);
            return true;
        }

        var (pops, _) = role is PrintedRole.Handler or PrintedRole.Text ? (0, 1) : IlStackEffects.Of(instruction, _method);
        var operands = new StackValue[pops];
        for (var i = pops - 1; i >= 0; i--)
        {
            operands[i] = PopValue();
        }

        for (var i = 0; i < pops; i++)
        {
            if (Printed.Saved.TryGetValue((instruction, i), out var piece))
            {
                SavePart(piece, operands[i], throughAddress: role is PrintedRole.ToString or PrintedRole.CharSpan);
            }
        }

        switch (role)
        {
            case PrintedRole.Print:
                Print(Printed.Prints[instruction]);
                break;

            case PrintedRole.AppendTo:
                AppendTo(Printed.Builds[instruction], operands[0]); // see MethodLowering.StringBuilders.cs
                break;

            case PrintedRole.Finish or PrintedRole.ToString or PrintedRole.Concat
                when Printed.Builds.TryGetValue(instruction, out var kept):
                PushObject(Build(kept));
                break;

            case PrintedRole.Handler or PrintedRole.Finish or PrintedRole.ToString or PrintedRole.Concat
                or PrintedRole.PartArray or PrintedRole.Text or PrintedRole.Box or PrintedRole.CharSpan or PrintedRole.SpanText
                or PrintedRole.NewHandler:
                Push(Placeholder);
                break;
        }

        return true;
    }

    /// <summary>Keeps a part's value in its slot until the text is printed.</summary>
    private void SavePart(PrintedPiece piece, StackValue value, bool throughAddress)
    {
        if (throughAddress)
        {
            Push(value);
            LoadIndirect(SupportedSubset.TryMapType(piece.Type, out var storage) ? storage : TirType.I32, piece.Type);
            value = PopValue();
        }

        var slot = PartSlot(piece);
        _ir.Store(slot, FromStack(value, slot.Type, piece.Type, $"the part of the text at {IlPrinter.Label(piece.Source.Offset)}"));
    }

    private TirSlot PartSlot(PrintedPiece piece)
    {
        if (!_partSlots.TryGetValue((piece.Source, piece.Operand), out var slot))
        {
            var label = IlPrinter.Label(piece.Source.Offset);
            var name = Printed.Roles[piece.Source] == PrintedRole.Concat ? $"part_{label}_{piece.Operand}" : $"part_{label}";
            slot = AddSlot(name, ObjectPartType(piece) ?? PartType(piece.Type.Kind)); // see MethodLowering.TextOfObjects.cs
            _partSlots.Add((piece.Source, piece.Operand), slot);
        }

        return slot;
    }

    /// <summary>Writes the parts of a text, then the end of the line for WriteLine.</summary>
    private void Print(PrintSite site)
    {
        var lineEnded = false;
        for (var i = 0; i < site.Pieces.Count; i++)
        {
            lineEnded = WritePart(site.Pieces[i], endLine: site.Line && i == site.Pieces.Count - 1);
        }

        if (site.Line && !lineEnded)
        {
            _ir.Call(_program.Runtime("WriteLine"));
        }
    }

    /// <summary>
    /// Writes one part: a literal, or a value with the Console.Write of its type, or aligned or formatted. True when it
    /// ended the line too, with the WriteLine of its type (only when <paramref name="endLine"/>).
    /// </summary>
    private bool WritePart(PrintedPiece piece, bool endLine)
    {
        if (piece.Literal is { } literal)
        {
            _ir.Call(_program.Runtime(endLine ? "WriteLineString" : "WriteString"), Text(literal));
            return endLine;
        }

        if (WriteObjectPart(piece)) // a nullable, a struct, an object: MethodLowering.TextOfObjects.cs
        {
            return false;
        }

        var kind = piece.Type.Kind;
        var value = _ir.Load(PartSlot(piece));
        var format = piece.Format;
        if (piece.Enum is { } enumType)
        {
            var bits = Widen(value, unsigned: !PrintedText.IsSigned(kind));
            if (piece.Alignment != 0)
            {
                _ir.Call(_program.TextFunctions.WriteAligned, _ir.Call(_program.EnumNames.Name(enumType), bits),
                    _ir.Const(TirType.I32, piece.Alignment));
                return false;
            }

            _ir.Call(_program.EnumNames.Write(enumType), bits);
            return false;
        }

        if (kind == IlPrimitiveKind.Char && piece.Alignment != 0)
        {
            _ir.Call(_program.TextFunctions.WriteChar, value, _ir.Const(TirType.I32, piece.Alignment));
            return false;
        }

        if (piece.Alignment == 0 && format.Kind is PrintedFormatKind.None or PrintedFormatKind.Decimal
            && format.Precision <= 1)
        {
            _ir.Call(_program.Runtime($"{(endLine ? "WriteLine" : "Write")}{RuntimeTypeName(kind)}"), value);
            return endLine;
        }

        var functions = _program.TextFunctions;
        var (precision, alignment) = (_ir.Const(TirType.I32, format.Precision), _ir.Const(TirType.I32, piece.Alignment));
        if (kind == IlPrimitiveKind.String)
        {
            _ir.Call(functions.WriteAligned, value, alignment);
        }
        else if (kind == IlPrimitiveKind.Bool)
        {
            _ir.Call(functions.WriteBool, value, alignment);
        }
        else if (format.Kind is PrintedFormatKind.UpperHex or PrintedFormatKind.LowerHex)
        {
            var upper = _ir.Const(TirType.I32, format.Kind == PrintedFormatKind.UpperHex ? 1 : 0);
            _ir.Call(functions.WriteHex, HexBits(value, kind), upper, precision, alignment);
        }
        else if (PrintedText.IsSigned(kind))
        {
            _ir.Call(functions.WriteSigned, Widen(value, unsigned: false), precision, alignment);
        }
        else
        {
            _ir.Call(functions.WriteDecimal, Widen(value, unsigned: true), _ir.Const(TirType.I32, 0), precision, alignment);
        }

        return false;
    }

    /// <summary>A whole number's bits of its own width, as an unsigned i64: an sbyte -1 is 0xFF.</summary>
    private TirValue HexBits(TirValue value, IlPrimitiveKind kind) => PrintedText.Bits(kind) switch
    {
        64 => value,
        32 => Widen(value, unsigned: true),
        var bits => Widen(_ir.Binary(TirBinaryOp.And, value, _ir.Const(TirType.I32, (1L << bits) - 1)), unsigned: true),
    };

    private TirValue Widen(TirValue value, bool unsigned) =>
        value.Type == TirType.I64 ? value : _ir.Convert(value, TirType.I64, unsigned);

    /// <summary>The type a part waits in: what the runtime's Console.Write of its type takes.</summary>
    private static TirType PartType(IlPrimitiveKind kind) => kind switch
    {
        IlPrimitiveKind.String => TirType.Ref,
        IlPrimitiveKind.Int64 or IlPrimitiveKind.UInt64 => TirType.I64,
        IlPrimitiveKind.Float32 => TirType.F32,
        IlPrimitiveKind.Float64 => TirType.F64,
        _ => TirType.I32,
    };

    /// <summary>
    /// The type in the name of the runtime's Console.Write function for a part, e.g. WriteInt32: the whole numbers
    /// narrower than int are written as an int, which prints the same.
    /// </summary>
    private static string RuntimeTypeName(IlPrimitiveKind kind) => kind switch
    {
        IlPrimitiveKind.String => "String",
        IlPrimitiveKind.Bool => "Bool",
        IlPrimitiveKind.Char => "Char",
        IlPrimitiveKind.UInt32 => "UInt32",
        IlPrimitiveKind.Int64 => "Int64",
        IlPrimitiveKind.UInt64 => "UInt64",
        IlPrimitiveKind.Float32 => "Float32",
        IlPrimitiveKind.Float64 => "Float64",
        _ => "Int32",
    };
}
