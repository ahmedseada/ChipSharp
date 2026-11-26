using System.Diagnostics.CodeAnalysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// The static fields a program may read and write (<c>ldsfld</c>, <c>stsfld</c>) and take the address of
/// (<c>ldsflda</c>): those of its own types that are not generic, of a type <see cref="ValueTypes.TryMapType"/> supports
/// (its structs and enums too), or of fixed text (a string, see <see cref="FixedText"/>). Each becomes a TIR global.
/// </summary>
internal static class StaticFields
{
    /// <summary>
    /// The field's type without its custom modifiers: a C# <c>volatile</c> field's type is e.g.
    /// <c>int32 modreq(IsVolatile)</c>. (What makes a read or write volatile is the <c>volatile.</c> prefix on it.)
    /// </summary>
    public static IlType Type(IlField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        var type = field.Type;
        while (type is IlModifiedType modified)
        {
            type = modified.UnmodifiedType;
        }

        return type;
    }

    /// <summary>
    /// The TIR type of the global the field becomes, when the field's type is supported: for an array that the program
    /// assigns while it runs, a ref, the array object (see <see cref="HeapArrays"/>; a static array has a global of its
    /// own, see <see cref="StaticArrays"/>).
    /// </summary>
    public static bool TryMapType(IlField field, [NotNullWhen(true)] out TirType? tirType)
    {
        if ((ValueTypes.TryMapType(Type(field), field.DeclaringType.Assembly, out tirType) && tirType != TirType.Void)
            || FixedText.TryMapType(Type(field), out tirType))
        {
            return true;
        }

        if (StackArrays.TryMapType(Type(field), field.DeclaringType.Assembly, out _))
        {
            tirType = TirType.Ref;
            return true;
        }

        tirType = null;
        return false;
    }
}
