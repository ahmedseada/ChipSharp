using System.Text;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Programs with fixed text, compiled by ChipSharp for the PC and run: each must print what the same assembly prints
/// on real .NET, Console.Write and Console.WriteLine of every type alike. Text that is not ASCII is written as UTF-8,
/// byte for byte what .NET writes with a UTF-8 console (the bytes .NET writes to a redirected console depend on the
/// PC's code page). The same programs run on the simulated Pico too (Pico/TextSimulatorTests).
/// </summary>
public sealed class TextProgramTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [HostFact]
    public Task Write_and_WriteLine_print_every_type_as_dotnet_does_WriteLine_ending_the_line() =>
        PrintsWhatDotnetPrints(TextPrograms.WriteAndWriteLine);

    [HostFact]
    public Task Literals_in_locals_parameters_returns_fields_and_consts_compared_with_null() =>
        PrintsWhatDotnetPrints(TextPrograms.Literals);

    [HostFact]
    public async Task Every_program_compiled_in_Debug_prints_the_same()
    {
        foreach (var (name, source) in TextPrograms.All)
        {
            var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Program"), "Program",
                Path.Combine(_scratch, name, "dotnet"));
            var module = Lowered.Lower(Lowered.Program(Lowered.CompileDebug(source, "Program")));

            var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, name, "chipsharp"));

            Assert.True(dotnet.Output.SequenceEqual(chipSharp.Output), $"{name}:\n{dotnet}\n{chipSharp}");
        }
    }

    [HostFact]
    public async Task Text_that_is_not_ASCII_is_written_as_UTF8()
    {
        var module = Lowered.Module(TextPrograms.NotAscii, assemblyName: "Program");

        var run = await HostRun.BuildAndRunAsync(module, _scratch);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(TextPrograms.NotAsciiOutput, run.Text);
        Assert.Equal(Encoding.UTF8.GetBytes(TextPrograms.NotAsciiOutput), run.Output);
    }

    /// <summary>Compiles <paramref name="source"/>, runs it on .NET and as a ChipSharp program, and compares.</summary>
    private async Task PrintsWhatDotnetPrints(string source)
    {
        var assembly = CSharpCompiler.Compile(source, "Program");
        var dotnet = await HostRun.RunOnDotnetAsync(assembly, "Program", Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(Lowered.Program(IlAssembly.Load(assembly, "Program.dll")));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.Output, chipSharp.Output);
        Assert.Equal(dotnet.ExitCode, chipSharp.ExitCode);
    }
}

/// <summary>C# programs with fixed text, for the PC and the Pico tests.</summary>
internal static class TextPrograms
{
    /// <summary>Every type Console.Write and Console.WriteLine print, at its extremes, between text.</summary>
    public const string WriteAndWriteLine = """
        using System;

        class Program
        {
            static void Main()
            {
                Console.Write("int: ");
                Console.Write(0);
                Console.Write(" ");
                Console.Write(-7);
                Console.Write(" ");
                Console.Write(int.MaxValue);
                Console.Write(" ");
                Console.WriteLine(int.MinValue);
                Console.Write("uint: ");
                Console.Write(0u);
                Console.Write(" ");
                Console.WriteLine(uint.MaxValue);
                Console.Write("long: ");
                Console.Write(long.MinValue);
                Console.Write(" ");
                Console.WriteLine(long.MaxValue);
                Console.Write("ulong: ");
                Console.Write(ulong.MaxValue);
                Console.Write(" ");
                Console.WriteLine(0UL);
                Console.Write("bool: ");
                Console.Write(true);
                Console.Write(" ");
                Console.WriteLine(false);
                Console.Write("float: ");
                Console.Write(1.5f);
                Console.Write(" ");
                Console.Write(-0f);
                Console.Write(" ");
                Console.Write(float.NaN);
                Console.Write(" ");
                Console.Write(1e-7f);
                Console.Write(" ");
                Console.WriteLine(float.MaxValue);
                Console.Write("double: ");
                Console.Write(0.1);
                Console.Write(" ");
                Console.Write(double.Epsilon);
                Console.Write(" ");
                Console.Write(-1e300);
                Console.Write(" ");
                Console.WriteLine(double.NegativeInfinity);
                Console.Write("text: [");
                Console.Write("x");
                Console.Write("");
                Console.Write((string)null);
                Console.WriteLine("]");
                Console.WriteLine((string)null);
                Console.WriteLine("");
                Console.WriteLine();
                Console.WriteLine(-1);
                Console.WriteLine(4000000000u);
                Console.WriteLine(-2L);
                Console.WriteLine(18000000000000000000UL);
                Console.WriteLine(true);
                Console.WriteLine(2.5f);
                Console.WriteLine(-0.0);
                Console.Write("no end of line");
            }
        }
        """;

    /// <summary>Text where C# keeps it: locals, parameters, return values, static fields and consts, and null.</summary>
    public const string Literals = """
        using System;

        class Program
        {
            const string Road = "road : ";
            static readonly string Green = "    green ";
            static string current = "none";

            static string LaneName(int lane) => lane switch
            {
                1 => "north",
                2 => "east",
                3 => "south",
                _ => null,
            };

            static void Show(string label, int value)
            {
                Console.Write(label);
                Console.WriteLine(value);
            }

            static void Main()
            {
                for (int lane = 1; lane <= 4; lane++)
                {
                    string name = LaneName(lane);
                    Console.Write(Road);
                    Console.Write(lane);
                    Console.Write(" (");
                    Console.Write(name ?? "unknown");
                    Console.WriteLine(")");
                    Show(Green, lane * 10);
                    if (name == null)
                    {
                        Console.WriteLine("    no name");
                    }

                    current = name;
                }

                Console.WriteLine(current is null);
                Console.WriteLine(string.Empty == null);
                Console.WriteLine((object)Road == (object)"road : ");
                Console.WriteLine((object)Road == (object)Green);
                Console.Write("lane : ");
                Console.Write(2);
                Console.Write(" interrupted, move to lane ");
                Console.WriteLine(3);
            }
        }
        """;

    /// <summary>Text that is not ASCII: Latin, a euro sign, Japanese, a smiley (a surrogate pair), a lone surrogate.</summary>
    public const string NotAscii = """
        using System;

        class Program
        {
            static void Main()
            {
                Console.WriteLine("caf\u00e9");
                Console.Write("\u20ac");
                Console.Write(5);
                Console.WriteLine(" \u65e5\u672c");
                Console.WriteLine("\U0001F642");
                Console.WriteLine("\uD800");
                Console.WriteLine("tab\there\0nul");
            }
        }
        """;

    /// <summary>What <see cref="NotAscii"/> prints; as UTF-8, a lone surrogate is U+FFFD.</summary>
    public const string NotAsciiOutput = "caf\u00e9\r\n\u20ac5 \u65e5\u672c\r\n\U0001F642\r\n\uFFFD\r\ntab\there\0nul\r\n";

    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(WriteAndWriteLine), WriteAndWriteLine),
        (nameof(Literals), Literals),
    ];
}
