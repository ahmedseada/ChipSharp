using System.Globalization;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The program's fixed text: one constant string object per distinct string literal (see <see cref="Strings"/>), added
/// when first used and shared by every method after that, named <c>&lt;text 0&gt;</c>, <c>&lt;text 1&gt;</c>, ... in
/// that order, and the two descriptors of strings, the runtime's.
/// </summary>
internal sealed partial class ProgramFunctions
{
    private readonly Dictionary<string, TirGlobal> _texts = new(StringComparer.Ordinal);
    private TirTypeDescriptor? _narrowString;
    private TirTypeDescriptor? _wideString;

    /// <summary>The descriptor of strings stored 1 byte per char, the runtime's <c>chipsharp_string_type</c>.</summary>
    public TirTypeDescriptor NarrowString => _narrowString ??= StringDescriptor(Strings.NarrowName, TirType.I8,
        Strings.NarrowSymbol);

    /// <summary>The descriptor of strings stored 2 bytes per char, the runtime's <c>chipsharp_wide_string_type</c>.</summary>
    public TirTypeDescriptor WideString => _wideString ??= StringDescriptor(Strings.WideName, TirType.I16,
        Strings.WideSymbol);

    /// <summary>The constant string object of <paramref name="text"/>: the same one for equal text.</summary>
    public TirGlobal Text(string text)
    {
        if (!_texts.TryGetValue(text, out var global))
        {
            var name = GlobalName(string.Create(CultureInfo.InvariantCulture, $"<text {_texts.Count}>"));
            global = module.AddConstantObject(name, Strings.IsNarrow(text) ? NarrowString : WideString, text.Length,
                Strings.Elements(text));
            _texts.Add(text, global);
        }

        return global;
    }

    private readonly Dictionary<string, TirGlobal> _charData = new(StringComparer.Ordinal);

    /// <summary>
    /// The UTF-16 chars of <paramref name="text"/> as a constant array of i16, <c>&lt;chars n&gt;</c>, the same one
    /// for equal text: what a <c>ReadOnlySpan&lt;char&gt;</c> of a literal views (see StringMethods.LiteralSpan).
    /// </summary>
    public TirGlobal CharData(string text)
    {
        if (!_charData.TryGetValue(text, out var global))
        {
            var name = GlobalName(string.Create(CultureInfo.InvariantCulture, $"<chars {_charData.Count}>"));
            var elements = text.Length == 0 ? [new TirConst(TirType.I16, 0)]
                : text.Select(c => new TirConst(TirType.I16, (short)c)).ToArray();
            global = module.AddArray(name, new TirArrayType(TirType.I16, elements.Length), elements, isConstant: true);
            _charData.Add(text, global);
        }

        return global;
    }

    private TirTypeDescriptor StringDescriptor(string name, TirType element, string symbol)
    {
        var descriptor = module.AddArrayType(name, element);
        descriptor.ExternalSymbol = symbol;
        return descriptor;
    }
}
