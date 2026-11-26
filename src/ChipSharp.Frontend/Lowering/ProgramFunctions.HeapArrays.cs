using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Arrays on the heap (see <see cref="Frontend.HeapArrays"/>): the program's kept parameters, the functions that turn
/// array values into array objects and back, and the descriptor of each array type, named as C# names it.
/// </summary>
internal sealed partial class ProgramFunctions
{
    private HeapArrayFunctions? _heapArrayFunctions;

    /// <summary>The kept and nullable array parameters of the program being translated.</summary>
    public HeapArrays HeapArrays { get; init; } = HeapArrays.None;

    /// <summary>The functions arrays on the heap use (an object to a value and back, null tests), shared by every method.</summary>
    public HeapArrayFunctions HeapArrayFunctions => _heapArrayFunctions ??= new HeapArrayFunctions(module, Arrays);

    /// <summary>
    /// The descriptor of the array type <paramref name="arrayType"/> of <paramref name="assembly"/>, e.g. "int[]",
    /// "int[][]", "object[]": its elements' TIR type, and for elements that are objects the descriptor of their type.
    /// </summary>
    public TirTypeDescriptor HeapArrayDescriptor(IlType arrayType, IlAssembly assembly)
    {
        var element = HeapArrays.ElementType(arrayType)
            ?? throw new ArgumentException($"{arrayType} is not an array type.", nameof(arrayType));
        if (!HeapArrays.TryMapElementType(element, assembly, out var storage))
        {
            throw new ArgumentException($"An array of {element} cannot be on the heap.", nameof(arrayType));
        }

        var plain = ValueTypes.WithoutModifiers(arrayType);
        var elementDescriptor = storage != TirType.Ref ? null
            : ValueTypes.WithoutModifiers(element) is IlArrayType inner ? HeapArrayDescriptor(inner, assembly)
            : ObjectTypes.Class(element, assembly) is { } type ? ClassDescriptor(type)
            : ObjectDescriptor;
        return ArrayDescriptor(CSharpNames.Type(plain), storage, elementDescriptor);
    }
}
