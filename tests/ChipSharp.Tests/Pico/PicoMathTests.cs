using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// System.Math and System.MathF on the Pico, which has no float hardware: a program prints over UART0, in the simulator,
/// the very bytes it prints on the PC (transcendental results too), with no heap; where .NET throws it says .NET's
/// message; a program links only the functions it calls; and each takes the time on the Cortex-M0+ it states.
/// </summary>
public sealed class PicoMathTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [SimulatorFact]
    [RunsLong]
    public async Task On_a_Pico_without_a_heap_every_member_prints_the_very_bytes_it_prints_on_the_PC()
    {
        var source = MathProgramTests.Program(40, pico: true).Source();
        var assembly = CSharpCompiler.Compile(source, "MathMembers");
        var dotnet = await HostRun.RunOnDotnetAsync(assembly, "MathMembers", Path.Combine(_scratch, "dotnet"));
        var module = IlToTir.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "MathMembers.dll")));
        var pc = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "host"));
        var targets = TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""));
        var target = new TargetLoader(targets).Load("pico-no-heap");

        var pico = PicoBuild.Build(source, "MathMembers", Path.Combine(_scratch, "pico"), target);
        var run = await Rp2040Simulator.RunAsync(pico.Uf2, 600_000);

        Assert.False(module.UsesHeap);
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.True(pc.Text.Split('\n').Length > 10_000, "The program prints too little to test much.");
        Assert.Equal(pc.Text, Encoding.ASCII.GetString(run.Uart0));
        MathProgram.AssertCloseToDotnet(dotnet.Text, pc.Text);
    }

    public static TheoryData<string, string> Failures => new()
    {
        { "Math.Sign(D(double.NaN))", "System.ArithmeticException: Function does not accept floating point Not-a-Number values." },
        { "Math.Clamp(D(2.5), 3.5, 1.25)", "System.ArgumentException: '3.5' cannot be greater than 1.25." },
        { "Math.Clamp((float)D(2.5), 0.1f, -1e30f)", "System.ArgumentException: '0.1' cannot be greater than -1E+30." },
        { "Math.Clamp((ulong)D(2), 18446744073709551615UL, 1UL)", "System.ArgumentException: '18446744073709551615' cannot be greater than 1." },
        { "Math.Clamp((int)D(2), -5, -1000000)", "System.ArgumentException: '-5' cannot be greater than -1000000." },
        { "MathF.Round((float)D(2.5), 7)", "System.ArgumentOutOfRangeException: Rounding digits must be between 0 and 6, inclusive. (Parameter 'digits')" },
        { "Math.Round(D(2.5), (MidpointRounding)9)", "System.ArgumentException: The value '9' is not valid for this usage of the type MidpointRounding. (Parameter 'mode')" },
        { "Math.Abs((long)D(-9.2233720368547758E+18))", "System.OverflowException: Negating the minimum value of a twos complement number is invalid." },
    };

    [SimulatorTheory]
    [MemberData(nameof(Failures))]
    public async Task Where_dotnet_throws_the_message_goes_to_UART0_and_the_program_stops_at_a_breakpoint(string statement,
        string message)
    {
        var program = PicoBuild.Build(MathProgramTests.Failing(statement), "Failing", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
        Assert.Equal($"1\r\nUnhandled exception. {message}\r\n", Encoding.UTF8.GetString(run.Uart0));
    }

    /// <summary>
    /// A program links only the Math functions it calls: one that calls Math.Sin has sin and its argument reduction (with
    /// Floor and ScaleB, which it uses), and none of the others; each family takes what <see cref="Families"/> says of
    /// flash, at most.
    /// </summary>
    [PicoFact]
    public void A_program_links_only_the_functions_it_calls()
    {
        var sin = Build("Math.Sin(x)", "Sin");
        var symbols = ElfFile.Read(sin.Elf).Symbols.Keys.ToHashSet();

        Assert.Contains("ChipSharp_Runtime_SinFloat64", symbols);
        Assert.Contains("chipsharp_musl_sin", symbols);
        Assert.Contains("chipsharp_rem_pio2_large", symbols);
        Assert.DoesNotContain("chipsharp_musl_cos", symbols);
        Assert.DoesNotContain("chipsharp_musl_pow", symbols);
        Assert.DoesNotContain("chipsharp_musl_exp", symbols);
        Assert.DoesNotContain("chipsharp_musl_exp_data", symbols);
        Assert.DoesNotContain("chipsharp_amd_atan2", symbols);
        Assert.DoesNotContain("ChipSharp_Runtime_PowFloat64", symbols);
        Assert.DoesNotContain("ChipSharp_Runtime_CeilingFloat64", symbols);

        // next to a program that does arithmetic on doubles and floats already: the soft-float routines are not the family's
        const string Arithmetic = "x * 1.5 + x / 3 - (float)x * 2.5f / 3f + (int)x + (float)(int)x";
        var baseline = Flash(Build(Arithmetic, "Baseline"));
        foreach (var (name, expression, most) in Families)
        {
            var flash = Flash(Build($"{Arithmetic} + {expression}", name)) - baseline;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name,-12} {flash,6} bytes of flash"));
            Assert.InRange(flash, 1, most);
        }
    }

    /// <summary>The functions by family, as a program calls them, and the most flash each family adds to a program.</summary>
    private static readonly (string Name, string Expression, long Most)[] Families =
    [
        ("Floor", "Math.Floor(x)", 512),
        ("Round", "Math.Round(x, 2, MidpointRounding.AwayFromZero)", 1024),
        ("Sin", "Math.Sin(x)", 11 * 1024),
        ("SinF", "MathF.Sin((float)x)", 10 * 1024),
        ("Atan2", "Math.Atan2(x, 0.5)", 7680),
        ("Exp", "Math.Exp(x)", 4 * 1024),
        ("ExpF", "MathF.Exp((float)x)", 2 * 1024),
        ("Log", "Math.Log(x)", 7 * 1024),
        ("Pow", "Math.Pow(x, 2.5)", 11 * 1024),
        ("PowF", "MathF.Pow((float)x, 2.5f)", 3 * 1024),
        ("Sinh", "Math.Sinh(x)", 4 * 1024),
        ("Cbrt", "Math.Cbrt(x)", 1536),
        ("FusedMultiplyAdd", "Math.FusedMultiplyAdd(x, x, x)", 3 * 1024),
    ];

    /// <summary>
    /// The time each function takes on the simulated Cortex-M0+ at 125 MHz (no float hardware), measured with the
    /// RP2040's microsecond timer over many calls: the MathF functions, which compute in floats or in a short double
    /// polynomial, take less than the Math ones; none more than its bound.
    /// </summary>
    [SimulatorFact]
    public async Task Each_function_takes_the_cycles_it_states_on_the_Cortex_M0()
    {
        (string Name, string Expression, int Most)[] functions =
        [
            ("Math.Sqrt", "Math.Sqrt(x)", 2200),
            ("Math.Floor", "Math.Floor(x)", 200),
            ("Math.Sin", "Math.Sin(x)", 14500),
            ("MathF.Sin", "MathF.Sin((float)x)", 9000),
            ("Math.Exp", "Math.Exp(x)", 9000),
            ("MathF.Exp", "MathF.Exp((float)x)", 5800),
            ("Math.Log", "Math.Log(x)", 10500),
            ("Math.Pow", "Math.Pow(x, 2.5)", 29000),
            ("MathF.Pow", "MathF.Pow((float)x, 2.5f)", 12500),
            ("Math.Atan2", "Math.Atan2(x, 0.5)", 14500),
        ];
        var body = new StringBuilder();
        foreach (var (name, expression, _) in functions)
        {
            body.Append(CultureInfo.InvariantCulture, $$"""
                        start = Registers.Read(TimeLow);
                        for (int i = 0; i < Calls; i++)
                        {
                            double x = D(0.1 + i * 0.37);
                            sum += {{expression}};
                        }

                        Console.WriteLine(Registers.Read(TimeLow) - start - empty);

                """);
        }

        var program = PicoBuild.Build($$"""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                const uint TimeLow = 0x40054028; // TIMER's TIMERAWL: the microseconds since the chip started
                const int Calls = 200;

                static double D(double x) => Math.Max(x, x);

                static void Main()
                {
                    double sum = 0;
                    uint start = Registers.Read(TimeLow);
                    for (int i = 0; i < Calls; i++)
                    {
                        double x = D(0.1 + i * 0.37);
                        sum += x;
                    }

                    uint empty = Registers.Read(TimeLow) - start;
            {{body}}
                    Console.WriteLine(sum);
                }
            }
            """, "Cycles", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 10_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        var lines = Encoding.ASCII.GetString(run.Uart0).Split("\r\n");
        var failures = new List<string>();
        for (var i = 0; i < functions.Length; i++)
        {
            // microseconds for 200 calls, at 125 cycles a microsecond
            var cycles = long.Parse(lines[i], CultureInfo.InvariantCulture) * 125 / 200;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{functions[i].Name,-12} {cycles,6} cycles"));
            if (cycles > functions[i].Most)
            {
                failures.Add($"{functions[i].Name}: {cycles} cycles, more than {functions[i].Most}");
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    private PicoProgram Build(string expression, string name) => PicoBuild.Build($$"""
        using System;

        class Program
        {
            static double D(double x) => Math.Max(x, x);

            static void Main()
            {
                double x = D(0.75);
                Console.WriteLine({{expression}});
            }
        }
        """, name, Path.Combine(_scratch, name));

    /// <summary>The bytes of flash a program takes: its code, constants and the starting values of its variables.</summary>
    private static long Flash(PicoProgram program) =>
        (long)MemoryReport.Read(new LinkedBinary(program.Elf), PicoBuild.Pico).Code!.Used;
}
