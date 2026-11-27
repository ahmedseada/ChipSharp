using System.Text;
using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Image.Uf2;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Memory;

/// <summary>
/// A recursion that never ends stops the program as .NET stops it, "Stack overflow." on a line of its own: on the PC on
/// standard error, with .NET's exit code (0xC00000FD, from Windows' guard page and the runtime's exception handler); on
/// the Pico over UART0, at a breakpoint, from the check the functions that can recurse make when they start. A
/// recursion that ends runs as before, and the functions that cannot recurse check nothing.
/// </summary>
public sealed class StackOverflowTests(ITestOutputHelper output) : IDisposable
{
    private const int StackOverflowExitCode = unchecked((int)0xC00000FD);

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>A recursion with no end (whose result is not a sum LLVM could turn into a loop), after a line.</summary>
    private const string Endless = """
        using System;

        static class Program
        {
            static int Depth(int n)
            {
                if (n == 0)
                {
                    return 0;
                }

                int below = Depth(n + 1);
                return below * 3 + n;
            }

            static void Main()
            {
                Console.WriteLine("start");
                Console.WriteLine(Depth(1));
            }
        }
        """;

    /// <summary>Two methods that call each other through a virtual call, a delegate and an interface, with no end.</summary>
    private const string Indirect = """
        using System;

        interface IStep
        {
            int Next(int n);
        }

        abstract class Walker
        {
            public abstract int Walk(int n);
        }

        sealed class Down : Walker, IStep
        {
            public Func<int, int> Again;

            public override int Walk(int n) => Again(n + 1) * 2 + 1;

            public int Next(int n) => Walk(n) - 1;
        }

        static class Program
        {
            static IStep step;

            static void Main()
            {
                var down = new Down();
                step = down;
                down.Again = n => step.Next(n) + n;
                Console.WriteLine("start");
                Console.WriteLine(step.Next(1));
            }
        }
        """;

    /// <summary>Deep recursions that end, on the Pico's 8 KB stack, and a frame of 1 KB on the stack in a loop.</summary>
    private const string Finite = """
        using System;

        static class Program
        {
            static int Depth(int n) => n == 0 ? 0 : 1 + Depth(n - 1) * 1;

            static long Sum(int[] values, int from) => from == values.Length ? 0 : values[from] + Sum(values, from + 1) / 2;

            static int Buffer(int seed)
            {
                Span<int> buffer = stackalloc int[256];
                for (int i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = seed + i;
                }

                int sum = 0;
                foreach (int value in buffer)
                {
                    sum += value;
                }

                return sum;
            }

            static void Main()
            {
                Console.WriteLine(Depth(200));
                Console.WriteLine(Sum(new int[] { 5, 6, 7, 8, 9, 10, 11 }, 0));
                Console.WriteLine(Buffer(3));
            }
        }
        """;

    [HostFact]
    public async Task On_the_PC_an_endless_recursion_writes_Stack_overflow_and_exits_with_dotnets_code()
    {
        var bytes = CSharpCompiler.Compile(Endless, "Endless");
        var dotnet = await HostRun.RunOnDotnetAsync(bytes, "Endless", Path.Combine(_scratch, "dotnet"), failing: true);
        var module = Lowered.Lower(WholeProgramPass.Analyze(Lowered.Compile(Endless, "Endless")));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));
        output.WriteLine($".NET:\n{dotnet}\nChipSharp:\n{chipSharp}");

        Assert.Equal("start\r\n", chipSharp.Text);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal("Stack overflow.\r\n", chipSharp.Errors);
        Assert.StartsWith(chipSharp.Errors, dotnet.Errors, StringComparison.Ordinal);
        Assert.Equal(StackOverflowExitCode, chipSharp.ExitCode);
        Assert.Equal(dotnet.ExitCode, chipSharp.ExitCode);
    }

    [HostFact]
    public async Task On_the_PC_a_recursion_through_virtual_calls_a_delegate_and_an_interface_ends_the_same_way()
    {
        var module = Lowered.Lower(WholeProgramPass.Analyze(Lowered.Compile(Indirect, "Indirect")));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.Equal(("start\r\n", "Stack overflow.\r\n", StackOverflowExitCode),
            (chipSharp.Text, chipSharp.Errors, chipSharp.ExitCode));
    }

    [SimulatorFact]
    public async Task On_the_Pico_an_endless_recursion_says_Stack_overflow_over_UART0_and_stops_at_a_breakpoint()
    {
        var program = PicoBuild.Build(Endless, "Endless", Path.Combine(_scratch, "endless"));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 2000);
        output.WriteLine(run.ToString());

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("hit a breakpoint", run.Stopped);
        Assert.Equal("start\r\nStack overflow.\r\n", Encoding.ASCII.GetString(run.Uart0));
    }

    [SimulatorFact]
    public async Task On_the_Pico_a_recursion_through_virtual_calls_a_delegate_and_an_interface_is_caught_too()
    {
        var program = PicoBuild.Build(Indirect, "Indirect", Path.Combine(_scratch, "indirect"));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 2000);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("hit a breakpoint", run.Stopped);
        Assert.Equal("start\r\nStack overflow.\r\n", Encoding.ASCII.GetString(run.Uart0));
    }

    [SimulatorFact]
    public async Task On_the_Pico_recursions_that_end_and_a_big_frame_run_as_on_dotnet()
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(Finite, "Finite"), "Finite",
            Path.Combine(_scratch, "dotnet"));
        var program = PicoBuild.Build(Finite, "Finite", Path.Combine(_scratch, "finite"));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 2000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.ASCII.GetString(run.Uart0));
    }

    [PicoFact]
    public void Only_the_functions_that_can_recurse_or_have_a_big_frame_check_the_stack_and_the_limit_is_above_its_bottom()
    {
        var module = Lowered.Lower(WholeProgramPass.Analyze(Lowered.Compile(Finite, "Finite"), PicoBuild.Pico));
        var ir = new LlvmCodeGenerator().Generate(module, PicoBuild.Pico).Files.Single().Content;
        var checking = ir.Split("\ndefine ").Skip(1).Select(function => function[..function.IndexOf("\n}", StringComparison.Ordinal)])
            .Where(function => function.Contains("@llvm.stacksave.p0()", StringComparison.Ordinal))
            .Select(function => function[(function.IndexOf('@') + 1)..function.IndexOf('(')]).Order().ToList();
        var hostTarget = new ChipSharp.Targets.TargetLoader(Path.Combine(AppContext.BaseDirectory, "targets")).Load("host");
        var host = new LlvmCodeGenerator().Generate(module, hostTarget).Files.Single().Content;
        var program = PicoBuild.Build(Finite, "Finite", Path.Combine(_scratch, "finite"));
        var elf = ElfFile.Read(program.Elf);

        Assert.Equal(["Program_Buffer", "Program_Depth", "Program_Sum"], checking);
        Assert.DoesNotContain("stacksave", host);

        // no heap in this program: the stack may go down to the variables; the limit is 1 KB above
        Assert.False(elf.Symbols.ContainsKey("__chipsharp_heap_start"));
        Assert.Equal(elf.Symbols["__chipsharp_bss_end"], elf.Symbols["__chipsharp_stack_bottom"]);
        Assert.Equal(elf.Symbols["__chipsharp_stack_bottom"] + 1024, elf.Symbols["__chipsharp_stack_limit"]);
    }
}
