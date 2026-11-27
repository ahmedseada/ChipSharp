using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Records;

/// <summary>
/// The programs of <see cref="RecordPrograms"/>, compiled by ChipSharp for the PC and run: each must print, byte for byte,
/// what the same C# prints on real .NET, compiled in Release and in Debug. Where .NET throws (a StringBuilder given an
/// index outside it, a GetType() of null), the program stops with .NET's message and exit code 1. The same programs run
/// on the simulated Pico too (<see cref="RecordSimulatorTests"/>).
/// </summary>
public sealed class RecordProgramTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => RecordPrograms.InReleaseAndDebug();

    [HostTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_what_dotnet_prints(string name, bool debug)
    {
        var source = RecordPrograms.Source(name);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(RecordPrograms.Compile(source, name, debug)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.Output, chipSharp.Output);
        Assert.Equal(0, chipSharp.ExitCode);
    }

    /// <summary>
    /// In a 16 KB heap the 6000 records, copies, builders and strings of the garbage program need many collections, while
    /// an array, a static field and a builder keep some: one the collector lost would change what it prints.
    /// </summary>
    [HostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Records_and_builders_kept_in_arrays_and_fields_live_through_many_collections(bool debug)
    {
        var host = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("host-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(RecordPrograms.Garbage, "Garbage"), "Garbage",
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(RecordPrograms.Compile(RecordPrograms.Garbage, "Garbage", debug), host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host);
        output.WriteLine($"PC, 16 KB heap: {result.Time.TotalMilliseconds:0} ms\n{result}");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    /// <summary>Each message is the one .NET 10 prints for the same statement (StringBuilderSubstituteTests checks every one).</summary>
    [HostTheory]
    [InlineData("new StringBuilder(-1)", "System.ArgumentOutOfRangeException: capacity ('-1') must be a non-negative value. " +
        "(Parameter 'capacity')\r\nActual value was -1.")]
    [InlineData("new StringBuilder(5, 3)", "System.ArgumentOutOfRangeException: Capacity exceeds maximum capacity. (Parameter 'capacity')")]
    [InlineData("new StringBuilder(0, 0)", "System.ArgumentOutOfRangeException: maxCapacity ('0') must be a non-negative and " +
        "non-zero value. (Parameter 'maxCapacity')\r\nActual value was 0.")]
    [InlineData("Text()[5]", "System.IndexOutOfRangeException: Index was outside the bounds of the array.")]
    [InlineData("Text().Remove(3, 9)", "System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative and " +
        "less than or equal to the size of the collection. (Parameter 'length')")]
    [InlineData("Text().Insert(-1, \"x\")", "System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative " +
        "and less than or equal to the size of the collection. (Parameter 'index')")]
    [InlineData("Text().Replace(\"\", \"x\")", "System.ArgumentException: The value cannot be an empty string. (Parameter 'oldValue')")]
    [InlineData("Text().Append((string)null, 0, 1)", "System.ArgumentNullException: Value cannot be null. (Parameter 'value')")]
    [InlineData("Text().ToString(9, 0)", "System.ArgumentOutOfRangeException: startIndex cannot be larger than length of " +
        "string. (Parameter 'startIndex')")]
    [InlineData("Copy(Text())", "System.ArgumentException: Either offset did not refer to a position in the string, or there " +
        "is an insufficient length of destination character array.")]
    [InlineData("new StringBuilder(2, 4).Append(\"abcde\")", "System.ArgumentOutOfRangeException: The length cannot be " +
        "greater than the capacity. (Parameter 'valueCount')")]
    [InlineData("Shorten(Text())", "System.ArgumentOutOfRangeException: value ('-1') must be a non-negative value. " +
        "(Parameter 'value')\r\nActual value was -1.")]
    [InlineData("Nothing().GetType()", "System.NullReferenceException: Object reference not set to an instance of an object.")]
    [InlineData("None().X", "System.NullReferenceException: Object reference not set to an instance of an object.")]
    public async Task What_dotnet_throws_for_stops_the_program_with_dotnets_message(string expression, string message)
    {
        var source = $$"""
            using System;
            using System.Text;

            record Point(int X, int Y);

            class Program
            {
                static StringBuilder Text() => new StringBuilder("hello");

                static object Nothing() => null;

                static Point None() => null;

                static string Copy(StringBuilder text)
                {
                    text.CopyTo(0, new char[3], 2, 2);
                    return "";
                }

                static int Shorten(StringBuilder text)
                {
                    text.Length = -1;
                    return 0;
                }

                static void Main()
                {
                    Console.WriteLine(1);
                    Console.WriteLine({{expression}});
                    Console.WriteLine(2);
                }
            }
            """;
        var module = Lowered.Lower(WholeProgramPass.Analyze(RecordPrograms.Compile(source, "Throws", debug: false)));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "throws"));

        Assert.Equal((1, "1\r\n", $"Unhandled exception. {message}\r\n"), (run.ExitCode, run.Text, run.Errors));
    }
}
