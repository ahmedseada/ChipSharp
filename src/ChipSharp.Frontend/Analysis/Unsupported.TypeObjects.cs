namespace ChipSharp.Frontend.Analysis;

/// <summary>What of types as objects (see <see cref="TypeObjects"/>) does not compile: the rest of reflection.</summary>
internal sealed partial record Unsupported
{
    private const string TypeMembers = "ChipSharp's System.Type has Name, FullName, Namespace, ToString, ==, Equals, " +
        "GetHashCode, IsValueType, IsArray, IsEnum, IsClass, IsInterface, IsPrimitive and IsGenericType";

    /// <summary><c>typeof</c> of a generic type itself, not an instance of it: <c>typeof(List&lt;&gt;)</c>.</summary>
    public static readonly Unsupported OpenGenericTypeOf = new("typeof of an open generic type (typeof(List<>))", false,
        "For now, name an instance of it, such as typeof(List<int>).");

    /// <summary>A member of System.Type (or of System.Reflection, System.Activator) that type objects do not have.</summary>
    public static Unsupported Reflection(string member) => new($"reflection ({member})", false,
        $"{TypeMembers}; for more, give the types a virtual method that says it, or switch on the object's type.");
}
