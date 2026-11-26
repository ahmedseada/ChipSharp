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
    private IReadOnlyList<IlMethod>? _typeInitializers;

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
    /// The initializers (<c>.cctor</c>) of the types the program uses are in it too, where their type is first used, and
    /// so are the <see cref="InterruptHandlers"/>, right after the entry point, and the methods they reach.
    /// </summary>
    public IReadOnlyList<IlMethod> Methods { get; }

    /// <summary>
    /// The methods the chip runs when an event happens (see <see cref="Interrupts"/>), at most one per event, each among
    /// <see cref="Methods"/>. Nothing in the program needs to call them.
    /// </summary>
    public IReadOnlyList<InterruptHandler> InterruptHandlers { get; init; } = [];

    /// <summary>
    /// The type initializers among <see cref="Methods"/> (static field initializers and static constructors), in the
    /// order the program runs them when it starts, before the entry point (see <see cref="StaticInitialization"/>):
    /// those of the types the entry point uses, then those of the types only the interrupt handlers use.
    /// </summary>
    public IReadOnlyList<IlMethod> TypeInitializers => _typeInitializers ??=
        StaticInitialization.RunOrder(Methods, InterruptHandlers.Select(handler => handler.Method));
}
