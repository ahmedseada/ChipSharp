namespace ChipSharp.Library;

/// <summary>
/// Marks a type of this library as ChipSharp's own C# for a type of another library, e.g.
/// <c>[Substitute(typeof(System.TimeSpan))] readonly struct TimeSpan</c>. Where a program uses a member of that type,
/// ChipSharp compiles the public member of this type with the same name and signature instead (in a signature, this
/// type stands for the type it substitutes), together with the program. A member this type does not have stays a
/// library member ChipSharp does not compile.
/// </summary>
/// <remarks>
/// A struct substitute is that struct for the program too: its locals, fields and parameters of the library type have
/// this struct's fields, so they must be the library type's (e.g. TimeSpan's one long of ticks). A substitute is written
/// in the C# ChipSharp compiles; where .NET throws, it calls a <see cref="Runtime"/> function, which stops the program.
/// </remarks>
/// <param name="type">The library type this type stands for.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, Inherited = false)]
internal sealed class SubstituteAttribute(Type type) : Attribute
{
    /// <summary>The library type this type stands for, e.g. <c>typeof(System.TimeSpan)</c>.</summary>
    public Type Type { get; } = type;
}
