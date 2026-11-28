using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Tests.Stm8;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Backends;

/// <summary>
/// What ChipSharp does about SDCC 4.6 on the STM8 and the PDK14, each checked against .NET and, on the STM8, run in
/// chipsim and in SDCC's own uCsim (<see cref="UcsimStm8"/>), which no one here wrote:
/// <list type="bullet">
/// <item>a loop entered at its test, as C# lays loops out, made SDCC give a value the loop still needs the stack place of
/// one the loop sets: the C code generator writes the blocks in reverse postorder, a loop's test first;</item>
/// <item>SDCC's libraries have no fmodf, so C#'s % on floats did not link: the runtimes have one, exact;</item>
/// <item>SDCC's generalized constant propagation crashes on some files: the toolchain compiles such a file again
/// without it.</item>
/// </list>
/// </summary>
public sealed class SdccMendsTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    /// <summary>
    /// Two loops, one in the other, and four values read after them: in TIR's order (each loop entered by a jump down to
    /// its test) SDCC 4.6 put the outer counter where the inner one is, and the program counted forever.
    /// </summary>
    private const string NestedLoops = """
        using System;

        class Program
        {
            static volatile int input = 1;
            static readonly int[] Values = { 3, 100000, -7, 123456789, 42 };

            static void Main()
            {
                int k = input;
                int a = Values[k], b = Values[k + 1], c = Values[k + 2], d = Values[k + 3];
                for (int i = -1; i < (k & 3) + 4; i++)
                {
                    for (int j = -2; j < (k & 3) + 2; j++)
                    {
                        Console.WriteLine(j);
                        Console.WriteLine(a ^ b);
                    }
                }

                Console.WriteLine(c - d);
            }
        }
        """;

    [SdccFact]
    public async Task Nested_loops_with_values_read_after_them_print_what_dotnet_prints_on_the_STM8_in_chipsim_and_uCsim()
    {
        var target = BackendTargets.Get("stm8s103-board");
        var outcome = await BackendRun.RunAsync(target, "NestedLoops", NestedLoops, Path.Combine(_scratch, "chipsim"));

        Assert.True(outcome.Skipped is null, outcome.Why);
        Assert.True(outcome.Run!.ExitCode == 0, outcome.Run.ToString());
        Assert.Equal(outcome.Dotnet!.Text, outcome.Run.Text);
        if (UcsimStm8.Path is not null)
        {
            var (module, binary, skip) = BackendRun.BuildForChip(target.Load(), "NestedLoops", NestedLoops,
                Path.Combine(_scratch, "ucsim"), UcsimStm8.ReportFile);
            Assert.Null(skip);
            var (output, failure, ended) = UcsimStm8.Run(binary!.Path, module!, TimeSpan.FromMinutes(1));
            Assert.True(failure is null && ended, failure ?? "it did not end in uCsim");
            Assert.Equal(outcome.Dotnet.Text, Encoding.UTF8.GetString(output));
        }
    }

    /// <summary>The pairs a float remainder is checked on: signs, zeros, infinities, NaN, subnormals, far exponents.</summary>
    private static readonly (float X, float Y)[] Remainders =
    [
        (7.5f, 2f), (-7.5f, 2f), (7.5f, -2f), (-7.5f, -2f), (1e20f, 3f), (3f, 1e20f), (-0f, 1f), (0.1f, 0.03f),
        (1f, float.PositiveInfinity), (-5f, float.NegativeInfinity), (float.PositiveInfinity, 1f), (1f, 0f), (float.NaN, 1f),
        (1f, float.NaN), (5e-45f, 3e-45f), (1.17549435E-38f, 1e-45f), (float.MaxValue, 1.17549435E-38f), (16777215f, 0.1f),
        (float.MaxValue, 3f), (2f, 2f), (-2f, 2f), (123456.789f, 0.001f),
    ];

    private static string RemainderProgram => $$"""
        using System;

        class Program
        {
            static readonly float[] X = { {{string.Join(", ", Remainders.Select(pair => Literal(pair.X)))}} };
            static readonly float[] Y = { {{string.Join(", ", Remainders.Select(pair => Literal(pair.Y)))}} };

            static void Main()
            {
                for (int i = 0; i < X.Length; i++)
                {
                    Console.WriteLine(X[i] % Y[i]);
                }
            }
        }
        """;

    [SdccFact]
    public void A_float_remainder_links_and_gives_dotnets_results_on_the_STM8_in_chipsim_and_uCsim()
    {
        // what .NET itself computes, written as Console.WriteLine writes it
        var expected = string.Concat(Remainders.Select(pair => (pair.X % pair.Y).ToString(CultureInfo.InvariantCulture) + "\r\n"));

        var program = Stm8sProgram.Build(_scratch, "Remainders", RemainderProgram); // undefined _fmodf before
        var run = program.Run(500);

        Assert.Equal(expected, Encoding.ASCII.GetString(run.Serial));
        if (UcsimStm8.Path is not null)
        {
            Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), UcsimStm8.Uart1(program.Hex, TimeSpan.FromMinutes(1))
                .Replace("\r\n", "\n", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// On the PFS154 the remainder links too; that SDCC's float routines (and fmodf's variables, which SDCC keeps in
    /// RAM on a PDK14) need more than the chip's 128 bytes of RAM, as any float arithmetic does there, is what the build
    /// says, not an undefined _fmodf.
    /// </summary>
    [SdccFact]
    public void A_float_remainder_links_on_the_PFS154()
    {
        var folder = Path.Combine(_scratch, "pfs154");
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, "Remainder.dll");
        File.WriteAllBytes(dll, CSharpCompiler.Compile(["""
            using ChipSharp.Hardware;

            class Program
            {
                static volatile float x = 7.5f, y = 2f;

                static void Main()
                {
                    Pin.Output(4);
                    Pin.Write(4, x % y == 1.5f);
                }
            }
            """], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], "Remainder"));

        var failure = Assert.Throws<ChipSharpException>(() => ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = "pfs154-bare",
            TargetsFolder = Path.Combine(AppContext.BaseDirectory, "targets"),
            OutputFolder = Path.Combine(folder, "out"),
        }));

        // the link's own words, under the message, before: "?ASlink-Warning-Undefined Global _fmodf referenced by module"
        Assert.DoesNotContain("Undefined Global _fmodf", failure.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("Remainder does not fit in target 'pfs154-bare': its variables", failure.Message, StringComparison.Ordinal);
    }

    private static string Literal(float value) => value switch
    {
        float.PositiveInfinity => "float.PositiveInfinity",
        float.NegativeInfinity => "float.NegativeInfinity",
        _ when float.IsNaN(value) => "float.NaN",
        _ when value == 0 && float.IsNegative(value) => "-0f",
        _ => value.ToString("R", CultureInfo.InvariantCulture) + "f",
    };
}
