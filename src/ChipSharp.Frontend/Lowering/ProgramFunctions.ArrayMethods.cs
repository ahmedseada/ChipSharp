namespace ChipSharp.Frontend.Lowering;

/// <summary>The methods of System.Array (see <see cref="Frontend.ArrayMethods"/>): the functions they call.</summary>
internal sealed partial class ProgramFunctions
{
    private ArrayMethodFunctions? _arrayMethodFunctions;

    /// <summary>The functions the methods of System.Array use, shared by every method.</summary>
    public ArrayMethodFunctions ArrayMethodFunctions => _arrayMethodFunctions ??= new ArrayMethodFunctions(module, Arrays);
}
