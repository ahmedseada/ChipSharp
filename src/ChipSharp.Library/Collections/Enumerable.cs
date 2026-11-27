namespace ChipSharp.Library.Collections;

/// <summary>
/// ChipSharp's <see cref="System.Linq.Enumerable"/>: <see cref="ToList{TSource}"/> and <see cref="ToArray{TSource}"/>,
/// which C# calls for a collection expression that is one spread of a sequence (<c>List&lt;string&gt; l = [.. Words()];</c>,
/// <c>string[] a = [.. Words()];</c>), with .NET's results: a list as <c>new List&lt;T&gt;(source)</c> makes it (its
/// capacity too), an array of the elements in order. The rest of LINQ is not here yet.
/// </summary>
[Substitute(typeof(System.Linq.Enumerable))]
public static class Enumerable
{
    public static List<TSource> ToList<TSource>(IEnumerable<TSource> source)
    {
        if (source == null)
        {
            ThrowHelper.ArgumentNull(nameof(source));
        }

        return new List<TSource>(source);
    }

    public static TSource[] ToArray<TSource>(IEnumerable<TSource> source)
    {
        if (source == null)
        {
            ThrowHelper.ArgumentNull(nameof(source));
        }

        if (source is ICollection<TSource> collection)
        {
            var count = collection.Count;
            if (count == 0)
            {
                return System.Array.Empty<TSource>();
            }

            var array = new TSource[count];
            collection.CopyTo(array, 0);
            return array;
        }

        return new List<TSource>(source).ToArray();
    }
}
