using System.Reflection;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// The program's classes (<see cref="ObjectTypes"/> says how they map to TIR): what ChipSharp compiles of them, which the
/// whole-program pass checks and the IL-to-TIR translation follows (<c>Lowering/MethodLowering.Classes.cs</c>).
/// <list type="bullet">
/// <item>A class of the program, sealed or not, nested or not, that is not generic and derives from object or from
/// another such class, with instance fields of every type an object may hold (<see cref="ObjectTypes.TryFieldType"/>:
/// numbers, bools, enums, structs, also ones that hold objects, other objects, the class itself, fixed text), field
/// initializers, constructors (overloads, <c>this(...)</c> and <c>base(...)</c>; object's own constructor does
/// nothing), instance methods and properties, and static members.</item>
/// <item><c>new</c> is <c>newobj</c>: a new zeroed object (<c>newobject</c>), then its constructor. It may not happen
/// while an interrupt handler runs (<see cref="Allocates"/>): the whole-program pass rejects it in what a handler
/// reaches, and a target without a heap has none at all.</item>
/// <item>A call of an instance method on an object is <c>callvirt</c>, as C# writes every one: for a method that is not
/// virtual (<see cref="IsOverridable"/>) that is the object's null check, then the call. A virtual method, an override
/// and an abstract one are not compiled yet: they arrive with virtual calls, as do object's ToString, Equals and
/// GetHashCode, records and interfaces of classes.</item>
/// <item>Objects compare by reference (<c>==</c>, <c>!=</c>, <c>is null</c>, <c>object.ReferenceEquals</c>), and
/// <c>?.</c> and <c>??</c> test them against null.</item>
/// </list>
/// Not yet: records (<see cref="IsRecord"/>), finalizers (<see cref="HasFinalizer"/>), classes derived from library
/// classes, generic classes, casts and type tests.
/// </summary>
internal static class ClassRules
{
    /// <summary>True for a record class: C# gives it a <c>&lt;Clone&gt;$</c> method, and virtual Equals, GetHashCode and ToString.</summary>
    public static bool IsRecord(IlTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Methods.Any(method => method.Name == "<Clone>$");
    }

    /// <summary>True for a class with a finalizer (<c>~Node()</c>): C# makes it an override of object's Finalize.</summary>
    public static bool HasFinalizer(IlTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Methods.Any(method => method is { Name: "Finalize", IsStatic: false, Parameters.Count: 0 }
            && (method.Attributes & MethodAttributes.Virtual) != 0);
    }

    /// <summary>
    /// True for a method that a <c>callvirt</c> would call through the object's virtual method table: virtual, override
    /// or abstract, and not sealed. A method that implements an interface without <c>virtual</c> is sealed (C# makes it
    /// virtual and final), so a call of it on its class calls it, like any method that is not virtual.
    /// </summary>
    public static bool IsOverridable(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return (method.Attributes & MethodAttributes.Virtual) != 0 && (method.Attributes & MethodAttributes.Final) == 0;
    }

    /// <summary>True for an abstract method: it has no body, and only an override of it runs.</summary>
    public static bool IsAbstract(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return (method.Attributes & MethodAttributes.Abstract) != 0;
    }

    /// <summary>
    /// True when <paramref name="instruction"/> makes an object on the heap: <c>newobj</c> of a class of the program, or
    /// <c>new object()</c>. (A <c>newobj</c> of a struct makes a value; one of a library type is not supported.)
    /// </summary>
    public static bool Allocates(IlInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return instruction is { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var constructor } }
            && (constructor.Definition is { } definition
                ? ObjectTypes.IsClass(definition.DeclaringType)
                : ObjectTypes.IsSystemObject(constructor.DeclaringType));
    }

    /// <summary>
    /// object's constructor, which every constructor of a class calls at last (C#'s <c>base()</c> of a class derived
    /// from object): it does nothing.
    /// </summary>
    public static bool IsObjectConstructor(IlMethodReference method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method is { Name: ".ctor", Definition: null, HasThis: true, ParameterTypes.Count: 0 }
            && ObjectTypes.IsSystemObject(method.DeclaringType);
    }

    /// <summary><c>object.ReferenceEquals(a, b)</c>: two objects compared by reference, as <c>==</c> compares them.</summary>
    public static bool IsReferenceEquals(IlMethodReference method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method is { Name: "ReferenceEquals", Definition: null, HasThis: false, ParameterTypes: [var a, var b] }
            && method.ReturnType == IlType.Bool && ObjectTypes.IsSystemObject(method.DeclaringType)
            && ObjectTypes.IsSystemObject(a) && ObjectTypes.IsSystemObject(b);
    }

    /// <summary>
    /// The C# name of a field: the property's for the backing field of an auto-property (<c>&lt;X&gt;k__BackingField</c>
    /// is X), any other as it is.
    /// </summary>
    public static string FieldName(IlField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var name = field.Name;
        return name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal)
            ? name[1..name.IndexOf('>', StringComparison.Ordinal)]
            : name;
    }
}
