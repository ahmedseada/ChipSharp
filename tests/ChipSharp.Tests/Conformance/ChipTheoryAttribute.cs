using ChipSharp.Tests.Support;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ChipSharp.Tests.Conformance;

/// <summary>Every chip the conformance suite runs on, by target: those with a simulator, and the slots of those to come.</summary>
internal static class ChipSimulators
{
    /// <summary>
    /// The adapters, one per chip: the Pico (rp2040js) and the Uno (avr8js), and the slots of the SDCC chips (the
    /// PFS154's and the STM8S103's simulators; the ESP32-C5's next).
    /// </summary>
    public static IReadOnlyList<ChipSimulator> All { get; } =
    [
        new PicoChipSimulator(), new UnoChipSimulator(), new Pfs154ChipSimulator(), new Stm8ChipSimulator(),
    ];

    /// <summary>The adapter of <paramref name="target"/>.</summary>
    public static ChipSimulator Get(string target) => All.Single(chip => chip.Target == target);
}

/// <summary>
/// A test of the conformance suite: it runs once on every chip whose target has what it needs (<see cref="Needs"/>: the
/// hardware contract's guards, "chip" for every chip), with the target's name as its one argument; on a chip whose
/// simulator cannot run here, or is still to come, the case is skipped with the adapter's reason.
/// </summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Conformance.ChipTheoryDiscoverer", "ChipSharp.Tests")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ChipTheoryAttribute(params string[] needs) : TheoryAttribute
{
    /// <summary>What a target must have: e.g. "uart", "interrupts.pin", "clock"; "chip" (or nothing) for every chip.</summary>
    public IReadOnlyList<string> Needs { get; } = needs;
}

/// <summary>
/// Finds a <see cref="ChipTheoryAttribute"/> test's cases: one per chip that has what it needs, each in a test collection of
/// its own (see <see cref="OwnCollection"/>), skipped when the chip's adapter says it cannot run.
/// </summary>
public sealed class ChipTheoryDiscoverer(IMessageSink diagnosticMessageSink) : TheoryDiscoverer(diagnosticMessageSink)
{
    public override IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod, IAttributeInfo theoryAttribute)
    {
        var needs = theoryAttribute is IReflectionAttributeInfo { Attribute: ChipTheoryAttribute theory }
            ? theory.Needs
            : theoryAttribute.GetConstructorArguments().FirstOrDefault() as string[] ?? [];
        foreach (var chip in ChipSimulators.All.Where(chip => needs.All(chip.Has)))
        {
            object[] row = [chip.Target];
            var method = OwnCollection.For(testMethod, OwnCollection.Key(row));
            if (chip.MissingFor(testMethod.Method.Name, needs) is { } reason)
            {
                foreach (var skipped in base.CreateTestCasesForSkippedDataRow(discoveryOptions, method, theoryAttribute, row,
                    reason))
                {
                    yield return skipped;
                }
            }
            else
            {
                foreach (var testCase in base.CreateTestCasesForDataRow(discoveryOptions, method, theoryAttribute, row))
                {
                    yield return testCase;
                }
            }
        }
    }
}
