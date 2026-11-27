using ChipSharp.Frontend.Analysis;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Collections;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Tuples;

/// <summary>
/// The programs of <see cref="TuplePrograms"/>, checked by the whole-program pass, compiled by ChipSharp for the PC and
/// run: each must print what the same assembly prints on real .NET, compiled in Release and in Debug. The tuples that
/// need no heap run on a PC without one; the tuples and collection expressions of the garbage program keep what they
/// hold through many collections of a small heap, with the heap checked at each.
/// </summary>
public sealed class TupleProgramTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => TuplePrograms.InReleaseAndDebug();

    public static TheoryData<string> Failures => TuplePrograms.FailureNames();

    [HostTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_what_dotnet_prints(string name, bool debug)
    {
        var source = TuplePrograms.Source(name);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(TuplePrograms.Compile(source, name, debug)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.ExitCode, chipSharp.ExitCode);
    }

    /// <summary>
    /// A failure prints what came before it, then on standard error what .NET 10 prints for the exception up to its stack
    /// trace (an inner exception's lines too), and the program exits with code 1.
    /// </summary>
    [HostTheory]
    [MemberData(nameof(Failures))]
    public async Task Each_failure_stops_with_the_words_dotnet_prints(string name)
    {
        var source = TuplePrograms.FailureSource(name);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name,
            Path.Combine(_scratch, "dotnet"), failing: true);
        var module = Lowered.Lower(WholeProgramPass.Analyze(TuplePrograms.Compile(source, name, debug: false)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.StartsWith("1\r\n", dotnet.Text, StringComparison.Ordinal);
        Assert.Equal((1, dotnet.Text, CollectionProgramTests.WithoutStackTrace(dotnet.Errors)),
            (chipSharp.ExitCode, chipSharp.Text, chipSharp.Errors));
    }

    /// <summary>
    /// The tuples of <see cref="TuplePrograms.Basics"/> need no heap: built for a PC whose target file says it has none,
    /// the program has no heap at all, and prints what .NET prints.
    /// </summary>
    [HostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tuples_need_no_heap_on_a_PC_without_one(bool debug)
    {
        var host = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("host-no-heap", """{ "inherits": "host", "capabilities": { "heap": false } }"""))).Load("host-no-heap");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(TuplePrograms.Basics, "Basics"), "Basics",
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(TuplePrograms.Compile(TuplePrograms.Basics, "Basics", debug), host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    /// <summary>
    /// In a 16 KB heap the garbage program's thousands of readings, lists and arrays made by collection expressions (with
    /// spans over the lists they spread) need many collections, while a static list of tuples and a dictionary keyed by
    /// tuples keep some of them: one lost, or read wrongly, changes the output. The heap is checked at every collection.
    /// </summary>
    [HostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tuples_and_collection_expressions_keep_what_they_hold_through_many_collections(bool debug)
    {
        var host = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("host-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(TuplePrograms.Garbage, "Garbage"), "Garbage",
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(
            TuplePrograms.Compile(TuplePrograms.Garbage, "Garbage", debug), host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host, verifyHeap: true);
        output.WriteLine($"PC, 16 KB heap: {result.Time.TotalMilliseconds:0} ms\n{result}");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    /// <summary>The Tuples sample in a 16 KB heap prints its expected output.</summary>
    [HostFact]
    public async Task The_tuples_sample_prints_its_expected_output_in_a_small_heap()
    {
        var host = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("host-16K");
        var source = File.ReadAllText(Path.Combine(Samples.Folder, "Tuples.cs"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(TuplePrograms.Compile(source, "Tuples", debug: false), host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(Samples.ExpectedOutput("Tuples"), System.Text.Encoding.UTF8.GetBytes(result.Raw));
    }
}
