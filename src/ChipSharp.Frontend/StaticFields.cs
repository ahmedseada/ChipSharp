using System.Diagnostics.CodeAnalysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// The static fields a program may read and write (<c>ldsfld</c>, <c>stsfld</c>): those of its own types that are not
/// generic, of a type <see cref="SupportedSubset.TryMapType"/> supports. Each becomes a TIR global.
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

    /// <summary>The TIR type of the global the field becomes, when the field's type is supported.</summary>
    public static bool TryMapType(IlField field, [NotNullWhen(true)] out TirType? tirType)
    {
        if (SupportedSubset.TryMapType(Type(field), out tirType) && tirType != TirType.Void)
        {
            return true;
        }

        tirType = null;
        return false;
    }
}
