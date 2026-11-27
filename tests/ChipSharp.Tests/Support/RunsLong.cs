using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCollectionOrderer("ChipSharp.Tests.Support.LongTestsFirst", "ChipSharp.Tests")]

namespace ChipSharp.Tests.Support;

/// <summary>
/// Marks a test that takes much longer than most (a big program in the simulator, a <c>dotnet build</c>): it starts
/// first, so that the test run does not end waiting for it alone. For tests in a collection of their own
/// (<see cref="OwnCollection"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RunsLongAttribute : Attribute
{
    /// <summary>What the name of the collection of such a test ends with.</summary>
    public const string Mark = " (runs long)";
}

/// <summary>Runs the collections of the tests marked <see cref="RunsLongAttribute"/> first, the rest as xunit orders them.</summary>
public sealed class LongTestsFirst : ITestCollectionOrderer
{
    private readonly DefaultTestCollectionOrderer _default = new();

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        _default.OrderTestCollections(testCollections)
            .OrderByDescending(collection => collection.DisplayName.EndsWith(RunsLongAttribute.Mark, StringComparison.Ordinal));
}
