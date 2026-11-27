namespace ChipSharp.Library.Collections;

/// <summary>
/// ChipSharp's <see cref="System.Runtime.InteropServices.CollectionsMarshal"/>: the span of a list's elements, and setting
/// how many it has, which C# uses for a collection expression into a list (<c>List&lt;int&gt; l = [1, 2, 3];</c>), as
/// .NET's. A span of objects arrives later, so this is for lists of values (numbers, bools, enums, structs) for now.
/// </summary>
[Substitute(typeof(System.Runtime.InteropServices.CollectionsMarshal))]
public static class CollectionsMarshal
{
    public static Span<T> AsSpan<T>(List<T>? list) =>
        list is null ? default : new Span<T>(list._items, 0, list._size);

    public static void SetCount<T>(List<T> list, int count)
    {
        if (count < 0)
        {
            ThrowHelper.NegativeArgument(nameof(count));
        }

        list._version++;
        if (count > list.Capacity)
        {
            list.Grow(count);
        }
        else if (count < list._size)
        {
            System.Array.Clear(list._items, count, list._size - count);
        }

        list._size = count;
    }
}
