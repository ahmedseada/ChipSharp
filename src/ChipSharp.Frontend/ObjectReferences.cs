using System.Diagnostics.CodeAnalysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// <c>ref</c>, <c>out</c> and <c>in</c> of an object or a string (<c>dictionary.TryGetValue(key, out string name)</c>,
/// <c>var (key, value) = pair;</c>, <c>queue.TryDequeue(out var job)</c>): like a reference to a value, a ptr, the address
/// of a variable (a local's or a parameter's slot, a field, an element) that holds a ref; <c>ldind.ref</c> and
/// <c>stind.ref</c> load and store the ref through it (<c>Lowering/MethodLowering.ObjectReferences.cs</c>). In generic
/// code over a class, <c>ldobj T</c>, <c>stobj T</c> and <c>initobj T</c> through such a reference are those
/// (<see cref="Generics.Specialization"/>). The collector finds the object through the variable, as before: a slot on the
/// stack, which it scans, or a field or element of an object it marks. A reference to an array (<c>ref int[]</c>) is not
/// one of these yet.
/// </summary>
internal static class ObjectReferences
{
    /// <summary>A ptr for a reference to an object or a string (not an array); false for any other type.</summary>
    public static bool TryMapType(IlType type, IlAssembly assembly, [NotNullWhen(true)] out TirType? tirType)
    {
        tirType = ValueTypes.WithoutModifiers(type) is IlByRefType { ElementType: var element }
            && ValueTypes.WithoutModifiers(element) is not IlArrayType
            && (ObjectTypes.TryMapType(element, assembly, out _) || FixedText.IsText(element))
            ? TirType.Ptr
            : null;
        return tirType is not null;
    }
}
