using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// Fixed text without a heap: C#'s string literals (<c>"road : 1"</c>, and <c>string.Empty</c>), the text written in
/// the program, which never changes, so it lives in read-only data (flash on a chip) and needs no heap. It may be
/// printed (<c>Console.Write</c>, <c>Console.WriteLine</c>), kept in locals, parameters, return values and static
/// fields (a <c>const</c> too, which C# puts in at each use), and compared with null. Text built while the program
/// runs (<c>+</c>, <c>$"..."</c>, <c>ToString()</c>) and printed straight away is printed text (see
/// <see cref="PrintedText"/>); kept, or built with <c>string.Format</c> and the other methods of string, it needs the
/// heap: it arrives with M3, and the whole-program pass says so.
/// </summary>
/// <remarks>
/// <para>
/// The representation: a string value (on IL's evaluation stack, in a local, a parameter, a return value or a static
/// field) is a ptr: null for C#'s null, else the address of the literal's record, a constant global
/// (<see cref="TirModule.AddConstant"/>) of <see cref="HeaderSize"/> bytes and then the text:
/// <code>
/// offset 0   u32   the length as .NET counts it, in UTF-16 code units: "café".Length is 4
/// offset 4   u32   how many bytes of UTF-8 follow: "café" takes 5
/// offset 8         the text in UTF-8, without a zero at the end (a literal may hold "\0")
/// </code>
/// Both numbers are little-endian on every target, and the runtime reads them a byte at a time, so the record needs
/// no alignment. Each distinct literal has one record, however many places use it: like .NET, which interns literals,
/// two equal literals are the same reference. A string compares with null (and with another string, by reference,
/// which for literals is what .NET gives too) as the ptr it is.
/// </para>
/// <para>
/// Text is written as UTF-8, on every target: the bytes after the header, as they are. That is what .NET writes when
/// the console's encoding is UTF-8 (on a chip, over the UART, a terminal shows it right). A lone surrogate
/// ("\uD800"), which UTF-8 cannot hold, is U+FFFD (EF BF BD), as .NET's UTF-8 encoder writes it. A Windows console
/// shows non-ASCII text right when its code page is UTF-8 (<c>chcp 65001</c>); .NET itself writes in the console's
/// code page, so there, for non-ASCII text, the bytes differ.
/// </para>
/// <para>
/// Text is not kept in the fields of structs, in arrays or behind references (<c>ref string</c>) yet: those, like a
/// null in place of an array, arrive with the heap.
/// </para>
/// </remarks>
internal static class FixedText
{
    /// <summary>The bytes of a record before its text: the UTF-16 length and the UTF-8 byte count, a u32 each.</summary>
    public const int HeaderSize = 8;

    /// <summary>
    /// The TIR type of a parameter, local, return type or static field of type <paramref name="type"/>: ptr for a
    /// string (custom modifiers looked through).
    /// </summary>
    public static bool TryMapType(IlType type, [NotNullWhen(true)] out TirType? tirType)
    {
        ArgumentNullException.ThrowIfNull(type);
        tirType = IsText(type) ? TirType.Ptr : null;
        return tirType is not null;
    }

    /// <summary>True for <c>string</c>, custom modifiers looked through.</summary>
    public static bool IsText(IlType type) => ValueTypes.WithoutModifiers(type) == IlType.String;

    /// <summary>
    /// The record of <paramref name="text"/>, as described above: its UTF-16 length, its UTF-8 byte count (both
    /// little-endian), then its UTF-8 bytes.
    /// </summary>
    public static byte[] Record(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var utf8 = Encoding.UTF8.GetBytes(text);
        var record = new byte[HeaderSize + utf8.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)text.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)utf8.Length);
        utf8.CopyTo(record, HeaderSize);
        return record;
    }

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
