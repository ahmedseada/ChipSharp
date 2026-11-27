using System.Globalization;
using System.Text;
using ChipSharp.Tests.Pico;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Memory;

/// <summary>
/// The garbage collector runs with interrupts on: a timer interrupt every 200 µs keeps coming while the Pico's heap,
/// nearly full of live objects, is collected again and again. The handler measures the longest time between two of its
/// runs with the chip's microsecond timer; the collector, which once kept interrupts waiting for a whole collection
/// (some 20 ms for such a heap), now delays them by no more than an interrupt's own entry.
/// </summary>
public sealed class GcLatencySimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public const string Program = """
        using System;
        using ChipSharp.Hardware;

        class Node
        {
            public int Value;
            public Node Next;

            public Node(int value, Node next)
            {
                Value = value;
                Next = next;
            }
        }

        static class Program
        {
            const uint TimeLow = 0x40054028; // TIMER's TIMERAWL: the microseconds since the chip started
            const int Period = 200;

            static volatile uint last;
            static volatile uint worst;
            static volatile int ticks;
            static Node live;

            static void Main()
            {
                // 12000 nodes of 16 bytes: 188 KB of the heap's 224 KB stay alive through every collection
                for (int i = 0; i < 12_000; i++)
                {
                    live = new Node(i, live);
                }

                last = Registers.Read(TimeLow);
                Timing.EveryMicroseconds(Period);
                long sum = 0;
                for (int i = 0; i < 30_000; i++)
                {
                    sum += new Node(i, null).Value; // garbage: a collection each time the free 36 KB are used up
                }

                Timing.Stop();
                int count = 0;
                for (Node node = live; node != null; node = node.Next)
                {
                    count++;
                }

                Console.WriteLine(sum);
                Console.WriteLine(count);
                Console.WriteLine(ticks > 100);
                Console.WriteLine(worst);
            }

            [Interrupt(Event.Timer)]
            static void Tick()
            {
                uint now = Registers.Read(TimeLow);
                uint gap = now - last;
                if (gap > worst)
                {
                    worst = gap;
                }

                last = now;
                ticks++;
            }
        }
        """;

    [SimulatorFact]
    public async Task A_timer_interrupt_keeps_its_time_while_a_nearly_full_heap_is_collected_again_and_again()
    {
        var program = PicoBuild.Build(Program, "Latency", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 20_000);
        var lines = Encoding.ASCII.GetString(run.Uart0).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        output.WriteLine($"{run}\n{string.Join("\n", lines)}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["449985000", "12000", "True"], lines.Take(3));
        var worst = int.Parse(lines[3], CultureInfo.InvariantCulture);
        output.WriteLine($"the longest time between two timer interrupts: {worst} µs (every 200 µs when nothing delays them)");
        Assert.InRange(worst, 200, 400);
    }
}
