using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>What spans (<see cref="Spans"/>) cannot do yet, in C# words.</summary>
internal sealed partial record Unsupported
{
    public static readonly Unsupported SpanOfArrayEscapes = Heap(
        "a span of an array that leaves the method that creates the array (returned, or stored where the caller " +
        "sees it)", false,
        "For now, keep the span in the method that creates the array; the methods it calls can take the span as a " +
        "parameter, and return it.");

    public static readonly Unsupported SpanReuse = Heap(
        "a span of an array or of stackalloc made in a loop while a span or reference made there before may still be " +
        "in use", false, "For now, make the array or the stackalloc once, before the loop, and reuse it.");

    public static readonly Unsupported SpanToArray = Heap("copying a span into a new array (ToArray)", false,
        "For now, copy it into an array of your own with CopyTo.");

    public static readonly Unsupported SpansOfReferences = Heap("spans of strings, objects or arrays", true,
        "For now, use spans of numbers, bools, structs or enums.");

    public static readonly Unsupported StackallocSizes = new("stackalloc of a size known only when the program runs",
        false, "For now, give it a constant size, such as stackalloc byte[16].");

    public static readonly Unsupported StackallocPointers = new("stackalloc into a pointer (int* p = stackalloc int[4])",
        false, "For now, make a span of it: Span<int> buffer = stackalloc int[4];");

    public static readonly Unsupported StackallocInitializers = new(
        "a stackalloc initializer that branches (stackalloc int[] { c ? 1 : 2 })", false,
        "For now, store the elements one by one after the stackalloc.");

    public static readonly Unsupported SpanCollectionExpressions = new(
        "collection expressions as spans (Span<int> s = [1, 2, 3])", true,
        "For now, use stackalloc int[] { 1, 2, 3 }, or an array: Span<int> s = new int[] { 1, 2, 3 };");

    public static readonly Unsupported PointerSpans = new("spans of pointers (new Span<T>(void*, int))", true,
        "For now, use stackalloc or an array.");

    public static readonly Unsupported LibraryGenericsOfProgramTypes = new(
        "the library's generic methods (MemoryExtensions.IndexOf, SequenceEqual, ...) on the program's own structs",
        true, "For now, write the loop: for (int i = 0; i < span.Length; i++) if (span[i].Equals(value)) ...");

    /// <summary>
    /// MemoryMarshal.Read, Write, AsBytes or Cast of a type that is not plain data: <paramref name="type"/> as C# writes
    /// it, and why (e.g. "the field Person.Name has type string"). .NET throws ArgumentException there when the program
    /// runs; ChipSharp says so when it compiles.
    /// </summary>
    public static Unsupported MemoryMarshalOf(string type, string why) => Rule(
        $"MemoryMarshal reads and writes as bytes only values with no references in them, laid out as C# lays them " +
        $"out by default, and {type} is not one ({why})",
        "Read and write numbers, bools, enums and structs of them, without [StructLayout(Pack = ...)].");

    /// <summary>
    /// The feature of a span type that cannot be compiled: what its elements need (<c>Span&lt;char&gt;</c> is characters,
    /// a span of strings or objects holds references); null when its elements are fine.
    /// </summary>
    public static Unsupported? ForSpan(IlType element, IlAssembly assembly) =>
        StackArrays.TryMapElementType(element, assembly, out _)
            ? null
            : WholeProgramPass.ForArrayElement(element, assembly) is var unsupported && unsupported == ArraysOfReferences
                ? SpansOfReferences
                : unsupported;

    /// <summary>
    /// True for the types and methods C# makes a collection expression into a span with: the InlineArray structs of
    /// System.Runtime.CompilerServices and the helpers of &lt;PrivateImplementationDetails&gt; that view them.
    /// </summary>
    public static bool IsCollectionExpression(IlType type) => ValueTypes.WithoutModifiers(type) switch
    {
        IlGenericInstanceType { Definition: IlNamedType { Namespace: "System.Runtime.CompilerServices" } named } =>
            named.Name.StartsWith("InlineArray", StringComparison.Ordinal),
        _ => false,
    };
}
