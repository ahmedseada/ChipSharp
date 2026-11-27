using System.Diagnostics.CodeAnalysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// Fixed text without a heap: C#'s string literals (<c>"road : 1"</c>, and <c>string.Empty</c>), the text written in
/// the program, which never changes, so it lives in read-only data (flash on a chip) and needs no heap. It may be
/// printed (<c>Console.Write</c>, <c>Console.WriteLine</c>), kept anywhere a string may be, compared, measured and
/// searched. Text built while the program runs (<c>+</c>, <c>$"..."</c>, <c>ToString()</c>) and printed straight away
/// is printed text (see <see cref="PrintedText"/>), which needs no heap either; kept, or made by the methods of string
/// that make new text (<c>Substring</c>, <c>Replace</c>, ...), it is a string on the heap.
/// </summary>
/// <remarks>
/// <para>
/// The representation (see <see cref="Strings"/>): a string value is a ref, null for C#'s null, else the address of a
/// string object; a literal's is a constant object (<see cref="TirModule.AddConstantObject"/>) in read-only data, its
/// header the runtime's string descriptor, then its length in UTF-16 code units ("café".Length is 4) and its chars,
/// 1 byte each when they are all below U+0100, else 2. Each distinct literal is one object, however many places use it:
/// like .NET, which interns literals, two equal literals are the same reference. A string compares with null (and with
/// another string, with <c>(object)a == b</c>, by reference) as the ref it is; <c>==</c> on strings compares their chars.
/// </para>
/// <para>
/// Text is written as UTF-8, on every target, as .NET writes it when the console's encoding is UTF-8 (on a chip, over
/// the UART, a terminal shows it right), by the runtime (src/ChipSharp.Runtime.Shared/chipsharp_console.c). A lone
/// surrogate ("\uD800"), which UTF-8 cannot hold, is U+FFFD (EF BF BD), as .NET's UTF-8 encoder writes it; a high
/// surrogate at the end of one write and the low one at the start of the next are one character, as in .NET, whose
/// console writer keeps the first half until the second comes. A Windows console shows non-ASCII text right when its
/// code page is UTF-8 (<c>chcp 65001</c>); .NET itself writes in the console's code page, so there, for non-ASCII text,
/// the bytes differ.
/// </para>
/// <para>
/// A reference to a string (<c>ref string</c>, <c>out string</c>) is not supported yet.
/// </para>
/// </remarks>
internal static class FixedText
{
    /// <summary>
    /// The TIR type of a parameter, local, return type, field or element of type <paramref name="type"/>: ref for a
    /// string (custom modifiers looked through), an object (see <see cref="Strings"/>).
    /// </summary>
    public static bool TryMapType(IlType type, [NotNullWhen(true)] out TirType? tirType)
    {
        ArgumentNullException.ThrowIfNull(type);
        tirType = IsText(type) ? TirType.Ref : null;
        return tirType is not null;
    }

    /// <summary>True for <c>string</c>, custom modifiers looked through.</summary>
    public static bool IsText(IlType type) => ValueTypes.WithoutModifiers(type) == IlType.String;

    /// <summary>
    /// True for <c>string.Empty</c>, the field <c>ldsfld</c> reads for it: it is the literal "", as in .NET.
    /// </summary>
    public static bool IsEmpty(IlFieldReference field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field is { Name: "Empty", IsDefinedInThisAssembly: false }
            && IsSystem(field.DeclaringType, "String");
    }

    /// <summary>
    /// True for a library method that builds or reads text, which needs the heap: a method, property, operator or
    /// constructor of string (<c>string.Concat</c> is what <c>+</c> calls, <c>op_Equality</c> what <c>==</c> calls)
    /// or of StringBuilder, the methods C# builds a <c>$"..."</c> with, any <c>ToString</c>, and the overloads of
    /// Console.Write and Console.WriteLine that format (<c>Console.WriteLine("{0} {1}", a, b)</c>).
    /// </summary>
    public static bool IsBuildingText(IlMethodReference method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method.Definition is not null)
        {
            return false;
        }

        var type = method.DeclaringType;
        return IsSystem(type, "String") || type is IlNamedType { Namespace: "System.Text", Name: "StringBuilder" }
            || Analysis.Unsupported.IsInterpolatedStringHandler(type)
            || method.Name == "ToString"
            || (IsSystem(type, "Console") && method.Name is "Write" or "WriteLine"
                && method.ParameterTypes is [var first, _, ..] && IsText(first));
    }

    private static bool IsSystem(IlType type, string name) =>
        type is IlNamedType { Namespace: "System", DeclaringType: null, IsDefinedInThisAssembly: false } named
        && named.Name == name;
}
