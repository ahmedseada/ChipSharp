using System.Globalization;
using System.Text;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Memory;

/// <summary>
/// System.GC's methods, on the PC and the Pico, against .NET: GC.GetTotalMemory counts the objects not known to be
/// garbage (here the bytes of their blocks; on .NET what its heap counts, so the programs compare only what both say:
/// more after making objects, less after a collection), GC.Collect frees what nothing reaches, GC.KeepAlive keeps an
/// object until it is called. And garbage the conservative stack scan could keep: a big structure made and dropped by a
/// method the program has returned from is all freed by the next collection.
/// </summary>
public sealed class GcMethodsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public const string Methods = """
        using System;

        class Node
        {
            public int[] Data;
            public Node Next;

            public Node(int size, Node next)
            {
                Data = new int[size];
                Next = next;
            }
        }

        static class Program
        {
            static Node kept;

            static long Build(int count)
            {
                Node list = null;
                for (int i = 0; i < count; i++)
                {
                    list = new Node(16, list);
                }

                long sum = 0;
                for (Node node = list; node != null; node = node.Next)
                {
                    sum += node.Data.Length;
                }

                return sum;
            }

            static void Main()
            {
                Console.WriteLine("start");
                long start = GC.GetTotalMemory(true);
                kept = new Node(1000, null);
                long withKept = GC.GetTotalMemory(false) - start;
                Console.WriteLine(withKept >= 4000);
                Console.WriteLine(Build(1000));
                Console.WriteLine(GC.GetTotalMemory(false) - start >= 80_000);
                GC.Collect();
                long collected = GC.GetTotalMemory(false) - start;
                Console.WriteLine(collected >= 4000 && collected < 20_000);
                kept = null;
                var local = new Node(2000, null);
                local.Data[1] = 5;
                long dropped = GC.GetTotalMemory(true) - start;
                Console.WriteLine(dropped >= 8000 && dropped < 20_000);
                GC.KeepAlive(local);
                Console.WriteLine(local.Data[1]);
            }
        }
        """;

    /// <summary>A method makes a list of some 180 KB (on the Pico) and returns its sum; then Main collects.</summary>
    public const string Dropped = """
        using System;

        class Node
        {
            public int[] Data;
            public Node Next;

            public Node(int size, Node next)
            {
                Data = new int[size];
                Next = next;
            }
        }

        static class Program
        {
            static long Build(int count)
            {
                Node list = null;
                for (int i = 0; i < count; i++)
                {
                    list = new Node(16, list);
                }

                long sum = 0;
                for (Node node = list; node != null; node = node.Next)
                {
                    sum += node.Data.Length + node.Data[0];
                }

                return sum;
            }

            static void Main()
            {
                Console.WriteLine("start");
                long start = GC.GetTotalMemory(true);
                Console.WriteLine(Build(2000));
                GC.Collect();
                long kept = GC.GetTotalMemory(false) - start;
                Console.WriteLine(kept < 1000);
                Console.WriteLine(kept);
            }
        }
        """;

    [HostFact]
    public async Task On_the_PC_GC_Collect_GetTotalMemory_and_KeepAlive_say_what_dotnet_says()
    {
        var (dotnet, chipSharp) = await OnHostAsync(Methods, "Methods");

        Assert.Equal("start\r\nTrue\r\n16000\r\nTrue\r\nTrue\r\nTrue\r\n5\r\n", dotnet.Text);
        Assert.Equal(dotnet.Text, chipSharp.Text);
    }

    [SimulatorFact]
    public async Task On_the_Pico_GC_Collect_GetTotalMemory_and_KeepAlive_say_what_dotnet_says()
    {
        var program = PicoBuild.Build(Methods, "Methods", Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 10_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("start\r\nTrue\r\n16000\r\nTrue\r\nTrue\r\nTrue\r\n5\r\n", Encoding.ASCII.GetString(run.Uart0));
    }

    [HostFact]
    public async Task On_the_PC_a_structure_a_returned_method_dropped_is_all_freed_by_the_next_collection()
    {
        var (dotnet, chipSharp) = await OnHostAsync(Dropped, "Dropped");
        var lines = chipSharp.Text.Split("\r\n");
        output.WriteLine($"PC: {lines[3]} bytes still in use after the collection");

        Assert.Equal(dotnet.Text.Split("\r\n")[..3], lines[..3]);
        Assert.Equal(["start", "32000", "True"], lines[..3]);
        Assert.Equal(0, long.Parse(lines[3], CultureInfo.InvariantCulture));
    }

    [SimulatorFact]
    public async Task On_the_Pico_a_structure_a_returned_method_dropped_is_all_freed_by_the_next_collection()
    {
        var program = PicoBuild.Build(Dropped, "Dropped", Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 10_000);
        var lines = Encoding.ASCII.GetString(run.Uart0).Split("\r\n");
        output.WriteLine($"Pico: {lines[3]} bytes still in use after the collection");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["start", "32000", "True", "0", ""], lines);
    }

    [Fact]
    public void On_a_chip_without_a_heap_the_GC_methods_need_one()
    {
        var tiny = new ChipSharp.Targets.TargetLoader(ChipSharp.Tests.Build.TargetFiles.WithShipped(Path.Combine(_scratch, "tiny"),
            ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");
        var analysis = WholeProgramPass.Check(Lowered.Compile("""
            static class Program
            {
                static void Main()
                {
                    System.GC.Collect();
                    System.Console.WriteLine(System.GC.GetTotalMemory(false));
                }
            }
            """, "NoHeap"), tiny);

        Assert.Equal(
        [
            "Program.Main() at IL_0000 (call): System.GC's methods (GC.Collect, GC.GetTotalMemory, GC.KeepAlive) need a " +
                "heap, and the target 'pico-no-heap' (264 KB RAM) has no heap. Without a heap there is no garbage to " +
                "collect and no object to keep alive: leave them out.",
            "Program.Main() at IL_0006 (call): System.GC's methods (GC.Collect, GC.GetTotalMemory, GC.KeepAlive) need a " +
                "heap, and the target 'pico-no-heap' (264 KB RAM) has no heap. Without a heap there is no garbage to " +
                "collect and no object to keep alive: leave them out.",
        ], analysis.Problems.Select(problem => problem.Line));
    }

    private async Task<(ProgramRun Dotnet, ProgramRun ChipSharp)> OnHostAsync(string source, string name)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(Lowered.Compile(source, name)));
        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));
        Assert.True(chipSharp.ExitCode == 0, chipSharp.ToString());
        return (dotnet, chipSharp);
    }
}
