using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Generics over classes in the translation: the instances are methods and classes like any other (see
/// <see cref="Generics.Instantiations"/>), so what is left here is what the whole-program pass cannot always see: an
/// array that comes from another block to where an interface is wanted.
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>
    /// For <see cref="FromStack"/>: an array going where an interface is wanted (<c>IEnumerable&lt;int&gt; e = a;</c>) is
    /// its object, which has its interface map when the whole-program pass saw the array go there (see
    /// <see cref="ArrayInterfaces"/>); one it did not see (an array from another block) has none to call through.
    /// </summary>
    private void CheckArrayAsInterface(StackValue value, IlType destination)
    {
        if (value.Type == StackArrays.ArrayType && VirtualMethods.IsInterface(destination, _method.Assembly)
            && !(ArrayTypeOf(value) is IlArrayType arrayType && ArrayInterfaces.ClassOf(arrayType, _method.Assembly) is { } type
                && _program.VirtualTables.Dispatch.Created.Contains(type)))
        {
            throw Error($"an array is used as {Analysis.CSharpNames.Type(ValueTypes.WithoutModifiers(destination))} " +
                "where it comes from another way into the code (a ?: or a branch), and ChipSharp gives an array its " +
                "interfaces where it sees the array go into one; for now, put it in a variable of the array's type first");
        }
    }
}
