using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Generics;

/// <summary>
/// Generics over classes (M3): a reference type as a type argument, next to the values of generics without a heap
/// (see <see cref="Instantiations"/>). <c>object</c>, a class of the program (an instance of a generic class too, and a
/// library substitute's), an interface an object may implement (see <see cref="VirtualMethods.IsInterface"/>) and an
/// array whose elements an array on the heap may hold are type arguments like a number is: the generic code gets an
/// instance of its own for each, with the type in place of T, so <c>Box&lt;Lane&gt;</c> is a class of its own, whose
/// descriptor names it as .NET does (<c>Box`1[Program+Lane]</c>), with its own static fields, and whose code casts to
/// T, makes a T[] and tests against T exactly as code written for Lane would. Copies that come out the same machine
/// code (a getter, a method that only moves T along) are folded by the linker, so they take the flash of one.
/// </summary>
/// <remarks>
/// An instance over a reference type works on objects, so on a target without a heap every place that uses one is a
/// heap use (<see cref="Use"/>): "generics over classes ... need a heap".
/// </remarks>
internal static class ReferenceArguments
{
    /// <summary>
    /// True when <paramref name="argument"/> is a reference type (not text, which is fixed text, a value here):
    /// <paramref name="problem"/> is then null when the instance can be made, else why not (an array of strings, a
    /// delegate, a generic class over a type that cannot be one, ...). False for a value, and a type not known here.
    /// </summary>
    public static bool IsReference(IlType argument, IlAssembly assembly, out Unsupported? problem)
    {
        ArgumentNullException.ThrowIfNull(argument);
        ArgumentNullException.ThrowIfNull(assembly);
        problem = null;
        switch (ValueTypes.WithoutModifiers(argument))
        {
            case IlPrimitiveType { Kind: IlPrimitiveKind.Object }:
                return true;

            case IlArrayType array:
                problem = HeapArrays.TryMapElementType(array.ElementType, assembly, out _)
                    ? null
                    : WholeProgramPass.ForArrayElement(array.ElementType, assembly);
                return true;

            case IlMultiDimensionalArrayType:
                problem = Unsupported.MultiDimensionalArrays;
                return true;

            case var plain and (IlNamedType or IlGenericInstanceType) when ObjectTypes.TryMapType(plain, assembly, out _):
                return true;

            case IlGenericInstanceType instance when Instantiations.Definition(instance, assembly) is { } generic
                && !ValueTypes.IsStruct(generic):
                problem = Instantiations.Refusal(assembly, instance.Arguments) ?? Kind(generic);
                return true;

            case IlNamedType { IsDefinedInThisAssembly: true } named when assembly.FindType(named.FullName) is { } type
                && !ValueTypes.IsStruct(type) && !ValueTypes.IsEnum(type):
                problem = Kind(type);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="type"/> names an instance (of a generic method's, a generic type's) one of whose type
    /// arguments, or theirs, is a reference type.
    /// </summary>
    public static bool HasReferenceArguments(IlTypeDefinition type, IlAssembly assembly) =>
        type.TypeArguments.Any(argument => Mentions(argument, assembly));

    /// <summary>
    /// The heap use <paramref name="instruction"/> of <paramref name="method"/> is when it calls, makes or names a member
    /// of an instance over a reference type (<c>Box&lt;Lane&gt;</c>, <c>Max&lt;object&gt;</c>): generics over classes,
    /// which a target without a heap does not have. Null for any other instruction.
    /// </summary>
    public static Unsupported? Use(IlMethod method, IlInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(instruction);
        var assembly = method.Assembly;

        // a library member that gives an object (EqualityComparer<int>.Default), outside the library's own code
        if (instruction.Operand is IlMethodOperand { Method.Definition: { } member } && !LibrarySubstitutes.Defines(method)
            && LibrarySubstitutes.Defines(member) && ObjectTypes.TryMapType(member.ReturnType, member.Assembly, out _))
        {
            return Unsupported.DefaultComparers;
        }

        var uses = instruction.Operand switch
        {
            IlMethodOperand { Method: { Definition: { } callee } } =>
                callee.MethodArguments.Any(argument => Mentions(argument, assembly))
                || HasReferenceArguments(callee.DeclaringType, assembly),
            IlFieldOperand { Field.Definition: { } field } => HasReferenceArguments(field.DeclaringType, assembly),
            IlTypeOperand { Type: var type } => Mentions(type, assembly) && type is IlGenericInstanceType,
            _ => false,
        };
        return uses ? Unsupported.HeapUse(Unsupported.GenericsOverClasses) : null;
    }

    /// <summary>True when <paramref name="type"/> is a reference type argument or an instance of a generic type with one.</summary>
    private static bool Mentions(IlType type, IlAssembly assembly) => ValueTypes.WithoutModifiers(type) switch
    {
        IlGenericInstanceType instance => instance.Arguments.Any(argument => Mentions(argument, assembly)),
        var plain => IsReference(plain, assembly, out _),
    };

    /// <summary>What a class, interface or delegate that cannot be a type argument is, as a feature.</summary>
    private static Unsupported Kind(IlTypeDefinition type) =>
        ObjectTypes.IsClass(type) || type.IsInterface ? Unsupported.GenericsOverClasses : Unsupported.Delegates;
}
