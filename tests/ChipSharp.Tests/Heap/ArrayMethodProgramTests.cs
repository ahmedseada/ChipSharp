using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;
using ChipSharp.Targets;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Heap;

/// <summary>
/// The methods of System.Array (see ChipSharp.Frontend's ArrayMethods) on the PC: the programs of
/// <see cref="ArrayMethodPrograms"/>, compiled the whole way in Release and Debug, the whole-program pass for the
/// target included, print what .NET prints; where .NET throws, the program stops with exactly the message .NET writes
/// before its stack trace. The methods that make no array work on a target without a heap too.
/// </summary>
public sealed class ArrayMethodProgramTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
    private TargetLoader? _targets;

    private TargetLoader Targets => _targets ??= new TargetLoader(Build.TargetFiles.WithShipped(
        Path.Combine(_scratch, "targets"),
        ("host-16K", """{ "inherits": "host", "capabilities": { "heap": "16K" } }"""),
        ("pc-without-heap", """{ "inherits": "host", "capabilities": { "heap": false } }""")));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [HostFact]
    public Task Copy_ConstrainedCopy_CopyTo_and_Clear_of_every_element_type_print_what_dotnet_prints() =>
        PrintsWhatDotnetPrints(ArrayMethodPrograms.Copies, "host");

    [HostFact]
    public Task Fill_IndexOf_LastIndexOf_and_Reverse_of_every_element_type_print_what_dotnet_prints() =>
        PrintsWhatDotnetPrints(ArrayMethodPrograms.Searches, "host");

    [HostFact]
    public Task Sort_and_BinarySearch_order_numbers_bools_NaN_and_both_zeros_as_dotnet_does() =>
        PrintsWhatDotnetPrints(ArrayMethodPrograms.Sorting, "host");

    [HostFact]
    public Task The_smallest_and_largest_numbers_sort_and_compare_as_dotnet_says() =>
        PrintsWhatDotnetPrints(ArrayMethodPrograms.Extremes, "host");

    [HostFact]
    public Task Resize_and_Clone_keep_every_element_through_hundreds_of_collections_in_a_small_heap() =>
        PrintsWhatDotnetPrints(ArrayMethodPrograms.NewArrays(3000), "host-16K");

    [HostFact]
    public Task Each_element_type_has_its_own_empty_array_that_type_tests_read_like_any_arrays() =>
        PrintsWhatDotnetPrints(ArrayMethodPrograms.EmptyArrays, "host");

    [HostFact]
    public async Task The_methods_that_make_no_array_need_no_heap()
    {
        var target = Targets.Load("pc-without-heap");
        Assert.False(target.HasHeap);

        await PrintsWhatDotnetPrints(ArrayMethodPrograms.WithoutHeap, "pc-without-heap");
    }

    /// <summary>The name of each failure of <see cref="ArrayMethodPrograms.Failures"/>.</summary>
    public static TheoryData<string> FailureNames => new(ArrayMethodPrograms.Failures.Select(failure => failure.Name));

    [HostTheory]
    [MemberData(nameof(FailureNames))]
    public async Task Where_dotnet_throws_the_program_stops_with_the_message_dotnet_writes_and_exit_code_1(string name)
    {
        // each failure is a program of its own, and a test case of its own
        var wrong = await FailureAsync(name, ArrayMethodPrograms.Failures.Single(failure => failure.Name == name).Statement);

        Assert.True(wrong is null, wrong);
    }

    /// <summary>
    /// Runs the failure on .NET and as a ChipSharp program (Release and Debug IL): the same "1" before it, and the same
    /// standard error up to .NET's stack trace. Null when they agree, else what differs.
    /// </summary>
    private async Task<string?> FailureAsync(string name, string statement)
    {
        var source = ArrayMethodPrograms.Failure(statement);
        var folder = Path.Combine(_scratch, name.Replace(' ', '-'));
        var dotnet = await ThrowingDotnet.RunAsync(CSharpCompiler.Compile(source, "Program"), Path.Combine(folder, "dotnet"));
        var expected = string.Concat(dotnet.Errors.Split('\n').TakeWhile(line => !line.StartsWith("   at ", StringComparison.Ordinal))
            .Select(line => line + "\n"));
        if (dotnet.Text != "1\r\n" || !expected.StartsWith("Unhandled exception. ", StringComparison.Ordinal))
        {
            return $"{name}: .NET did not throw as expected:\n{dotnet}";
        }

        foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
        {
            var module = Module(source, optimization, Targets.Load("host"));
            var run = await HostRun.BuildAndRunAsync(module, Path.Combine(folder, optimization.ToString()));
            if (run.Text != "1\r\n" || run.ExitCode != 1 || run.Errors != expected)
            {
                return $"{name} ({optimization}): expected\n{expected}but got exit code {run.ExitCode}\n{run.Text}{run.Errors}";
            }
        }

        return null;
    }

    /// <summary>
    /// Compiles <paramref name="source"/> (Release and Debug), runs it on .NET and as ChipSharp programs for the target
    /// <paramref name="target"/>, and compares.
    /// </summary>
    private async Task PrintsWhatDotnetPrints(string source, string target)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Program"), "Program",
            Path.Combine(_scratch, "dotnet"));
        Assert.NotEmpty(dotnet.Output);

        foreach (var optimization in new[] { OptimizationLevel.Release, OptimizationLevel.Debug })
        {
            var module = Module(source, optimization, Targets.Load(target));

            var run = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, optimization.ToString()), Targets.Load(target));
            output.WriteLine($"{optimization}, {target}: {run.Time.TotalMilliseconds:0} ms");

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Equal(dotnet.Text, run.Raw);
        }
    }

    /// <summary>The program in <paramref name="source"/>, checked for <paramref name="target"/> and translated.</summary>
    private static TirModule Module(string source, OptimizationLevel optimization, TargetDescription target)
    {
        var assembly = CSharpCompiler.Compile(source, "Program", OutputKind.ConsoleApplication, optimization);
        return Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Program.dll"), target));
    }
}

/// <summary>
/// .NET running a program that throws (<see cref="HostRun.RunOnDotnetAsync(byte[], string, string)"/> wants one that
/// does not): its output and its standard error, with the exception's message and stack trace.
/// </summary>
internal static class ThrowingDotnet
{
    public static Task<ProgramRun> RunAsync(byte[] assembly, string folder) =>
        HostRun.RunOnDotnetAsync(assembly, "Program", folder, failing: true);
}
