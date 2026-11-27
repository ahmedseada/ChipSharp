using ChipSharp.Frontend.Analysis;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Boxing;

/// <summary>
/// The programs of <see cref="BoxingPrograms"/>, compiled by ChipSharp for the PC and run: each must print, byte for byte,
/// what the same C# prints on real .NET, compiled in Release and in Debug. Where .NET throws (an unboxing to another type,
/// null, a nullable's Value), the program stops with .NET's message and exit code 1. The same programs run on the
/// simulated Pico too (<see cref="BoxingSimulatorTests"/>).
/// </summary>
public sealed class BoxingProgramTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => BoxingPrograms.InReleaseAndDebug();

    [HostTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_what_dotnet_prints(string name, bool debug)
    {
        var source = BoxingPrograms.Source(name);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(BoxingPrograms.Compile(source, name, debug)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.Output, chipSharp.Output);
        Assert.Equal(0, chipSharp.ExitCode);
    }

    /// <summary>
    /// In a 16 KB heap the 20000 boxes of the garbage program (some 400 KB on the PC) need dozens of collections, while an
    /// array, a static field and a field of an object keep some: a box one of them lost would change what it prints.
    /// </summary>
    [HostTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Boxes_kept_in_arrays_and_fields_live_through_dozens_of_collections(bool debug)
    {
        var host = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K").Load("host-16K");
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(BoxingPrograms.Garbage, "Garbage"), "Garbage",
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(BoxingPrograms.Compile(BoxingPrograms.Garbage, "Garbage", debug), host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host);
        output.WriteLine($"PC, 16 KB heap: {result.Time.TotalMilliseconds:0} ms\n{result}");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    /// <summary>Each message is the one .NET 10 prints for the same statement.</summary>
    [HostTheory]
    [InlineData("(int)(object)5u", "System.InvalidCastException: Unable to cast object of type 'System.UInt32' to type 'System.Int32'.")]
    [InlineData("(long)(object)5", "System.InvalidCastException: Unable to cast object of type 'System.Int32' to type 'System.Int64'.")]
    [InlineData("(char)(object)(ushort)65", "System.InvalidCastException: Unable to cast object of type 'System.UInt16' to type 'System.Char'.")]
    [InlineData("(bool)(object)(byte)1", "System.InvalidCastException: Unable to cast object of type 'System.Byte' to type 'System.Boolean'.")]
    [InlineData("(int)Nothing()", "System.NullReferenceException: Object reference not set to an instance of an object.")]
    [InlineData("(Point)(object)\"text\"", "System.InvalidCastException: Unable to cast object of type 'System.String' to type 'Point'.")]
    [InlineData("(Point)(object)Mode.On", "System.InvalidCastException: Unable to cast object of type 'Mode' to type 'Point'.")]
    [InlineData("(Mode)(object)1L", "System.InvalidCastException: Unable to cast object of type 'System.Int64' to type 'Mode'.")]
    [InlineData("(int?)(object)Mode.On", "System.InvalidCastException: Unable to cast object of type 'Mode' to type 'System.Nullable`1[System.Int32]'.")]
    [InlineData("(Mode?)(object)\"on\"", "System.InvalidCastException: Unable to cast object of type 'System.String' to type 'System.Nullable`1[Program+Mode]'.")]
    [InlineData("((int?)null).Value", "System.InvalidOperationException: Nullable object must have a value.")]
    [InlineData("(int)Missing()", "System.InvalidOperationException: Nullable object must have a value.")]
    [InlineData("((IComparable)5).CompareTo(5L)", "System.ArgumentException: Object must be of type Int32.")]
    [InlineData("((IComparable)2.5).CompareTo(\"x\")", "System.ArgumentException: Object must be of type Double.")]
    [InlineData("((IComparable)Mode.On).CompareTo(Other.Yes)", "System.ArgumentException: Object must be the same type as the enum. The type passed in was 'Program+Other'; the enum type was 'Program+Mode'.")]
    public async Task What_dotnet_throws_for_stops_the_program_with_dotnets_message(string expression, string message)
    {
        var source = $$"""
            using System;

            struct Point
            {
                public int X;
            }

            class Program
            {
                enum Mode { Off, On }

                enum Other { No, Yes }

                static object Nothing() => null;

                static int? Missing() => null;

                static void Main()
                {
                    Console.WriteLine(1);
                    Console.WriteLine({{expression}});
                    Console.WriteLine(2);
                }
            }
            """;
        var module = Lowered.Lower(WholeProgramPass.Analyze(BoxingPrograms.Compile(source, "Throws", debug: false)));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "throws"));

        Assert.Equal((1, "1\r\n", $"Unhandled exception. {message}\r\n"), (run.ExitCode, run.Text, run.Errors));
    }
}
