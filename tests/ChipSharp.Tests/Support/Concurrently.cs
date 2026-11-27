namespace ChipSharp.Tests.Support;

/// <summary>
/// Runs the cases of a test that checks many programs a few at a time, rather than one after another: each case builds
/// and runs its program in a folder of its own and asserts the same as before, and the test fails as soon as one case
/// does (with that case's failure).
/// </summary>
internal static class Concurrently
{
    /// <summary>How many cases run at once: half the processors (the other tests run meanwhile), at least 2.</summary>
    public static int Degree { get; } = Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>Runs <paramref name="body"/> for every item, <see cref="Degree"/> at a time.</summary>
    public static Task ForEachAsync<T>(IEnumerable<T> items, Func<T, Task> body) =>
        Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Degree },
            async (item, _) => await body(item).ConfigureAwait(false));

    /// <summary>
    /// Runs <paramref name="body"/> for every item, <see cref="Degree"/> at a time, and returns what each gave, in the
    /// order of the items.
    /// </summary>
    public static async Task<TResult[]> SelectAsync<T, TResult>(IReadOnlyList<T> items, Func<T, Task<TResult>> body)
    {
        var results = new TResult[items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = Degree },
            async (i, _) => results[i] = await body(items[i]).ConfigureAwait(false));
        return results;
    }
}
