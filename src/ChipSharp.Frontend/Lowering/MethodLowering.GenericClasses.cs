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
    /// For <see cref="FromStack"/>: an array going where an interface is wanted (<c>IEnumerable&lt;int&gt; e = a;</c>)
    /// would have no interface map to call through: arrays used as interfaces arrive with the collections.
    /// </summary>
    private void CheckArrayAsInterface(StackValue value, IlType destination)
    {
        if (value.Type == StackArrays.ArrayType && VirtualMethods.IsInterface(destination, _method.Assembly))
        {
            throw Error($"an array is used as {Analysis.CSharpNames.Type(ValueTypes.WithoutModifiers(destination))}, " +
                $"and {Analysis.Unsupported.ArraysAsInterfaces.Heading}");
        }
    }
}
