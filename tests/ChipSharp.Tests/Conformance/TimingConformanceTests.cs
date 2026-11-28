// a ChipTheory's cases, one per chip, are its discoverer's (ChipTheoryDiscoverer), not a data attribute's
#pragma warning disable xUnit1003

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// The conformance suite's time: the delays wait at least the time given and little more (every chip); the timer event
/// comes every period until Stop, and once for After (the chips with one); and the clock counts the time that passes, for
/// Stopwatch, Environment.TickCount64 and DateTime.UtcNow from Clock.Start (the chips with one).
/// </summary>
public sealed class TimingConformanceTests : ConformanceTests
{
    [ChipTheory("chip")]
    public async Task The_delays_wait_at_least_the_time_given_and_little_more(string target)
    {
        var chip = ChipSimulators.Get(target);
        var slack = chip.Tolerances.DelaySlackMilliseconds;

        var run = await RunAsync(chip, "Delays", """
            Pin.Output(Output);
            Pin.Output(Second);
            for (var i = 0; i < 4; i++)
            {
                Pin.Toggle(Output);
                Timing.DelayMilliseconds(5);
            }

            for (var i = 0; i < 4; i++)
            {
                Pin.Toggle(Second);
                Timing.DelayMicroseconds(500);
            }

            Timing.DelayMilliseconds(0);
            Timing.DelayMicroseconds(-5);
            Timing.Delay(TimeSpan.FromMilliseconds(2));
            Pin.Toggle(Output);
            """, new ChipStimulus(200));

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        var output = run.Levels(chip.Pins.Output).Skip(1).Select(change => change.Milliseconds).ToList();
        var second = run.Levels(chip.Pins.Second).Skip(1).Select(change => change.Milliseconds).ToList();
        Assert.True(output.Count == 5 && second.Count == 4, run.ToString());
        for (var i = 1; i < 4; i++)
        {
            AssertIn(output[i] - output[i - 1], 5 - Printed, 5 + slack, "DelayMilliseconds(5)", run);
            AssertIn(second[i] - second[i - 1], 0.5 - Printed, 0.5 + slack, "DelayMicroseconds(500)", run);
        }

        // the last 500 us, two delays that return at once, and 2 ms of a TimeSpan
        AssertIn(output[4] - second[3], 2.5 - Printed, 2.5 + 3 * slack, "Delay(TimeSpan.FromMilliseconds(2))", run);
    }

    [ChipTheory("interrupts.timer")]
    public async Task The_timer_event_comes_every_period_until_Stop(string target)
    {
        var chip = ChipSimulators.Get(target);
        var latency = chip.Tolerances.LatencyMilliseconds;

        var run = await RunAsync(chip, "TimerEvery", """
            Pin.Output(Output);
            Timing.EveryMilliseconds(5);
            while (ticks < 6)
            {
            }

            Timing.Stop();
            Timing.DelayMilliseconds(20);
            """, new ChipStimulus(200), """
                static volatile int ticks;

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    ticks++;
                    Pin.Toggle(Output);
                }
            """);

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        var toggles = Toggles(run, chip.Pins.Output);
        Assert.True(toggles.Count == 6, $"{toggles.Count} events, not 6\n{run}");
        AssertIn(toggles[0], 5 - Printed, 5 + latency + chip.Tolerances.DelaySlackMilliseconds, "the first event", run);
        for (var i = 1; i < toggles.Count; i++)
        {
            AssertIn(toggles[i] - toggles[i - 1], 5 - latency, 5 + latency, "the period", run);
        }
    }

    [ChipTheory("interrupts.timer")]
    public async Task After_runs_the_timer_event_once_after_the_time_given(string target)
    {
        var chip = ChipSimulators.Get(target);
        var latency = chip.Tolerances.LatencyMilliseconds + chip.Tolerances.DelaySlackMilliseconds;

        // once after 3 ms (and not again in 10 ms), then every 800 us three times, then once after 1500 us
        var run = await RunAsync(chip, "TimerAfter", """
            Pin.Output(Output);
            Timing.AfterMilliseconds(3);
            while (ticks < 1)
            {
            }

            Timing.DelayMilliseconds(10);
            Timing.EveryMicroseconds(800);
            while (ticks < 4)
            {
            }

            Timing.Stop();
            Timing.AfterMicroseconds(1500);
            while (ticks < 5)
            {
            }

            Timing.DelayMilliseconds(5);
            """, new ChipStimulus(200), """
                static volatile int ticks;

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    ticks++;
                    Pin.Toggle(Output);
                }
            """);

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        var toggles = Toggles(run, chip.Pins.Output);
        Assert.True(toggles.Count == 5, $"{toggles.Count} events, not 5\n{run}");
        AssertIn(toggles[0], 3 - Printed, 3 + latency, "AfterMilliseconds(3)", run);
        AssertIn(toggles[1] - toggles[0], 10.8 - Printed, 10.8 + 2 * latency, "the first of EveryMicroseconds(800)", run);
        AssertIn(toggles[2] - toggles[1], 0.8 - latency, 0.8 + latency, "EveryMicroseconds(800)", run);
        AssertIn(toggles[3] - toggles[2], 0.8 - latency, 0.8 + latency, "EveryMicroseconds(800)", run);
        AssertIn(toggles[4] - toggles[3], 1.5 - Printed, 1.5 + 2 * latency, "AfterMicroseconds(1500)", run);
    }

    [ChipTheory("clock")]
    public async Task The_clock_counts_the_time_for_Stopwatch_TickCount64_and_DateTime_UtcNow(string target)
    {
        var chip = ChipSimulators.Get(target);

        var run = await RunAsync(chip, "Clock", """
            Check.Start();
            Pin.Output(Output);
            var start = Stopwatch.GetTimestamp();
            for (var i = 1; i <= 4; i++)
            {
                while (Stopwatch.GetElapsedTime(start).Ticks < i * 50_000L)
                {
                }

                Pin.Toggle(Output);
            }

            Check.That(Stopwatch.Frequency == 1_000_000);
            var milliseconds = Environment.TickCount64;
            Timing.DelayMilliseconds(10);
            var passed = Environment.TickCount64 - milliseconds;
            Check.That(passed >= 10 && passed <= 11);
            Clock.Start(new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc));
            Timing.DelayMilliseconds(15);
            var now = DateTime.UtcNow;
            Check.That(now.Year == 2026 && now.Month == 11 && now.Day == 28 && now.Hour == 12 && now.Minute == 0
                && now.Second == 0);
            Check.That(now.Millisecond >= 15 && now.Millisecond <= 16);
            """, new ChipStimulus(200));

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        AssertChecks(chip, run, 4);
        var toggles = run.Levels(chip.Pins.Output).Skip(1).Select(change => change.Milliseconds).ToList();
        Assert.True(toggles.Count == 4, run.ToString());
        for (var i = 1; i < toggles.Count; i++)
        {
            AssertIn(toggles[i] - toggles[i - 1], 5 - chip.Tolerances.ClockMilliseconds, 5 + chip.Tolerances.ClockMilliseconds,
                "5 ms of Stopwatch", run);
        }
    }
}
