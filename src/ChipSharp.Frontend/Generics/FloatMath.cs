using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Generics;

/// <summary>
/// The static members of double and float that are those of Math and MathF (<c>double.Sin(x)</c>,
/// <c>float.Round(x, 2)</c>, <c>double.Ieee754Remainder(x, y)</c>), and so the generic math of a float type
/// (<c>T.Sqrt(x)</c> through <c>IRootFunctions&lt;T&gt;</c>, <c>T.Sin(x)</c> through <c>ITrigonometricFunctions&lt;T&gt;</c>,
/// ...): .NET's own call Math's and MathF's, so ChipSharp makes each a call of the Math or MathF method it calls, which
/// is a runtime function or ChipSharp.Library's (<see cref="LibrarySubstitutes"/>). Clamp of a float is Math's (MathF
/// has none).
/// </summary>
internal static class FloatMath
{
    private static readonly IlNamedType Math = new("System", "Math", null, "System.Runtime");
    private static readonly IlNamedType MathF = new("System", "MathF", null, "System.Runtime");

    /// <summary>The members double and float share with Math and MathF, by their names in double and float.</summary>
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "Abs", "Acos", "Acosh", "Asin", "Asinh", "Atan", "Atan2", "Atanh", "BitDecrement", "BitIncrement", "Cbrt",
        "Ceiling", "Clamp", "CopySign", "Cos", "Cosh", "Exp", "Floor", "FusedMultiplyAdd", "Ieee754Remainder", "ILogB",
        "Log", "Log10", "Log2", "Max", "MaxMagnitude", "Min", "MinMagnitude", "Pow", "ReciprocalEstimate",
        "ReciprocalSqrtEstimate", "Round", "ScaleB", "Sign", "Sin", "SinCos", "Sinh", "Sqrt", "Tan", "Tanh", "Truncate",
    };

    /// <summary>
    /// The Math or MathF method a static method <paramref name="name"/> of a float type of <paramref name="kind"/> with
    /// <paramref name="signature"/> (in its float type) calls; null when it is not one of them.
    /// </summary>
    public static IlMethodReference? Reference(IlPrimitiveKind kind, string name, IlMethodSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (kind is not (IlPrimitiveKind.Float32 or IlPrimitiveKind.Float64) || signature.HasThis
            || signature.GenericParameterCount != 0 || !Names.Contains(name))
        {
            return null;
        }

        var type = kind == IlPrimitiveKind.Float64 || name == "Clamp" ? Math : MathF;
        return new IlMethodReference(type, name == "Ieee754Remainder" ? "IEEERemainder" : name, signature, [], null);
    }

    /// <summary>
    /// <paramref name="reference"/>, which a program's IL makes, as the call of Math's or MathF's method when it is a
    /// static method of double or float that is one (see <see cref="Reference"/>); any other reference as it is.
    /// </summary>
    public static IlMethodReference Link(IlMethodReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var kind = reference.DeclaringType switch
        {
            IlPrimitiveType { Kind: var primitive } => primitive,
            IlNamedType { FullName: "System.Double", Assembly: not null } => IlPrimitiveKind.Float64,
            IlNamedType { FullName: "System.Single", Assembly: not null } => IlPrimitiveKind.Float32,
            _ => IlPrimitiveKind.Void,
        };
        return reference is { Definition: null, GenericArguments.Count: 0 }
            && Reference(kind, reference.Name, reference.Signature) is { } math
            ? math
            : reference;
    }
}
