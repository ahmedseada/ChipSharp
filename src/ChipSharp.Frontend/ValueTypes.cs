using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// The value types a program defines and the references C# takes to values, as TIR types, on top of what
/// <see cref="SupportedSubset.TryMapType"/> maps:
/// <list type="bullet">
/// <item>an enum of the program is its underlying integer type: <c>enum Color : byte</c> is i8, and a value of it is
/// widened and narrowed like a byte;</item>
/// <item>a struct of the program that is not generic is a <see cref="TirStructType"/> named as C# writes it (e.g.
/// <c>Program.Point</c>), with its instance fields in their order. There is one per struct, made once and shared by
/// every module, because TIR's struct types are equal only to themselves. So is an instance of a generic struct, one
/// per list of type arguments (<c>Pair&lt;int&gt;</c>, see <see cref="Generics.Instantiations"/>);</item>
/// <item>a <c>Span&lt;T&gt;</c> or <c>ReadOnlySpan&lt;T&gt;</c> is an array value, <c>{ ptr, i32 }</c> (see
/// <see cref="Spans"/>), so a ref struct may hold one;</item>
/// <item>a reference (<c>ref</c>, <c>out</c>, <c>in</c>, a ref local, a ref return, a struct method's <c>this</c>) to a
/// value of any of these types is a ptr. In verifiable C# a reference never outlives what it points to, so a plain
/// address is all it needs.</item>
/// </list>
/// Custom modifiers (<c>modreq(IsVolatile)</c>, <c>modreq(InAttribute)</c>, <c>modreq(IsExternalInit)</c>) change
/// nothing here and are looked through. A struct that cannot be compiled has a <see cref="StructProblem"/>.
/// </summary>
internal static class ValueTypes
{
    private static readonly ConditionalWeakTable<IlTypeDefinition, TirStructType> Structs = new();

    /// <summary>
    /// The TIR type of a parameter, local, return type or field: what <see cref="SupportedSubset.TryMapType"/> maps,
    /// or an enum, struct or reference of the program as described above.
    /// </summary>
    public static bool TryMapType(IlType type, IlAssembly assembly, [NotNullWhen(true)] out TirType? tirType)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(assembly);

        if (SupportedSubset.TryMapType(type, out tirType) || Spans.TryMapType(type, assembly, out tirType))
        {
            return true;
        }

        switch (type)
        {
            case IlModifiedType modified:
                return TryMapType(modified.UnmodifiedType, assembly, out tirType);

            case IlByRefType byRef when TryMapType(byRef.ElementType, assembly, out var element) && element != TirType.Void:
                tirType = TirType.Ptr;
                return true;

            case IlNamedType or IlGenericInstanceType when Definition(type, assembly) is { } definition:
                if (IsEnum(definition))
                {
                    return EnumType(definition) is { } underlying && SupportedSubset.TryMapType(underlying, out tirType);
                }

                if (ProblemOf(definition) is null)
                {
                    tirType = StructType(definition);
                    return true;
                }

                break;
        }

        tirType = null;
        return false;
    }

    /// <summary>
    /// The struct or enum of <paramref name="assembly"/> that <paramref name="type"/> names (modifiers looked through),
    /// or the library substitute of the library type it names (<see cref="LibrarySubstitutes"/>), when it is not
    /// generic, or the instance of a generic struct it names (<see cref="Generics.Instantiations.Type"/>: a struct of
    /// its own for each list of type arguments); null for any other type.
    /// </summary>
    public static IlTypeDefinition? Definition(IlType type, IlAssembly assembly) =>
        (WithoutModifiers(type) switch
        {
            IlNamedType named => named.IsDefinedInThisAssembly
                ? assembly.FindType(named.FullName)
                : LibrarySubstitutes.FindType(named),
            IlGenericInstanceType instance => Generics.Instantiations.Type(instance, assembly),
            _ => null,
        }) is { GenericParameters.Count: 0 } definition && (IsStruct(definition) || IsEnum(definition))
            ? definition
            : null;

    /// <summary>True for a struct: a type that extends System.ValueType (enums extend System.Enum).</summary>
    public static bool IsStruct(IlTypeDefinition type) =>
        type.BaseType is IlNamedType { Namespace: "System", Name: "ValueType", DeclaringType: null };

    /// <summary>True for an enum: a type that extends System.Enum.</summary>
    public static bool IsEnum(IlTypeDefinition type) =>
        type.BaseType is IlNamedType { Namespace: "System", Name: "Enum", DeclaringType: null };

    /// <summary>True for an instance method (a constructor too) of a struct: its <c>this</c> is a reference to the struct.</summary>
    public static bool IsStructInstanceMethod(IlMethod method) => !method.IsStatic && IsStruct(method.DeclaringType);

    /// <summary>
    /// The method of the program that <paramref name="instruction"/> runs: what a <c>call</c> calls, or the constructor a
    /// <c>newobj</c> of a struct calls (a <c>newobj</c> of a class creates an object, which is not supported). Null for
    /// any other instruction, and for a library method.
    /// </summary>
    public static IlMethod? CalledMethod(IlInstruction instruction) => instruction switch
    {
        { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method.Definition: { } callee } } => callee,
        { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method.Definition: { } constructor } }
            when IsStructInstanceMethod(constructor) => constructor,
        _ => null,
    };

    /// <summary>The type of a struct method's <c>this</c>: a reference to the struct, e.g. <c>Point&amp;</c>.</summary>
    public static IlType ThisType(IlMethod method) => new IlByRefType(method.DeclaringType.Type);

    /// <summary>The integer type of an enum, which C# keeps in its one instance field, <c>value__</c>.</summary>
    public static IlType? EnumType(IlTypeDefinition type) =>
        type.Fields.SingleOrDefault(field => !field.IsStatic) is { } value ? WithoutModifiers(value.Type) : null;

    /// <summary>
    /// The type that says how a value of <paramref name="type"/> is widened onto the evaluation stack and narrowed back:
    /// the type itself without modifiers, or for an enum of the program its integer type.
    /// </summary>
    public static IlType Underlying(IlType type, IlAssembly assembly)
    {
        var plain = WithoutModifiers(type);
        return Definition(plain, assembly) is { } definition && IsEnum(definition) && EnumType(definition) is { } integer
            ? integer
            : plain;
    }

    /// <summary>The type without its custom modifiers, e.g. <c>int32</c> for <c>int32 modreq(IsVolatile)</c>.</summary>
    public static IlType WithoutModifiers(IlType type)
    {
        while (type is IlModifiedType modified)
        {
            type = modified.UnmodifiedType;
        }

        return type;
    }

    /// <summary>A struct's instance fields, in the order they are laid out: the order C# declares them in.</summary>
    public static IEnumerable<IlField> InstanceFields(IlTypeDefinition type) => type.Fields.Where(field => !field.IsStatic);

    /// <summary>The TIR struct of a struct the program can compile (<see cref="ProblemOf"/> is null).</summary>
    public static TirStructType StructType(IlTypeDefinition type) => Structs.GetValue(type, CreateStructType);

    /// <summary>The struct type an instance field of a struct lives in, and the field's number in it.</summary>
    public static (TirStructType Struct, int Index) FieldOf(IlField field)
    {
        var index = InstanceFields(field.DeclaringType).TakeWhile(f => f != field).Count();
        return (StructType(field.DeclaringType), index);
    }

    /// <summary>
    /// Why a struct cannot be compiled, or null when it can: its layout, or the first of its instance fields whose
    /// type is not supported. A field of another struct of the program that cannot be compiled gives that struct's
    /// problem, so the problem always names the field to change.
    /// </summary>
    public static StructProblem? ProblemOf(IlTypeDefinition type) => ProblemOf(type, []);

    /// <summary>The problem of the struct of the program that <paramref name="type"/> is or refers to; null for any other type.</summary>
    public static StructProblem? ProblemOf(IlType type, IlAssembly assembly) =>
        WithoutModifiers(type) is IlByRefType reference ? ProblemOf(reference.ElementType, assembly)
        : Definition(type, assembly) is { } definition && IsStruct(definition) ? ProblemOf(definition)
        : null;

    private static StructProblem? ProblemOf(IlTypeDefinition type, HashSet<IlTypeDefinition> enclosing)
    {
        if ((type.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout)
        {
            return new StructProblem(StructProblemKind.ExplicitLayout, type);
        }

        // C# makes "fixed int buffer[4]" a field of a nested struct <buffer>e__FixedBuffer, as big as the four ints but
        // with only the first as a field: unsafe code reaches the others
        if (type.Name.StartsWith('<') && type.Name.EndsWith("e__FixedBuffer", StringComparison.Ordinal))
        {
            return new StructProblem(StructProblemKind.FixedBuffer, type);
        }

        if (!enclosing.Add(type))
        {
            return new StructProblem(StructProblemKind.Cycle, type);
        }

        try
        {
            foreach (var field in InstanceFields(type))
            {
                var fieldType = WithoutModifiers(field.Type);
                if (fieldType is IlByRefType)
                {
                    return new StructProblem(StructProblemKind.RefField, type, field);
                }

                if (Definition(fieldType, type.Assembly) is { } inner && IsStruct(inner))
                {
                    if (ProblemOf(inner, enclosing) is { } problem)
                    {
                        return problem;
                    }
                }
                else if (!TryMapType(fieldType, type.Assembly, out _))
                {
                    return new StructProblem(StructProblemKind.FieldType, type, field);
                }
            }
        }
        finally
        {
            enclosing.Remove(type);
        }

        return null;
    }

    private static TirStructType CreateStructType(IlTypeDefinition type)
    {
        var fields = InstanceFields(type).Select(field => (field.Name, TryMapType(field.Type, type.Assembly, out var tirType)
            ? tirType
            : throw new InvalidOperationException($"The field {field} of {type} has no TIR type.")));

        // an empty struct still takes a byte in .NET, and a TIR struct needs a field
        var list = fields.ToList();
        return new TirStructType(CSharpNames.Type(type.Type), list.Count > 0 ? list : [("<empty>", TirType.I8)]);
    }
}

/// <summary>What keeps a struct from compiling.</summary>
internal enum StructProblemKind
{
    /// <summary><c>[StructLayout(LayoutKind.Explicit)]</c> with <c>[FieldOffset]</c>: fields that overlap.</summary>
    ExplicitLayout,

    /// <summary>The struct C# makes for a <c>fixed</c> buffer, which only unsafe code reaches into.</summary>
    FixedBuffer,

    /// <summary>A <c>ref</c> field (of a <c>ref struct</c>).</summary>
    RefField,

    /// <summary>A field whose type is not supported: <see cref="StructProblem.Field"/>.</summary>
    FieldType,

    /// <summary>The struct contains itself, which only broken metadata can say.</summary>
    Cycle,
}

/// <summary>Why <paramref name="Struct"/> cannot be compiled; <paramref name="Field"/> is the field to blame, if one is.</summary>
internal sealed record StructProblem(StructProblemKind Kind, IlTypeDefinition Struct, IlField? Field = null)
{
    /// <summary>The struct as C# writes it, e.g. "Program.Point".</summary>
    public string StructName => CSharpNames.Type(Struct.Type);

    /// <summary>The field as C# names it, e.g. "Person.Name" (a property's backing field by the property's name).</summary>
    public string FieldName => Field is null ? "" : $"{StructName}.{CSharpFieldName(Field.Name)}";

    /// <summary>The problem in words, for the translation's errors: "the field Person.Name has type string".</summary>
    public string Text => Kind switch
    {
        StructProblemKind.ExplicitLayout => $"the struct {StructName} has an explicit layout",
        StructProblemKind.FixedBuffer => $"the struct {StructName} is a fixed buffer",
        StructProblemKind.RefField => $"the struct {StructName} has a ref field, {FieldName}",
        StructProblemKind.FieldType => $"the field {FieldName} has type {CSharpNames.Type(Field!.Type)}",
        _ => $"the struct {StructName} contains itself",
    };

    /// <summary>"X" for the backing field of the auto-property X, <c>&lt;X&gt;k__BackingField</c>; any other name as it is.</summary>
    private static string CSharpFieldName(string name) =>
        name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal)
            ? name[1..name.IndexOf('>', StringComparison.Ordinal)]
            : name;
}
