using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>Strings in the whole-program pass (see <see cref="StringMethods"/>).</summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// The verdicts on the calls of <paramref name="method"/> of the methods of string, char and parsing that ChipSharp
    /// compiles: fine, or a heap use for one that makes new text (<see cref="Unsupported.MakingText"/>).
    /// </summary>
    private static Dictionary<IlInstruction, Unsupported?> ForStrings(IlMethod method)
    {
        var verdicts = new Dictionary<IlInstruction, Unsupported?>();
        var instructions = method.Body!.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            if (StringMethods.Find(method, i) is { } called)
            {
                verdicts[instructions[i]] = StringMethods.Verdict(called);
            }
            else if (StringMethods.IsPrefix(method, i))
            {
                verdicts[instructions[i]] = null;
            }
        }

        return verdicts;
    }
}
