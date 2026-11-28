// a ChipTheory's cases, one per chip, are its discoverer's (ChipTheoryDiscoverer), not a data attribute's
#pragma warning disable xUnit1003

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// The conformance suite's pins, on every chip: outputs low until written, Write and Toggle; inputs read the level
/// driven from outside; a pin no chip has is ignored; and the pin event, on the chips with one, for the edges watched.
/// </summary>
public sealed class PinConformanceTests : ConformanceTests
{
    [ChipTheory("chip")]
    public async Task Outputs_are_low_until_written_and_follow_Write_and_Toggle_and_a_pin_no_chip_has_is_ignored(
        string target)
    {
        var chip = ChipSimulators.Get(target);

        var run = await RunAsync(chip, "PinOutputs", """
            Pin.Output(Output);
            Pin.Output(Second);
            Timing.DelayMicroseconds(200);
            Pin.Write(Output, true);
            Pin.Toggle(Second);
            Timing.DelayMicroseconds(200);
            Pin.Toggle(Output);
            Pin.Write(Second, true);
            Timing.DelayMicroseconds(200);
            Pin.Write(Output, true);
            Pin.Write(Second, false);
            Pin.Output(Program.None);
            Pin.Toggle(Program.None);
            Pin.Write(Program.None, true);
            """, new ChipStimulus(100), "    static int None = -1;");

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        Assert.Equal(["low", "high", "low", "high"], run.Levels(chip.Pins.Output).Select(change => change.State));
        Assert.Equal(["low", "high", "low"], run.Levels(chip.Pins.Second).Select(change => change.State));
        var output = run.Levels(chip.Pins.Output);
        for (var i = 1; i < output.Count; i++)
        {
            AssertIn(output[i].Milliseconds - output[i - 1].Milliseconds, 0.2 - Printed, 0.2 + chip.Tolerances.DelaySlackMilliseconds,
                "the time between two writes", run);
        }
    }

    [ChipTheory("chip")]
    public async Task An_input_reads_the_level_driven_from_outside(string target)
    {
        var chip = ChipSimulators.Get(target);

        var run = await RunAsync(chip, "PinInput", """
            Check.Start();
            Pin.Input(Input);
            Check.That(!Pin.Read(Input));
            Timing.DelayMilliseconds(20);
            Check.That(Pin.Read(Input));
            Timing.DelayMilliseconds(20);
            Check.That(!Pin.Read(Input));
            """, new ChipStimulus(100)
            {
                Inputs = [new(chip.Pins.Input, false), new(chip.Pins.Input, true, 10), new(chip.Pins.Input, false, 30)],
            });

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        AssertChecks(chip, run, 3);
    }

    [ChipTheory("interrupts.pin")]
    public async Task The_pin_event_runs_the_handler_for_the_edges_watched_and_none_once_unwatched(string target)
    {
        var chip = ChipSimulators.Get(target);
        var pin = chip.Pins.Input;

        // rising edges until 35 ms, falling ones until 55 ms, both until 70 ms, then none
        var run = await RunAsync(chip, "PinEvent", """
            Pin.Output(Output);
            Pin.Input(Input);
            Pin.WatchRising(Input);
            Timing.DelayMilliseconds(35);
            Pin.WatchFalling(Input);
            Timing.DelayMilliseconds(20);
            Pin.Watch(Input);
            Timing.DelayMilliseconds(15);
            Pin.Unwatch(Input);
            Timing.DelayMilliseconds(20);
            """, new ChipStimulus(200)
            {
                Inputs =
                [
                    new(pin, false), new(pin, true, 10), new(pin, false, 20), new(pin, true, 30), new(pin, false, 40),
                    new(pin, true, 50), new(pin, false, 60), new(pin, true, 65), new(pin, false, 80),
                ],
            }, """
                [Interrupt(Event.Pin)]
                static void Changed() => Pin.Toggle(Output);
            """);

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        var toggles = run.Levels(chip.Pins.Output).Skip(1).Select(change => change.Milliseconds).ToList();
        double[] edges = [10, 30, 40, 60, 65];
        Assert.True(toggles.Count == edges.Length, $"{toggles.Count} events, not {edges.Length}\n{run}");
        for (var i = 0; i < edges.Length; i++)
        {
            AssertIn(toggles[i], edges[i] - Printed, edges[i] + chip.Tolerances.LatencyMilliseconds, $"the event of the edge at {edges[i]} ms", run);
        }
    }
}
