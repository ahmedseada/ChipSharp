using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Virtual calls, interfaces and type tests (see <see cref="VirtualTables"/>): the descriptors of interfaces, made when
/// first needed, the functions a slot runs, and the method tables, which <see cref="FillVirtualTables"/> fills in once
/// every method is translated.
/// </summary>
internal sealed partial class ProgramFunctions
{
    private readonly Dictionary<IlType, TirTypeDescriptor> _interfaceDescriptors = [];
    private VirtualTables? _virtualTables;
    private ObjectFunctions? _objectFunctions;

    /// <summary>The slots and tables of the program, from every method declared.</summary>
    public VirtualTables VirtualTables => _virtualTables ??= new VirtualTables([.. _methods.Keys]);

    /// <summary>The functions type tests, interface calls and object's own methods use.</summary>
    public ObjectFunctions ObjectFunctions => _objectFunctions ??= new ObjectFunctions(module, () => ObjectDescriptor);

    /// <summary>
    /// The descriptor of an interface (see <see cref="VirtualMethods.IsInterface"/>), the same one every time: what an
    /// interface map names, and a cast to the interface tests against.
    /// </summary>
    public TirTypeDescriptor InterfaceDescriptor(IlType type)
    {
        var plain = ValueTypes.WithoutModifiers(type);
        if (!_interfaceDescriptors.TryGetValue(plain, out var descriptor))
        {
            descriptor = module.AddInterface(CSharpNames.Type(plain));
            descriptor.RuntimeName = VirtualMethods.RuntimeName(plain);
            _interfaceDescriptors.Add(plain, descriptor);
        }

        return descriptor;
    }

    /// <summary>The class of the program whose descriptor <paramref name="descriptor"/> is; null for System.Object's and the others.</summary>
    public IlTypeDefinition? ClassOf(TirTypeDescriptor descriptor) =>
        _classes.FirstOrDefault(pair => pair.Value == descriptor).Key;

    /// <summary>The function <paramref name="target"/> is: a method of the program's, or object's own Equals or GetHashCode.</summary>
    public TirFunction Function(DispatchTarget target)
    {
        if (target.Method is { } method)
        {
            return _methods.TryGetValue(method, out var lowered)
                ? lowered.Function
                : throw new ChipSharpException($"{method} runs through a virtual call, but it is not among the program's methods.");
        }

        return target.ObjectMethod switch
        {
            "Equals" => ObjectFunctions.ObjectEquals,
            "GetHashCode" => ObjectFunctions.ObjectHashCode,
            var other => throw new ChipSharpException($"System.Object.{other} is not compiled yet."),
        };
    }

    /// <summary>
    /// Once every method is translated: gives each class's descriptor its .NET name (<c>Program+Node</c>, for an
    /// InvalidCastException's message), and each class the program makes (System.Object too, for <c>new object()</c>)
    /// its virtual method table and its interface map, with a method table for each interface that has a descriptor.
    /// </summary>
    public void FillVirtualTables()
    {
        foreach (var (type, descriptor) in _classes)
        {
            descriptor.RuntimeName = VirtualMethods.RuntimeName(type.Type);
        }

        if (_classes.Count == 0 && _systemObject is null)
        {
            return;
        }

        var tables = VirtualTables;
        var dispatch = tables.Dispatch;
        foreach (var type in dispatch.Created)
        {
            if (!_classes.TryGetValue(type, out var descriptor))
            {
                continue;
            }

            foreach (var slot in tables.TableOf(type))
            {
                descriptor.VirtualMethods.Add(dispatch.Resolve(type, slot) is { } target ? Function(target) : null);
            }

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

        if (dispatch.CreatesPlainObjects && _systemObject is { } systemObject)
        {
            foreach (var slot in tables.ObjectTable)
            {
                systemObject.VirtualMethods.Add(Function(new DispatchTarget(null, slot.Name)));
            }
        }
    }
}
