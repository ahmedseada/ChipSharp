using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The program's arrays in static fields (see <see cref="Frontend.StaticArrays"/>): one global array per field, named
/// like it, added when first used. A constant table is a constant global (read-only data, in flash on a chip), a
/// static fixed array a variable (in RAM); each starts with its initializer's constants, or at zero.
/// <code>
/// constant [4 x i8] @Program.Digits = [63, 6, 91, 79]
/// global [16 x i32] @Program.buffer
/// </code>
/// </summary>
internal sealed partial class ProgramFunctions
{
    private readonly Dictionary<IlField, TirGlobal> _staticArrays = [];

    /// <summary>The arrays in static fields of the program being translated.</summary>
    public StaticArrays StaticArrays { get; init; } = StaticArrays.None;

    /// <summary>The global that holds the elements of <paramref name="array"/>, which has at least one.</summary>
    public TirGlobal StaticArray(StaticArray array)
    {
        if (!_staticArrays.TryGetValue(array.Field, out var global))
        {
            var name = GlobalName($"{array.Field.DeclaringType.FullName}.{array.Field.Name}");
            global = module.AddArray(name, new TirArrayType(array.ElementType, array.Length), array.Elements,
                array.IsConstant);
            _staticArrays.Add(array.Field, global);
        }

        return global;
    }
}
