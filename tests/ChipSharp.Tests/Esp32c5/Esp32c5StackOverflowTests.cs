using System.Text;
using ChipSharp.Simulators;
using ChipSharp.Tests.Memory;
using ChipSharp.Tests.Simulators;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>
/// The stack check of the riscv32 architecture ("stackCheck") on the ESP32-C5: a recursion that never ends, directly or
/// through a virtual call, a delegate and an interface, prints "Stack overflow." on the USB console and fails; one that
/// ends, with a big frame on the stack, prints what .NET prints.
/// </summary>
public sealed class Esp32c5StackOverflowTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Esp32c5Theory]
    [InlineData(nameof(StackOverflowTests.Endless))]
    [InlineData(nameof(StackOverflowTests.Indirect))]
    public void An_endless_recursion_says_Stack_overflow_on_the_console_and_fails(string which)
    {
        var source = which == nameof(StackOverflowTests.Endless) ? StackOverflowTests.Endless : StackOverflowTests.Indirect;
        var program = Esp32c5Build.Build(source, which, _scratch);

        var run = Simulation.Run(new SimulationOptions { Chip = "esp32c5", Image = program.Elf, Milliseconds = 2000 });

        Assert.Equal(3, run.ExitCode);
        Assert.Equal("start\r\nStack overflow.\r\n", Encoding.ASCII.GetString(run.SerialOf("USB")));
    }

    [Esp32c5Fact]
    public async Task Recursions_that_end_and_a_big_frame_run_as_on_dotnet()
    {
        var dotnet = await ChipSharp.Tests.Lowering.HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(StackOverflowTests.Finite,
            "Finite"), "Finite", Path.Combine(_scratch, "dotnet"));
        var program = Esp32c5Build.Build(StackOverflowTests.Finite, "Finite", Path.Combine(_scratch, "finite"));

        var run = Simulation.Run(new SimulationOptions { Chip = "esp32c5", Image = program.Elf, Milliseconds = 2000 });

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), Encoding.ASCII.GetString(run.SerialOf("USB")));
    }
}
