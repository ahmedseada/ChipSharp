using System.Globalization;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// What printed text (see <see cref="PrintedText"/>) writes with: the runtime's Console.Write functions, the functions
/// that write an aligned or formatted part (<see cref="PrintedTextFunctions"/>) and the digits of hex numbers.
/// </summary>
internal sealed partial class ProgramFunctions
{
    private readonly Dictionary<bool, TirGlobal> _hexDigits = [];
    private PrintedTextFunctions? _textFunctions;

    /// <summary>The functions that write an aligned or formatted part of printed text, shared by every method.</summary>
    public PrintedTextFunctions TextFunctions => _textFunctions ??= new PrintedTextFunctions(module, this);

    /// <summary>
    /// The external function of the runtime's <c>ChipSharp.Runtime.&lt;name&gt;</c>, one a library call becomes too
    /// (<see cref="KnownExternals"/>), e.g. "WriteString" or "WriteLineInt32".
    /// </summary>
    public TirFunction Runtime(string name) =>
        External(KnownExternals.All.Single(function => function.DisplayName == $"ChipSharp.Runtime.{name}"));

    /// <summary>
    /// The sixteen hex digits, 0 to F (or 0 to f), as sixteen records of fixed text one after another in one constant
    /// (see <see cref="FixedText"/>): the record of digit d is <c>d * (FixedText.HeaderSize + 1)</c> bytes in.
    /// </summary>
    public TirGlobal HexDigits(bool upper)
    {
        if (!_hexDigits.TryGetValue(upper, out var global))
        {
            var digits = upper ? "0123456789ABCDEF" : "0123456789abcdef";
            global = module.AddConstant(GlobalName(upper ? "<hex digits>" : "<lowercase hex digits>"),
                digits.SelectMany(digit => FixedText.Record(digit.ToString(CultureInfo.InvariantCulture))));
            _hexDigits.Add(upper, global);
        }

        return global;
    }
}
