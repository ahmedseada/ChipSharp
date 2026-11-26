using System.Globalization;
using System.Text;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>How names, constants and strings are spelled in LLVM IR text.</summary>
internal static class LlvmText
{
    /// <summary>
    /// A name LLVM takes without quotes: every character outside <c>[A-Za-z0-9_]</c> becomes <c>_</c>, and a name
    /// that starts with a digit gets a <c>_</c> in front. "Program.Square" becomes "Program_Square".
    /// </summary>
    public static string Name(string text)
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

    /// <summary>A constant as an operand: <c>42</c>, <c>-1</c>, <c>true</c>, or a float in hex (see <see cref="Float"/>).</summary>
    public static string Constant(TirConst constant) => constant.Type.Kind switch
    {
        TirTypeKind.Bool => constant.IntegerValue != 0 ? "true" : "false",
        TirTypeKind.F32 => Float((float)constant.FloatValue),
        TirTypeKind.F64 => Float(constant.FloatValue),
        _ => Integer(constant.IntegerValue),
    };

    /// <summary>Zero of a type, the value every slot and global starts with: every byte zero.</summary>
    public static string Zero(TirType type) => type.Kind switch
    {
        TirTypeKind.Bool => "false",
        TirTypeKind.F32 or TirTypeKind.F64 => Float(0.0),
        TirTypeKind.Ptr => "null",
        TirTypeKind.Struct or TirTypeKind.Array => "zeroinitializer",
        _ => "0",
    };

    /// <summary>
    /// The exact form LLVM takes for a float or double constant: the 64 bits of the value as a double, in hex,
    /// e.g. 1.5 is <c>0x3FF8000000000000</c>. A float is widened to double first, which is exact.
    /// Decimal text would only be accepted for values it spells exactly.
    /// </summary>
    public static string Float(double value) =>
        "0x" + BitConverter.DoubleToInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture);

    public static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A quoted LLVM string, e.g. for <c>source_filename</c>: printable ASCII stays as it is, and every other
    /// byte of the UTF-8 text (and the quote and backslash) is written as <c>\XX</c>.
    /// </summary>
    public static string Quoted(string text) => Escaped("\"", Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// An array of bytes as an LLVM constant, <c>c"..."</c>, escaped as <see cref="Quoted"/> escapes, e.g.
    /// <c>c"caf\C3\A9"</c>.
    /// </summary>
    public static string Bytes(IEnumerable<byte> bytes) => Escaped("c\"", bytes);

    private static string Escaped(string start, IEnumerable<byte> bytes)
    {
        var quoted = new StringBuilder(start);
        foreach (var b in bytes)
        {
            if (b is >= 0x20 and < 0x7F && b != '"' && b != '\\')
            {
                quoted.Append((char)b);
            }
            else
            {
                quoted.Append('\\').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return quoted.Append('"').ToString();
    }
}
