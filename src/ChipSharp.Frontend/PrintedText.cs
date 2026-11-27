using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// Printed text without a heap: text the program builds and prints straight away, <c>Console.WriteLine($"road :
/// {lane}")</c>, <c>Console.Write("road : " + lane)</c>, <c>Console.WriteLine(lane.ToString())</c>. No string is ever
/// made: each part of the text is written where the text is printed, one after another, with the Console.Write
/// functions of fixed text and numbers (see <see cref="FixedText"/>), so the output is byte for byte what .NET prints.
/// </summary>
/// <remarks>
/// <para>
/// The IL C# makes for such text, in Release and in Debug alike:
/// <list type="bullet">
/// <item><c>$"..."</c> with a hole that is not text: a <c>DefaultInterpolatedStringHandler</c> local, its constructor,
/// <c>AppendLiteral</c> for each piece of literal text, <c>AppendFormatted</c> (<c>&lt;T&gt;(T)</c>, with an alignment,
/// a format or both, and the overloads of string) for each hole, then <c>ToStringAndClear</c>. Always that for more
/// than four parts;</item>
/// <item><c>+</c>, and <c>$"..."</c> whose holes are all text: <c>string.Concat</c> of two to four strings, or of a
/// <c>string[]</c> (<c>newarr</c>, then <c>dup</c>, the index, the part, <c>stelem.ref</c> for each) for more; a
/// number or bool among them is its <c>ToString()</c> (<c>ldarga x</c>, <c>call Int32::ToString()</c>), and a lone
/// part is followed by <c>?? ""</c> (<c>dup</c>, <c>brtrue</c>, <c>pop</c>, <c>ldstr ""</c>);</item>
/// <item><c>x.ToString()</c> and <c>x.ToString("X4")</c> of a number or bool.</item>
/// </list>
/// .NET evaluates every hole, side effects and all, while it builds the text, and prints only at the end. So does
/// ChipSharp: where the IL hands a hole's value to the text, the translation keeps it in a slot of its own, and the
/// <c>Console.Write</c> or <c>Console.WriteLine</c> that prints the text writes the literal text and those slots one
/// after another. A hole that prints something itself prints first, as in .NET.
/// </para>
/// <para>
/// A hole prints as <c>Console.Write</c> does: text (null as nothing), every whole number, bool, float and double. An
/// alignment pads with spaces like .NET, on the left (<c>{x,5}</c>) or the right (<c>{x,-5}</c>), to the length .NET
/// counts (UTF-16 code units, for text). The formats are those written exactly like .NET: D and D&lt;n&gt;, X and
/// X&lt;n&gt; (x for lowercase hex digits, of the value's own width, so -1 is FF for an sbyte) and G for whole
/// numbers, G and R for floats and doubles; a format of text or of a bool is ignored, as .NET ignores it. The rest is
/// rejected, with what to write for now: other formats, an aligned float, an enum (.NET prints its name), a struct.
/// </para>
/// <para>
/// Keeping the text needs the heap (M3): built text that is stored in a variable or a field, returned, passed to
/// anything but Console.Write and Console.WriteLine, measured (<c>.Length</c>), or one of two texts where ways join
/// (<c>c ? $"a{x}" : "b"</c>) is rejected where that happens. A string local written once and read once is no keeping:
/// it is what C#'s own temporaries are (Debug's, and those around a <c>switch</c> expression), and what Release makes
/// of <c>string s = $"..."; Console.WriteLine(s);</c>, leaving the variable out.
/// </para>
/// <para>
/// The analysis follows what each value on IL's evaluation stack can be (see <see cref="PrintedTextFlow"/>), so the
/// parts of a text may come in on several ways (<c>"lane " + lane + (cut ? " cut" : "")</c>): the whole-program pass
/// takes its verdicts, the translation its plan, so the two always agree.
/// </para>
/// </remarks>
internal sealed class PrintedText
{
    private PrintedText(IReadOnlyDictionary<IlInstruction, PrintedRole> roles,
        IReadOnlyDictionary<IlInstruction, Unsupported?> verdicts, IReadOnlyDictionary<IlInstruction, PrintSite> prints,
        IReadOnlySet<int> handlerLocals, IReadOnlyDictionary<IlInstruction, PrintSite>? builds = null)
    {
        Roles = roles;
        Verdicts = verdicts;
        Prints = prints;
        Builds = builds ?? new Dictionary<IlInstruction, PrintSite>();
        HandlerLocals = handlerLocals;
        var saved = new Dictionary<(IlInstruction, int), PrintedPiece>();
        foreach (var piece in prints.Values.Concat(Builds.Values).SelectMany(site => site.Pieces).Where(piece => piece.Literal is null))
        {
            saved[(piece.Source, piece.Operand)] = piece;
        }

        Saved = saved;
    }

    /// <summary>What each instruction that builds, prints or tests printed text does for it.</summary>
    public IReadOnlyDictionary<IlInstruction, PrintedRole> Roles { get; }

    /// <summary>
    /// The verdict on every instruction printed text is about: null when it is fine, else what is not supported (an
    /// instruction that keeps built text, an unsupported hole). An instruction that is not in it has nothing to do
    /// with printed text.
    /// </summary>
    public IReadOnlyDictionary<IlInstruction, Unsupported?> Verdicts { get; }

    /// <summary>Each Console.Write and Console.WriteLine of built text, with what it writes.</summary>
    public IReadOnlyDictionary<IlInstruction, PrintSite> Prints { get; }

    /// <summary>
    /// The built text that is kept (on a target with a heap): the instruction that finishes it (ToStringAndClear, a
    /// Concat, a ToString), where the string is made from its parts, with them.
    /// </summary>
    public IReadOnlyDictionary<IlInstruction, PrintSite> Builds { get; }

    /// <summary>
    /// The values a print writes, by the instruction that takes them off the stack and their operand number there
    /// (0 is the deepest): each is kept in a slot until the print.
    /// </summary>
    public IReadOnlyDictionary<(IlInstruction Source, int Operand), PrintedPiece> Saved { get; }

    /// <summary>The locals C# builds a <c>$"..."</c> in (a DefaultInterpolatedStringHandler): only their address is used.</summary>
    public IReadOnlySet<int> HandlerLocals { get; }

    /// <summary>The printed text of <paramref name="method"/>.</summary>
    public static PrintedText Of(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var handlers = method.Body?.Locals.Where(local => Unsupported.IsInterpolatedStringHandler(local.Type))
            .Select(local => local.Index).ToHashSet() ?? [];
        if (method.Body is not { Instructions.Count: > 0 } body)
        {
            return new PrintedText(new Dictionary<IlInstruction, PrintedRole>(),
                new Dictionary<IlInstruction, Unsupported?>(), new Dictionary<IlInstruction, PrintSite>(), handlers);
        }

        var flow = new PrintedTextFlow(method, body, handlers).Run();
        return new PrintedText(flow.Roles, flow.Verdicts, flow.Prints, handlers, flow.Builds);
    }

    /// <summary>True for an instruction printed text is about (see <see cref="Verdicts"/>).</summary>
    public bool Owns(IlInstruction instruction) => Verdicts.ContainsKey(instruction);

    /// <summary>
    /// The format <paramref name="format"/> of a hole of <paramref name="type"/> (a whole number, bool, float, double
    /// or text) as ChipSharp writes it: null, empty and G for every type, R for floats and doubles; D, D&lt;n&gt;, X,
    /// X&lt;n&gt;, x and x&lt;n&gt; for whole numbers (n up to 999999999, as .NET takes it); anything for a bool or text,
    /// whose format .NET ignores. False for a format ChipSharp does not write.
    /// </summary>
    public static bool TryParseFormat(string? format, IlPrimitiveKind type, out PrintedFormat parsed)
    {
        parsed = default;
        if (type is IlPrimitiveKind.Bool or IlPrimitiveKind.String or IlPrimitiveKind.Char || string.IsNullOrEmpty(format)
            || format == "G")
        {
            return true;
        }

        if (IsFloat(type))
        {
            return format == "R";
        }

        if (format == "g")
        {
            return true;
        }

        var kind = format[0] switch
        {
            'D' or 'd' => PrintedFormatKind.Decimal,
            'X' => PrintedFormatKind.UpperHex,
            'x' => PrintedFormatKind.LowerHex,
            _ => PrintedFormatKind.None,
        };
        var digits = format.AsSpan(1);
        if (kind == PrintedFormatKind.None || digits.Length > 9 || digits.ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        parsed = new PrintedFormat(kind, digits.Length == 0 ? 0 : int.Parse(digits, CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>
    /// The primitive kind of a hole's type when Console.Write prints it (bool, char, every whole number but nint and
    /// nuint, float, double, string); false for any other type.
    /// </summary>
    public static bool TryGetPrinted(IlType type, out IlPrimitiveKind kind)
    {
        kind = (ValueTypes.WithoutModifiers(type) as IlPrimitiveType)?.Kind ?? IlPrimitiveKind.Void;
        return kind is IlPrimitiveKind.Bool or IlPrimitiveKind.String or IlPrimitiveKind.Char or IlPrimitiveKind.Float32
            or IlPrimitiveKind.Float64 || IsWholeNumber(kind);
    }

    /// <summary>True for sbyte to ulong (not nint and nuint, which Console.Write does not print).</summary>
    public static bool IsWholeNumber(IlPrimitiveKind kind) => kind is >= IlPrimitiveKind.Int8 and <= IlPrimitiveKind.UInt64;

    public static bool IsFloat(IlPrimitiveKind kind) => kind is IlPrimitiveKind.Float32 or IlPrimitiveKind.Float64;

    /// <summary>True for sbyte, short, int and long.</summary>
    public static bool IsSigned(IlPrimitiveKind kind) =>
        kind is IlPrimitiveKind.Int8 or IlPrimitiveKind.Int16 or IlPrimitiveKind.Int32 or IlPrimitiveKind.Int64;

    /// <summary>How many bits a whole number has: 8 for sbyte and byte, ..., 64 for long and ulong.</summary>
    public static int Bits(IlPrimitiveKind kind) => kind switch
    {
        IlPrimitiveKind.Int8 or IlPrimitiveKind.UInt8 => 8,
        IlPrimitiveKind.Int16 or IlPrimitiveKind.UInt16 => 16,
        IlPrimitiveKind.Int32 or IlPrimitiveKind.UInt32 => 32,
        _ => 64,
    };

    /// <summary>
    /// <paramref name="text"/> aligned as .NET aligns a hole: padded with spaces on the left to
    /// <paramref name="alignment"/> characters, or on the right to -<paramref name="alignment"/>.
    /// </summary>
    public static string Align(string text, int alignment) =>
        alignment >= 0 ? text.PadLeft(alignment) : text.PadRight(-alignment);

    /// <summary>True for a method of System.String, System.Console, ... (of the library, not of the program).</summary>
    public static bool IsSystem(IlMethodReference method, string type) =>
        method is { Definition: null, DeclaringType: IlNamedType { Namespace: "System", DeclaringType: null } named }
        && named.Name == type && !named.IsDefinedInThisAssembly;

    /// <summary>
    /// The type <paramref name="method"/> is a ToString of: <c>x.ToString()</c> (or <c>x.ToString(format)</c>) of a
    /// primitive (bool, char, a number), called on the value's address.
    /// </summary>
    public static bool IsToString(IlMethodReference method, [NotNullWhen(true)] out IlPrimitiveType? type)
    {
        ArgumentNullException.ThrowIfNull(method);
        type = method is { Name: "ToString", HasThis: true, Definition: null, GenericArguments.Count: 0 }
            && method.ParameterTypes is [] or [IlPrimitiveType { Kind: IlPrimitiveKind.String }]
            && StackArrays.Primitive(method.DeclaringType) is IlPrimitiveType
            {
                Kind: IlPrimitiveKind.Bool or IlPrimitiveKind.Char or (>= IlPrimitiveKind.Int8 and <= IlPrimitiveKind.Float64),
            } primitive
                ? primitive
                : null;
        return type is not null;
    }
}

/// <summary>What an instruction does for printed text (see <see cref="PrintedText"/>).</summary>
internal enum PrintedRole
{
    /// <summary><c>ldloca</c> of a DefaultInterpolatedStringHandler: the text being built.</summary>
    Handler,

    /// <summary>The handler's constructor: a new text.</summary>
    Start,

    /// <summary><c>AppendLiteral</c> or <c>AppendFormatted</c>: a part.</summary>
    Append,

    /// <summary><c>ToStringAndClear</c>: the handler's text, done.</summary>
    Finish,

    /// <summary><c>x.ToString()</c> of a number or bool: a text of one part, the value read through its address.</summary>
    ToString,

    /// <summary><c>string.Concat</c>: the parts of its strings, one after another.</summary>
    Concat,

    /// <summary><c>newarr string</c> for <c>string.Concat(string[])</c>.</summary>
    PartArray,

    /// <summary><c>stelem.ref</c> into such an array: a part.</summary>
    ArrayPart,

    /// <summary><c>Console.Write(string)</c> or <c>Console.WriteLine(string)</c> of built text: where it is written.</summary>
    Print,

    /// <summary>
    /// <c>ldstr</c> (or <c>ldnull</c>) right before the instruction that takes it as a literal part or a format: the
    /// text is known from the IL, so the value is never needed.
    /// </summary>
    Text,

    /// <summary><c>brtrue</c> or <c>brfalse</c> on built text (C#'s <c>?? ""</c>), which is never null.</summary>
    NullTest,

    /// <summary><c>constrained.</c> before an enum's ToString (<c>color.ToString()</c>): nothing of its own.</summary>
    Prefix,

    /// <summary><c>new ReadOnlySpan&lt;char&gt;(ref c)</c> for a <c>string.Concat</c> of spans: a char, read through its address.</summary>
    CharSpan,

    /// <summary><c>string.op_Implicit</c>, text as a span for a <c>string.Concat</c> of spans: its parts.</summary>
    SpanText,

    /// <summary><c>box</c> of a hole of a format (<c>Console.WriteLine(color)</c>, <c>string.Format("{0}", x)</c>): a part.</summary>
    Box,
}

/// <summary>How a whole number is written: as Console.Write writes it, in decimal with at least n digits, or in hex.</summary>
internal enum PrintedFormatKind
{
    None,
    Decimal,
    UpperHex,
    LowerHex,
}

/// <summary>A hole's format: its kind, and its precision (the least number of digits; 0 when it has none).</summary>
internal readonly record struct PrintedFormat(PrintedFormatKind Kind, int Precision);

/// <summary>
/// One part of a printed text, in the order it is written: literal text (<paramref name="Literal"/>, aligned already),
/// or a value of <paramref name="Type"/>, the one <paramref name="Source"/> takes off the stack as its operand
/// <paramref name="Operand"/> (0 is the deepest: the address a ToString is called on, the handler, ...).
/// </summary>
internal sealed record PrintedPiece(
    IlInstruction Source,
    int Operand,
    IlPrimitiveType Type,
    string? Literal,
    int Alignment,
    PrintedFormat Format)
{
    /// <summary>For an enum's value, written by its name (see <see cref="Lowering.EnumNameFunctions"/>): the enum; <see cref="Type"/> is its underlying type.</summary>
    public IlTypeDefinition? Enum { get; init; }
}

/// <summary>What a Console.Write or Console.WriteLine of built text writes: its parts, then the end of a line for WriteLine.</summary>
internal sealed record PrintSite(IReadOnlyList<PrintedPiece> Pieces, bool Line);
