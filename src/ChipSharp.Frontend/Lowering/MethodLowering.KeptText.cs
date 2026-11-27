using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Kept text (see <see cref="PrintedText.Builds"/>): built text that the program keeps, on a target with a heap, is a
/// string the instruction that finishes it makes from its parts, where printed text would have printed them. The parts
/// wait in their slots as for printed text, so every hole is worked out first, in order, as in .NET; then each part
/// becomes a string (a literal is its own, a number's text is made by the runtime, with its format and alignment) and
/// the runtime puts them together (<c>ChipSharp.Runtime.StringConcat</c>, from an array of them in a slot), once.
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>The string of the parts of <paramref name="site"/>, one after another.</summary>
    private TirValue Build(PrintSite site)
    {
        var pieces = site.Pieces;
        if (pieces.Count == 0)
        {
            return Text("");
        }

        if (pieces is [{ Literal: { } only }])
        {
            return Text(only);
        }

        // (a part of a nullable ends the block it starts in: the slot's address is taken where it is used)
        var slot = AddSlot("parts", new TirArrayType(TirType.Ref, pieces.Count));
        for (var i = 0; i < pieces.Count; i++)
        {
            var text = PartText(pieces[i]);
            _ir.Store(_ir.ElementAddress(_ir.AddressOf(slot), TirType.Ref, _ir.Const(TirType.I32, i)), text);
        }

        var concat = _program.RuntimeFunction("StringConcat", TirType.Ref, TirType.Ptr, TirType.I32);
        return _ir.Call(concat, _ir.AddressOf(slot), _ir.Const(TirType.I32, pieces.Count));
    }

    /// <summary>One part as a string: a literal, text (null as nothing), or the text of a value as .NET writes it.</summary>
    private TirValue PartText(PrintedPiece piece)
    {
        if (piece.Literal is { } literal)
        {
            return Text(literal);
        }

        if (ObjectPartText(piece) is { } objectText) // a nullable, a struct, an object: MethodLowering.TextOfObjects.cs
        {
            return objectText;
        }

        var value = _ir.Load(PartSlot(piece));
        var kind = piece.Type.Kind;
        var format = piece.Format;
        var precision = _ir.Const(TirType.I32, format.Precision);
        TirValue text = kind switch
        {
            _ when piece.Enum is { } enumType =>
                _ir.Call(_program.EnumNames.Name(enumType), Widen(value, unsigned: !PrintedText.IsSigned(kind))),
            IlPrimitiveKind.String => value,
            IlPrimitiveKind.Bool => _ir.Call(StringOf("Bool", TirType.I32), value),
            IlPrimitiveKind.Char => _ir.Call(StringOf("Char", TirType.I32), value),
            IlPrimitiveKind.Float32 => _ir.Call(StringOf("Float32", TirType.F32), value),
            IlPrimitiveKind.Float64 => _ir.Call(StringOf("Float64", TirType.F64), value),
            _ when format.Kind is PrintedFormatKind.UpperHex or PrintedFormatKind.LowerHex =>
                _ir.Call(StringOf("Hex", TirType.I64, TirType.I32, TirType.I32), HexBits(value, kind), precision,
                    _ir.Const(TirType.I32, format.Kind == PrintedFormatKind.UpperHex ? 1 : 0)),
            _ when PrintedText.IsSigned(kind) =>
                _ir.Call(StringOf("Int64", TirType.I64, TirType.I32), Widen(value, unsigned: false), precision),
            _ => _ir.Call(StringOf("UInt64", TirType.I64, TirType.I32), Widen(value, unsigned: true), precision),
        };

        return piece.Alignment == 0
            ? text
            : _ir.Call(_program.RuntimeFunction("StringAligned", TirType.Ref, TirType.Ref, TirType.I32), text,
                _ir.Const(TirType.I32, piece.Alignment));
    }

    /// <summary>The runtime function that makes the text of a value: <c>ChipSharp.Runtime.StringOf&lt;what&gt;</c>.</summary>
    private TirFunction StringOf(string what, params TirType[] parameters) =>
        _program.RuntimeFunction($"StringOf{what}", TirType.Ref, parameters);
}
