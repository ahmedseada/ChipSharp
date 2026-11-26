using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The errors of the translation, written like the IL reader's: the file, the method and, for an instruction,
/// its offset, e.g. <c>Test.dll: Program.Main() at IL_0003: ldstr is not supported yet.</c>
/// </summary>
internal static class LoweringErrors
{
    /// <summary>An error about the method as a whole, e.g. its signature.</summary>
    public static ChipSharpException In(IlMethod method, string message) =>
        new($"{method.Assembly.FilePath}: {method}: {message}.");

    /// <summary>An error about the instruction at <paramref name="offset"/>.</summary>
    public static ChipSharpException At(IlMethod method, int offset, string message) =>
        method.Assembly.Error(method, offset, message);

    /// <summary>
    /// The TIR type of a parameter, local or return type, or an error that starts with <paramref name="subject"/>,
    /// e.g. "parameter 'x' has type" or "it returns". For a struct of the program it says what keeps the struct out.
    /// </summary>
    public static TirType MapType(IlMethod method, IlType type, string subject)
    {
        if (SupportedSubset.TryMapType(type, method.Assembly, out var tirType))
        {
            return tirType;
        }

        var why = ValueTypes.ProblemOf(type, method.Assembly)?.Text
            ?? "ChipSharp compiles bool, whole numbers, float and double";
        throw In(method, $"{subject} {type}, which is not supported yet ({why})");
    }

    /// <summary>The IL name of an evaluation stack type: int32, int64, native int, float32 or float64.</summary>
    public static string StackTypeName(TirType type) => type.Kind switch
    {
        TirTypeKind.I32 => "int32",
        TirTypeKind.I64 => "int64",
        TirTypeKind.NativeInt => "native int",
        TirTypeKind.F32 => "float32",
        TirTypeKind.F64 => "float64",
        _ => type.Name,
    };

    /// <summary>What the evaluation stack holds, bottom first, e.g. "int32, int64", or "nothing".</summary>
    public static string StackText(IEnumerable<TirType> types)
    {
        var text = string.Join(", ", types.Select(StackTypeName));
        return text.Length == 0 ? "nothing" : text;
    }
}
