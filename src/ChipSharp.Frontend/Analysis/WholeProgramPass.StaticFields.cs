using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

public static partial class WholeProgramPass
{
    /// <summary>
    /// Why ChipSharp cannot compile the static field that <paramref name="instruction"/> (an <c>ldsfld</c>,
    /// <c>stsfld</c> or <c>ldsflda</c>) reads, writes or takes the address of, or null, also for any other instruction.
    /// ChipSharp compiles the static fields of the program's own types that are not generic, of the types <see cref="ValueTypes.TryMapType"/>
    /// has, and of fixed text (<c>static readonly string</c>); a field of another type is that type's problem, e.g.
    /// classes and objects for a <c>static object</c>. <c>string.Empty</c> is the literal "".
    /// </summary>
    private static Unsupported? ForStaticField(IlInstruction instruction)
    {
        if (instruction is not
            { OpCode: IlOpCode.Ldsfld or IlOpCode.Stsfld or IlOpCode.Ldsflda, Operand: IlFieldOperand { Field: var reference } })
        {
            return null;
        }

        if (FixedText.IsEmpty(reference))
        {
            return null;
        }

        if (!reference.IsDefinedInThisAssembly)
        {
            return Unsupported.LibraryField($"{CSharpNames.Type(reference.DeclaringType)}.{reference.Name}");
        }

        if (reference.Definition is not { } field || field.DeclaringType.GenericParameters.Count > 0)
        {
            return Unsupported.GenericTypes;
        }

        if (!field.IsStatic)
        {
            return Unsupported.Fields;
        }

        if (StaticFields.TryMapType(field, out _))
        {
            return null;
        }

        return ForAnyType(StaticFields.Type(field), field.DeclaringType.Assembly) ?? Unsupported.GenericTypes;
    }
}
