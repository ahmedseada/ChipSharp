using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// The holes of printed and kept text that are not numbers, bools, chars, enums or text (see <see cref="Boxing"/>):
/// <list type="bullet">
/// <item>an object (<c>$"{shape}"</c>, <c>string.Format("{0}", o)</c>): written as its ToString gives it, through its
/// slot as a <c>callvirt</c> of it would run it, and null as nothing, as .NET writes it;</item>
/// <item>a struct (<c>$"{point}"</c>, <c>Console.WriteLine(point)</c>, <c>string.Format("{0}", point)</c>): its
/// ToString, its own or ValueType's (its type's name), run on the value, with no box;</item>
/// <item>a nullable (<c>$"{n}"</c>, <c>Console.WriteLine(n)</c>): its value as a hole of the value's type would be
/// written (with its alignment and format), and nothing (spaces, aligned) when it has no value.</item>
/// </list>
/// An object or a struct takes no format: a format there is one ChipSharp does not write yet.
/// </summary>
internal sealed partial class PrintedTextFlow
{
    /// <summary>True for a value type whose box a format takes as a hole of its own: a struct, a nullable of a hole's type.</summary>
    private bool IsValueHole(IlType type)
    {
        var plain = StackArrays.Primitive(ValueTypes.WithoutModifiers(type));
        return Nullables.ValueOf(plain) is { } inner
            ? !Nullables.IsNullable(inner) && IsBoxedHole(inner)
            : Boxing.Of(plain, _method.Assembly) is { Kind: BoxedKind.Struct };
    }

    /// <summary>
    /// The parts of a hole of <paramref name="type"/> when it is a nullable, a struct or an object (see the summary);
    /// null for any other type, which <see cref="HoleParts"/> takes as before.
    /// </summary>
    private (Parts? Parts, Unsupported? Problem)? ObjectHoleParts(IlType type, Value value, IlInstruction instruction,
        int operand, int alignment, string? format)
    {
        var plain = StackArrays.Primitive(ValueTypes.WithoutModifiers(type));
        if (Nullables.ValueOf(plain) is { } inner)
        {
            if (Nullables.IsNullable(inner) || !ValueTypes.TryMapType(plain, _method.Assembly, out _))
            {
                return null;
            }

            var (parts, problem) = HoleParts(inner, value, instruction, operand, alignment, format);
            return problem is not null
                ? (null, problem)
                : (Parts.None.Then(parts!.Pieces.Select(piece => piece with { Nullable = plain })), null);
        }

        if (Boxing.Of(plain, _method.Assembly) is { Kind: BoxedKind.Struct } boxed)
        {
            return string.IsNullOrEmpty(format)
                ? (Parts.None.Then([new PrintedPiece(instruction, operand, IlType.Object, null, alignment, default) { Value = boxed }]), null)
                : (null, Unsupported.Format(format));
        }

        return !FixedText.IsText(plain) && (ObjectTypes.TryMapType(plain, _method.Assembly, out _) || plain is IlArrayType)
            ? ObjectParts(plain, instruction, operand, alignment, format)
            : null;
    }

    /// <summary>
    /// The parts of a value of a composite format that is neither text nor a value boxed for it: an object, of the type
    /// its variable has (the value <paramref name="depth"/> deep on the stack before instruction <paramref name="index"/>),
    /// or object when that is not known.
    /// </summary>
    private (Parts? Parts, Unsupported? Problem) ObjectValueParts(Value value, IlInstruction instruction, int index,
        int depth, int operand, int alignment, string? format)
    {
        if (value is Mixed or Handler or PartArray or SpanText)
        {
            return TextParts(value, instruction, operand, alignment);
        }

        var type = VirtualMethods.StackType(_method, index, depth) is { } known
            && ObjectTypes.TryMapType(StackArrays.Primitive(ValueTypes.WithoutModifiers(known)), _method.Assembly, out _)
                ? StackArrays.Primitive(ValueTypes.WithoutModifiers(known))
                : IlType.Object;
        return ObjectParts(type, instruction, operand, alignment, format);
    }

    /// <summary>One part: an object of a variable of <paramref name="type"/>, written by its ToString.</summary>
    private static (Parts? Parts, Unsupported? Problem) ObjectParts(IlType type, IlInstruction instruction, int operand,
        int alignment, string? format) =>
        string.IsNullOrEmpty(format)
            ? (Parts.None.Then([new PrintedPiece(instruction, operand, IlType.Object, null, alignment, default) { Object = type }]), null)
            : (null, Unsupported.Format(format));
}
