using System.Globalization;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Type objects (see <see cref="TypeObjects"/>): one constant object of ChipSharp.Library's Type per type, made when
/// first needed (<c>@&lt;type Program+Node&gt;</c>), whose fields are its names, constant strings like literals, and its
/// flags; and <c>ChipSharp.Type.Of</c>, which <c>GetType()</c> calls, whose body <see cref="FillTypeObjects"/> writes
/// once every method is translated: it compares the object's header with each descriptor of a type the program has
/// objects of, and gives that type's object.
/// <code>
/// function ref ChipSharp.Type.Of(ref %object)        the type object of %object's type (not null)
/// </code>
/// </summary>
internal sealed partial class ProgramFunctions
{
    private readonly Dictionary<string, TirGlobal> _typeObjects = new(StringComparer.Ordinal);
    private TirFunction? _typeOfObject;

    /// <summary>The program's assembly: the one a type of the program comes from, in a type object's FullName.</summary>
    public IlAssembly? Program { get; init; }

    /// <summary>The function <c>GetType()</c> calls: the type object of an object, which must not be null.</summary>
    public TirFunction TypeOfObject
    {
        get
        {
            if (_typeOfObject is null)
            {
                // the class of type objects, a type object's own type, has its descriptor and tables from the start
                ClassDescriptor(TypeObjects.TypeClass);
                _typeOfObject = module.AddFunction("ChipSharp.Type.Of", TirType.Ref, ("object", TirType.Ref));
            }

            return _typeOfObject;
        }
    }

    /// <summary>The type object of <paramref name="type"/>, as <paramref name="assembly"/>'s IL names it: <c>typeof</c>.</summary>
    public TirGlobal TypeObject(IlType type, IlAssembly assembly) =>
        TypeObject(VirtualMethods.RuntimeName(type), TypeObjects.FlagsOf(type, assembly), Program ?? assembly);

    /// <summary>
    /// Once every method is translated (and the descriptors have their names, see <see cref="FillVirtualTables"/>):
    /// the body of <c>ChipSharp.Type.Of</c>, when the program calls GetType(), and the name of type objects' own type.
    /// </summary>
    public void FillTypeObjects()
    {
        if (_classes.TryGetValue(TypeObjects.TypeClass, out var typeClass))
        {
            typeClass.RuntimeName = TypeObjects.RuntimeTypeName;
        }

        if (_typeOfObject is not { } function)
        {
            return;
        }

        var program = Program ?? TypeObjects.TypeClass.Assembly;
        var known = new List<(TirTypeDescriptor Descriptor, TirGlobal Type)>();
        foreach (var descriptor in module.TypeDescriptors.Where(descriptor => descriptor.Kind != TirTypeDescriptorKind.Interface).ToList())
        {
            if (Describe(descriptor, program) is var (text, flags))
            {
                known.Add((descriptor, TypeObject(text, flags, program)));
            }
        }

        var ir = new TirBuilder(function.AddBlock("entry"));
        foreach (var (i, (descriptor, type)) in known.Index())
        {
            var (found, next) = (function.AddBlock($"found{i}"), function.AddBlock($"next{i}"));
            ir.Branch(ir.Compare(TirCompareOp.Eq, ir.TypeOf(function.Parameters[0]), ir.AddressOf(descriptor)), found, next);
            ir.Block = found;
            ir.Return(ir.AddressOf(type));
            ir.Block = next;
        }

        // never reached: every object's type is one of the program's descriptors
        ir.Return(ir.Zero(TirType.Ref));
    }

    /// <summary>
    /// The type object of the type whose .NET name (ToString) is <paramref name="text"/> and whose flags are
    /// <paramref name="flags"/> (<see cref="TypeObjects.FlagsOf"/>), made once.
    /// </summary>
    private TirGlobal TypeObject(string text, int flags, IlAssembly program)
    {
        if (!_typeObjects.TryGetValue(text, out var global))
        {
            var typeClass = TypeObjects.TypeClass;
            var objectType = ObjectTypes.ObjectType(typeClass);
            var (name, fullName, ns) = TypeObjects.Names(text, program);
            if (TypeObjects.IsGeneric(text))
            {
                flags |= TypeObjects.GenericFlag;
            }

            TirValue? TextOf(string? value) => value is null ? null : new TirGlobalAddress(Text(value));
            var fields = objectType.Fields.Skip(1).Select(field => field.Name switch
            {
                "_name" => TextOf(name),
                "_fullName" => TextOf(fullName),
                "_namespace" => TextOf(ns),
                "_text" => TextOf(text),
                "_flags" => new TirConst(TirType.I32, flags),
                var other => throw new InvalidOperationException($"ChipSharp.Library's Type has a field {other} that " +
                    "type objects do not fill in."),
            }).ToList();
            global = module.AddObject(GlobalName(string.Create(CultureInfo.InvariantCulture, $"<type {text}>")), objectType,
                ClassDescriptor(typeClass), fields);
            _typeObjects.Add(text, global);
        }

        return global;
    }

    /// <summary>
    /// The .NET name and the flags of the type whose objects have <paramref name="descriptor"/>; null for one no object
    /// has (System.Delegate and System.MulticastDelegate, the bases of delegate types).
    /// </summary>
    private (string Text, int Flags)? Describe(TirTypeDescriptor descriptor, IlAssembly program)
    {
        if (descriptor == _narrowString || descriptor == _wideString)
        {
            return ("System.String", 0);
        }

        if (descriptor.IsArray)
        {
            return (descriptor.RuntimeName ?? descriptor.Name, TypeObjects.ArrayFlag);
        }

        // a class the program makes objects of (a type object's own too)
        if (_classes.FirstOrDefault(pair => pair.Value == descriptor).Key is { } type)
        {
            return type == TypeObjects.TypeClass ? (TypeObjects.RuntimeTypeName, 0)
                : VirtualTables.Dispatch.Created.Contains(type)
                    ? (descriptor.RuntimeName ?? descriptor.Name, TypeObjects.FlagsOf(Generics.Instantiations.Reference(type), program))
                    : null;
        }

        if (_boxes is not null && _boxes.Made.FirstOrDefault(made => made.Descriptor == descriptor) is { Boxed: { } boxed })
        {
            return (boxed.RuntimeName, TypeObjects.FlagsOf(boxed.Type, program));
        }

        if (descriptor == _systemObject)
        {
            return VirtualTables.Dispatch.CreatesPlainObjects ? (ObjectTypes.SystemObjectName, 0) : null;
        }

        return _delegateTypes.Values.Any(known => known.Descriptor == descriptor)
            ? (descriptor.RuntimeName ?? descriptor.Name, 0)
            : null;
    }
}
