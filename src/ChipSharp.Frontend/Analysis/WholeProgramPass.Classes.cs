using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Classes in the whole-program pass (see <see cref="ClassRules"/>): which objects, fields, constructors and calls it
/// accepts, and for the rest which feature to name. A class that cannot be compiled (a record, one with a finalizer or a
/// field of an unsupported type, one derived from a library class) is reported where the program makes an object of it,
/// uses its fields or runs its methods; a type of a parameter, local or field may be any class.
/// </summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// The verdicts on classes for the instructions of <paramref name="method"/> that have one (null: the instruction is
    /// fine): <c>newobj</c> of a class of the program or of object, <c>callvirt</c> (but of a library method other than
    /// object's Equals, GetHashCode and GetType, which the rules of text and of the library judge), object's constructor
    /// and <c>object.ReferenceEquals</c>, and the field instructions on an object's fields.
    /// </summary>
    private static Dictionary<IlInstruction, Unsupported?> ForClasses(IlMethod method)
    {
        var verdicts = new Dictionary<IlInstruction, Unsupported?>();
        var instructions = method.Body!.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            switch (instructions[i])
            {
                case { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var constructor } } instruction
                    when ClassRules.Allocates(instruction):
                    verdicts[instruction] = constructor.Definition is { } definition ? ForClass(definition.DeclaringType) : null;
                    break;

                case { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method.Definition.DeclaringType: var type } } instruction
                    when type.BaseType is IlNamedType { Namespace: "System", Name: "MulticastDelegate" }:
                    verdicts[instruction] = Unsupported.Delegates;
                    break;

                case { OpCode: IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } } instruction:
                    if (i > 0 && instructions[i - 1].OpCode == IlOpCode.Constrained)
                    {
                        // a call on a value through constrained., as before: of an interface or object's method
                        verdicts[instruction] = Unsupported.InstanceCalls;
                    }
                    else if (callee.Definition is { } called)
                    {
                        verdicts[instruction] = ForInstanceCall(called);
                    }
                    else if (ObjectTypes.IsSystemObject(callee.DeclaringType) && !FixedText.IsBuildingText(callee))
                    {
                        // object's Equals, GetHashCode, GetType; its ToString builds text (a StringBuilder's too)
                        verdicts[instruction] = Unsupported.ObjectMethods;
                    }

                    break;

                case { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: var callee } } instruction
                    when ClassRules.IsObjectConstructor(callee) || ClassRules.IsReferenceEquals(callee):
                    verdicts[instruction] = null;
                    break;

                case { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: { Name: ".ctor", Definition: null } callee } }
                    instruction when method is { Name: ".ctor", IsStatic: false } && ObjectTypes.IsClass(method.DeclaringType):
                    // base(...) of a library class: the class's problem
                    verdicts[instruction] = ForClass(method.DeclaringType) ?? Unsupported.LibraryMethod(CSharpNames.Method(callee));
                    break;

                case { OpCode: IlOpCode.Ldfld or IlOpCode.Ldflda or IlOpCode.Stfld,
                    Operand: IlFieldOperand { Field.Definition: { IsStatic: false } field } } instruction
                    when ObjectTypes.IsClass(field.DeclaringType) && field.DeclaringType.GenericParameters.Count == 0:
                    verdicts[instruction] = ForClass(field.DeclaringType);
                    break;
            }
        }

        return verdicts;
    }

    /// <summary>
    /// Why an instance method of a class (or of an interface, or a delegate's) cannot be compiled, or null when it can: a
    /// virtual one too, and an interface's default method (one with a body), which a virtual call reaches. Null too for
    /// one of a generic class: the generic type is reported on its own.
    /// </summary>
    private static Unsupported? ForClassMethod(IlMethod method)
    {
        var type = method.DeclaringType;
        if (!ObjectTypes.IsClass(type))
        {
            return type.IsInterface ? method.HasBody ? null : Unsupported.Interfaces : Unsupported.Delegates;
        }

        return type.GenericParameters.Count > 0 ? null : ForClass(type);
    }

    /// <summary>
    /// What a <c>callvirt</c> of <paramref name="callee"/>, a method of the program, needs; null for a plain call and a
    /// virtual one (an interface's is <see cref="ForVirtualCalls"/>').
    /// </summary>
    private static Unsupported? ForInstanceCall(IlMethod callee)
    {
        var type = callee.DeclaringType;
        return type.IsInterface ? Unsupported.Interfaces
            : ValueTypes.IsStruct(type) ? Unsupported.InstanceCalls
            : !ObjectTypes.IsClass(type) ? Unsupported.Delegates
            : type.GenericParameters.Count > 0 ? Unsupported.GenericClasses
            : ForClass(type);
    }

    /// <summary>
    /// Why the objects of a class of the program cannot be compiled, or null when they can: the class or one of its base
    /// classes is a record, has a finalizer, derives from a library class or a generic class, or has a field of a type an
    /// object cannot hold (named with the field: "characters (char) in the field Program.Node.Letter").
    /// </summary>
    private static Unsupported? ForClass(IlTypeDefinition type)
    {
        for (IlTypeDefinition? current = type; current is not null;)
        {
            if (ClassRules.HasFinalizer(current))
            {
                return Unsupported.Finalizers;
            }

            if (current.GenericParameters.Count > 0)
            {
                return Unsupported.GenericClasses;
            }

            foreach (var field in ValueTypes.InstanceFields(current))
            {
                if (!ObjectTypes.TryFieldType(field, out _))
                {
                    return ForObjectField(field);
                }
            }

            var baseType = current.BaseType!;
            if (ObjectTypes.IsSystemObject(baseType))
            {
                return null;
            }

            current = ObjectTypes.Class(baseType, current.Assembly);
            if (current is null)
            {
                return baseType is IlGenericInstanceType { Definition: IlNamedType { IsDefinedInThisAssembly: true } }
                    ? Unsupported.GenericClasses
                    : CSharpNames.Type(baseType) is var name && name.EndsWith("Exception", StringComparison.Ordinal)
                        ? Unsupported.Exceptions
                        : Unsupported.LibraryBaseClass(name);
            }
        }

        return null;
    }

    /// <summary>The problem of an instance field of a class whose type an object cannot hold, naming the field.</summary>
    private static Unsupported ForObjectField(IlField field)
    {
        var unsupported = (ValueTypes.WithoutModifiers(field.Type) is IlArrayType
            ? Unsupported.ArrayEscapes
            : ForAnyType(field.Type, field.DeclaringType.Assembly)) ?? Unsupported.TypeNamed(CSharpNames.Type(field.Type));
        return unsupported with
        {
            What = $"{unsupported.What} in the field {ObjectTypes.Name(field.DeclaringType)}.{ClassRules.FieldName(field)}",
        };
    }

    /// <summary>
    /// A <c>newobj</c> of a library type that is not supported: an exception (<c>throw new ...Exception()</c>), a
    /// delegate (its constructor takes an object and a function pointer), else the type, which is not available.
    /// </summary>
    private static Unsupported ForLibraryNew(IlMethodReference constructor) =>
        constructor.ParameterTypes is [IlPrimitiveType { Kind: IlPrimitiveKind.Object }, IlPrimitiveType { Kind: IlPrimitiveKind.NativeInt }]
            ? Unsupported.Delegates
            : CSharpNames.Type(constructor.DeclaringType) is var name && name.EndsWith("Exception", StringComparison.Ordinal)
                ? Unsupported.Exceptions
                : Unsupported.LibraryType(name);
}
