using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Lambdas;

/// <summary>
/// The programs of <see cref="DelegatePrograms"/>, checked by the whole-program pass, compiled by ChipSharp for the PC and
/// run: each must print what the same assembly prints on real .NET, compiled in Release and in Debug. Invoking null, and
/// Combine of two types, stop the program as .NET does. The same programs run on the simulated Pico too
/// (<see cref="DelegateSimulatorTests"/>).
/// </summary>
public sealed class DelegateProgramTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => DelegatePrograms.InReleaseAndDebug();

    [HostTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_what_dotnet_prints(string name, bool debug)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(DelegatePrograms.Source(name), name), name,
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(DelegatePrograms.Compile(name, debug)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.Output, chipSharp.Output);
        Assert.Equal(dotnet.ExitCode, chipSharp.ExitCode);
    }

    /// <summary>
    /// In a 16 KB heap the churn's 20000 closures (each with an array), its multicast lists and their copies need many
    /// collections while delegates in static fields, in objects and on the stack are in use.
    /// </summary>
    [HostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closures_go_on_through_many_collections_in_a_small_heap(bool debug)
    {
        var host = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("host-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(DelegatePrograms.Churn, "Churn"), "Churn",
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(DelegatePrograms.Compile("Churn", debug), host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host);
        output.WriteLine($"PC, 16 KB heap: {result.Time.TotalMilliseconds:0} ms\n{result}");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    /// <summary>Invoking a null delegate, directly or through a field, stops the program with .NET's NullReferenceException message.</summary>
    [HostTheory]
    [InlineData("Func<int> none = null; Console.WriteLine(none());")]
    [InlineData("Console.WriteLine(Field(3));")]
    [InlineData("Action<int> none = Pick(false); none.Invoke(1);")]
    public async Task Invoking_null_stops_the_program_with_dotnets_null_reference_message(string statements)
    {
        var source = $$"""
            using System;

            static class Program
            {
                static Func<int, int> field;

                static int Field(int x) => field(x);

                static Action<int> Pick(bool some) => some ? x => Console.WriteLine(x) : null;

                static void Main()
                {
                    Console.WriteLine(1);
                    {{statements}}
                    Console.WriteLine(2);
                }
            }
            """;
        var module = Lowered.Lower(WholeProgramPass.Analyze(DelegatePrograms.Compile(source, "Null", debug: false)));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "null"));

        Assert.Equal((1, "1\r\n", "Unhandled exception. System.NullReferenceException: Object reference not set to an " +
            "instance of an object.\r\n"), (run.ExitCode, run.Text, run.Errors));
    }

    /// <summary>
    /// What .NET throws for the methods of System.Array that take a delegate and for Combine of two types, ChipSharp stops
    /// the program with, saying the same (each message here is the one .NET 10 prints for the same call).
    /// </summary>
    [HostTheory]
    [InlineData("Array.Find(numbers, null)", "System.ArgumentNullException: Value cannot be null. (Parameter 'match')")]
    [InlineData("Array.Exists(none, x => true)", "System.ArgumentNullException: Value cannot be null. (Parameter 'array')")]
    [InlineData("Array.ConvertAll<int, long>(numbers, null).Length",
        "System.ArgumentNullException: Value cannot be null. (Parameter 'converter')")]
    [InlineData("Array.FindIndex(numbers, 4, x => true)", "System.ArgumentOutOfRangeException: Index was out of range. " +
        "Must be non-negative and less than or equal to the size of the collection. (Parameter 'startIndex')")]
    [InlineData("Array.FindIndex(numbers, 0, 4, x => true)", "System.ArgumentOutOfRangeException: Count must be positive " +
        "and count must refer to a location within the string/array/collection. (Parameter 'count')")]
    [InlineData("Array.FindLastIndex(numbers, 3, x => true)", "System.ArgumentOutOfRangeException: Index was out of " +
        "range. Must be non-negative and less than the size of the collection. (Parameter 'startIndex')")]
    [InlineData("Count(Delegate.Combine((Action)(() => { }), (Func<int>)(() => 1)))",
        "System.ArgumentException: Delegates must be of the same type.")]
    [InlineData("Count(Delegate.Remove((Action)(() => { }), (Func<int>)(() => 1)))",
        "System.ArgumentException: Delegates must be of the same type.")]
    public async Task A_failure_stops_the_program_with_dotnets_message(string expression, string exception)
    {
        var source = $$"""
            using System;

            static class Program
            {
                static int Count(Delegate d) => d.GetInvocationList().Length;

                static void Main()
                {
                    int[] numbers = { 1, 2, 3 };
                    int[] none = null;
                    Console.WriteLine(numbers.Length + (none == null ? 0 : 1));
                    Console.WriteLine({{expression}});
                }
            }
            """;
        var module = Lowered.Lower(WholeProgramPass.Analyze(DelegatePrograms.Compile(source, "Failure", debug: false)));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "failure"));

        Assert.Equal((1, "3\r\n", $"Unhandled exception. {exception}\r\n"), (run.ExitCode, run.Text, run.Errors));
    }
}
