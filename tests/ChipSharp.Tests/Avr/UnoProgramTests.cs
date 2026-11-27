using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The samples and the test corpora (<see cref="UnoPrograms"/>) on the Arduino Uno: each program that makes no object is
/// built for the Uno and run in the avr8js simulator, and prints over USART0 exactly what .NET prints; each one that
/// makes objects is refused, as the Uno has no heap, with the whole-program pass saying so place by place; one too large
/// for its 32 KB of flash is refused by the build, with the memory report's numbers.
/// </summary>
public sealed class UnoProgramTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> ProgramNames => [.. UnoPrograms.All.Select(program => program.Name)];

    [AvrSimulatorTheory]
    [MemberData(nameof(ProgramNames))]
    public async Task Each_program_prints_over_USART0_what_dotnet_prints_or_is_refused_for_its_objects_or_size(string name)
    {
        var file = name.Replace('/', '_');
        var assembly = CSharpCompiler.Compile(UnoPrograms.Source(name), file);
        var check = UnoBuild.Check(assembly, file);
        if (!check.IsSupported)
        {
            Assert.All(check.Groups, group => Assert.Matches("needs? a heap, and the target 'uno-console' " +
                @"\(2 KB RAM\) has no heap", group.Line));
            Assert.DoesNotContain(name, UnoPrograms.TooLarge);
            return;
        }

        if (UnoPrograms.TooLarge.Contains(name))
        {
            var error = Assert.Throws<ChipSharpException>(() => UnoBuild.Build(assembly, file, Path.Combine(_scratch, "uno")));
            Assert.Matches(@"^\S+ does not fit in target 'uno-console': its code, constants and the starting values " +
                @"of its variables need [0-9.]+ KB of flash, [0-9.]+ KB more than the target's 31\.5 KB|" +
                @"takes \d+ bytes, more than the 32767 that 16-bit addresses reach on the target", error.Message);
            return;
        }

        var dotnet = await HostRun.RunOnDotnetAsync(assembly, file, Path.Combine(_scratch, "dotnet"));
        var program = UnoBuild.Build(assembly, file, Path.Combine(_scratch, "uno"));

        var run = await AvrSimulator.RunAsync(program.Hex, 120_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("the program has finished", run.Stopped);
        if (UnoPrograms.Differences.ContainsKey(name))
        {
            Assert.Equal(dotnet.Text.Split('\n').Length, Encoding.UTF8.GetString(run.Usart0).Split('\n').Length);
            Assert.NotEqual(dotnet.Output, run.Usart0);
            return;
        }

        Assert.Equal(dotnet.Text, Encoding.UTF8.GetString(run.Usart0));
        Assert.Equal(dotnet.Output, run.Usart0);
    }

    /// <summary>
    /// Text that is not ASCII, fixed (a literal of 2 bytes per char in flash) and printed: UTF-8, a lone surrogate as
    /// U+FFFD (.NET writes the console's code page when its output is not a console, so the expected bytes are written
    /// out here, as the PC's and the Pico's tests have them).
    /// </summary>
    [AvrSimulatorFact]
    public async Task Text_that_is_not_ASCII_goes_over_USART0_as_UTF8()
    {
        foreach (var (name, source, output) in new[]
        {
            ("NotAscii", TextPrograms.NotAscii, TextPrograms.NotAsciiOutput),
            ("PrintedNotAscii", PrintedTextPrograms.NotAscii, PrintedTextPrograms.NotAsciiOutput),
        })
        {
            var program = UnoBuild.Build(source, name, Path.Combine(_scratch, name));

            var run = await AvrSimulator.RunAsync(program.Hex, 10_000);

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Equal(Encoding.UTF8.GetBytes(output), run.Usart0);
        }
    }
}
