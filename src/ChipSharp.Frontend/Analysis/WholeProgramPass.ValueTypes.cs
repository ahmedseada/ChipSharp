using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Structs, enums and references in the whole-program pass (see <see cref="ValueTypes"/>): which ones it accepts, and
/// for the rest which feature to name. Objects stay out: a <c>newobj</c> of a class creates an object and a field of a
/// class is an instance field of an object, while the same instructions on a struct are fine.
/// </summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// Why a parameter, local, return or field type cannot be compiled, or null when it can (also for a generic
    /// parameter, whose generic method or type is the problem). A type in <paramref name="method"/>'s signature or body
    /// is named with the method's generic parameters.
    /// </summary>
    private static Unsupported? ForAnyType(IlType type, IlAssembly assembly, IlMethod? method = null)
    {
        if (ValueTypes.TryMapType(type, assembly, out _))
        {
            return null;
        }

        var unsupported = ForValueType(type, assembly, method) ?? Unsupported.ForType(type, assembly);
        return unsupported != Unsupported.OtherType ? unsupported
            : Unsupported.TypeNamed(method is null ? CSharpNames.Type(type) : CSharpNames.Type(type, method));
    }

    /// <summary>
    /// What keeps a pointer, a reference or a struct of the program from compiling; null for any other type, which
    /// <see cref="Unsupported.ForType"/> describes. A reference (<c>ref</c>, <c>out</c>, <c>in</c>) has the problem of
    /// what it refers to; one to text is a problem of its own.
    /// </summary>
    private static Unsupported? ForValueType(IlType type, IlAssembly assembly, IlMethod? method) =>
        ValueTypes.WithoutModifiers(type) switch
        {
            IlPointerType or IlPinnedType => Unsupported.UnsafeCode,
            IlByRefType reference when FixedText.IsText(reference.ElementType) => Unsupported.TextReferences,
            IlByRefType { ElementType: IlArrayType } => Unsupported.ArrayReferences,
            IlByRefType reference when ObjectTypes.TryMapType(reference.ElementType, assembly, out _) =>
                Unsupported.ObjectReferences,
            IlByRefType reference => ForAnyType(reference.ElementType, assembly, method),
            var plain when Spans.IsSpan(plain, out var element, out _) => Unsupported.ForSpan(element, assembly),
            var plain when Unsupported.IsCollectionExpression(plain) => Unsupported.SpanCollectionExpressions,
            var plain when ValueTypes.Definition(plain, assembly) is { } definition => ForStruct(definition),
            _ => null,
        };

    /// <summary>
    /// What an array of <paramref name="elementType"/> stands for when it is not supported where it is: an array
    /// kept where it outlives its method (a field, a return value) when arrays hold such elements (see
    /// <see cref="StackArrays"/>), else what the element type needs: floating point, a field of a struct, ... (an
    /// array of references is one feature, an array of strings another). Null for a generic parameter's T[].
    /// </summary>
    internal static Unsupported? ForArrayElement(IlType elementType, IlAssembly assembly)
    {
        if (StackArrays.TryMapElementType(elementType, assembly, out _))
        {
            return Unsupported.ArrayEscapes;
        }

        if (elementType is IlArrayType or IlMultiDimensionalArrayType)
        {
            return Unsupported.ArraysOfReferences;
        }

        var element = ForAnyType(StackArrays.Primitive(elementType), assembly);
        if (element == Unsupported.Strings)
        {
            return Unsupported.ArraysOfText;
        }

        return element == Unsupported.ClassesAndObjects
            || element == Unsupported.Interfaces || element == Unsupported.Delegates
            ? Unsupported.ArraysOfReferences
            : element;
    }

    /// <summary>
    /// Why a struct of the program cannot be compiled, or null when it can (or is an enum). A field of an unsupported
    /// type is that type's problem, naming the field: "text strings in the field Person.Name". An array in a field
    /// outlives the method that made it, whatever its elements are (they may be this very struct).
    /// </summary>
    private static Unsupported? ForStruct(IlTypeDefinition type)
    {
        if (!ValueTypes.IsStruct(type))
        {
            return null;
        }

        if (type.GenericParameters.Count > 0)
        {
            return Unsupported.GenericTypes;
        }

        return ValueTypes.ProblemOf(type) switch
        {
            null => null,
            { Kind: StructProblemKind.ExplicitLayout } => Unsupported.ExplicitLayout,
            { Kind: StructProblemKind.FixedBuffer } => Unsupported.UnsafeCode,
            { Kind: StructProblemKind.RefField } => Unsupported.RefFields,
            { Kind: StructProblemKind.FieldType, Field: { } field } problem =>
                (ValueTypes.WithoutModifiers(field.Type) is IlArrayType
                    ? Unsupported.ArrayEscapes
                    : ForAnyType(field.Type, type.Assembly)) is { } unsupported
                    ? unsupported with { What = $"{unsupported.What} in the field {problem.FieldName}" }
                    : Unsupported.TypeNamed(problem.StructName),
            var problem => Unsupported.TypeNamed(problem.StructName),
        };
    }

    /// <summary>
    /// Why an instance method cannot be compiled: one of a class when it is virtual or its class cannot be compiled
    /// (<see cref="ForClassMethod"/>); one of a struct only when the struct cannot be compiled. Null for one of a generic
    /// struct: the generic type is reported on its own.
    /// </summary>
    private static Unsupported? ForInstanceMethod(IlMethod method) =>
        !ValueTypes.IsStruct(method.DeclaringType) ? ForClassMethod(method)
        : method.DeclaringType.GenericParameters.Count > 0 ? null
        : ForStruct(method.DeclaringType);

    /// <summary>
    /// What an instruction does with structs and references that cannot be compiled: a field of a library type or of a
    /// struct that cannot be compiled; a <c>newobj</c> of a library type; a reference to an object (<c>ldind.ref</c>); an
    /// address turned into a number (unsafe code). Null for anything else, which <see cref="ForInstruction"/> checks.
    /// (The objects of the program's classes and their fields are <see cref="ForClasses"/>'.)
    /// </summary>
    private static Unsupported? ForStructOrReference(IlMethod method, IlInstruction instruction)
    {
        var assembly = method.Assembly;
        switch (instruction)
        {
            case { OpCode: IlOpCode.Ldfld or IlOpCode.Ldflda or IlOpCode.Stfld, Operand: IlFieldOperand { Field: var field } }:
                return ForInstanceField(field);

            case { OpCode: IlOpCode.Initobj or IlOpCode.Ldobj or IlOpCode.Stobj or IlOpCode.Cpobj,
                Operand: IlTypeOperand { Type: var type } }:
                return ForAnyType(type, assembly, method);

            case { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var constructor } }:
                // the objects of classes have their verdicts already (ForClasses), a span's constructor spans' (ForSpans)
                return constructor.Definition is { } definition
                    ? ValueTypes.IsStruct(definition.DeclaringType) ? ForStruct(definition.DeclaringType) : Unsupported.CreatingObjects
                    : Spans.Call(constructor) is not null ? null
                    : ForLibraryNew(constructor);

            case { OpCode: IlOpCode.Ldind_Ref or IlOpCode.Stind_Ref }:
                return null; // a reference to an object or a string (ObjectReferences)

            case { Name: var name } when name.StartsWith("conv.", StringComparison.Ordinal) && TakesAddress(Previous(method, instruction)):
                // &x: the address of a local or field, as a number
                return Unsupported.UnsafeCode;

            default:
                return null;
        }
    }

    /// <summary>Why an ldfld, ldflda or stfld of <paramref name="reference"/> cannot be compiled, or null.</summary>
    private static Unsupported? ForInstanceField(IlFieldReference reference)
    {
        if (!reference.IsDefinedInThisAssembly)
        {
            return Unsupported.LibraryField(CSharpNames.Type(reference.DeclaringType) + "." + reference.Name);
        }

        if (reference.Definition is not { } field || field.DeclaringType.GenericParameters.Count > 0)
        {
            return Unsupported.GenericTypes;
        }

        if (!ValueTypes.IsStruct(field.DeclaringType))
        {
            return field.IsStatic ? null : Unsupported.Fields;
        }

        return ForStruct(field.DeclaringType);
    }

    private static IlInstruction? Previous(IlMethod method, IlInstruction instruction) =>
        method.Body!.IndexOf(instruction.Offset) is > 0 and var index ? method.Body.Instructions[index - 1] : null;

    private static bool TakesAddress(IlInstruction? instruction) => instruction?.OpCode is IlOpCode.Ldloca
        or IlOpCode.Ldloca_S or IlOpCode.Ldarga or IlOpCode.Ldarga_S or IlOpCode.Ldsflda or IlOpCode.Ldflda
        or IlOpCode.Ldelema;
}
