using System.Globalization;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The program's fixed text (see <see cref="FixedText"/>): one constant global per distinct string literal, its
/// record, added when first used and shared by every method after that, named <c>&lt;text 0&gt;</c>,
/// <c>&lt;text 1&gt;</c>, ... in that order.
/// </summary>
internal sealed partial class ProgramFunctions
{
    private readonly Dictionary<string, TirGlobal> _texts = new(StringComparer.Ordinal);

    /// <summary>The constant global that holds the record of <paramref name="text"/>: the same one for equal text.</summary>
    public TirGlobal Text(string text)
    {
        if (!_texts.TryGetValue(text, out var global))
        {
            var name = GlobalName(string.Create(CultureInfo.InvariantCulture, $"<text {_texts.Count}>"));
            global = module.AddConstant(name, FixedText.Record(text));
            _texts.Add(text, global);
        }

        return global;
    }
}
