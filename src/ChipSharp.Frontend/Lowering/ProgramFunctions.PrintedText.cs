using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// What printed text (see <see cref="PrintedText"/>) writes with: the runtime's Console.Write functions, the functions
/// that write an aligned or formatted part (<see cref="PrintedTextFunctions"/>).
/// </summary>
internal sealed partial class ProgramFunctions
{
    private PrintedTextFunctions? _textFunctions;

    /// <summary>The functions that write an aligned or formatted part of printed text, shared by every method.</summary>
    public PrintedTextFunctions TextFunctions => _textFunctions ??= new PrintedTextFunctions(module, this);

    /// <summary>
    /// The external function of the runtime's <c>ChipSharp.Runtime.&lt;name&gt;</c>, one a library call becomes too
    /// (<see cref="KnownExternals"/>), e.g. "WriteString" or "WriteLineInt32".
    /// </summary>
    public TirFunction Runtime(string name) =>
        External(KnownExternals.All.Single(function => function.DisplayName == $"ChipSharp.Runtime.{name}"));
}
