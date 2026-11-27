using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Collections;

/// <summary>
/// The programs of <see cref="CollectionPrograms"/>, checked by the whole-program pass, compiled by ChipSharp for the PC
/// and run: each must print what the same assembly prints on real .NET, compiled in Release and in Debug. Each failure
/// stops as .NET's unhandled exception does, with its words (the stack trace aside) on standard error and exit code 1;
/// the collections keep what they hold through many collections of a small heap.
/// </summary>
public sealed class CollectionProgramTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => CollectionPrograms.InReleaseAndDebug();

    public static TheoryData<string> Failures => CollectionPrograms.FailureNames();

    [HostTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_what_dotnet_prints(string name, bool debug)
    {
        var source = CollectionPrograms.Source(name);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(CollectionPrograms.Compile(source, name, debug)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.ExitCode, chipSharp.ExitCode);
    }

    /// <summary>
    /// A failure prints what came before it, then on standard error what .NET 10 prints for the exception up to its stack
    /// trace (an inner exception's lines too), and the program exits with code 1 (.NET's is 0xE0434352).
    /// </summary>
    [HostTheory]
    [MemberData(nameof(Failures))]
    public async Task Each_failure_stops_with_the_words_dotnet_prints(string name)
    {
        var source = CollectionPrograms.FailureSource(name);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name,
            Path.Combine(_scratch, "dotnet"), failing: true);
        var module = Lowered.Lower(WholeProgramPass.Analyze(CollectionPrograms.Compile(source, name, debug: false)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.Equal("1\r\n", dotnet.Text);
        Assert.Equal((1, "1\r\n", WithoutStackTrace(dotnet.Errors)), (chipSharp.ExitCode, chipSharp.Text, chipSharp.Errors));
    }

    /// <summary>
    /// .NET 10 runs the finally blocks around a throw after it prints the exception (its second pass), where ChipSharp
    /// stops: a finally runs only when its try block is left normally, so here only the output before the throw is the
    /// same, and the finally's line is .NET's alone.
    /// </summary>
    [HostFact]
    public async Task A_throw_stops_the_program_without_running_the_finally_around_it()
    {
        const string Source = """
            using System;

            class Program
            {
                static void Main()
                {
                    try
                    {
                        Console.WriteLine("before");
                        throw new InvalidOperationException("stop");
                    }
                    finally
                    {
                        Console.WriteLine("finally");
                    }
                }
            }
            """;
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(Source, "Stop"), "Stop",
            Path.Combine(_scratch, "dotnet"), failing: true);
        var module = Lowered.Lower(WholeProgramPass.Analyze(CollectionPrograms.Compile(Source, "Stop", debug: false)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.Equal("before\r\nfinally\r\n", dotnet.Text);
        Assert.Equal((1, "before\r\n", "Unhandled exception. System.InvalidOperationException: stop\r\n"),
            (chipSharp.ExitCode, chipSharp.Text, chipSharp.Errors));
    }

    /// <summary>
    /// In a 16 KB heap the garbage program's thousands of lists, dictionaries, sets and stacks need many collections,
    /// while a dictionary and a queue in static fields keep some of them: one lost, or read wrongly, changes the output.
    /// </summary>
    [HostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Collections_keep_what_they_hold_through_many_collections_in_a_small_heap(bool debug)
    {
        var host = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("host-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(CollectionPrograms.Garbage, "Garbage"),
            "Garbage", Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(
            CollectionPrograms.Compile(CollectionPrograms.Garbage, "Garbage", debug), host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host);
        output.WriteLine($"PC, 16 KB heap: {result.Time.TotalMilliseconds:0} ms\n{result}");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    /// <summary>The Collections sample in a 16 KB heap: its hours of readings are garbage the collector frees again and again.</summary>
    [HostFact]
    public async Task The_collections_sample_prints_its_expected_output_in_a_small_heap()
    {
        var host = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("host-16K");
        var source = File.ReadAllText(Path.Combine(Samples.Folder, "Collections.cs"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(CollectionPrograms.Compile(source, "Collections", debug: false), host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(Samples.ExpectedOutput("Collections"), System.Text.Encoding.UTF8.GetBytes(result.Raw));
    }

    /// <summary>What .NET writes for an unhandled exception, without the lines of its stack trace ("   at ...").</summary>
    internal static string WithoutStackTrace(string errors) => string.Concat(errors.Split("\r\n")
        .Where(line => line.Length > 0 && !line.StartsWith("   at ", StringComparison.Ordinal))
        .Select(line => line + "\r\n"));
}
