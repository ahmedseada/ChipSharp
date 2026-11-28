using System.Globalization;
using System.Text;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Memory;
using ChipSharp.Tests.Strings;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Ch32v003;

/// <summary>
/// What RV32EC and the ilp32e ABI make different on the CH32V003, run on the simulated chip against .NET, beyond the
/// corpora of the backend conformance suite (<see cref="Ch32v003LanguagePrograms"/>), its console USART1: calls past the
/// six argument registers, structs passed and returned, the runtime's multiplication and division helpers and
/// compiler-rt's float in software, every edge case printed as .NET prints it. A division .NET stops with an exception
/// says .NET's message after what .NET printed before, through the runtime function of that exception, and stops at the
/// failure stop. The stack check of the riscv32 architecture works on riscv32e too: a recursion with no end, through
/// one function or two, is stopped by chipsharp_stack_overflow (the stack pointer put back at the top of the stack, then
/// "Stack overflow." and the failure stop) before it reaches the variables, also one that prints a double at every
/// level; recursions that end and a frame of 768 bytes run as on .NET.
/// </summary>
public sealed class Ch32v003LanguageTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    public static TheoryData<string> Programs => [nameof(Ch32v003LanguagePrograms.Calls),
        nameof(Ch32v003LanguagePrograms.Arithmetic), nameof(Ch32v003LanguagePrograms.Doubles),
        nameof(Ch32v003LanguagePrograms.Floats), nameof(Ch32v003LanguagePrograms.Finite)];

    public static TheoryData<string> Failures => [.. Ch32v003LanguagePrograms.Failures.Select(failure => failure.Name)];

    [Ch32v003Theory]
    [MemberData(nameof(Programs))]
    public async Task Each_program_prints_what_dotnet_prints(string name)
    {
        var source = name switch
        {
            nameof(Ch32v003LanguagePrograms.Calls) => Ch32v003LanguagePrograms.Calls,
            nameof(Ch32v003LanguagePrograms.Arithmetic) => Ch32v003LanguagePrograms.Arithmetic,
            nameof(Ch32v003LanguagePrograms.Doubles) => Ch32v003LanguagePrograms.Doubles,
            nameof(Ch32v003LanguagePrograms.Floats) => Ch32v003LanguagePrograms.Floats,
            _ => Ch32v003LanguagePrograms.Finite,
        };
        var dotnet = await Dotnet(source, name, failing: false);

        var run = Ch32v003Build.Run(Ch32v003Build.Build(source, name, Path.Combine(_scratch, name)), 60_000,
            "chipsharp_stack_overflow");

        Assert.True(run.Ended, run.ToString());
        Assert.Empty(run.Called);
        Assert.Equal(dotnet.Text, Encoding.UTF8.GetString(run.Output));
    }

    [Ch32v003Theory]
    [MemberData(nameof(Failures))]
    public async Task A_division_dotnet_stops_with_an_exception_says_so_after_what_dotnet_printed_and_stops(string name)
    {
        var (_, source, failure) = Ch32v003LanguagePrograms.Failures.Single(f => f.Name == name);
        var dotnet = await Dotnet(source, name, failing: true);

        var run = Ch32v003Build.Run(Ch32v003Build.Build(source, name, Path.Combine(_scratch, name)), 1000, failure,
            "chipsharp_halt_failure");

        Assert.Equal(3, run.Simulation.ExitCode);
        Assert.Contains("ebreak", run.Simulation.Stopped);
        Assert.Equal([failure, "chipsharp_halt_failure"], run.Called.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("not printed", dotnet.Text);
        Assert.Equal(dotnet.Text + dotnet.Errors.Split("\r\n")[0] + "\r\n", Encoding.UTF8.GetString(run.Output));
    }

    [Ch32v003Theory]
    [InlineData(nameof(StackOverflowTests.Endless))]
    [InlineData(nameof(Ch32v003LanguagePrograms.Mutual))]
    public void An_endless_recursion_says_Stack_overflow_and_stops_before_it_reaches_the_variables(string name)
    {
        var source = name == nameof(StackOverflowTests.Endless) ? StackOverflowTests.Endless : Ch32v003LanguagePrograms.Mutual;
        var program = Ch32v003Build.Build(source, name, Path.Combine(_scratch, name));

        var run = Ch32v003Build.Run(program, 1000, "chipsharp_stack_overflow", "ChipSharp_Runtime_StackOverflow");

        Assert.Equal(3, run.Simulation.ExitCode);
        Assert.Contains("ebreak", run.Simulation.Stopped);
        Assert.Equal(["ChipSharp_Runtime_StackOverflow", "chipsharp_stack_overflow"], run.Called.Order(StringComparer.Ordinal));
        Assert.Equal("start\r\nStack overflow.\r\n", Encoding.UTF8.GetString(run.Output));
    }

    /// <summary>
    /// The text of a double takes some 790 bytes of stack on RV32E: a recursion that prints one at every level is still
    /// stopped by the stack check with all its lines whole, the margin ("link.stackMargin") having room for the text
    /// below the last function that checked (with a quarter of 512 bytes it ran through the variables: the console's
    /// waiting surrogate became a U+FFFD among the numbers).
    /// </summary>
    [Simulators.Ch32v003Fact]
    public void A_recursion_that_prints_doubles_says_Stack_overflow_with_its_lines_whole()
    {
        var program = Ch32v003Build.Build(Ch32v003LanguagePrograms.Printing, "Printing", Path.Combine(_scratch, "printing"));

        var run = Ch32v003Build.Run(program, 5000, "chipsharp_stack_overflow");

        Assert.Equal(3, run.Simulation.ExitCode);
        Assert.Contains("chipsharp_stack_overflow", run.Called);
        var lines = Encoding.UTF8.GetString(run.Output).Split("\r\n");
        Assert.Equal("start", lines[0]);
        Assert.InRange(lines.Length, 10, 200);
        Assert.Equal(Enumerable.Range(1, lines.Length - 3).Select(n => (n * 0.5).ToString(CultureInfo.InvariantCulture)),
            lines[1..^2]);
        Assert.Equal(["Stack overflow.", ""], lines[^2..]);
    }

    private Task<ProgramRun> Dotnet(string source, string name, bool failing) =>
        HostRun.RunOnDotnetAsync(CSharpCompiler.Compile([source, StringPrograms.Utf8Console], name), name,
            Path.Combine(_scratch, "dotnet"), failing);
}
