using ChipSharp.Abstractions;
using ChipSharp.Runtime.Esp32c5;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>
/// What <c>chipsharp check</c> and the build refuse on <c>esp32c5-devkitc</c>, before the program runs: a pin the board
/// keeps, an analog input outside GPIO1-GPIO6, a clock the chip's timers or buses do not make, a CPU clock the runtime
/// does not run, and a program that does not fit in the SRAM. A <c>Pull.Down</c> is the chip's own and is allowed.
/// </summary>
public sealed class Esp32c5CheckTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    private static string[] Problems(string source)
    {
        var assembly = CSharpCompiler.Compile([source], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], "Refused");
        var check = Esp32c5Build.Check(assembly, "Refused", Esp32c5Build.Board);
        Assert.False(check.IsSupported);
        return [.. check.Groups.SelectMany(group => group.Problems.Select(problem => problem.Line))];
    }

    [Fact]
    public void A_pull_down_and_the_pins_the_board_leaves_are_allowed()
    {
        var assembly = CSharpCompiler.Compile(["""
            using ChipSharp.Hardware;

            Pin.Input(4, Pull.Down);
            Pin.Input(5, Pull.Up);
            Pin.Output(27);
            Adc.Input(6);
            Pwm.Output(7, 1_000_000);
            Pwm.Output(8, 1);
            I2c.Open(0, 4, 5, 1_000_000);
            Spi.Open(0, 0, 1, 24, 26_000_000, 0);
            """], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], "Allowed");

        Assert.True(Esp32c5Build.Check(assembly, "Allowed", Esp32c5Build.Board).IsSupported);
    }

    [Fact]
    public void A_pin_outside_the_boards_list_and_an_analog_input_outside_GPIO1_to_GPIO6_are_refused()
    {
        var problems = string.Join("\n", Problems("""
            using ChipSharp.Hardware;

            Pin.Output(13);
            Pin.Input(29, Pull.Down);
            Pin.Watch(16);
            Adc.Input(7);
            Adc.Input(0);
            Pwm.Output(14, 50);
            """));

        Assert.Contains("13 cannot be a pin a program uses", problems);
        Assert.Contains("29 cannot be a pin a program uses", problems);
        Assert.Contains("16 cannot be watched", problems);
        Assert.Contains("GPIO 7 cannot be an analog input", problems);
        Assert.Contains("GPIO 0 cannot be an analog input", problems);
        Assert.Contains("14 cannot be a PWM output", problems);
        Assert.DoesNotContain("needs a pull-down", problems);
    }

    [Fact]
    public void A_PWM_I2C_or_SPI_frequency_the_chip_does_not_make_is_refused_with_the_range_it_makes()
    {
        var problems = string.Join("\n", Problems("""
            using ChipSharp.Hardware;

            Pwm.Output(4, 0);
            Pwm.Output(5, 1_000_001);
            I2c.Open(0, 4, 5, 100);
            I2c.Open(0, 4, 5, 1_000_001);
            Spi.Open(0, 0, 1, 24, 30_000_000, 0);
            Spi.Open(0, 0, 1, 24, 1000, 0);
            """));

        Assert.Contains("a PWM frequency of 0 Hz (Pwm.Output's frequency) is not one the target 'esp32c5-devkitc' makes", problems);
        Assert.Contains("a PWM frequency of 1000001 Hz", problems);
        Assert.Contains("It makes 1 to 1000000 Hz.", problems);
        Assert.Contains("an I2C clock of 100 Hz", problems);
        Assert.Contains("an I2C clock of 1000001 Hz", problems);
        Assert.Contains("It makes 200 to 1000000 Hz.", problems);
        Assert.Contains("an SPI clock of 30000000 Hz", problems);
        Assert.Contains("an SPI clock of 1000 Hz", problems);
        Assert.Contains("It makes 46875 to 26000000 Hz.", problems);
    }

    [Theory]
    [InlineData("48000000", "0")]
    [InlineData("160000000", "1")]
    public void The_CPU_clock_is_the_crystals_48_MHz_or_the_PLLs_160_MHz(string clockHz, string onPll)
    {
        var file = Esp32c5Runtime.ClockHeader(WithClock(clockHz)).Content;

        Assert.Contains($"#define C5_CPU_ON_PLL {onPll}", file);
    }

    [Theory]
    [InlineData("240000000")]
    [InlineData("80000000")]
    [InlineData("100")]
    public void Any_other_clockHz_stops_the_build_saying_what_the_runtime_runs(string clockHz)
    {
        var error = Assert.Throws<ChipSharpException>(() => Esp32c5Runtime.ClockHeader(WithClock(clockHz)));

        Assert.Contains($"'clockHz' is \"{clockHz}\"", error.Message);
        Assert.Contains("48000000 (the crystal, the ROM's) or 160000000", error.Message);
    }

    [Fact]
    public void A_program_larger_than_the_SRAM_stops_the_build_saying_how_much_it_needs()
    {
        var error = Assert.Throws<ChipSharpException>(() => Esp32c5Build.Build("""
            static class Program
            {
                static readonly int[] Table = new int[110_000];
                static volatile int sink;

                static void Main()
                {
                    Table[sink] = 1;
                    sink = Table[sink + 1];
                }
            }
            """, "Big", _scratch));

        Assert.Contains("Big does not fit in target 'esp32c5-devkitc'", error.Message);
        Assert.Contains("its code, constants and variables", error.Message);
        Assert.Contains("and the stack (16 KB, link.stack) need ", error.Message);
        Assert.Contains("KB of SRAM, ", error.Message);
        Assert.Contains("more than the target's", error.Message);
    }

    /// <summary>The board with its "clockHz" set to <paramref name="clockHz"/>, from a copy of the target files.</summary>
    private TargetDescription WithClock(string clockHz)
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "targets");
        var targets = Path.Combine(_scratch, "targets");
        foreach (var file in Directory.GetFiles(shipped, "*.json", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(targets, Path.GetRelativePath(shipped, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.WriteAllText(copy, File.ReadAllText(file).Replace("\"clockHz\": 48000000", $"\"clockHz\": {clockHz}", StringComparison.Ordinal));
        }

        return new TargetLoader(targets).Load("esp32c5-devkitc");
    }
}
