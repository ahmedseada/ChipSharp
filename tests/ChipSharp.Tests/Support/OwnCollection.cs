using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ChipSharp.Tests.Support;

/// <summary>
/// Puts a test (each case of a theory too) in a test collection of its own, so that it runs in parallel with the
/// others, those of its own class too: xunit runs the tests of one collection one after another, and by default a
/// class is a collection. For the tests that build and run programs (<c>[HostFact]</c>, <c>[SimulatorTheory]</c>, ...),
/// which wait on clang, the programs and the simulator, and share nothing but their own scratch folders.
/// </summary>
internal static class OwnCollection
{
    /// <summary>
    /// The test method, in a collection of its own named after it and <paramref name="key"/> (a theory's data row): the
    /// same collection, with the same unique ID, every time it is found.
    /// </summary>
    public static ITestMethod For(ITestMethod testMethod, string key)
    {
        var testClass = testMethod.TestClass;
        var name = $"{testClass.Class.Name}.{testMethod.Method.Name}{key}";
        var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(name)));
        var runsLong = testMethod.Method.GetCustomAttributes(typeof(RunsLongAttribute)).Any() ? RunsLongAttribute.Mark : "";
        var collection = new TestCollection(testClass.TestCollection.TestAssembly, testClass.TestCollection.CollectionDefinition,
            $"{name} (in a collection of its own){runsLong}", id);
        return new TestMethod(new TestClass(collection, testClass.Class), testMethod.Method);
    }

    /// <summary>A key for a theory's data row: its values, in parentheses.</summary>
    public static string Key(object?[]? dataRow) =>
        dataRow is null ? "" : $"({string.Join(", ", dataRow.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)))})";
}

/// <summary>Finds a fact as xunit does, in a test collection of its own (see <see cref="OwnCollection"/>).</summary>
public sealed class OwnCollectionFactDiscoverer(IMessageSink diagnosticMessageSink) : FactDiscoverer(diagnosticMessageSink)
{
    protected override IXunitTestCase CreateTestCase(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod,
        IAttributeInfo factAttribute) =>
        new OwnCollectionTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(), OwnCollection.For(testMethod, ""));
}

/// <summary>
/// A fact in a collection of its own: xunit's own test case, but for how it is written down between finding the tests
/// and running them. xunit writes its own facts in short, with only the collection's ID, and a runner then takes every
/// fact of a class for the same collection; this one is written in full, with the collection's name.
/// </summary>
public sealed class OwnCollectionTestCase : XunitTestCase
{
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public OwnCollectionTestCase()
    {
    }

    public OwnCollectionTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay,
        TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod)
    {
    }
}

/// <summary>Finds a theory's cases as xunit does, each in a test collection of its own (see <see cref="OwnCollection"/>).</summary>
public sealed class OwnCollectionTheoryDiscoverer(IMessageSink diagnosticMessageSink) : TheoryDiscoverer(diagnosticMessageSink)
{
    protected override IEnumerable<IXunitTestCase> CreateTestCasesForDataRow(ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod, IAttributeInfo theoryAttribute, object[] dataRow) =>
        base.CreateTestCasesForDataRow(discoveryOptions, OwnCollection.For(testMethod, OwnCollection.Key(dataRow)),
            theoryAttribute, dataRow);

    protected override IEnumerable<IXunitTestCase> CreateTestCasesForSkippedDataRow(
        ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo theoryAttribute,
        object[] dataRow, string skipReason) =>
        base.CreateTestCasesForSkippedDataRow(discoveryOptions, OwnCollection.For(testMethod, OwnCollection.Key(dataRow)),
            theoryAttribute, dataRow, skipReason);

    protected override IEnumerable<IXunitTestCase> CreateTestCasesForTheory(ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod, IAttributeInfo theoryAttribute) =>
        base.CreateTestCasesForTheory(discoveryOptions, OwnCollection.For(testMethod, ""), theoryAttribute);

    protected override IEnumerable<IXunitTestCase> CreateTestCasesForSkip(ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod, IAttributeInfo theoryAttribute, string skipReason) =>
        base.CreateTestCasesForSkip(discoveryOptions, OwnCollection.For(testMethod, ""), theoryAttribute, skipReason);
}
