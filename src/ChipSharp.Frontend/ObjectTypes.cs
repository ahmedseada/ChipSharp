using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// The objects of a program as TIR types: the one place to ask how a reference type and a class map to TIR. M3's
/// features extend it (classes, interfaces, arrays on the heap, strings) and do not reshape it.
/// <list type="bullet">
/// <item>A value of a reference type is a <see cref="TirType.Ref"/> wherever it is: a local, a parameter, a return
/// value, a field, an element, the evaluation stack (<see cref="TryMapType"/>). Null is <c>zero ref</c>.</item>
/// <item>A class's objects are laid out as its object type (<see cref="ObjectType"/>, a <see cref="TirStructType"/>
/// named like the class): the header (<see cref="TirTypeDescriptor.HeaderField"/>), the instance fields of its base
/// classes, System.Object's first (it has none), then its own, each in the order C# declares them. So a field is at
/// the same place in the objects of every class derived from its own (<see cref="FieldOf"/>), and the object type of a
/// base class is the first fields of a derived one's, as <see cref="TirModule.AddClass"/> wants. A field named like
/// one before it (C# allows a private field of the same name in a base class) is named after its class too, e.g.
/// <c>Program.Derived.count</c>.</item>
/// <item>A field is the TIR type of its C# type: a number, bool, struct or enum as <see cref="ValueTypes"/> maps it, a
/// reference type as <see cref="TryMapType"/>, fixed text a ptr (<see cref="FixedText"/>: text is not on the heap
/// yet, so the collector never follows it).</item>
/// <item>Each class has one type descriptor per module, made with its base's first; System.Object's has no base (see
/// <c>ProgramFunctions.ClassDescriptor</c>).</item>
/// </list>
/// It knows System.Object, the program's own classes that are not generic and the interfaces they may implement (see
/// <see cref="VirtualMethods.IsInterface"/>). A parameter, local, return value, static
/// field or struct field may be of any of them (<see cref="SupportedSubset.TryMapType(IlType, IlAssembly, out TirType?)"/>
/// asks this mapping); what keeps a class from compiling (<see cref="ClassRules"/>) is reported where the program
/// makes one, uses its fields or runs its methods.
/// </summary>
internal static class ObjectTypes
{
    private static readonly ConditionalWeakTable<IlTypeDefinition, TirStructType> Layouts = new();

    /// <summary>The object type of System.Object: the header alone.</summary>
    public static TirStructType SystemObject { get; } = TirTypeDescriptor.ObjectStruct("System.Object");

    /// <summary>The descriptor name of System.Object, the root of every class.</summary>
    public const string SystemObjectName = "System.Object";

    /// <summary>
    /// <see cref="TirType.Ref"/> for a reference type this mapping knows: System.Object (C#'s <c>object</c>), the
    /// classes of the program (<see cref="Class"/>) and the interfaces their objects implement
    /// (<see cref="VirtualMethods.IsInterface"/>). False for any other type, e.g. a struct, which
    /// <see cref="ValueTypes"/> maps.
    /// </summary>
    public static bool TryMapType(IlType type, IlAssembly assembly, [NotNullWhen(true)] out TirType? tirType)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(assembly);
        tirType = IsSystemObject(type) || Class(type, assembly) is not null || VirtualMethods.IsInterface(type, assembly)
            ? TirType.Ref
            : null;
        return tirType is not null;
    }

    /// <summary>True for System.Object, as a signature names it (<c>object</c>) or as a base type does.</summary>
    public static bool IsSystemObject(IlType type) => ValueTypes.WithoutModifiers(type) is
        IlPrimitiveType { Kind: IlPrimitiveKind.Object }
        or IlNamedType { Namespace: "System", Name: "Object", DeclaringType: null, IsDefinedInThisAssembly: false };

    /// <summary>The class of <paramref name="assembly"/> that <paramref name="type"/> names (modifiers looked through), or null.</summary>
    public static IlTypeDefinition? Class(IlType type, IlAssembly assembly) =>
        ValueTypes.WithoutModifiers(type) switch
        {
            IlNamedType { IsDefinedInThisAssembly: true } named => assembly.FindType(named.FullName),

            // an instance of a generic class (generics over classes), of the program's or a library substitute's
            IlGenericInstanceType instance => Generics.Instantiations.Type(instance, assembly),
            _ => null,
        } is { GenericParameters.Count: 0 } definition && IsClass(definition)
            ? definition
            : null;

    /// <summary>
    /// True for a class: a type with a base type that is not an interface, a struct, an enum or a delegate. (A static
    /// class is one too; nothing ever makes an object of it.)
    /// </summary>
    public static bool IsClass(IlTypeDefinition type) => type is { IsInterface: false, BaseType: not null }
        && !ValueTypes.IsStruct(type) && !ValueTypes.IsEnum(type)
        && type.BaseType is not IlNamedType { Namespace: "System", Name: "MulticastDelegate" or "Delegate" };

    /// <summary>
    /// The base class of a class of the program; null when it is System.Object. A base class from a library other than
    /// System.Object is an error that says so.
    /// </summary>
    public static IlTypeDefinition? BaseClass(IlTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var baseType = type.BaseType ?? throw new ArgumentException($"{type} has no base type.", nameof(type));
        if (IsSystemObject(baseType))
        {
            return null;
        }

        return Class(baseType, type.Assembly) ?? throw new ChipSharpException(
            $"{type.Assembly.FilePath}: the class {Name(type)} derives from {CSharpNames.Type(baseType)}, and ChipSharp " +
            "compiles classes that derive from object or from the program's own classes");
    }

    /// <summary>The C# name of a class, e.g. "Program.Node": its descriptor's and its object type's name.</summary>
    public static string Name(IlTypeDefinition type) => CSharpNames.Type(type.Type);

    /// <summary>The object type of a class of the program (see the summary): one per class, shared by every module.</summary>
    public static TirStructType ObjectType(IlTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Layouts.GetValue(type, CreateObjectType);
    }

    /// <summary>
    /// An instance field of a class: the object type of its class and its number there, which is its number in the
    /// object type of every class derived from it too.
    /// </summary>
    public static (TirStructType ObjectType, int Index) FieldOf(IlField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var owner = field.DeclaringType;
        var objectType = ObjectType(owner);
        var inherited = BaseClass(owner) is { } baseClass ? ObjectType(baseClass).Fields.Count : 1;
        var own = ValueTypes.InstanceFields(owner).TakeWhile(f => f != field).Count();
        return (objectType, inherited + own);
    }

    /// <summary>The TIR type of an instance field of a class; an error that names the field when it has none yet.</summary>
    public static TirType FieldType(IlField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (TryFieldType(field, out var type))
        {
            return type;
        }

        throw new ChipSharpException($"{field.DeclaringType.Assembly.FilePath}: the field {field.Name} of " +
            $"{Name(field.DeclaringType)} has type {CSharpNames.Type(field.Type)}, which is not supported in an object yet");
    }

    /// <summary>
    /// The TIR type of an instance field of a class, when its type has one: a value (<see cref="ValueTypes"/>), an
    /// object (<see cref="TryMapType"/>), fixed text, or an array, which the field holds as its array object, a ref (see
    /// <see cref="HeapArrays"/>). The one test of which fields an object may have.
    /// </summary>
    public static bool TryFieldType(IlField field, [NotNullWhen(true)] out TirType? type)
    {
        ArgumentNullException.ThrowIfNull(field);
        var assembly = field.DeclaringType.Assembly;
        if ((ValueTypes.TryMapType(field.Type, assembly, out type) && type != TirType.Void)
            || TryMapType(field.Type, assembly, out type) || FixedText.TryMapType(field.Type, out type))
        {
            return true;
        }

        type = StackArrays.TryMapType(ValueTypes.WithoutModifiers(field.Type), assembly, out _) ? TirType.Ref : null;
        return type is not null;
    }

    private static TirStructType CreateObjectType(IlTypeDefinition type)
    {
        var inherited = BaseClass(type) is { } baseClass ? ObjectType(baseClass).Fields.Skip(1) : [];
        var fields = inherited.Select(field => (field.Name, field.Type)).ToList();
        foreach (var field in ValueTypes.InstanceFields(type))
        {
            var name = fields.Any(f => f.Name == field.Name) ? $"{Name(type)}.{field.Name}" : field.Name;
            fields.Add((name, FieldType(field)));
        }

        return TirTypeDescriptor.ObjectStruct(Name(type), fields);
    }
}
