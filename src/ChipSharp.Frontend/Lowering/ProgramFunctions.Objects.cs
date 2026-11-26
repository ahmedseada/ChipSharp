using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The type descriptors of the program's objects (see <see cref="ObjectTypes"/>), one per type, added to the module when
/// first needed and shared by every method after that: the one place a method asks for them.
/// </summary>
internal sealed partial class ProgramFunctions
{
    private readonly Dictionary<IlTypeDefinition, TirTypeDescriptor> _classes = [];
    private readonly Dictionary<string, TirTypeDescriptor> _arrayTypes = new(StringComparer.Ordinal);
    private TirTypeDescriptor? _systemObject;

    /// <summary>System.Object's descriptor: the root of every class, with no base and no fields.</summary>
    public TirTypeDescriptor ObjectDescriptor =>
        _systemObject ??= module.AddClass(ObjectTypes.SystemObjectName, ObjectTypes.SystemObject);

    /// <summary>The descriptor of a class of the program, made after its base class's (System.Object's at the root).</summary>
    public TirTypeDescriptor ClassDescriptor(IlTypeDefinition type)
    {
        if (!_classes.TryGetValue(type, out var descriptor))
        {
            var baseDescriptor = ObjectTypes.BaseClass(type) is { } baseClass ? ClassDescriptor(baseClass) : ObjectDescriptor;
            descriptor = module.AddClass(ObjectTypes.Name(type), ObjectTypes.ObjectType(type), baseDescriptor);
            _classes.Add(type, descriptor);
        }

        return descriptor;
    }

    /// <summary>
    /// The descriptor of the array type named <paramref name="name"/> (C#'s name, e.g. "int[]" or "Program.Node[]"),
    /// whose elements are <paramref name="element"/>, and for elements that are objects, of
    /// <paramref name="elementDescriptor"/>'s type; the same one every time it is asked for by that name.
    /// </summary>
    public TirTypeDescriptor ArrayDescriptor(string name, TirType element, TirTypeDescriptor? elementDescriptor = null)
    {
        if (!_arrayTypes.TryGetValue(name, out var descriptor))
        {
            descriptor = module.AddArrayType(name, element);
            descriptor.ElementDescriptor = elementDescriptor;
            _arrayTypes.Add(name, descriptor);
        }

        return descriptor;
    }
}
