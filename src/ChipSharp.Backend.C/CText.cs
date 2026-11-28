using System.Globalization;
using System.Text;
using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>How names, numbers and bytes are spelled in the C the generator writes.</summary>
internal static class CText
{
    /// <summary>
    /// The words a name the generator makes must not be: C99's keywords (and C11's, and the compilers' own), and the names
    /// &lt;stdint.h&gt; and &lt;stddef.h&gt; define, which the file includes.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum", "extern",
        "float", "for", "goto", "if", "inline", "int", "long", "register", "restrict", "return", "short", "signed",
        "sizeof", "static", "struct", "switch", "typedef", "union", "unsigned", "void", "volatile", "while", "asm",
        "typeof", "bool", "true", "false", "alignas", "alignof", "nullptr", "static_assert", "thread_local",
        "constexpr", "NULL", "offsetof", "size_t", "ptrdiff_t", "wchar_t", "max_align_t", "intptr_t", "uintptr_t",
        "intmax_t", "uintmax_t", "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t",
        "uint64_t", "reentrant", "interrupt", "critical", "naked", "code", "data", "near", "far",
    };

    /// <summary>
    /// A symbol as the llvm code generator makes it from a display name: every character outside <c>[A-Za-z0-9_]</c>
    /// becomes <c>_</c>, and a name that starts with a digit gets a <c>_</c> in front. "ChipSharp.Runtime.WriteLineInt32"
    /// becomes "ChipSharp_Runtime_WriteLineInt32": the runtime's functions have these names in every backend.
    /// </summary>
    public static string Symbol(string text)
    {
        var name = new StringBuilder(text.Length + 1);
        if (text.Length > 0 && char.IsAsciiDigit(text[0]))
        {
            name.Append('_');
        }

        foreach (var c in text)
        {
            name.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }

        return name.ToString();
    }

    /// <summary>
    /// A name of the generator's own choosing made from <paramref name="text"/> (a C# name): <see cref="Symbol"/>, then
    /// made safe for C, where a name that starts with an underscore is the compiler's (it gets <paramref name="prefix"/>
    /// in front, e.g. "&lt;text 0&gt;" becomes "cs_text_0_") and a keyword cannot be one (it gets an underscore after it).
    /// </summary>
    public static string Identifier(string text, string prefix = "cs")
    {
        var name = Symbol(text);
        if (name.Length == 0 || name[0] == '_')
        {
            name = prefix + (name.StartsWith('_') ? name : "_" + name);
        }

        return IsReserved(name) ? name + "_" : name;
    }

    /// <summary>True for a keyword or a name the included headers define.</summary>
    public static bool IsReserved(string name) => Reserved.Contains(name);

    public static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A whole number of any value as a C constant expression: decimal, and the one value C cannot write as a literal
    /// (long.MinValue, whose magnitude no signed type holds) as <c>(-9223372036854775807 - 1)</c>.
    /// </summary>
    public static string SignedInteger(long value) =>
        value == long.MinValue ? "(-9223372036854775807 - 1)" : Integer(value);

    /// <summary>
    /// A float (f32) or double (f64) constant, exactly: a whole number below 2^24 (a float) or 2^53 (a double) in decimal
    /// (<c>3.0f</c>, <c>-2.0</c>), any other finite value in C99's hexadecimal form, which says its bits and leaves the
    /// compiler no rounding to do, with the decimal value after it (<c>0x1.99999ap-4f /* 0.1 */</c>); infinities and NaN
    /// as divisions by zero (<c>(1.0f / 0.0f)</c>, <c>(-(0.0 / 0.0))</c> for .NET's NaN, whose sign bit is set), which
    /// a compiler works out with IEEE arithmetic.
    /// </summary>
    public static string Float(double value, bool isFloat)
    {
        var suffix = isFloat ? "f" : "";
        if (double.IsNaN(value))
        {
            var bits = isFloat ? BitConverter.SingleToInt32Bits((float)value) : BitConverter.DoubleToInt64Bits(value);
            return bits < 0 ? $"(-(0.0{suffix} / 0.0{suffix}))" : $"(0.0{suffix} / 0.0{suffix})";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? $"(1.0{suffix} / 0.0{suffix})" : $"(-1.0{suffix} / 0.0{suffix})";
        }

        var whole = isFloat ? 1 << 24 : 1L << 53;
        if (value == Math.Floor(value) && Math.Abs(value) < whole)
        {
            var sign = value < 0 || (value == 0 && double.IsNegative(value)) ? "-" : "";
            // as a long: a double's custom format "0" keeps only 15 digits (4503599627370496 would be 4503599627370500)
            return $"{sign}{((long)Math.Abs(value)).ToString(CultureInfo.InvariantCulture)}.0{suffix}";
        }

        var hex = isFloat ? HexFloat((float)value) : HexDouble(value);
        var text = isFloat
            ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
        return $"{hex}{suffix} /* {text} */";
    }

    /// <summary>A finite, nonzero double in C99's hexadecimal form, e.g. 0.1 as <c>0x1.999999999999ap-4</c>.</summary>
    private static string HexDouble(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        return Hex(bits < 0, exponent == 0 ? 0 : 1, mantissa, 13, exponent == 0 ? -1022 : exponent - 1023);
    }

    /// <summary>A finite, nonzero float in C99's hexadecimal form, e.g. 0.1f as <c>0x1.99999ap-4</c>.</summary>
    private static string HexFloat(float value)
    {
        var bits = BitConverter.SingleToInt32Bits(value);
        var exponent = (bits >> 23) & 0xFF;
        var mantissa = (long)(bits & 0x7FFFFF) << 1; // 24 bits: six hexadecimal digits
        return Hex(bits < 0, exponent == 0 ? 0 : 1, mantissa, 6, exponent == 0 ? -126 : exponent - 127);
    }

    private static string Hex(bool negative, int lead, long mantissa, int digits, int exponent)
    {
        var fraction = mantissa.ToString("x" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            .TrimEnd('0');
        return string.Create(CultureInfo.InvariantCulture,
            $"{(negative ? "-" : "")}0x{lead}{(fraction.Length > 0 ? "." + fraction : "")}p{(exponent < 0 ? "-" : "+")}" +
            $"{Math.Abs(exponent)}");
    }

    /// <summary>
    /// Bytes as the initializer of a C array, sixteen to a line after the first: <c>{ 104, 105 }</c>. Each line after
    /// the first starts with <paramref name="indent"/>.
    /// </summary>
    public static string Bytes(IReadOnlyList<byte> bytes, string indent)
    {
        var text = new StringBuilder("{ ");
        for (var i = 0; i < bytes.Count; i++)
        {
            if (i > 0)
            {
                text.Append(i % 16 == 0 ? ",\n" + indent : ", ");
            }

            text.Append(bytes[i].ToString(CultureInfo.InvariantCulture));
        }

        return text.Append(" }").ToString();
    }

    /// <summary>
    /// Text for a C comment: printable ASCII stays, any other character becomes <c>?</c>, and what would end the comment
    /// or start a trigraph is broken up.
    /// </summary>
    public static string Comment(string text)
    {
        var safe = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            safe.Append(c is >= ' ' and < (char)0x7F ? c : '?');
        }

        return safe.ToString().Replace("*/", "* /", StringComparison.Ordinal).Replace("??", "? ?", StringComparison.Ordinal);
    }

    /// <summary>
    /// The bytes of fixed text as a comment, when they are printable ASCII (a string literal's): <c>"Hello"</c>; null
    /// for any other bytes.
    /// </summary>
    public static string? TextOf(IReadOnlyList<byte> bytes)
    {
        var count = bytes.Count > 0 && bytes[^1] == 0 ? bytes.Count - 1 : bytes.Count;
        if (count == 0 || bytes.Take(count).Any(b => b is < 0x20 or >= 0x7F))
        {
            return null;
        }

        return "\"" + Comment(Encoding.ASCII.GetString([.. bytes.Take(count)])) + "\"";
    }

    /// <summary>The value of an integer or bool constant, read as unsigned in <paramref name="bits"/> bits.</summary>
    public static ulong Unsigned(TirConst constant, int bits) =>
        bits >= 64 ? unchecked((ulong)constant.IntegerValue) : unchecked((ulong)constant.IntegerValue) & ((1UL << bits) - 1);
}
