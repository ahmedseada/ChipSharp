using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// StringBuilder (ChipSharp.Library's substitute, src/ChipSharp.Library/StringBuilder.cs) and the text C# builds into
/// one: <c>sb.Append($"...")</c> and <c>sb.AppendLine($"...")</c>, which C# makes with a
/// <c>StringBuilder.AppendInterpolatedStringHandler</c> (a handler like DefaultInterpolatedStringHandler, whose
/// constructor takes the builder too), and <c>sb.AppendFormat("...", a, b)</c> of a literal format. Printed text
/// follows them (<see cref="PrintedText"/>, Analysis/PrintedTextFlow.StringBuilders.cs): the parts wait in their slots,
/// as for printed text, and the call appends each part, in order, with the substitute's <c>Append(string)</c> (a literal
/// as it is, a number as its text), then <c>AppendLine()</c> for AppendLine. So no string of the whole text is made,
/// as in .NET.
/// </summary>
internal static class StringBuilders
{
    private static readonly IlNamedType Builder = new("System.Text", "StringBuilder", null, "System.Runtime");

    /// <summary>True for System.Text.StringBuilder, as a library type (not its substitute).</summary>
    public static bool IsBuilder(IlType type) =>
        ValueTypes.WithoutModifiers(type) is IlNamedType { FullName: "System.Text.StringBuilder", IsDefinedInThisAssembly: false };

    /// <summary>True for StringBuilder.AppendInterpolatedStringHandler, C#'s handler of <c>sb.Append($"...")</c>.</summary>
    public static bool IsHandler(IlType type) => ValueTypes.WithoutModifiers(type) is IlNamedType
    {
        Name: "AppendInterpolatedStringHandler", DeclaringType: { FullName: "System.Text.StringBuilder" },
        IsDefinedInThisAssembly: false,
    };

    /// <summary>
    /// For <c>sb.Append(ref handler)</c>: false, for <c>sb.AppendLine(ref handler)</c>: true (it ends the line); null for
    /// any other method.
    /// </summary>
    public static bool? AppendsHandler(IlMethodReference callee)
    {
        ArgumentNullException.ThrowIfNull(callee);
        return callee is { Name: "Append" or "AppendLine", HasThis: true, Definition: null, ParameterTypes: [IlByRefType { ElementType: var handler }] }
            && IsBuilder(callee.DeclaringType) && IsHandler(handler)
            ? callee.Name == "AppendLine"
            : null;
    }

    /// <summary>
    /// True for <c>sb.AppendFormat(format, ...)</c> of one to three objects or a <c>params</c> span of them (not of an
    /// IFormatProvider, nor of an <c>object[]</c>).
    /// </summary>
    public static bool IsAppendFormat(IlMethodReference callee)
    {
        ArgumentNullException.ThrowIfNull(callee);
        return callee is { Name: "AppendFormat", HasThis: true, Definition: null, GenericArguments.Count: 0 }
            && IsBuilder(callee.DeclaringType)
            && callee.ParameterTypes is [IlPrimitiveType { Kind: IlPrimitiveKind.String }, _, ..] parameters
            && parameters.Count <= 4
            && (parameters.Skip(1).All(ObjectTypes.IsSystemObject)
                || (parameters.Count == 2 && Spans.IsSpan(parameters[1], out var element, out _) && ObjectTypes.IsSystemObject(element)));
    }

    /// <summary>True when <paramref name="instruction"/> appends built text to a builder (see the summary).</summary>
    public static bool AppendsBuiltText(IlInstruction instruction) =>
        instruction is { OpCode: IlOpCode.Call or IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } }
        && (AppendsHandler(callee) is not null || IsAppendFormat(callee));

    /// <summary>The substitute's <c>Append(string)</c>, which appends each part.</summary>
    public static IlMethod AppendText => Method("Append", IlType.String);

    /// <summary>The substitute's <c>AppendLine()</c>, which ends the line of an AppendLine.</summary>
    public static IlMethod AppendLine => Method("AppendLine");

    private static IlMethod Method(string name, params IlType[] parameters) =>
        LibrarySubstitutes.FindType(Builder)?.Methods.FirstOrDefault(method => method.Name == name
            && method.Parameters.Select(parameter => parameter.Type).SequenceEqual(parameters))
        ?? throw new InvalidOperationException($"ChipSharp.Library's StringBuilder has no {name}.");
}
