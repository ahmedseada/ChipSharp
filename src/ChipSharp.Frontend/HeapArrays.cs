using System.Diagnostics.CodeAnalysis;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// Arrays on the heap: on a target with a heap, an array that outlives the method that makes it, or whose size is known
/// only when the program runs, is an object on the heap, which the garbage collector frees once nothing uses it. Arrays
/// may be null, hold references (arrays of arrays, of objects), and be kept in static fields, in the fields of objects
/// and in other arrays. An array that stays in its method with a constant size is still on the stack (see
/// <see cref="StackArrays"/>), and an array a static field is given once still needs no heap (see
/// <see cref="StaticArrays"/>): the cheap ones stay cheap, on every target.
/// </summary>
/// <remarks>
/// <para>
/// <b>Values and stored references.</b> An array value, on IL's evaluation stack, in a local, a parameter or a return
/// value, is the same <see cref="StackArrays.ArrayType"/> view, <c>{ ptr data, i32 length }</c>, for every kind of
/// array: on the stack, in a static field (in flash, or in RAM) and on the heap. So one parameter takes them all, and
/// what builds on array values (indexing, the bounds check, <c>.Length</c>, spans over arrays) works on each. For an array
/// on the heap, <c>data</c> is the address of its element 0 inside the object (<see cref="TirLayout.ArrayDataOffset"/>
/// from its start), which keeps the object alive while the view is on the stack or in a register: the collector reads
/// the stack conservatively and takes an address inside an object for the object. Null is the view of zeros (C#'s
/// <c>default</c>), and every array that is not null has a <c>data</c> that is not null, the empty ones too (see
/// below), so a null test of a view tests <c>data</c>.
/// </para>
/// <para>
/// The places the collector reads precisely hold the array object itself, a <c>ref</c>: a static field that the program
/// assigns while it runs (a static root), a field of an object, an element of an array of references. Storing a view
/// there turns it into the object (<c>arrayobject data</c>: <c>data</c> less the offset of element 0); reading one turns
/// it back into a view (<c>arraydata</c> and <c>arraylength</c>, or the null view). That is right only for an array that
/// is an object, so the rule below makes sure that every array that can reach such a place is one.
/// </para>
/// <para>
/// <b>The rule</b> (checked by the whole-program pass and again by the IL-to-TIR translation, with
/// <see cref="ArrayEscapeAnalysis"/>, which follows where each array of a method can come from): an array <i>is kept</i>
/// where it is returned, stored in a static field (but its own initializer's, see <see cref="StaticArrays"/>), in a field
/// of an object or in an element of another array, turned into an object (<c>object o = array;</c>), where a reference
/// to one of its elements is returned, or where it is passed to a parameter that is kept in the method it belongs to
/// (<see cref="Keeps"/>: found for the whole program, through every call, until nothing changes). A <c>newarr</c> makes
/// an array on the heap when what it makes is kept, when its size is not a constant, or when it is made again in a loop
/// while the one from before is still in use; any other stays on the stack. What a method gets back from a call, or reads
/// from a field or an element, was kept, so it is an object (or null). An array in a static field that needs no heap
/// (a constant table in flash, or a fixed array in RAM) has no object around it, and must not be kept:
/// <see cref="Unsupported.StaticArrayKept"/>. On a target without a heap, everything this needs is what it was before the
/// heap came: "needs a heap" (see <see cref="Unsupported.IsHeapUse"/>).
/// </para>
/// <para>
/// <b>Null</b> stops the program with .NET's NullReferenceException message where .NET throws it: the length of null,
/// an element of null. A view that may be null (<see cref="MayBeNull"/>, found like the kept parameters) is measured with
/// a null test, and its bounds check tells null from an index outside the array only when the index is not in range, so
/// an element of an array that may be null costs nothing more on the way that works. A view that is never null (a
/// <c>newarr</c>, a static array, a parameter no caller gives null or an array read from a field) is used as before.
/// </para>
/// <para>
/// <b>Empty arrays</b> have a <c>data</c> that is not null all the same: <c>Array.Empty&lt;T&gt;()</c> (C#'s <c>[]</c>) is
/// one empty array object of the program's, outside the heap (in flash on a chip), which may be kept like any object;
/// an empty static array is that one too; an empty array on the stack has a byte of its own, so that two compare
/// as different arrays; an empty array on the heap is an object like any other (the heap gives it one more 8 bytes, so
/// that the address of its elements, its end, is inside it).
/// </para>
/// <para>
/// <b>Elements</b> are those of arrays on the stack (numbers, bools, structs and enums) and references: arrays
/// (<c>int[][]</c>), <c>object</c> and the classes of the program, whose elements are refs, which the collector follows
/// (also in an array of references on the stack, which it reads conservatively). An array of strings waits for strings
/// on the heap. A reference to an array (<c>ref int[]</c>, <c>out int[]</c>) is not supported: a local holds a view, a
/// field an object. Multi-dimensional arrays and array covariance (<c>object[] o = new int[2][]</c>) are not supported.
/// </para>
/// </remarks>
internal sealed class HeapArrays
{
    private readonly HashSet<(IlMethod Method, int Argument)> _kept = [];
    private readonly HashSet<(IlMethod Method, int Argument)> _maybeNull = [];

    private HeapArrays()
    {
    }

    /// <summary>No array parameter is kept, and none is ever given null.</summary>
    public static HeapArrays None { get; } = new();

    /// <summary>
    /// The array parameters of <paramref name="methods"/> (the program: its entry point and every method it reaches) that
    /// are kept, and those that may be null, from what each method does with its own and passes to the others, until
    /// nothing changes. <paramref name="statics"/> are the program's arrays in static fields.
    /// </summary>
    public static HeapArrays Find(IReadOnlyList<IlMethod> methods, StaticArrays statics)
    {
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(statics);

        var found = new HeapArrays();
        var summaries = methods.Where(method => method.Body is { Instructions.Count: > 0 }).Distinct()
            .ToDictionary(method => method, method => ArrayEscapeAnalysis.Summarize(method, statics));
        foreach (var (method, summary) in summaries)
        {
            found._kept.UnionWith(summary.KeptArguments.Select(argument => (method, argument)));
            found._maybeNull.UnionWith(summary.Passes.Where(pass => pass.MayBeNull).Select(pass => (pass.Callee, pass.Argument)));
        }

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (method, summary) in summaries)
            {
                foreach (var pass in summary.Passes)
                {
                    // what a kept parameter is given is kept; what a parameter that may be null is given may be null
                    if (found._kept.Contains((pass.Callee, pass.Argument)))
                    {
                        foreach (var argument in pass.Arguments)
                        {
                            changed |= found._kept.Add((method, argument));
                        }
                    }

                    if (pass.Arguments.Any(argument => found._maybeNull.Contains((method, argument))))
                    {
                        changed |= found._maybeNull.Add((pass.Callee, pass.Argument));
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// True when the array that <paramref name="method"/> takes as its argument <paramref name="argument"/> (by IL argument
    /// number, <c>this</c> first) is kept, there or in a method it passes it to: every caller must give it an array on the
    /// heap (or null).
    /// </summary>
    public bool Keeps(IlMethod method, int argument) => _kept.Contains((method, argument));

    /// <summary>True when a caller may give <paramref name="method"/> null (or an array that may be null) as its argument <paramref name="argument"/>.</summary>
    public bool MayBeNull(IlMethod method, int argument) => _maybeNull.Contains((method, argument));

    /// <summary>
    /// The TIR type an element of type <paramref name="elementType"/> has in memory, in an array on the stack or on the
    /// heap: what <see cref="StackArrays.TryMapElementType"/> gives a number, bool, struct or enum, and ref for an array
    /// (of such elements, also an array of arrays), <c>object</c> or a class of <paramref name="assembly"/>.
    /// </summary>
    public static bool TryMapElementType(IlType elementType, IlAssembly assembly, [NotNullWhen(true)] out TirType? storage)
    {
        ArgumentNullException.ThrowIfNull(elementType);
        ArgumentNullException.ThrowIfNull(assembly);
        if (StackArrays.TryMapElementType(elementType, assembly, out storage))
        {
            return true;
        }

        storage = IsReferenceElement(elementType, assembly) ? TirType.Ref : null;
        return storage is not null;
    }

    /// <summary>True for an element type whose elements are references: an array of a supported element type, object, a class.</summary>
    public static bool IsReferenceElement(IlType elementType, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(elementType);
        var element = ValueTypes.WithoutModifiers(StackArrays.Primitive(elementType));
        return element is IlArrayType inner ? TryMapElementType(inner.ElementType, assembly, out _)
            : ObjectTypes.TryMapType(element, assembly, out _);
    }

    /// <summary>The element type of an array type (modifiers looked through), or null for any other type.</summary>
    public static IlType? ElementType(IlType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ValueTypes.WithoutModifiers(type) is IlArrayType array ? StackArrays.Primitive(array.ElementType) : null;
    }

    /// <summary>
    /// The heap use a parameter, local or return type of <paramref name="assembly"/> is: an array of references (also inside
    /// an array of arrays), which a target without a heap does not have; null for any other type.
    /// </summary>
    public static Unsupported? ForType(IlType type, IlAssembly assembly) =>
        ElementType(type) is { } element && IsReferenceElement(element, assembly)
            ? Unsupported.HeapUse(Unsupported.ArraysOfReferences)
            : null;
}
