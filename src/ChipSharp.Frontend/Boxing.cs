using System.Reflection;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Generics;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>What a boxed value is: a number (bool, char, the whole numbers, nint, nuint, float, double), an enum, or a struct.</summary>
internal enum BoxedKind
{
    Number,
    Enum,
    Struct,
}

/// <summary>
/// A value type whose values the program boxes (see <see cref="Boxing"/>): its IL type as the program names it, its
/// kind, the TIR type of the value in the box, the primitive it is (a number) or is made of (an enum's underlying type),
/// and its definition (an enum or a struct of the program, an instance of a generic struct). Two are equal when they are
/// the same type: the same primitive, or the same definition.
/// </summary>
internal sealed record BoxedType(IlType Type, BoxedKind Kind, TirType ValueType, IlPrimitiveType? Primitive,
    IlTypeDefinition? Definition)
{
    /// <summary>The name .NET gives the type, <c>System.Int32</c>, <c>Program+Point</c>: a box's descriptor's name.</summary>
    public string RuntimeName => VirtualMethods.RuntimeName(Type);

    /// <summary>The type as C# writes it, e.g. "int" or "Program.Point".</summary>
    public string Name => CSharpNames.Type(Type);

    /// <summary>The primitive kind unboxing compares: a number's own, an enum's underlying one; null for a struct.</summary>
    public IlPrimitiveKind? UnboxedKind => Kind == BoxedKind.Struct ? null : Primitive!.Kind;

    public bool Equals(BoxedType? other) =>
        other is not null && Kind == other.Kind && Definition == other.Definition && Primitive == other.Primitive;

    public override int GetHashCode() => HashCode.Combine(Kind, Definition, Primitive);

    public override string ToString() => $"boxed {Name}";
}

/// <summary>
/// Boxing on a target with a heap: <c>box T</c> of a value type makes an object of the type's own class, whose header is
/// the box's descriptor and whose one field (<c>value</c>) is the value; unboxing reads it back. The rules are .NET's:
/// <list type="bullet">
/// <item>A box's descriptor (one per boxed type, see Lowering/BoxingFunctions.cs) holds the type's .NET name
/// (<c>System.Int32</c>, <c>Program+Point</c>, <c>Pair`1[System.Int32]</c>), for an InvalidCastException's message and
/// for object's ToString; its base is System.Object, so a box is no class of the program's, and it has a virtual method
/// table and an interface map like a class's: <see cref="Resolve"/> says what each slot runs on it.</item>
/// <item>ToString, Equals and GetHashCode are the struct's own overrides, else .NET's: a number's and a bool's text and
/// hash, an enum's name and its number's hash, and for a struct ValueType's: its type's name, and Equals field by field
/// (numbers by value, floats as their Equals compares them, NaN equal to NaN and -0.0 to 0.0; objects with their own
/// Equals; nested structs with theirs), which gives what .NET's bitwise or field-by-field comparison gives.
/// ValueType's GetHashCode is not reproducible in .NET (it mixes in a number of the process); ChipSharp's is a
/// combination of the fields' hash codes, the same for equal values and in every run.</item>
/// <item>Interfaces: a boxed struct implements the interfaces its struct does, with the struct's methods (on the value
/// in the box, so a call that changes it changes the box); a boxed number implements IComparable,
/// IComparable&lt;T&gt; and IEquatable&lt;T&gt;, an enum IComparable.</item>
/// <item>Unboxing (<c>unbox.any</c>, <c>unbox</c>) gives the value when the box is of the same type, an enum and its
/// underlying type counting as one (<c>(int)(object)Color.Green</c>, <c>(Color)(object)1</c>), but not two numbers of
/// different types (<c>(int)(object)5u</c> fails); null stops the program with NullReferenceException's message, a box
/// of another type with InvalidCastException's, as .NET's says them. A type test (<c>o is int</c>) wants exactly the
/// type: a boxed enum is not an int.</item>
/// <item><c>Nullable&lt;T&gt;</c> boxes as .NET boxes it: an empty one is null, one with a value is a boxed T; unboxing
/// to <c>T?</c> takes null (an empty one) or exactly a boxed T.</item>
/// </list>
/// A constrained call on a value (<c>constrained. T callvirt</c>, C#'s <c>point.ToString()</c> on a struct without one
/// of its own, or generic code over values) runs the same methods on the value itself, with no box.
/// </summary>
internal static class Boxing
{
    /// <summary>The library interfaces a boxed number implements, besides IComparable&lt;T&gt; and IEquatable&lt;T&gt;.</summary>
    private const string ComparableName = "System.IComparable";

    /// <summary>
    /// The boxed type of a value of <paramref name="type"/> (of <paramref name="assembly"/>'s IL): a number, an enum, or a
    /// struct the program can compile (an instance of a generic one too); for <c>T?</c>, T's, which is what its box holds.
    /// Null for any other type: a reference, a library struct (TimeSpan), a span, a pointer.
    /// </summary>
    public static BoxedType? Of(IlType type, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(assembly);
        var plain = Nullables.ValueOf(type) ?? StackArrays.Primitive(ValueTypes.WithoutModifiers(type));
        if (plain is IlPrimitiveType { Kind: >= IlPrimitiveKind.Bool and <= IlPrimitiveKind.NativeUInt } primitive)
        {
            return SupportedSubset.TryMapType(primitive, out var number)
                ? new BoxedType(primitive, BoxedKind.Number, number, primitive, null)
                : null;
        }

        // a library struct but the collections' (KeyValuePair, their enumerators, which .NET boxes too) and the tuples
        if (ValueTypes.Definition(plain, assembly) is not { } definition
            || (LibrarySubstitutes.Substituted.ContainsKey(definition.GenericDefinition ?? definition)
                && !(definition.GenericDefinition ?? definition).FullName.StartsWith("ChipSharp.Library.Pairs.", StringComparison.Ordinal)
                && !(definition.GenericDefinition ?? definition).FullName.StartsWith("ChipSharp.Library.Collections.", StringComparison.Ordinal)
                && !Tuples.IsTuple(definition)))
        {
            return null;
        }

        if (ValueTypes.IsEnum(definition))
        {
            return ValueTypes.EnumType(definition) is IlPrimitiveType underlying
                && SupportedSubset.TryMapType(underlying, out var bits) && bits != TirType.Bool
                ? new BoxedType(plain, BoxedKind.Enum, bits, underlying, definition)
                : null;
        }

        return ValueTypes.IsStruct(definition) && ValueTypes.ProblemOf(definition) is null
            && !Spans.IsSpan(plain, out _, out _)
            ? new BoxedType(plain, BoxedKind.Struct, ValueTypes.StructType(definition), null, definition)
            : null;
    }

    /// <summary>The boxed type of a struct or enum of the program (an instance of a generic struct too); null when it is none.</summary>
    public static BoxedType? OfDefinition(IlTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Of(type.GenericDefinition is null ? type.Type : StructType(type), type.Assembly);
    }

    /// <summary>
    /// The value type instruction <paramref name="index"/> of <paramref name="instructions"/> (of a method of
    /// <paramref name="assembly"/>) boxes, for <see cref="VirtualDispatch"/>: <c>box</c> (but of a number or an enum that
    /// Console.Write prints at once, which printed text writes without a box), and a struct a <c>$"..."</c> prints, whose
    /// ToString its box would run. Null for any other instruction.
    /// </summary>
    public static BoxedType? BoxedAt(IReadOnlyList<IlInstruction> instructions, int index, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        switch (instructions[index])
        {
            case { OpCode: IlOpCode.Box, Operand: IlTypeOperand { Type: var type } }:
                var boxed = Of(type, assembly);
                return boxed is { Kind: not BoxedKind.Struct } && index + 1 < instructions.Count
                    && IsConsoleWriteOfObject(instructions[index + 1])
                    ? null
                    : boxed;

            case
            {
                OpCode: IlOpCode.Call,
                Operand: IlMethodOperand { Method: { Name: "AppendFormatted", Definition: null, GenericArguments: [var printed] } handler },
            } when Unsupported.IsInterpolatedStringHandler(handler.DeclaringType):
                return Of(printed, assembly) is { Kind: BoxedKind.Struct } value ? value : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// For the <c>callvirt</c> at <paramref name="index"/> after <c>constrained. T</c>, a call of object's ToString,
    /// Equals or GetHashCode or of an interface's method on a value (C#'s <c>point.ToString()</c>, and generic code over
    /// values): the value type (T's, for <c>T?</c>) and what the call runs (see <see cref="Constrained"/>). Null for any
    /// other instruction.
    /// </summary>
    public static (BoxedType Boxed, DispatchTarget Target)? ConstrainedAt(IReadOnlyList<IlInstruction> instructions,
        int index, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        return index > 0 && instructions[index] is { OpCode: IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } }
            && instructions[index - 1] is { OpCode: IlOpCode.Constrained, Operand: IlTypeOperand { Type: var type } }
            && Constrained(type, callee, assembly) is { } target
            ? (target.Boxed!, target)
            : null;
    }

    /// <summary>
    /// What <c>constrained. type callvirt callee</c> runs on the value, with no box: what the slot runs on a box of the
    /// type (<see cref="Resolve"/>), on the value the reference refers to; for a nullable, what it runs on its value
    /// (ToString, Equals and GetHashCode only, which say what they say for no value themselves). Null when the call is
    /// none of those.
    /// </summary>
    public static DispatchTarget? Constrained(IlType type, IlMethodReference callee, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(callee);
        if (Of(type, assembly) is not { } boxed || VirtualMethods.SlotCalled(callee) is not { } slot
            || (Nullables.IsNullable(type) && slot.Kind != DispatchSlotKind.Object))
        {
            return null;
        }

        return Resolve(boxed, slot);
    }

    /// <summary>The slot of object's <paramref name="name"/>: Equals, GetHashCode or ToString.</summary>
    public static DispatchSlot ObjectSlot(string name) => new(DispatchSlotKind.Object, IlType.Object, name, name switch
    {
        "Equals" => new IlMethodSignature(true, IlType.Bool, [IlType.Object]),
        "GetHashCode" => new IlMethodSignature(true, IlType.Int32, []),
        "ToString" => new IlMethodSignature(true, IlType.String, []),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not one of object's virtual methods."),
    });

    /// <summary>
    /// True for an instruction that hands an object to text, whose ToString ChipSharp calls: <c>AppendFormatted</c> of
    /// a reference (not a string) for <c>$"..."</c>, and <c>string.Format</c>, <c>string.Concat</c> and
    /// <c>Console.Write</c> or <c>WriteLine</c> of a format, with values as objects.
    /// </summary>
    public static bool PrintsObjectAt(IlInstruction instruction, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        if (instruction is not { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: { Definition: null } callee } })
        {
            return false;
        }

        if (callee is { Name: "AppendFormatted", GenericArguments: [var hole] } && Unsupported.IsInterpolatedStringHandler(callee.DeclaringType))
        {
            var plain = StackArrays.Primitive(ValueTypes.WithoutModifiers(hole));
            return !FixedText.IsText(plain) && (ObjectTypes.TryMapType(plain, assembly, out _) || plain is IlArrayType);
        }

        return callee is { HasThis: false, Name: "Format" or "Concat" or "Write" or "WriteLine" }
            && callee.DeclaringType is IlNamedType { Namespace: "System", Name: "String" or "Console", IsDefinedInThisAssembly: false }
            && callee.ParameterTypes.Any(type => ObjectTypes.IsSystemObject(type) || IsObjects(type));
    }

    /// <summary>True for <c>object[]</c> and <c>ReadOnlySpan&lt;object&gt;</c>, a <c>params</c> of objects.</summary>
    private static bool IsObjects(IlType type) => ValueTypes.WithoutModifiers(type) switch
    {
        IlArrayType { ElementType: var element } => ObjectTypes.IsSystemObject(element),
        var span => Spans.IsSpan(span, out var element, out _) && ObjectTypes.IsSystemObject(element),
    };

    /// <summary>True for a call of <c>object.Equals(object, object)</c>.</summary>
    public static bool IsObjectEquals(IlInstruction instruction) => instruction is
    {
        OpCode: IlOpCode.Call,
        Operand: IlMethodOperand
        {
            Method: { Name: "Equals", HasThis: false, Definition: null, ParameterTypes: [var first, var second] } callee,
        },
    } && ObjectTypes.IsSystemObject(callee.DeclaringType) && ObjectTypes.IsSystemObject(first)
        && ObjectTypes.IsSystemObject(second);

    /// <summary>True for a call of Console.Write(object) or Console.WriteLine(object).</summary>
    public static bool IsConsoleWriteOfObject(IlInstruction? instruction) => instruction is
    {
        OpCode: IlOpCode.Call,
        Operand: IlMethodOperand
        {
            Method:
            {
                Name: "Write" or "WriteLine", Definition: null,
                DeclaringType: IlNamedType { FullName: "System.Console" },
                ParameterTypes: [IlPrimitiveType { Kind: IlPrimitiveKind.Object }],
            },
        },
    };

    /// <summary>
    /// True when unboxing a box of <paramref name="boxed"/> to <paramref name="target"/> gives its value: the same type,
    /// or numbers and enums of the same primitive kind (an enum is its underlying type to unboxing).
    /// </summary>
    public static bool Unboxes(BoxedType target, BoxedType boxed) =>
        target.Kind == BoxedKind.Struct || boxed.Kind == BoxedKind.Struct ? target == boxed : target.UnboxedKind == boxed.UnboxedKind;

    /// <summary>True when a box of <paramref name="boxed"/> implements <paramref name="interfaceType"/>.</summary>
    public static bool Implements(BoxedType boxed, IlType interfaceType)
    {
        ArgumentNullException.ThrowIfNull(boxed);
        var plain = VirtualMethods.Canonical(interfaceType);
        return boxed.Kind switch
        {
            BoxedKind.Struct => InterfacesOf(boxed.Definition!).Contains(plain),
            BoxedKind.Enum => IsLibrary(plain, ComparableName),
            _ => IsLibrary(plain, ComparableName) || OfItself(boxed, plain) is not null,
        };
    }

    /// <summary>
    /// True when a variable of <paramref name="variable"/> may hold a box of <paramref name="boxed"/>: object (or a type
    /// not known here), System.ValueType, System.Enum for an enum, or an interface the box implements.
    /// </summary>
    public static bool Fits(BoxedType boxed, IlType? variable)
    {
        ArgumentNullException.ThrowIfNull(boxed);
        if (variable is null || ObjectTypes.IsSystemObject(variable))
        {
            return true;
        }

        var plain = ValueTypes.WithoutModifiers(variable);
        return plain is IlNamedType { Namespace: "System", Name: "ValueType", IsDefinedInThisAssembly: false }
            || (boxed.Kind == BoxedKind.Enum && plain is IlNamedType { Namespace: "System", Name: "Enum", IsDefinedInThisAssembly: false })
            || Implements(boxed, plain);
    }

    /// <summary>True when <paramref name="slot"/> is one a box of <paramref name="boxed"/> has: object's, or an interface's it implements.</summary>
    public static bool Has(BoxedType boxed, DispatchSlot slot) => slot.Kind switch
    {
        DispatchSlotKind.Object => true,
        DispatchSlotKind.Interface => Implements(boxed, slot.Owner),
        _ => false,
    };

    /// <summary>
    /// What <paramref name="slot"/> runs on a box of <paramref name="boxed"/> (see the summary): a method of the struct, or
    /// of ChipSharp.Library's Numbers for a number's IComparable&lt;T&gt; and IEquatable&lt;T&gt;, called on the value in
    /// the box; or what ChipSharp makes for it (<see cref="DispatchTarget.ObjectMethod"/>: ToString, Equals, GetHashCode,
    /// and IComparable's CompareTo of a number or an enum). Null when the box has no such slot.
    /// </summary>
    public static DispatchTarget? Resolve(BoxedType boxed, DispatchSlot slot)
    {
        ArgumentNullException.ThrowIfNull(boxed);
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Kind == DispatchSlotKind.Object)
        {
            return boxed.Kind == BoxedKind.Struct && Override(boxed.Definition!, slot.Name) is { } own
                ? DispatchTarget.Of(own) with { Boxed = boxed }
                : new DispatchTarget(null, slot.Name) { Boxed = boxed };
        }

        if (slot.Kind != DispatchSlotKind.Interface || !Implements(boxed, slot.Owner))
        {
            return null;
        }

        if (boxed.Kind == BoxedKind.Struct)
        {
            var callee = new IlMethodReference(slot.Owner, slot.Name, slot.Signature, [], null);
            return ConstrainedCalls.Implementation(boxed.Definition!, callee, boxed.Definition!.Assembly) is { HasBody: true } method
                ? DispatchTarget.Of(method) with { Boxed = boxed }
                : null;
        }

        if (IsLibrary(ValueTypes.WithoutModifiers(slot.Owner), ComparableName))
        {
            return slot.Name == "CompareTo" ? new DispatchTarget(null, "CompareTo") { Boxed = boxed } : null;
        }

        return NumbersMethod(boxed, slot) is { } numbers ? DispatchTarget.Of(numbers) with { Boxed = boxed } : null;
    }

    /// <summary>
    /// The struct's own override of object's <paramref name="name"/> (ToString, Equals or GetHashCode) with a body; null
    /// when it has none (it runs ValueType's).
    /// </summary>
    public static IlMethod? Override(IlTypeDefinition type, string name)
    {
        ArgumentNullException.ThrowIfNull(type);
        var signature = name switch
        {
            "Equals" => new IlMethodSignature(true, IlType.Bool, [IlType.Object]),
            "GetHashCode" => new IlMethodSignature(true, IlType.Int32, []),
            "ToString" => new IlMethodSignature(true, IlType.String, []),
            _ => null,
        };
        return signature is null ? null : type.Methods.FirstOrDefault(method => method is { IsStatic: false, HasBody: true }
            && method.Name == name && (method.Attributes & MethodAttributes.Virtual) != 0 && method.Signature == signature);
    }

    /// <summary>
    /// The methods the program runs when it boxes <paramref name="boxed"/> (or prints such a value): the struct's own
    /// ToString, Equals and GetHashCode, and those of the structs in its fields; for the whole-program pass to reach.
    /// </summary>
    public static IEnumerable<IlMethod> OwnMethods(BoxedType boxed)
    {
        ArgumentNullException.ThrowIfNull(boxed);
        return boxed.Kind != BoxedKind.Struct
            ? []
            : StructsIn(boxed.Definition!).SelectMany(type => new[] { "ToString", "Equals", "GetHashCode" }
                .Select(name => Override(type, name)).OfType<IlMethod>()).Distinct();
    }

    /// <summary>
    /// The structs whose values ValueType's Equals of <paramref name="boxed"/> boxes: those in its fields (in the fields
    /// of a nullable too) that have an Equals of their own, which takes the other value as an object.
    /// </summary>
    public static IEnumerable<BoxedType> BoxedByEquals(BoxedType boxed)
    {
        ArgumentNullException.ThrowIfNull(boxed);
        if (boxed.Kind != BoxedKind.Struct || Override(boxed.Definition!, "Equals") is not null)
        {
            return [];
        }

        return StructsIn(boxed.Definition!).Skip(1).Where(type => Override(type, "Equals") is not null)
            .Select(type => Of(type.GenericDefinition is null ? type.Type : StructType(type), type.Assembly))
            .OfType<BoxedType>();
    }

    /// <summary>The interfaces a struct implements, as its metadata lists them, with an instance's type arguments in place.</summary>
    private static IReadOnlyList<IlType> InterfacesOf(IlTypeDefinition type) => type.GenericDefinition is null
        ? [.. type.Assembly.Interfaces(type).Select(VirtualMethods.Canonical)]
        : [.. type.Assembly.Interfaces(type.GenericDefinition)
            .Select(i => VirtualMethods.Canonical(new Substitution(type.TypeArguments, []).Apply(i)))];

    /// <summary>The struct and every struct in its fields (and in theirs), each once, the struct first.</summary>
    private static List<IlTypeDefinition> StructsIn(IlTypeDefinition type)
    {
        var found = new List<IlTypeDefinition>();
        void Visit(IlTypeDefinition current)
        {
            if (found.Contains(current))
            {
                return;
            }

            found.Add(current);
            foreach (var field in ValueTypes.InstanceFields(current))
            {
                var fieldType = Nullables.ValueOf(field.Type) ?? ValueTypes.WithoutModifiers(field.Type);
                if (ValueTypes.Definition(fieldType, current.Assembly) is { } inner && ValueTypes.IsStruct(inner)
                    && !LibrarySubstitutes.Substituted.ContainsKey(inner.GenericDefinition ?? inner))
                {
                    Visit(inner);
                }
            }
        }

        Visit(type);
        return found;
    }

    /// <summary>The IL type of an instance of a generic struct: its definition's type with the instance's type arguments.</summary>
    private static IlType StructType(IlTypeDefinition instance) =>
        new IlGenericInstanceType(instance.GenericDefinition!.Type, instance.TypeArguments);

    /// <summary>The method of ChipSharp.Library's Numbers for a number's IComparable&lt;T&gt;.CompareTo or IEquatable&lt;T&gt;.Equals.</summary>
    private static IlMethod? NumbersMethod(BoxedType boxed, DispatchSlot slot) =>
        LibrarySubstitutes.Assembly.FindType(ConstrainedCalls.NumbersType)?.Methods.FirstOrDefault(method =>
            method.IsStatic && method.Name == slot.Name && method.ReturnType == slot.Signature.ReturnType
            && method.Signature.ParameterTypes.SequenceEqual([new IlByRefType(boxed.Primitive!), .. slot.Signature.ParameterTypes]));

    /// <summary>IComparable&lt;T&gt; or IEquatable&lt;T&gt; of a number's own type: the interface; null for any other.</summary>
    private static IlType? OfItself(BoxedType boxed, IlType interfaceType) =>
        interfaceType is IlGenericInstanceType
        {
            Definition: IlNamedType { IsDefinedInThisAssembly: false, FullName: "System.IComparable`1" or "System.IEquatable`1" },
            Arguments: [var argument],
        } && StackArrays.Primitive(ValueTypes.WithoutModifiers(argument)) == boxed.Primitive
            ? interfaceType
            : null;

    private static bool IsLibrary(IlType type, string fullName) =>
        type is IlNamedType { IsDefinedInThisAssembly: false } named && named.FullName == fullName;
}
