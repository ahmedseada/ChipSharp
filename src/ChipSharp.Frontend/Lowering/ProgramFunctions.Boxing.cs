using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Boxing (see <see cref="Boxing"/>): the functions and descriptors of boxes, and their method tables, which
/// <see cref="FillBoxedTables"/> fills in with the classes' once every method is translated.
/// </summary>
internal sealed partial class ProgramFunctions
{
    private BoxingFunctions? _boxes;

    /// <summary>The descriptors of boxes and the functions boxing, unboxing and the methods of boxed values use.</summary>
    public BoxingFunctions Boxes => _boxes ??= new BoxingFunctions(module, this);

    /// <summary>
    /// Gives each box the program makes its virtual method table (object's slots, in the classes' order) and its
    /// interface map, with a method table for each interface with a descriptor that its type implements.
    /// </summary>
    private void FillBoxedTables()
    {
        if (_boxes is not { } boxes)
        {
            return;
        }

        // a method of a box may make another box (ValueType's Equals boxes a struct with an Equals of its own): until none
        var tables = VirtualTables;
        var filled = new HashSet<BoxedType>();
        while (boxes.Made.FirstOrDefault(made => !filled.Contains(made.Boxed)) is ({ } boxed, { } descriptor))
        {
            filled.Add(boxed);
            foreach (var slot in tables.ObjectTable)
            {
                descriptor.VirtualMethods.Add(Boxing.Resolve(boxed, slot) is { } target ? Function(target) : null);
            }

            foreach (var (interfaceType, interfaceDescriptor) in _interfaceDescriptors)
            {
                if (Boxing.Implements(boxed, interfaceType))
                {
                    var methods = tables.InterfaceTable(interfaceType)
                        .Select(slot => Boxing.Resolve(boxed, slot) is { } target ? Function(target) : null);
                    descriptor.Interfaces.Add(new TirInterfaceMethods(interfaceDescriptor, [.. methods]));
                }
            }
        }
    }
}
