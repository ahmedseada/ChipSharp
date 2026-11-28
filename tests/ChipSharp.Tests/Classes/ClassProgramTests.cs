using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Classes;

/// <summary>
/// The programs of <see cref="ClassPrograms"/>, checked by the whole-program pass, compiled by ChipSharp for the PC and
/// run: each must print what the same assembly prints on real .NET, compiled in Release and in Debug (whose IL keeps
/// every value in a local). The same programs run on the simulated Pico too (<see cref="ClassSimulatorTests"/>).
/// </summary>
public sealed class ClassProgramTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => ClassPrograms.InReleaseAndDebug();

    [HostTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_what_dotnet_prints(string name, bool debug)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(ClassPrograms.Source(name), name), name,
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(ClassPrograms.Compile(name, debug)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.Output, chipSharp.Output);
        Assert.Equal(dotnet.ExitCode, chipSharp.ExitCode);
    }

    /// <summary>
    /// In a 16 KB heap the survivors' program makes some 2.6 MB of objects: without hundreds of collections it would
    /// run out of memory, and a collection that missed one of the places a survivor is kept in would change what it
    /// prints.
    /// </summary>
    [HostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Objects_in_every_place_a_reference_lives_survive_hundreds_of_collections(bool debug)
    {
        var host = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("host-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(ClassPrograms.Survivors, "Survivors"),
            "Survivors", Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(ClassPrograms.Compile("Survivors", debug), host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host);
        output.WriteLine($"PC, 16 KB heap: {result.Time.TotalMilliseconds:0} ms\n{result}");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    /// <summary>A field of null, a method called on null, and a struct field of null stop the program as .NET does.</summary>
    [HostTheory]
    [InlineData("Field", "return node.Value;")]
    [InlineData("Method", "return node.Twice();")]
    [InlineData("Store", "node.Value = 1; return 0;")]
    [InlineData("StructField", "return node.Place.X;")]
    public async Task Null_stops_the_program_with_dotnets_null_reference_message(string name, string body)
    {
        var source = $$"""
            using System;

            struct Point
            {
                public int X;
            }

            class Node
            {
                public int Value;
                public Point Place;

                public int Twice() => Value * 2;
            }

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(1);
                    Console.WriteLine(Use(Find(3)));
                    Console.WriteLine(2);
                }

                static Node Find(int key) => key == 0 ? new Node() : null;

                static int Use(Node node)
                {
                    {{body}}
                }
            }
            """;
        var module = Lowered.Lower(WholeProgramPass.Analyze(ClassPrograms.Compile(source, name, debug: false)));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, name));

        Assert.Equal((1, "1\r\n", "Unhandled exception. System.NullReferenceException: Object reference not set to an " +
            "instance of an object.\r\n"), (run.ExitCode, run.Text, run.Errors));
    }
}
