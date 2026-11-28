using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Classes;

/// <summary>
/// The programs of <see cref="VirtualPrograms"/>, checked by the whole-program pass, compiled by ChipSharp for the PC and
/// run: each must print what the same assembly prints on real .NET, compiled in Release and in Debug. A cast that fails
/// and a virtual call on null stop the program as .NET does. The same programs run on the simulated Pico too
/// (<see cref="VirtualSimulatorTests"/>).
/// </summary>
public sealed class VirtualProgramTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => VirtualPrograms.InReleaseAndDebug();

    /// <summary>The program that casts each of its objects to Program.Fruit, the last of them wrongly.</summary>
    public const string Casts = """
        using System;

        interface IThing
        {
        }

        namespace Market
        {
            class Crate
            {
            }
        }

        interface IStore<T>
        {
        }

        partial class Program
        {
            class Fruit
            {
            }

            class Apple : Fruit
            {
            }

            static void Main()
            {
                Console.WriteLine(1);
                Console.WriteLine(Use(Pick()) == null);
                Console.WriteLine(2);
            }
        }
        """;

    [HostTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_what_dotnet_prints(string name, bool debug)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(VirtualPrograms.Source(name), name), name,
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(VirtualPrograms.Compile(name, debug)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.Output, chipSharp.Output);
        Assert.Equal(dotnet.ExitCode, chipSharp.ExitCode);
    }

    /// <summary>
    /// In a 16 KB heap the zoo's 20000 animals (some 600 KB) need dozens of collections while every one of them is called
    /// through its virtual method table and its interface map, and tested against a class: a collection that lost a kept
    /// animal, or broke the header of one, would change what it prints.
    /// </summary>
    [HostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Virtual_calls_go_on_through_dozens_of_collections_in_a_small_heap(bool debug)
    {
        var host = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("host-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(VirtualPrograms.Zoo, "Zoo"), "Zoo",
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(VirtualPrograms.Compile("Zoo", debug), host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host);
        output.WriteLine($"PC, 16 KB heap: {result.Time.TotalMilliseconds:0} ms\n{result}");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    /// <summary>
    /// A cast that fails stops the program with .NET's message, which names a nested type by its own name (Program+Apple
    /// is Apple), any other by its full name, and an instance of a generic type with its type arguments, as .NET 10 does
    /// (each message here is the one .NET 10 prints for the same program).
    /// </summary>
    [HostTheory]
    [InlineData("object Pick() => new Apple();", "static Program Use(object thing) => (Program)thing;", "Apple", "Program")]
    [InlineData("object Pick() => new Market.Crate();", "static Fruit Use(object thing) => (Fruit)thing;",
        "Market.Crate", "Fruit")]
    [InlineData("object Pick() => new Fruit();", "static IThing Use(object thing) => (IThing)thing;", "Fruit", "IThing")]
    [InlineData("object Pick() => new object();", "static IStore<int> Use(object thing) => (IStore<int>)thing;",
        "System.Object", "IStore`1[System.Int32]")]
    [InlineData("object Pick() => new int[2];", "static Apple Use(object thing) => (Apple)thing;", "System.Int32[]",
        "Apple")]
    [InlineData("object Pick() => new Fruit[1];", "static Apple[] Use(object thing) => (Apple[])thing;", "Fruit[]",
        "Apple[]")]
    public async Task A_cast_that_fails_stops_the_program_with_dotnets_message(string pick, string use, string from,
        string to)
    {
        var source = Casts.Replace("    class Fruit", $"    static {pick}\n\n    {use}\n\n    class Fruit",
            StringComparison.Ordinal);
        var module = Lowered.Lower(WholeProgramPass.Analyze(VirtualPrograms.Compile(source, "Casts", debug: false)));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.Equal((1, "1\r\n", $"Unhandled exception. System.InvalidCastException: Unable to cast object of type " +
            $"'{from}' to type '{to}'.\r\n"), (run.ExitCode, run.Text, run.Errors));
    }

    /// <summary>A virtual call, an interface call and Equals on null stop the program with .NET's NullReferenceException message.</summary>
    [HostTheory]
    [InlineData("Virtual", "return shape.Area();")]
    [InlineData("Interface", "IShape described = shape; return described.Area();")]
    [InlineData("Equals", "return shape.Equals(shape) ? 1 : 0;")]
    public async Task A_virtual_call_on_null_stops_the_program_with_dotnets_null_reference_message(string name, string body)
    {
        var source = $$"""
            using System;

            interface IShape
            {
                int Area();
            }

            abstract class Shape : IShape
            {
                public abstract int Area();
            }

            class Square : Shape
            {
                public override int Area() => 4;

                public override bool Equals(object other) => other is Square;

                public override int GetHashCode() => 4;
            }

            class Circle : Shape
            {
                public override int Area() => 3;
            }

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(Use(new Square()) + Use(new Circle()));
                    Console.WriteLine(Use(null));
                }

                static int Use(Shape shape)
                {
                    {{body}}
                }
            }
            """;
        var module = Lowered.Lower(WholeProgramPass.Analyze(VirtualPrograms.Compile(source, name, debug: false)));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, name));

        Assert.Equal(1, run.ExitCode);
        Assert.Equal("Unhandled exception. System.NullReferenceException: Object reference not set to an instance of an " +
            "object.\r\n", run.Errors);
    }
}
