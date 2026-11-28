using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Targets;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// What <c>chipsharp check</c> says of the limits of the SDCC chips (the PFS154 and the STM8S103 board) before SDCC
/// runs: no 64-bit double, <c>Pwm.Duty</c> (it takes one), <c>Console.ReadLine</c> and objects (no heap), a console the
/// PFS154 has not, a pin event on a pin other than PA0 and PB0.
/// </summary>
public sealed class SdccCheckTests
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private static string[] Lines(string source, string target) =>
    [
        .. WholeProgramPass.Check(InterruptCheckTests.Load(source), Targets.Load(target)).Groups.Select(group => group.Line),
    ];

    [Theory]
    [InlineData("pfs154-bare")]
    [InlineData("stm8s103-board")]
    public void A_double_is_refused_with_float_as_the_alternative(string target)
    {
        var lines = Lines("""
            class Program
            {
                static double scale = 1.5;

                static void Main()
                {
                    double half = scale / 2;
                    System.Math.Sqrt(half);
                }
            }
            """, target);

        Assert.All(lines, line => Assert.StartsWith("double (C#'s 64-bit floating point) is not available, and the " +
            $"target '{target}' compiles C whose double has 32 bits (\"double64\": false): ", line));
        Assert.All(lines, line => Assert.EndsWith("Use float and MathF instead; a program with double would compute " +
            "with less precision than .NET does.", line));
        Assert.Single(lines);
    }

    [Theory]
    [InlineData("pfs154-bare")]
    [InlineData("stm8s103-board")]
    public void Float_and_MathF_are_no_problem(string target)
    {
        Assert.Empty(Lines("""
            class Program
            {
                static void Main()
                {
                    float x = 1.5f * 2;
                    System.MathF.Sqrt(x);
                }
            }
            """, target));
    }

    [Theory]
    [InlineData("pfs154-bare", 14)]
    [InlineData("stm8s103-board", 28)]
    public void Pwm_Duty_says_to_use_PulseMicroseconds(string target, int pin)
    {
        var lines = Lines("""
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Pwm.Output(%PIN%, 1000);
                    Pwm.Duty(%PIN%, 0.5f);
                }
            }
            """.Replace("%PIN%", pin.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
            target);

        var line = Assert.Single(lines);
        Assert.StartsWith($"Pwm.Duty takes a double, and the target '{target}' has no 64-bit double (\"double64\": false): " +
            "1 place in Program.Main()", line);
        Assert.EndsWith("Give the time high instead: Pwm.PulseMicroseconds(pin, microseconds), from the start of each period.",
            line);
    }

    [Theory]
    [InlineData("pfs154-bare")]
    [InlineData("stm8s103-board")]
    public void Console_ReadLine_needs_a_heap(string target)
    {
        var lines = Lines("""
            using System;

            class Program
            {
                static void Main()
                {
                    var line = Console.ReadLine();
                }
            }
            """, target);

        Assert.Contains(lines, line => line.StartsWith(target == "pfs154-bare"
            ? "Console.Read and Console.ReadLine need a console that reads, and the target 'pfs154-bare' has none: "
            : "Console.ReadLine makes a string, which needs a heap, and the target 'stm8s103-board' has no heap: ",
            StringComparison.Ordinal));
    }

    [Fact]
    public void The_PFS154_has_no_console_and_every_Console_call_says_so()
    {
        var lines = Lines("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine("hello");
                    Console.Write(1);
                }
            }
            """, "pfs154-bare");

        var line = Assert.Single(lines);
        Assert.StartsWith("Console.Write and Console.WriteLine need a console, and the target 'pfs154-bare' has none: " +
            "2 places in Program.Main()", line);
        Assert.EndsWith("Show what the program does another way, e.g. on a pin with ChipSharp.Hardware.Pin.", line);
    }

    [Theory]
    [InlineData("pfs154-bare")]
    [InlineData("stm8s103-board")]
    public void Objects_need_a_heap_the_chips_have_not(string target)
    {
        var lines = Lines("""
            class Program
            {
                static int count;

                static void Main()
                {
                    count = new Counter().Count;
                }
            }

            class Counter
            {
                public int Count = 3;
            }
            """, target);

        Assert.Contains(lines, line => line.Contains($"and the target '{target}' (", StringComparison.Ordinal)
            && line.Contains("has no heap", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pin_event_on_the_PFS154_is_PA0_or_PB0_only()
    {
        const string Program = """
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Pin.WatchFalling(%PIN%);
                }

                [Interrupt(Event.Pin)]
                static void Pressed() { }
            }
            """;

        Assert.Empty(Lines(Program.Replace("%PIN%", "0", StringComparison.Ordinal), "pfs154-bare"));
        Assert.Empty(Lines(Program.Replace("%PIN%", "8", StringComparison.Ordinal), "pfs154-bare"));
        var line = Assert.Single(Lines(Program.Replace("%PIN%", "3", StringComparison.Ordinal), "pfs154-bare"));
        Assert.StartsWith("pin 3 (PA3) cannot be watched (the pin event) on the target 'pfs154-bare': 1 place in Program.Main()",
            line, StringComparison.Ordinal);
        Assert.EndsWith("Watched (the pin event) can be pins 0 (PA0) or 8 (PB0).", line, StringComparison.Ordinal);
        Assert.Empty(Lines(Program.Replace("%PIN%", "3", StringComparison.Ordinal), "stm8s103-board"));
    }
}
