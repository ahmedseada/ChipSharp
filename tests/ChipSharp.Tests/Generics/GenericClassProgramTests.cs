using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Generics;

/// <summary>
/// The programs of <see cref="GenericClassPrograms"/>, checked by the whole-program pass, compiled by ChipSharp for the
/// PC and run: each must print what the same assembly prints on real .NET, compiled in Release and in Debug. A cast to an
/// instance of a generic class that fails says so with .NET's names; the garbage collector keeps what instances hold
/// through dozens of collections in a small heap. The same programs run on the simulated Pico too
/// (<see cref="GenericClassSimulatorTests"/>).
/// </summary>
public sealed class GenericClassProgramTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => GenericClassPrograms.InReleaseAndDebug();

    [HostTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_what_dotnet_prints(string name, bool debug)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(GenericClassPrograms.Source(name), name), name,
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(GenericClassPrograms.Compile(name, debug)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.Output, chipSharp.Output);
        Assert.Equal(dotnet.ExitCode, chipSharp.ExitCode);
    }

    /// <summary>
    /// In a 16 KB heap the 60000 objects of the garbage program (boxes, readings, windows and their arrays of T) need dozens
    /// of collections, while a stack of an instance and the static fields of two instances keep some: a collection that
    /// lost one, or one that read an instance's object wrongly, would change what it prints.
    /// </summary>
    [HostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Instances_keep_their_objects_through_dozens_of_collections_in_a_small_heap(bool debug)
    {
        var host = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("host-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(GenericClassPrograms.Garbage, "Garbage"),
            "Garbage", Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(GenericClassPrograms.Compile("Garbage", debug), host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host);
        output.WriteLine($"PC, 16 KB heap: {result.Time.TotalMilliseconds:0} ms\n{result}");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    /// <summary>
    /// A cast that fails names an instance of a generic class as .NET does: a nested generic type by its own name with
    /// its arity, its type arguments in brackets with their full names (each message here is the one .NET 10 prints).
    /// </summary>
    [HostTheory]
    [InlineData("object Pick() => new Box<Lane>();", "static Box<int> Use(object thing) => (Box<int>)thing;",
        "Box`1[Program+Lane]", "Box`1[System.Int32]")]
    [InlineData("object Pick() => new Pair<Lane, int>();", "static IStore<Lane> Use(object thing) => (IStore<Lane>)thing;",
        "Pair`2[Program+Lane,System.Int32]", "IStore`1[Program+Lane]")]
    [InlineData("object Pick() => new Box<Lane[]>();", "static Box<Box<Lane>> Use(object thing) => (Box<Box<Lane>>)thing;",
        "Box`1[Program+Lane[]]", "Box`1[Program+Box`1[Program+Lane]]")]
    [InlineData("object Pick() => new Lane();", "static Box<object> Use(object thing) => (Box<object>)thing;",
        "Lane", "Box`1[System.Object]")]
    public async Task A_cast_to_an_instance_that_fails_names_it_as_dotnet_does(string pick, string use, string from, string to)
    {
        var source = $$"""
            using System;

            interface IStore<T>
            {
            }

            partial class Program
            {
                class Lane
                {
                }

                class Box<T>
                {
                }

                class Pair<A, B> : IStore<B>
                {
                }

                static {{pick}}

                {{use}}

                static void Main()
                {
                    Console.WriteLine(1);
                    Console.WriteLine(Use(Pick()) == null);
                    Console.WriteLine(2);
                }
            }
            """;
        var module = Lowered.Lower(WholeProgramPass.Analyze(GenericClassPrograms.Compile(source, "Casts", debug: false)));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.Equal((1, "1\r\n", $"Unhandled exception. System.InvalidCastException: Unable to cast object of type " +
            $"'{from}' to type '{to}'.\r\n"), (run.ExitCode, run.Text, run.Errors));
    }
}
