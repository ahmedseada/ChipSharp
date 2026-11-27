using System.Globalization;
using System.Text;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Targets;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Memory;

/// <summary>
/// Fragmentation: a workload that keeps a number of slots filled with objects of every size (text of 10 to 200 chars,
/// arrays of 4 to 256 ints, arrays grown by Array.Resize up to 256 ints, chains of small objects), each replaced in a
/// pseudo-random order, so that what lives and what is garbage is scattered over the heap. In a small heap it must not
/// run out of memory while what lives is well under the heap's size; it prints what .NET prints. The collector never
/// moves an object, so the free memory comes in pieces: the measurement finds how much of the heap the workload may
/// keep alive before an allocation finds no piece big enough.
/// </summary>
public sealed class FragmentationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
    private TargetLoader? _targets;

    private TargetLoader Targets => _targets ??= HeapRun.Targets(Path.Combine(_scratch, "targets"), "32K");

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>
    /// The workload with <paramref name="slots"/> slots for <paramref name="steps"/> steps: prints a checksum of what the
    /// slots held, then (for the measurement, not .NET's to compare) the most bytes GC.GetTotalMemory(true) found alive,
    /// every 100 steps.
    /// </summary>
    public static string Workload(int slots, int steps) => $$"""
        using System;

        class Item
        {
            public int Kind;
            public string Text;
            public int[] Numbers;
            public Item Next;
        }

        static class Program
        {
            const int Slots = {{slots}};
            const int Steps = {{steps}};
            static Item[] slots = new Item[Slots];
            static uint seed = 12345;

            static uint Random()
            {
                seed = seed * 1664525 + 1013904223;
                return seed >> 8;
            }

            static Item Make(uint r)
            {
                var item = new Item { Kind = (int)(r % 4) };
                switch (item.Kind)
                {
                    case 0:
                        item.Text = new string((char)('a' + r % 26), 10 + (int)(r % 191));
                        break;
                    case 1:
                        item.Numbers = new int[4 + (int)(r % 253)];
                        item.Numbers[0] = (int)(r % 1000);
                        break;
                    case 2:
                        int[] grown = new int[4];
                        int limit = 4 << (int)(r % 7);
                        for (int size = 4; size < limit; size *= 2)
                        {
                            Array.Resize(ref grown, size * 2);
                            grown[size] = size;
                        }

                        item.Numbers = grown;
                        break;
                    default:
                        for (int i = 0; i < 1 + (int)(r % 8); i++)
                        {
                            item.Next = new Item { Kind = i, Next = item.Next };
                        }

                        break;
                }

                return item;
            }

            static long Check(Item item)
            {
                long check = item.Kind;
                if (item.Text != null)
                {
                    check += item.Text.Length * 31 + item.Text[item.Text.Length - 1];
                }

                if (item.Numbers != null)
                {
                    check += item.Numbers.Length * 7 + item.Numbers[0] + item.Numbers[item.Numbers.Length / 2];
                }

                for (Item next = item.Next; next != null; next = next.Next)
                {
                    check += next.Kind + 1;
                }

                return check;
            }

            static void Main()
            {
                long check = 0;
                long most = 0;
                for (int step = 0; step < Steps; step++)
                {
                    uint at = Random() % Slots;
                    if (slots[at] != null)
                    {
                        check = check * 3 + Check(slots[at]);
                    }

                    slots[at] = Make(Random());
                    if (step % 100 == 99)
                    {
                        long alive = GC.GetTotalMemory(true);
                        if (alive > most)
                        {
                            most = alive;
                        }
                    }
                }

                Console.WriteLine(check);
                Console.WriteLine(most);
            }
        }
        """;

    [HostFact]
    public async Task On_the_PC_a_32_KB_heap_half_alive_with_objects_of_every_size_never_runs_out_and_prints_what_dotnet_prints()
    {
        var (lines, dotnet) = await OnHostAsync(slots: 36, steps: 5000, withDotnet: true);
        output.WriteLine($"PC, 32 KB heap, 36 slots: at most {lines[1]} bytes alive");

        Assert.Equal(dotnet![0], lines[0]);
        Assert.InRange(long.Parse(lines[1], CultureInfo.InvariantCulture), 12 << 10, 24 << 10);
    }

    [SimulatorFact]
    public async Task On_the_Pico_a_32_KB_heap_half_alive_with_objects_of_every_size_never_runs_out_and_prints_what_dotnet_prints()
    {
        const int Slots = 48;
        const int Steps = 3000;
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(Workload(Slots, Steps), "Workload"), "Workload",
            Path.Combine(_scratch, "dotnet"));
        var pico = Targets.Load("pico-32K");
        var module = Lowered.Lower(WholeProgramPass.Analyze(Lowered.Compile(Workload(Slots, Steps), "Workload"), pico));

        var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, "pico"), pico, 120_000, verifyHeap: true);
        output.WriteLine($"Pico, 32 KB heap, {Slots} slots: at most {result.Lines[1]} bytes alive, " +
            $"{result.Time.TotalMilliseconds:0} ms simulated");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text.Split("\r\n")[0], result.Lines[0]);
        Assert.InRange(result.Number(1), 12 << 10, 24 << 10);
    }

    /// <summary>
    /// The measurement: more and more slots in a 32 KB heap on the PC, until an allocation finds no free block big enough.
    /// The README gives what it found.
    /// </summary>
    [HostFact]
    public async Task How_much_of_a_32_KB_heap_the_workload_keeps_alive_before_it_runs_out()
    {
        var table = new StringBuilder();
        long heap = 0;
        long lastFit = 0;
        for (var slots = 36; slots <= 160; slots += 2)
        {
            var (lines, _) = await OnHostAsync(slots, steps: 3000, withDotnet: false, mayRunOut: true);
            if (lines.Length < 2 || !long.TryParse(lines[1], CultureInfo.InvariantCulture, out var most))
            {
                table.Append(CultureInfo.InvariantCulture, $"{slots} slots: out of memory\n");
                break;
            }

            heap = 32 << 10;
            lastFit = most;
            table.Append(CultureInfo.InvariantCulture, $"{slots} slots: at most {most} bytes alive ({100.0 * most / heap:0}%)\n");
        }

        output.WriteLine(table.ToString());
        Assert.True(lastFit >= heap / 2, $"the workload ran out of memory with less than half of the heap alive:\n{table}");
    }

    private async Task<(string[] Lines, string[]? Dotnet)> OnHostAsync(int slots, int steps, bool withDotnet,
        bool mayRunOut = false)
    {
        var source = Workload(slots, steps);
        string[]? dotnet = null;
        if (withDotnet)
        {
            dotnet = (await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Workload"), "Workload",
                Path.Combine(_scratch, "dotnet"))).Text.Split("\r\n");
        }

        var host = Targets.Load("host-32K");
        var module = Lowered.Lower(WholeProgramPass.Analyze(Lowered.Compile(source, "Workload"), host));
        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, $"host-{slots}"), host, verifyHeap: !mayRunOut);
        Assert.True(mayRunOut || result.ExitCode == 0, result.ToString());
        return ([.. result.Lines], dotnet);
    }
}
