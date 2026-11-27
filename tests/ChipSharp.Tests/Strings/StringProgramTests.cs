using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Strings;

/// <summary>
/// The programs of <see cref="StringPrograms"/>, compiled by ChipSharp for the PC and run: each must print, byte for
/// byte, what the same C# prints on real .NET with a UTF-8 console, compiled in Release and in Debug. The same programs
/// run on the simulated Pico too (<see cref="StringSimulatorTests"/>).
/// </summary>
public sealed class StringProgramTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => StringPrograms.InReleaseAndDebug();

    [HostTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_what_dotnet_prints(string name, bool debug)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(StringPrograms.ForDotnet(StringPrograms.Source(name), name), name,
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(StringPrograms.Compile(StringPrograms.Source(name), name, debug)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.Output, chipSharp.Output);
        Assert.Equal(0, chipSharp.ExitCode);
    }

    /// <summary>
    /// In a 16 KB heap the program makes over a megabyte of strings: only hundreds of collections make room for them,
    /// and a string kept in a static field, an array or an object (or a literal, outside the heap) that one of them
    /// missed would change what it prints.
    /// </summary>
    [HostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Strings_kept_everywhere_live_through_hundreds_of_collections(bool debug)
    {
        var host = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("host-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(StringPrograms.ForDotnet(StringPrograms.Garbage, "Garbage"),
            "Garbage", Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(StringPrograms.Compile(StringPrograms.Garbage, "Garbage", debug),
            host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host);
        output.WriteLine($"PC, 16 KB heap: {result.Time.TotalMilliseconds:0} ms\n{result}");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    /// <summary>Where .NET throws, the program stops with .NET's message, as for an index outside an array.</summary>
    [HostTheory]
    [InlineData("\"abc\".Substring(4)", "System.ArgumentOutOfRangeException: startIndex cannot be larger than length of string. (Parameter 'startIndex')")]
    [InlineData("\"abc\".Substring(-1)", "System.ArgumentOutOfRangeException: startIndex ('-1') must be a non-negative value. (Parameter 'startIndex')\r\nActual value was -1.")]
    [InlineData("\"abc\".Substring(1, 3)", "System.ArgumentOutOfRangeException: Index and length must refer to a location within the string. (Parameter 'length')")]
    [InlineData("\"abc\"[3]", "System.IndexOutOfRangeException: Index was outside the bounds of the array.")]
    [InlineData("\"abc\".IndexOf('a', 4)", "System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than or equal to the size of the collection. (Parameter 'startIndex')")]
    [InlineData("\"abc\".Insert(4, \"x\")", "System.ArgumentOutOfRangeException: startIndex ('4') must be less than or equal to '3'. (Parameter 'startIndex')\r\nActual value was 4.")]
    [InlineData("\"abc\".Remove(1, 5)", "System.ArgumentOutOfRangeException: count ('5') must be less than or equal to '2'. (Parameter 'count')\r\nActual value was 5.")]
    [InlineData("\"abc\".Replace(\"\", \"x\")", "System.ArgumentException: The value cannot be an empty string. (Parameter 'oldValue')")]
    [InlineData("\"abc\".PadLeft(-2)", "System.ArgumentOutOfRangeException: totalWidth ('-2') must be a non-negative value. (Parameter 'totalWidth')\r\nActual value was -2.")]
    [InlineData("int.Parse(\"12a\")", "System.FormatException: The input string '12a' was not in a correct format.")]
    [InlineData("int.Parse(\"2147483648\")", "System.OverflowException: Value was either too large or too small for an Int32.")]
    [InlineData("byte.Parse(\"-1\")", "System.OverflowException: Value was either too large or too small for an unsigned byte.")]
    [InlineData("\"abc\".Contains((string)null)", "System.ArgumentNullException: Value cannot be null. (Parameter 'value')")]
    [InlineData("((string)null).Length", "System.NullReferenceException: Object reference not set to an instance of an object.")]
    [InlineData("\"abc\".AsSpan(2, 2).Length", "System.ArgumentOutOfRangeException: Specified argument was out of the range of valid values. (Parameter 'start')")]
    public async Task What_dotnet_throws_for_stops_the_program_with_dotnets_message(string expression, string message)
    {
        var source = $$"""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(1);
                    Console.WriteLine({{expression}});
                    Console.WriteLine(2);
                }
            }
            """;
        var module = Lowered.Lower(WholeProgramPass.Analyze(StringPrograms.Compile(source, "Throws", debug: false)));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "throws"));

        Assert.Equal((1, "1\r\n", $"Unhandled exception. {message}\r\n"), (run.ExitCode, run.Text, run.Errors));
    }
}
