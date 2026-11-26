using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// Interrupt handlers in the whole-program pass: the methods marked [Interrupt(...)] are read from their attribute,
/// become part of the program though nothing calls them, and must follow the rules, which the pass reports in C# words.
/// </summary>
public sealed class InterruptCheckTests
{
    private const string ShapeRule = "an [Interrupt] method must be static, return void and take no parameters. The " +
        "chip calls it with nothing to pass and nowhere to return a value to: share values through static volatile fields.";

    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    [Fact]
    public void The_attribute_and_its_event_are_read_from_the_method_and_other_attributes_are_left_alone()
    {
        var assembly = Load("""
            using System.Runtime.CompilerServices;
            using ChipSharp.Hardware;

            class Program
            {
                static void Main() { }

                [Interrupt(Event.Timer)]
                [MethodImpl(MethodImplOptions.NoInlining)]
                static void Tick() { }

                [Interrupt("pin")]
                static void Changed() { }

                [Interrupt(null)]
                static void Nothing() { }

                [MethodImpl(MethodImplOptions.NoInlining)]
                static void Other() { }
            }
            """);

        var tick = Assert.Single(assembly.FindMethods("Program.Tick"));
        var attribute = Assert.Single(assembly.FindAttributes(tick, Interrupts.AttributeType));
        Assert.Equal("[ChipSharp.Hardware]ChipSharp.Hardware.InterruptAttribute", attribute.Type.ToString());
        Assert.Equal(["timer"], attribute.Arguments);
        Assert.Empty(assembly.FindAttributes(Assert.Single(assembly.FindMethods("Program.Other")), Interrupts.AttributeType));
        Assert.Equal(
            [("Program.Tick", "timer"), ("Program.Changed", "pin"), ("Program.Nothing", null)],
            Interrupts.FindMarked(assembly).Select(marked => (marked.Method.DisplayName, marked.Event)));
    }

    [Fact]
    public void A_handler_is_part_of_the_program_though_nothing_calls_it_and_knows_the_targets_interrupt()
    {
        var source = """
            using ChipSharp.Hardware;

            Pin.Output(25);
            Timing.EveryMilliseconds(500);
            while (true)
            {
            }

            partial class Program
            {
                [Interrupt(Event.Timer)]
                static void Tick() => Blink.Toggle();

                static void Unused() { }
            }

            static class Blink
            {
                public static void Toggle() => Pin.Toggle(25);
            }
            """;

        var anywhere = WholeProgramPass.Check(Load(source));
        var pico = WholeProgramPass.Check(Load(source), Targets.Load("pico"));

        Assert.True(anywhere.IsSupported, anywhere.Details);
        Assert.Equal(["Program.<Main>$", "Program.Tick", "Blink.Toggle"], anywhere.Methods.Select(m => m.DisplayName));
        var handler = Assert.Single(anywhere.InterruptHandlers);
        Assert.Equal(("Program.Tick", "timer", null), (handler.Method.DisplayName, handler.Event, handler.Interrupt));

        Assert.True(pico.IsSupported, pico.Details);
        var wired = Assert.Single(pico.ToProgram().InterruptHandlers);
        Assert.Equal(new TargetInterrupt("timer", 1, "chipsharp_clear_timer_event"), wired.Interrupt);
        Assert.Equal("chipsharp_isr_irq1", wired.Interrupt!.Symbol);
    }

    [Fact]
    public void A_handler_must_be_static_return_void_and_take_no_parameters()
    {
        var problems = Problems("""
            using ChipSharp.Hardware;

            class Program
            {
                static void Main() { }

                [Interrupt(Event.Timer)]
                void Instance() { }

                [Interrupt(Event.Pin)]
                static int Result() => 1;

                [Interrupt(Event.Pin)]
                static void Parameter(int pin) { }
            }
            """);

        Assert.Equal(
            [
                $"Program.Instance(): {ShapeRule}",
                $"Program.Result(): {ShapeRule}",
                $"Program.Parameter(int): {ShapeRule}",
            ],
            problems);
    }

    [Fact]
    public void A_handler_names_an_event_of_the_target_or_without_one_of_ChipSharp_Hardware()
    {
        var source = """
            using ChipSharp.Hardware;

            class Program
            {
                static void Main() { }

                [Interrupt("tick")]
                static void Tick() { }

                [Interrupt(null)]
                static void Nothing() { }
            }
            """;

        Assert.Equal(
            [
                "Program.Tick(): [Interrupt(\"tick\")] names an event ChipSharp.Hardware does not have. The events are " +
                    "\"timer\" (Event.Timer) and \"pin\" (Event.Pin).",
                "Program.Nothing(): [Interrupt(null)] names an event ChipSharp.Hardware does not have. The events are " +
                    "\"timer\" (Event.Timer) and \"pin\" (Event.Pin).",
            ],
            Problems(source));
        Assert.Equal(
            "Program.Tick(): [Interrupt(\"tick\")] names an event target 'pico' does not have. Its events are " +
                "\"timer\" (Event.Timer) and \"pin\" (Event.Pin).",
            Problems(source, Targets.Load("pico"))[0]);
    }

    [Fact]
    public void On_the_PC_a_program_with_a_handler_is_refused_with_the_reason()
    {
        var analysis = WholeProgramPass.Check(Load(BlinkSource), Targets.Load("host"));

        Assert.Equal(
            """
            Test.dll uses 1 C# feature that M0 does not compile yet (1 place):
              [Interrupt] methods need interrupts, and target 'host' has none: 1 place in Program.Toggle(). Interrupts are a chip's, not a PC's: build the program for a chip, e.g. with -t pico.
            For every place, run: chipsharp check Test.dll --details
            """.ReplaceLineEndings("\n"),
            analysis.Summary);
    }

    [Fact]
    public void Each_event_has_one_handler()
    {
        var problems = Problems("""
            using ChipSharp.Hardware;

            class Program
            {
                static void Main() => Timing.AfterMilliseconds(5);

                [Interrupt(Event.Timer)]
                static void First() { }

                [Interrupt(Event.Timer)]
                static void Second() => First();
            }
            """);

        const string Rule = "the timer event has more than one [Interrupt] method. Keep one: it can call the others.";
        Assert.Equal([$"Program.First(): {Rule}", $"Program.Second(): {Rule}"], problems);
    }

    [Fact]
    public void Starting_an_event_that_no_method_handles_is_refused_where_it_starts()
    {
        var problems = Problems("""
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Pin.Input(15);
                    Pin.WatchFalling(15);
                    Timing.EveryMicroseconds(100);
                    Timing.Stop();
                    Pin.Unwatch(15);
                }
            }
            """);

        Assert.Equal(
            [
                "Program.Main() at IL_0009 (call): the program starts the pin event, which no [Interrupt(Event.Pin)] " +
                    "method handles. Add one, e.g. [Interrupt(Event.Pin)] static void OnPin() { ... }: without it the " +
                    "chip stops in the interrupt.",
                "Program.Main() at IL_0010 (call): the program starts the timer event, which no " +
                    "[Interrupt(Event.Timer)] method handles. Add one, e.g. [Interrupt(Event.Timer)] static void " +
                    "OnTimer() { ... }: without it the chip stops in the interrupt.",
            ],
            problems);
    }

    [Fact]
    public void A_handler_cannot_wait_nor_can_what_it_calls()
    {
        var problems = Problems("""
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Timing.DelayMilliseconds(1);
                    Pause();
                    Timing.AfterMilliseconds(1);
                }

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    Pin.Toggle(25);
                    Pause();
                }

                static void Pause() => Timing.DelayMicroseconds(10);
            }
            """);

        Assert.Equal(
            [
                "Program.Pause() at IL_0002 (call): an interrupt handler cannot wait (Timing.DelayMilliseconds, " +
                    "Timing.DelayMicroseconds). The interrupt that ends the wait cannot come while a handler runs, so " +
                    "it would wait forever: keep handlers short, and wait in Main, e.g. for a static volatile field the " +
                    "handler sets.",
            ],
            problems);
    }

    [Fact]
    public void A_static_field_a_handler_shares_with_the_rest_of_the_program_must_be_volatile()
    {
        var problems = Problems("""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static int ticks;
                static long total;
                static volatile int presses;
                static int onlyTheHandlers;
                static readonly int Step = 3;
                static int period = 100;

                static void Main()
                {
                    period = 250;
                    Timing.EveryMilliseconds(period);
                    while (ticks < 10)
                    {
                    }

                    Console.WriteLine(total + Step);
                    Console.WriteLine(presses);
                }

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    ticks++;
                    total += Step;
                    onlyTheHandlers++;
                }

                [Interrupt(Event.Pin)]
                static void Pressed()
                {
                    presses++;
                    onlyTheHandlers--;
                }
            }
            """);

        const string Ticks = "the static field Program.ticks is shared by an interrupt handler and the rest of the " +
            "program, and is not volatile. Declare it volatile (static volatile int ticks;): otherwise the program may " +
            "keep a copy of it, and never see the other side's change.";
        const string Total = "the static field Program.total is shared by an interrupt handler and the rest of the " +
            "program, and is not volatile. A long cannot be volatile in C#, and the chip reads and writes it in two " +
            "halves, so one side could see half of the other's change: share an int or a uint instead.";
        Assert.Equal(
            [
                $"Program.Main() at IL_0014 (ldsfld): {Ticks}",
                $"Program.Main() at IL_001d (ldsfld): {Total}",
                $"Program.Tick() at IL_0000 (ldsfld): {Ticks}",
                $"Program.Tick() at IL_0007 (stsfld): {Ticks}",
                $"Program.Tick() at IL_000c (ldsfld): {Total}",
                $"Program.Tick() at IL_0018 (stsfld): {Total}",
            ],
            problems);

        // Main writes period and the handler does not use it: a field only one side uses is not shared
        Assert.DoesNotContain(problems, line => line.Contains("period", StringComparison.Ordinal));
    }

    [Fact]
    public void A_static_field_shared_through_its_address_counts_and_a_struct_one_cannot_be_volatile()
    {
        // ref ticks and position.X take the field's address (ldsflda): the handler may write it through that
        var problems = Problems("""
            using System;
            using ChipSharp.Hardware;

            struct Point
            {
                public int X;
                public int Y;
            }

            class Program
            {
                static int ticks;
                static Point position;

                static void Bump(ref int value) => value++;

                static void Main()
                {
                    Timing.EveryMilliseconds(10);
                    Console.WriteLine(ticks + position.X);
                }

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    Bump(ref ticks);
                    position.X++;
                }
            }
            """);

        const string Ticks = "the static field Program.ticks is shared by an interrupt handler and the rest of the " +
            "program, and is not volatile. Declare it volatile (static volatile int ticks;): otherwise the program may " +
            "keep a copy of it, and never see the other side's change.";
        const string Position = "the static field Program.position is shared by an interrupt handler and the rest of " +
            "the program, and is not volatile. A struct cannot be volatile in C#, and the chip reads and writes it field " +
            "by field, so one side could see part of the other's change: share each value in a volatile int or uint " +
            "field.";
        Assert.Equal(
            [
                $"Program.Main() at IL_0007 (ldsfld): {Ticks}",
                $"Program.Main() at IL_000c (ldsflda): {Position}",
                $"Program.Tick() at IL_0000 (ldsflda): {Ticks}",
                $"Program.Tick() at IL_000a (ldsflda): {Position}",
            ],
            problems);
    }

    [Fact]
    public void A_field_Main_writes_and_a_handler_reads_must_be_volatile_too_but_not_one_only_initializers_write()
    {
        var problems = Problems("""
            using ChipSharp.Hardware;

            class Program
            {
                static int led = 25;
                static readonly int Button = 15;

                static void Main()
                {
                    Pin.Input(Button);
                    Pin.Watch(Button);
                    led = 24;
                }

                [Interrupt(Event.Pin)]
                static void Changed() => Pin.Toggle(led + Button - 15);
            }
            """);

        Assert.Equal(2, problems.Length);
        Assert.All(problems, line => Assert.Contains("the static field Program.led is shared", line));
    }

    [Fact]
    public void The_summary_lists_each_broken_rule_once_with_its_places()
    {
        var analysis = Check("""
            using ChipSharp.Hardware;

            class Program
            {
                static int count;

                static void Main()
                {
                    Timing.EveryMilliseconds(1);
                    while (count < 3)
                    {
                    }
                }

                [Interrupt(Event.Timer)]
                static void Tick() => count++;

                [Interrupt(Event.Pin)]
                static void Changed(int pin) { }
            }
            """);

        Assert.Equal(
            """
            Test.dll uses 2 C# features that M0 does not compile yet (4 places):
              an [Interrupt] method must be static, return void and take no parameters: 1 place in Program.Changed(int). The chip calls it with nothing to pass and nowhere to return a value to: share values through static volatile fields.
              the static field Program.count is shared by an interrupt handler and the rest of the program, and is not volatile: 3 places in Program.Main() (IL_0006) and Program.Tick() (IL_0000 and IL_0007). Declare it volatile (static volatile int count;): otherwise the program may keep a copy of it, and never see the other side's change.
            For every place, run: chipsharp check Test.dll --details
            """.ReplaceLineEndings("\n"),
            analysis.Summary);
    }

    [Fact]
    public void The_targets_interrupts_come_from_its_file_and_a_PC_has_none()
    {
        Assert.Equal(
            [new TargetInterrupt("timer", 1, "chipsharp_clear_timer_event"), new TargetInterrupt("pin", 13, "chipsharp_clear_pin_event")],
            Interrupts.ForTarget(Targets.Load("pico")).Values);
        Assert.Equal(["timer", "pin"], Interrupts.ForTarget(Targets.Load("pico-w")).Keys);
        Assert.Empty(Interrupts.ForTarget(Targets.Load("host")));
        Assert.Equal(Interrupts.LibraryEvents, [ChipSharp.Hardware.Event.Timer, ChipSharp.Hardware.Event.Pin]);
    }

    [Theory]
    [InlineData("""{ "timer": 1 }""", "Target 'test': 'interrupts.timer' must give the event's interrupt number, e.g. { \"irq\": 1 }.")]
    [InlineData("""{ "timer": { "irq": -1 } }""", "Target 'test': 'interrupts.timer' must give the event's interrupt number, e.g. { \"irq\": 1 }.")]
    [InlineData("""{ "timer": { "irq": 1, "clear": "clear it" } }""",
        "Target 'test': 'interrupts.timer.clear' must be the name of a C function of the runtime, e.g. \"chipsharp_clear_timer_event\".")]
    [InlineData("""{ "timer": { "irq": 1 }, "pin": { "irq": 1 } }""",
        "Target 'test': 'interrupts.timer' and 'interrupts.pin' both have interrupt 1; each event needs an interrupt of its own.")]
    [InlineData("""[ "timer" ]""", "Target 'test': 'interrupts' must be an object of events, e.g. { \"timer\": { \"irq\": 1 } }.")]
    public void A_table_that_is_not_well_formed_is_an_error_that_says_how_to_write_it(string table, string message)
    {
        var target = new TargetDescription
        {
            Name = "test",
            InheritanceChain = ["test"],
            Backend = "llvm",
            PointerSize = 4,
            Endian = Endianness.Little,
            Settings = new JsonObject { ["interrupts"] = JsonNode.Parse(table) },
        };

        var error = Assert.Throws<ChipSharpException>(() => Interrupts.ForTarget(target));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void A_handler_with_no_clear_function_is_put_in_its_interrupt_as_it_is()
    {
        var table = Interrupts.ForTarget(new TargetDescription
        {
            Name = "test",
            InheritanceChain = ["test"],
            Backend = "llvm",
            PointerSize = 4,
            Endian = Endianness.Little,
            Settings = new JsonObject { ["interrupts"] = JsonNode.Parse("""{ "uart": { "irq": 20 } }""") },
        });

        Assert.Equal(new TargetInterrupt("uart", 20, null), table["uart"]);
        Assert.Equal("chipsharp_isr_irq20", table["uart"].Symbol);
    }

    /// <summary>The timer blink: the handler is a method of the partial Program of the top-level statements.</summary>
    internal const string BlinkSource = """
        using ChipSharp.Hardware;

        Pin.Output(25);
        Timing.EveryMilliseconds(500);
        while (true)
        {
        }

        partial class Program
        {
            [Interrupt(Event.Timer)]
            static void Toggle() => Pin.Toggle(25);
        }
        """;

    /// <summary>Compiles <paramref name="source"/> against ChipSharp.Hardware; the assembly is Test.dll in messages.</summary>
    internal static IlAssembly Load(string source) =>
        IlAssembly.Load(CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly], "Test"), "Test.dll");

    private static AnalysisResult Check(string source, TargetDescription? target = null) =>
        WholeProgramPass.Check(Load(source), target);

    /// <summary>The problems one per place, as the details list them.</summary>
    private static string[] Problems(string source, TargetDescription? target = null)
    {
        var analysis = Check(source, target);
        Assert.False(analysis.IsSupported);
        return [.. analysis.Problems.Select(problem => problem.Line)];
    }
}
