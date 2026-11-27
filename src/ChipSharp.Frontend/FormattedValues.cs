using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// Library structs that printed text writes with a format, with no heap: DateTime, DateOnly and TimeOnly
/// (ChipSharp.Library's). A hole of one (<c>$"{now:HH:mm:ss}"</c>, <c>$"{today,12:d}"</c>), its box for a composite
/// format (<c>Console.WriteLine(now)</c>, <c>Console.WriteLine("{0:t}", now)</c>) and a call of its ToString
/// (<c>now.ToString("yyyy-MM-dd")</c>, <c>now.ToShortTimeString()</c>) are a part of the text (see
/// <see cref="PrintedText"/> and <see cref="PrintedPiece.Formatted"/>), the format known from the IL.
/// </summary>
/// <remarks>
/// <para>
/// A substitute struct is formatted text when it has the two methods the translation calls for such a part, with the
/// value's address as <c>this</c> and the format as a constant span of its chars (see StringMethods.LiteralSpan):
/// <c>internal void Print(ReadOnlySpan&lt;char&gt; format, int alignment, bool toString)</c>, which printed text calls
/// where the text is printed (it writes to the console a few chars at a time, no string made), and
/// <c>internal string Text(ReadOnlySpan&lt;char&gt; format, int alignment, bool toString)</c>, which kept text calls (a
/// string, on the heap). <c>toString</c> is true for a part that is the struct's ToString, false for a hole: .NET writes
/// a hole with TryFormat, whose refusals of a bad format say other things than ToString's.
/// </para>
/// <para>
/// The whole-program pass reaches Print for every printed part and Text for every kept one
/// (<see cref="Reached"/>), and not the ToString that printed text writes itself, so a program that only prints dates
/// needs no heap.
/// </para>
/// </remarks>
internal static class FormattedValues
{
    /// <summary>The substitute of <paramref name="type"/> when it is formatted text (see the summary); null for any other type.</summary>
    public static IlTypeDefinition? Of(IlType type, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(assembly);
        return ValueTypes.Definition(StackArrays.Primitive(ValueTypes.WithoutModifiers(type)), assembly) is { } definition
            && definition.Assembly == LibrarySubstitutes.Assembly && Print(definition) is not null
                ? definition
                : null;
    }

    /// <summary>The struct's <c>Print</c>, or null.</summary>
    public static IlMethod? Print(IlTypeDefinition type) => Find(type, "Print");

    /// <summary>The struct's <c>Text</c>, or null.</summary>
    public static IlMethod? Text(IlTypeDefinition type) => Find(type, "Text");

    /// <summary>
    /// When <paramref name="callee"/> is a ToString of formatted text: the struct, whether it takes the format as its
    /// argument (<c>ToString(string)</c>), and the format it stands for when not (null for <c>ToString()</c>, "D" for
    /// <c>ToLongDateString()</c>, ...). Null for any other method.
    /// </summary>
    public static (IlTypeDefinition Type, bool TakesFormat, string? Format)? ToStringOf(IlMethodReference callee)
    {
        ArgumentNullException.ThrowIfNull(callee);
        if (callee is not { HasThis: true, GenericArguments.Count: 0, Definition: { } definition }
            || definition.DeclaringType.Assembly != LibrarySubstitutes.Assembly || Print(definition.DeclaringType) is null)
        {
            return null;
        }

        var type = definition.DeclaringType;
        return (definition.Name, callee.ParameterTypes) switch
        {
            ("ToString", []) => (type, false, null),
            ("ToString", [IlPrimitiveType { Kind: IlPrimitiveKind.String }]) => (type, true, null),
            ("ToLongDateString", []) => (type, false, "D"),
            ("ToLongTimeString", []) => (type, false, "T"),
            ("ToShortDateString", []) => (type, false, "d"),
            ("ToShortTimeString", []) => (type, false, "t"),
            _ => null,
        };
    }

    /// <summary>
    /// True when <paramref name="method"/> may write formatted text: it calls a method of such a struct, an instance for
    /// one (<c>AppendFormatted&lt;DateTime&gt;</c>), or boxes one. Only then is its printed text looked at.
    /// </summary>
    public static bool MayWrite(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method.Body?.Instructions.Any(instruction => instruction.Operand switch
        {
            IlMethodOperand { Method: var callee } => ToStringOf(callee) is not null
                || callee.GenericArguments.Any(argument => Of(argument, method.Assembly) is not null),
            IlTypeOperand { Type: var type } when instruction.OpCode == IlOpCode.Box => Of(type, method.Assembly) is not null,
            _ => false,
        }) ?? false;
    }

    /// <summary>
    /// What the printed and kept text of <paramref name="method"/> runs of formatted text: Print for each printed part,
    /// Text for each kept one; and the calls of a ToString that printed text writes itself, which run nothing.
    /// </summary>
    public static (IEnumerable<IlMethod> Methods, IReadOnlySet<IlInstruction> Written) Reached(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!MayWrite(method))
        {
            return ([], new HashSet<IlInstruction>());
        }

        var printed = PrintedText.Of(method);
        var methods = printed.Prints.Values.SelectMany(site => site.Pieces).Select(piece => piece.Formatted)
            .OfType<IlTypeDefinition>().Select(type => Print(type)!)
            .Concat(printed.Builds.Values.SelectMany(site => site.Pieces).Select(piece => piece.Formatted)
                .OfType<IlTypeDefinition>().Select(type => Text(type)!));
        var written = printed.Roles.Where(role => role.Value == PrintedRole.ToString
                && role.Key.Operand is IlMethodOperand { Method: var callee } && ToStringOf(callee) is not null)
            .Select(role => role.Key).ToHashSet();
        return (methods.Distinct(), written);
    }

    private static IlMethod? Find(IlTypeDefinition type, string name) => type.Methods.FirstOrDefault(method =>
        method.Name == name && !method.IsStatic && method.Parameters.Count == 3);
}
