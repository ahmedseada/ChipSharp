namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.Type"/>: what <c>typeof(X)</c> and <c>GetType()</c> give, one object per type,
/// which ChipSharp makes as a constant in read-only data (flash on a chip), never on the heap, with the names .NET gives
/// the type (see ChipSharp.Frontend's TypeObjects): so two of the same type are the same object, as in .NET, and
/// <c>==</c>, <c>Equals</c> and a record's <c>EqualityContract</c> compare them by reference. The garbage collector
/// never marks one. Nothing makes one with <c>new</c>: its constructor never runs.
/// </summary>
/// <remarks>
/// Its hash code is its name's (<c>ToString()</c>'s, as a string's is: the same in every run), where .NET's is the
/// object's own. Reflection beyond the names and these tests (members, attributes, <c>Activator</c>, base types,
/// generic arguments) is not here.
/// </remarks>
[Substitute(typeof(System.Type))]
internal sealed class Type : MemberInfo
{
    /// <summary>The bits of <see cref="_flags"/>, which ChipSharp.Frontend's TypeObjects sets.</summary>
    private const int ValueTypeFlag = 1, ArrayFlag = 2, EnumFlag = 4, InterfaceFlag = 8, PrimitiveFlag = 16, GenericFlag = 32;

    private readonly string _name;
    private readonly string? _fullName;
    private readonly string? _namespace;
    private readonly string _text;
    private readonly int _flags;

    private Type(string name, string? fullName, string? ns, string text, int flags)
    {
        _name = name;
        _fullName = fullName;
        _namespace = ns;
        _text = text;
        _flags = flags;
    }

    public override string Name => _name;

    public string? FullName => _fullName;

    public string? Namespace => _namespace;

    public bool IsValueType => (_flags & ValueTypeFlag) != 0;

    public bool IsArray => (_flags & ArrayFlag) != 0;

    public bool IsEnum => (_flags & EnumFlag) != 0;

    public bool IsInterface => (_flags & InterfaceFlag) != 0;

    public bool IsClass => (_flags & (ValueTypeFlag | InterfaceFlag)) == 0;

    public bool IsPrimitive => (_flags & PrimitiveFlag) != 0;

    public bool IsGenericType => (_flags & GenericFlag) != 0;

    public override string ToString() => _text;

    public override bool Equals(object? o) => (object)this == o;

    public bool Equals(Type? o) => (object)this == o;

    public override int GetHashCode() => _text.GetHashCode();

    public static bool operator ==(Type? left, Type? right) => (object?)left == right;

    public static bool operator !=(Type? left, Type? right) => (object?)left != right;
}

/// <summary>
/// ChipSharp's <see cref="System.Reflection.MemberInfo"/>, the base of <see cref="Type"/>, which declares
/// <c>Name</c>: C# calls <c>type.Name</c> as MemberInfo's.
/// </summary>
[Substitute(typeof(System.Reflection.MemberInfo))]
internal abstract class MemberInfo
{
    public abstract string Name { get; }
}

/// <summary>
/// ChipSharp's <see cref="System.Runtime.CompilerServices.RuntimeHelpers"/>, of the members C# calls by itself:
/// <c>EnsureSufficientExecutionStack</c>, which a record's <c>PrintMembers</c> calls first, does nothing here (a stack
/// that overflows stops the program where it does).
/// </summary>
[Substitute(typeof(System.Runtime.CompilerServices.RuntimeHelpers))]
internal static class RuntimeHelpers
{
    public static void EnsureSufficientExecutionStack()
    {
    }
}
