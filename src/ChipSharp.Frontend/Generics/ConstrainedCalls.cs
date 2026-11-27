using System.Reflection;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Generics;

/// <summary>
/// <c>constrained. T</c> before a call in an instance of generic code, where T is known (see
/// <see cref="Specialization"/>): what the call does on that T, without boxing.
/// <list type="bullet">
/// <item>On a struct: a call of its own method for the one named, an interface's (implicitly, a public method with its
/// name and signature, or explicitly, <c>void IDevice.Tick()</c>), object's ToString, Equals or GetHashCode it
/// overrides, or a static method for an interface's static abstract one. The struct's address, which the constrained
/// call has as its <c>this</c>, is what its own method takes.</item>
/// <item>On a number or bool: <c>ToString()</c> is its own, which printed text prints; <c>CompareTo</c>,
/// <c>Equals</c> and <c>GetHashCode</c> are those of ChipSharp.Library's <c>Numbers</c>, which take its address; the
/// static members of generic math are the operations themselves (<see cref="GenericMath"/>), and those a float type
/// shares with Math and MathF (<c>T.Sqrt</c>, <c>T.Sin</c>, ...) calls of those (<see cref="FloatMath"/>).</item>
/// </list>
/// </summary>
internal static class ConstrainedCalls
{
    /// <summary>ChipSharp.Library's class of the numbers' methods.</summary>
    public const string NumbersType = "ChipSharp.Library.Numbers";

    private static readonly IlNamedType Numbers = new("ChipSharp.Library", "Numbers", null, "ChipSharp.Library");

    /// <summary>The instructions <c>constrained. type</c> and the call of <paramref name="callee"/> become; null when unknown.</summary>
    public static IReadOnlyList<(IlOpCode, IlOperand?)>? Replacement(IlAssembly owner, IlType type, IlMethodReference callee)
    {
        if (Substitution.IsOpen(type))
        {
            return null;
        }

        var plain = StackArrays.Primitive(ValueTypes.WithoutModifiers(type));
        if (plain is IlPrimitiveType { Kind: not (IlPrimitiveKind.String or IlPrimitiveKind.Object) } primitive)
        {
            // T.Sqrt(x), T.Sin(x), ... on a float type: the Math or MathF method double's or float's calls
            if (!callee.HasThis && callee.GenericArguments.Count == 0 && FloatMath.Reference(primitive.Kind, callee.Name,
                    Interface(callee.DeclaringType).Apply(callee.Signature)) is { } math)
            {
                return [(IlOpCode.Call, new IlMethodOperand(LibrarySubstitutes.Link(owner, math)))];
            }

            return callee.HasThis ? NumberMethod(primitive, callee) : GenericMath.Replacement(primitive.Kind, callee);
        }

        return ValueTypes.Definition(plain, owner) is { } definition && ValueTypes.IsStruct(definition)
            && Implementation(definition, callee, owner) is { } implementation
            ? [(IlOpCode.Call, new IlMethodOperand(new IlMethodReference(plain, implementation.Name,
                implementation.Signature, [], implementation)))]
            : null;
    }

    /// <summary>
    /// The method of <paramref name="type"/>, a struct (or an instance of a generic struct), that a call of
    /// <paramref name="callee"/> (in <paramref name="owner"/>'s IL) on it runs: the one that implements it explicitly,
    /// else the one with its name and signature (virtual, for an instance method: C# makes interface implementations and
    /// overrides virtual; for a library substitute, the public one that stands for the library type's); null when it has
    /// none (it does not override ToString, ...), or when <paramref name="callee"/> is generic itself.
    /// </summary>
    public static IlMethod? Implementation(IlTypeDefinition type, IlMethodReference callee, IlAssembly owner)
    {
        if (callee.GenericArguments.Count > 0)
        {
            return null;
        }

        var wanted = Interface(callee.DeclaringType).Apply(callee.Signature);
        var generic = type.GenericDefinition ?? type;
        var ofType = new Substitution(type.TypeArguments, []);
        foreach (var (body, declaration) in generic.Assembly.MethodImplementations(generic))
        {
            var declared = ofType.Apply(declaration.DeclaringType);
            if (declaration.Name == callee.Name && declared == callee.DeclaringType
                && Interface(declared).Apply(declaration.Signature) == wanted)
            {
                return Instantiations.MethodOf(type, body);
            }
        }

        if (LibrarySubstitutes.Substituted.ContainsKey(generic))
        {
            return type.Methods.FirstOrDefault(method => method.Name == callee.Name
                && (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public
                && LibrarySubstitutes.HasSignature(method, wanted, owner));
        }

        return type.Methods.FirstOrDefault(method => method.Name == callee.Name && method.IsStatic == !callee.HasThis
            && (method.IsStatic || (method.Attributes & MethodAttributes.Virtual) != 0)
            && method.GenericParameters.Count == 0 && method.ReturnType == wanted.ReturnType
            && method.Signature.ParameterTypes.SequenceEqual(wanted.ParameterTypes));
    }

    /// <summary>
    /// <paramref name="reference"/>, a call of a number's own CompareTo, Equals or GetHashCode (<c>x.CompareTo(y)</c> on
    /// an int, outside generic code too), linked to the method of ChipSharp.Library's Numbers that does what it does;
    /// any other reference as it is.
    /// </summary>
    public static IlMethodReference Link(IlMethodReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference is { HasThis: true, Definition: null, GenericArguments.Count: 0, Name: "CompareTo" or "Equals" or "GetHashCode" }
            && StackArrays.Primitive(reference.DeclaringType) is IlPrimitiveType primitive
            && NumberMethod(primitive, reference) is [(IlOpCode.Call, IlMethodOperand { Method: var linked })]
            ? linked
            : reference;
    }

    /// <summary>
    /// A number's own ToString, or the method of ChipSharp.Library's Numbers with the name of <paramref name="callee"/>
    /// that takes the number's address and then what <paramref name="callee"/> takes (CompareTo, Equals, GetHashCode).
    /// </summary>
    private static IReadOnlyList<(IlOpCode, IlOperand?)>? NumberMethod(IlPrimitiveType type, IlMethodReference callee)
    {
        if (callee is { Name: "ToString", ParameterTypes.Count: 0, Definition: null } && LibraryName(type.Kind) is { } name)
        {
            var own = new IlNamedType("System", name, null, "System.Runtime");
            var toString = new IlMethodReference(own, "ToString", new IlMethodSignature(true, IlType.String, []), [], null);
            return [(IlOpCode.Call, new IlMethodOperand(toString))];
        }

        var wanted = Interface(callee.DeclaringType).Apply(callee.Signature);
        var method = LibrarySubstitutes.Assembly.FindType(NumbersType)?.Methods.FirstOrDefault(method =>
            method.IsStatic && method.Name == callee.Name && method.ReturnType == wanted.ReturnType
            && method.Signature.ParameterTypes.SequenceEqual([new IlByRefType(type), .. wanted.ParameterTypes]));
        return method is null
            ? null
            : [(IlOpCode.Call, new IlMethodOperand(new IlMethodReference(Numbers, method.Name, method.Signature, [], method)))];
    }

    /// <summary>The type arguments of an interface (or a class) a method is called on, for its <c>!0</c>, <c>!1</c>, ...</summary>
    private static Substitution Interface(IlType type) =>
        new(type is IlGenericInstanceType { Arguments: var arguments } ? arguments : [], []);

    /// <summary>The name of the library type of a primitive, e.g. "Int32" for int32.</summary>
    private static string? LibraryName(IlPrimitiveKind kind) => kind switch
    {
        IlPrimitiveKind.Bool => "Boolean",
        IlPrimitiveKind.Char => "Char",
        IlPrimitiveKind.Int8 => "SByte",
        IlPrimitiveKind.UInt8 => "Byte",
        IlPrimitiveKind.Int16 => "Int16",
        IlPrimitiveKind.UInt16 => "UInt16",
        IlPrimitiveKind.Int32 => "Int32",
        IlPrimitiveKind.UInt32 => "UInt32",
        IlPrimitiveKind.Int64 => "Int64",
        IlPrimitiveKind.UInt64 => "UInt64",
        IlPrimitiveKind.Float32 => "Single",
        IlPrimitiveKind.Float64 => "Double",
        IlPrimitiveKind.NativeInt => "IntPtr",
        IlPrimitiveKind.NativeUInt => "UIntPtr",
        _ => null,
    };
}
