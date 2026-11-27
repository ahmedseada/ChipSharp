using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Arrays as the interfaces of collections (see <see cref="ArrayInterfaces"/>): the descriptor of each array type whose
/// class of interfaces (<c>SzArray&lt;T&gt;</c>) the program counts as made gets that class's interface map, with a
/// method table for each interface that has a descriptor, so a call through an interface, a cast and a type test find it
/// in an array's header as in an object's.
/// </summary>
internal sealed partial class ProgramFunctions
{
    private void FillArrayInterfaces()
    {
        var tables = VirtualTables;
        var dispatch = tables.Dispatch;
        foreach (var type in dispatch.Created.Where(ArrayInterfaces.IsArrayClass))
        {
            var descriptor = HeapArrayDescriptor(ArrayInterfaces.ArrayTypeOf(type), type.Assembly);
            foreach (var (interfaceType, interfaceDescriptor) in _interfaceDescriptors)
            {
                if (VirtualMethods.Implements(type, interfaceType))
                {
                    var methods = tables.InterfaceTable(interfaceType)
                        .Select(slot => dispatch.Resolve(type, slot) is { } target ? Function(target) : null);
                    descriptor.Interfaces.Add(new TirInterfaceMethods(interfaceDescriptor, [.. methods]));
                }
            }
        }
    }
}
