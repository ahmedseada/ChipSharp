using System.Buffers.Binary;

namespace ChipSharp.Frontend;

/// <summary>
/// Strings: C#'s <c>string</c> as an object, the same for a literal in flash and for text the program builds on the
/// heap, with .NET's meaning exactly: <c>Length</c> and <c>s[i]</c> count UTF-16 code units, and two strings compare
/// char by char (ordinal).
/// </summary>
/// <remarks>
/// <para>
/// <b>The value.</b> A string (on IL's evaluation stack, in a local, a parameter, a return value, a field of an object or
/// of a struct, a static field, an element of an array) is a <c>ref</c>: null, or the address of a string object, which
/// the garbage collector follows like any other reference. A literal is an object outside the heap (in flash on a chip:
/// <see cref="Ir.TirModule.AddConstantObject"/>), which the collector never marks; built text is an object on the heap.
/// Code never needs to know which: both are laid out the same way.
/// </para>
/// <para>
/// <b>The object: compact strings.</b> A string object is laid out as an array object (see
/// <see cref="Ir.TirLayout"/>): its header, the address of its type's descriptor, then its <c>Length</c> (an i32, in
/// UTF-16 code units), then from <see cref="Ir.TirLayout.ArrayDataOffset"/> its characters, in one of two forms:
/// <code>
/// "string"            1 byte per char, when every char of the text is below U+0100 (Latin-1: ASCII, é, ü, ...)
/// "string (UTF-16)"   2 bytes per char (little-endian UTF-16 code units, surrogates as they are) for any other text
/// </code>
/// Each form has its own descriptor (an array type of i8 and of i16 elements, so the collector sizes a string like
/// any array), which the runtime defines (<c>chipsharp_string_type</c> and <c>chipsharp_wide_string_type</c> in
/// src/ChipSharp.Runtime.Shared/chipsharp_string.c) so that its C makes strings too. The form is canonical: a string is
/// stored 1 byte per char exactly when every char is below U+0100, so two equal strings have the same form. On the
/// Pico a literal takes 8 bytes and then 1 byte per char ("road : 1" takes 16), as many as UTF-8 for ASCII and fewer for
/// Latin-1 letters; a string on the heap takes the same, rounded up to 8 bytes. Text is written as UTF-8 wherever it
/// goes, as .NET writes it with a UTF-8 console.
/// </para>
/// <para>
/// <b>Spans of chars.</b> A <c>ReadOnlySpan&lt;char&gt;</c> views 2-byte chars, so <c>s.AsSpan()</c> views a UTF-16
/// string where it is, and a 1-byte string through a UTF-16 copy of it on the heap (which a target without a heap does
/// not have).
/// </para>
/// </remarks>
internal static class Strings
{
    /// <summary>The name of the descriptor of strings stored 1 byte per char.</summary>
    public const string NarrowName = "string";

    /// <summary>The name of the descriptor of strings stored 2 bytes per char.</summary>
    public const string WideName = "string (UTF-16)";

    /// <summary>The runtime's descriptor of strings stored 1 byte per char.</summary>
    public const string NarrowSymbol = "chipsharp_string_type";

    /// <summary>The runtime's descriptor of strings stored 2 bytes per char.</summary>
    public const string WideSymbol = "chipsharp_wide_string_type";

    /// <summary>True when <paramref name="text"/> is stored 1 byte per char: every char is below U+0100.</summary>
    public static bool IsNarrow(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.All(c => c < 0x100);
    }

    /// <summary>The bytes of the chars of <paramref name="text"/> in its form: 1 byte each, or 2 (little-endian).</summary>
    public static byte[] Elements(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsNarrow(text))
        {
            return [.. text.Select(c => (byte)c)];
        }

        var bytes = new byte[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), text[i]);
        }

        return bytes;
    }
}
