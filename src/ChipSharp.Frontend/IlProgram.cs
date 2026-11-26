using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// The part of an assembly ChipSharp compiles: its entry point and every method the entry point can reach,
/// already checked against <see cref="SupportedSubset"/> by the whole-program pass. The IL-to-TIR translation reads it.
/// </summary>
/// <remarks>
/// One shared rule: the entry point may take a <c>string[] args</c> it never reads (C#'s top-level statements
/// produce one). The whole-program pass accepts it only when no instruction loads it, and the translation
/// leaves that parameter out of the TIR function.
/// </remarks>
public sealed class IlProgram
{
    public IlProgram(IlAssembly assembly, IReadOnlyList<IlMethod> methods)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(methods);
        if (methods.Count == 0)
        {
            throw new ArgumentException("A program has at least its entry point.", nameof(methods));
        }

        Assembly = assembly;
        Methods = methods;
    }

    public IlAssembly Assembly { get; }

    /// <summary>The method the program starts with; always the first of <see cref="Methods"/>.</summary>
    public IlMethod EntryPoint => Methods[0];

    /// <summary>
    /// Every method of the program the entry point can reach, itself included: the entry point first, then in the
    /// order they are first called. Each appears once. Library calls (see <see cref="KnownExternals"/>) are not in it.
    /// </summary>
    public IReadOnlyList<IlMethod> Methods { get; }
}
