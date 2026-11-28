using ChipSharp.Frontend.Analysis;
using ChipSharp.Targets;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// ChipSharp.Hardware's <c>Pin.Input(pin, Pull)</c> in the whole-program pass: every chip has a pull-up, but only a chip
/// whose target file says <c>"pullDown": true</c> has a pull-down, and <c>chipsharp check</c> refuses a constant
/// <c>Pull.Down</c> for the others.
/// </summary>
public sealed class PullCheckTests
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private const string PullUpAndNone = """
        using ChipSharp.Hardware;

        class Program
        {
            static void Main()
            {
                Pin.Input(3);
                Pin.Input(4, Pull.None);
                Pin.Input(5, Pull.Up);
            }
        }
        """;

    private const string PullDown = """
        using ChipSharp.Hardware;

        class Program
        {
            static void Main()
            {
                Pin.Input(5, Pull.Down);
            }
        }
        """;

    [Theory]
    [InlineData("pico", true)]
    [InlineData("esp32c5-devkitc", true)]
    [InlineData("uno", false)]
    [InlineData("pfs154-bare", false)]
    [InlineData("stm8s103-board", false)]
    public void Only_a_chip_with_a_pull_down_lets_a_program_ask_for_one(string target, bool pullDown)
    {
        var description = Targets.Load(target);
        Assert.Equal(pullDown, description.HasCapability("pullDown"));

        Assert.True(WholeProgramPass.Check(InterruptCheckTests.Load(PullUpAndNone), description).IsSupported);

        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load(PullDown), description);
        Assert.Equal(pullDown, analysis.IsSupported);
        if (!pullDown)
        {
            var lines = analysis.Groups.Select(group => group.Line).ToList();
            Assert.Contains($"Pin.Input(pin, Pull.Down) needs a pull-down, and the target '{target}' has none (\"pullDown\": false)",
                lines);
            Assert.Contains("Use Pull.Up with a button to ground (every chip has a pull-up), or wire a resistor from the pin to ground.",
                lines);
        }
    }

    [Fact]
    public void A_pull_that_is_not_a_constant_is_the_runtimes_to_check_and_a_program_without_a_target_may_use_any()
    {
        const string variable = """
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    var pull = System.Environment.TickCount > 0 ? Pull.Up : Pull.Down;
                    Pin.Input(5, pull);
                }
            }
            """;

        Assert.True(WholeProgramPass.Check(InterruptCheckTests.Load(variable), Targets.Load("uno")).IsSupported);
        Assert.True(WholeProgramPass.Check(InterruptCheckTests.Load(PullDown)).IsSupported);
    }
}
