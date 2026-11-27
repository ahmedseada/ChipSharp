using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// Arrays as the interfaces of collections in rapid type analysis (see <see cref="ArrayInterfaces"/>): where the program
/// uses an array of T as an object or an interface, the class of an array's interfaces, <c>SzArray&lt;T&gt;</c>, counts as
/// made, so the slots called on the interfaces reach its methods, which run on the array.
/// </summary>
internal sealed partial class VirtualDispatch
{
    /// <summary>
    /// Takes in instruction <paramref name="index"/> of <paramref name="method"/>: an array it uses as an object or an
    /// interface. Returns the methods that this makes part of the program (none twice), generic code left out.
    /// </summary>
    public IReadOnlyList<IlMethod> FollowArrays(IlMethod method, int index)
    {
        List<IlMethod> reached = [];
        foreach (var array in ArrayInterfaces.Converted(method, index))
        {
            if (ArrayInterfaces.ClassOf(array, method.Assembly) is { } type && _isCreated.Add(type))
            {
                reached.AddRange(Reached(Create(type)));
            }
        }

        return reached;
    }

    /// <summary>
    /// What a call through an interface's <paramref name="slot"/> runs on an array of <paramref name="receiver"/>'s type
    /// (an array is what the variable has): the method of its class of interfaces, when the program made it one; null when
    /// the receiver is no array or the slot no interface's.
    /// </summary>
    private IReadOnlyList<DispatchTarget>? ArrayTargets(DispatchSlot slot, IlType? receiver)
    {
        if (slot.Kind != DispatchSlotKind.Interface || receiver is null
            || ValueTypes.WithoutModifiers(receiver) is not IlArrayType array)
        {
            return null;
        }

        var element = StackArrays.Primitive(ValueTypes.WithoutModifiers(array.ElementType));
        return
        [
            .. _created.Where(type => ArrayInterfaces.IsArrayClass(type) && type.TypeArguments[0].Equals(element)
                    && VirtualMethods.Has(type, slot))
                .Select(type => Resolve(type, slot))
                .OfType<DispatchTarget>(),
        ];
    }
}
