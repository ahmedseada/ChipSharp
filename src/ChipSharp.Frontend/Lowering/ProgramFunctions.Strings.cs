using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Strings (see <see cref="Strings"/>): the runtime's functions that read and make them, by name, each added to the
/// module when first used.
/// </summary>
internal sealed partial class ProgramFunctions
{
    private EnumNameFunctions? _enumNames;

    /// <summary>The functions that write and give the names of enum values, shared by every method.</summary>
    public EnumNameFunctions EnumNames => _enumNames ??= new EnumNameFunctions(module, this);

    /// <summary>
    /// The external function <c>ChipSharp.Runtime.&lt;name&gt;</c> of the runtime (its C symbol
    /// <c>ChipSharp_Runtime_&lt;name&gt;</c>), e.g. "StringConcat" or "WriteHexDigits", which returns
    /// <paramref name="returns"/> and takes <paramref name="parameters"/>.
    /// </summary>
    public TirFunction RuntimeFunction(string name, TirType returns, params TirType[] parameters) =>
        RuntimeFunction(new ExternalFunction($"ChipSharp.Runtime.{name}", returns, parameters, $"ChipSharp.Runtime.{name}"));

    /// <summary>
    /// The external function of <paramref name="external"/> (see <see cref="StringMethods"/>), marked as one that makes
    /// objects when it makes strings (<see cref="StringMethods.MakesObjects"/>).
    /// </summary>
    public TirFunction RuntimeFunction(ExternalFunction external)
    {
        var function = External(external);
        function.MakesObjects |= StringMethods.MakesObjects(external.DisplayName);
        return function;
    }
}
