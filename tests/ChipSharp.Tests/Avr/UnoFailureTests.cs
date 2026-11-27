using System.Globalization;
using System.Text;
using ChipSharp.Tests.Library;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// Where .NET throws, a program on the Arduino Uno says over USART0 what .NET prints first for an exception nothing
/// catches, and stops at BREAK: the failures of the runtime's C (an index, a division, a string's search or parse, Math),
/// whose messages stay in flash, and those of the library's substitutes (DateTime, TimeSpan), whose text is the
/// program's fixed text in flash, put together by the shared chipsharp_exceptions.c.
/// </summary>
public sealed class UnoFailureTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private static readonly int index = 3;
    private static readonly int zero = int.Parse("0", CultureInfo.InvariantCulture);

    /// <summary>Each failing statement, with what .NET does for it.</summary>
    private static readonly (string Statement, Func<object> Dotnet)[] Failures =
    [
        ("int[] a = { 1, 2, 3 }; Console.WriteLine(a[index]);", () => new[] { 1, 2, 3 }[index]),
        ("Console.WriteLine(100 / zero);", () => 100 / zero),
        ("Console.WriteLine(\"abc\"[index + 2]);", () => "abc"[index + 2]),
        ("Console.WriteLine(\"abc\".IndexOf('a', index + 6));", () => "abc".IndexOf('a', index + 6)),
        ("Console.WriteLine(int.Parse(\"x\" + \"1\"));", () => int.Parse("x1", CultureInfo.InvariantCulture)),
        ("Console.WriteLine(int.Parse(\"99999999999\"));", () => int.Parse("99999999999", CultureInfo.InvariantCulture)),
        ("Console.WriteLine(Math.Round(1.5, index + 13));", () => Math.Round(1.5, index + 13)),
        ("Console.WriteLine(Math.Clamp(1, index, 2));", () => Math.Clamp(1, index, 2)),
        ("Span<int> s = stackalloc int[3]; Console.WriteLine(s.Slice(index, 2).Length);",
            () => new int[3].AsSpan().Slice(index, 2).Length),
        .. DateTimePrograms.Failures.Take(4),
        .. TimeSpanPrograms.Failures.Take(2),
    ];

    public static TheoryData<int> FailureNumbers => [.. Enumerable.Range(0, Failures.Length)];

    [AvrSimulatorTheory]
    [MemberData(nameof(FailureNumbers))]
    public async Task Where_dotnet_throws_the_program_says_so_over_USART0_and_stops_at_BREAK(int number)
    {
        var (statement, dotnet) = Failures[number];
        var source = TimeSpanPrograms.Failing(statement).ReplaceLineEndings("\n").Replace("class Program\n{",
            "class Program\n{\n    static int index = 3;\n    static int zero;\n", StringComparison.Ordinal);
        var name = "Failure" + number.ToString(CultureInfo.InvariantCulture);
        var program = UnoBuild.Build(source, name, Path.Combine(_scratch, name));

        var run = await AvrSimulator.RunAsync(program.Hex, 10_000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains("breakpoint", run.Stopped);
        Assert.Equal("1\r\n" + TimeSpanPrograms.Unhandled(dotnet), Encoding.UTF8.GetString(run.Usart0));
    }
}
