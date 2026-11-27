using ChipSharp.Abstractions;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// Serial ports, analog inputs and PWM outputs in the whole-program pass: ChipSharp.Hardware's Uart, Adc and Pwm and
/// Console.Read are C# of ChipSharp's library that calls the runtime; a target without them refuses them, and a pin or
/// port given as a constant that the target does not have for the function is found before the program runs.
/// </summary>
public sealed class SerialAnalogCheckTests : IDisposable
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    /// <summary>Every method of the three classes, and Console's input, with the pins of the Pico's examples.</summary>
    private const string EveryCall = """
        using System;
        using ChipSharp.Hardware;

        class Program
        {
            static void Main()
            {
                Uart.Open(1, 4, 5, 9600);
                Uart.Write(1, "hi"u8);
                Uart.WriteByte(1, 33);
                Span<byte> buffer = stackalloc byte[8];
                var count = Uart.Read(1, buffer) + Uart.ReadByte(1) + Uart.Available(1);
                Uart.Watch(1);
                Uart.Unwatch(1);
                Adc.Input(26);
                var reading = Adc.Read(26) + count;
                Pwm.Output(15, 50);
                Pwm.Duty(15, Adc.Volts(26) / 3.3);
                Pwm.PulseMicroseconds(15, 1500);
                Pwm.Stop(15);
                if (Adc.Temperature() > reading + Console.Read())
                {
                    Console.WriteLine(Console.ReadLine());
                }
            }

            [Interrupt(Event.Uart)]
            static void Received() { }
        }
        """;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void Every_call_becomes_the_librarys_CSharp_and_the_runtime_functions_it_calls()
    {
        var module = IlToTir.Lower(WholeProgramPass.Analyze(InterruptCheckTests.Load(EveryCall), Targets.Load("pico")));

        Assert.Equal(
            [
                "ChipSharp.Runtime.AdcInput", "ChipSharp.Runtime.AdcRead", "ChipSharp.Runtime.AdcReference",
                "ChipSharp.Runtime.AdcTemperature", "ChipSharp.Runtime.ConsoleRead", "ChipSharp.Runtime.PwmDuty",
                "ChipSharp.Runtime.PwmOutput", "ChipSharp.Runtime.PwmPulse", "ChipSharp.Runtime.PwmStop",
                "ChipSharp.Runtime.UartAvailable", "ChipSharp.Runtime.UartOpen", "ChipSharp.Runtime.UartReadByte",
                "ChipSharp.Runtime.UartUnwatch", "ChipSharp.Runtime.UartWatch", "ChipSharp.Runtime.UartWriteByte",
            ],
            module.Functions.Where(f => f.Kind == TirFunctionKind.External && IsSerialAnalog(f.Name))
                .Select(f => f.Name).Order(StringComparer.Ordinal));
        var external = module.Functions.Where(f => f.Kind == TirFunctionKind.External).ToDictionary(f => f.Name);
        Assert.Equal(TirType.F64, external["ChipSharp.Runtime.AdcReference"].ReturnType);
        Assert.Equal([TirType.I32, TirType.F64], external["ChipSharp.Runtime.PwmDuty"].Parameters.Select(p => p.Type));
        Assert.Equal(TirType.I32, external["ChipSharp.Runtime.ConsoleRead"].ReturnType);
        Assert.Contains(module.Functions, f => f.Name == "ChipSharp.Library.Uart.Write" && f.Kind == TirFunctionKind.Defined);
        Assert.Contains(module.Functions, f => f.Name == "ChipSharp.Library.Console.ReadLine");
        // the uart event's handler goes where IRQ 26 is (rp2040.json)
        Assert.Contains(module.Functions, f => f.ExportName == "chipsharp_isr_irq26");
    }

    [Fact]
    public void A_heap_less_target_uses_them_heap_free()
    {
        var source = EveryCall.Replace("Console.WriteLine(Console.ReadLine());", "Console.WriteLine(Console.Read());",
            StringComparison.Ordinal);

        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load(source), NoHeap);

        Assert.True(analysis.IsSupported, analysis.Summary);
        Assert.False(IlToTir.Lower(analysis.ToProgram()).UsesHeap);
    }

    [Fact]
    public void Console_ReadLine_makes_a_string_and_a_heap_less_target_says_so_with_what_to_write()
    {
        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load(EveryCall), NoHeap);

        Assert.Contains(analysis.Groups, group => group.Line.StartsWith(
            "Console.ReadLine makes a string, which needs a heap, and the target 'pico-no-heap' has no heap: 1 place in " +
            "Program.Main() (IL_", StringComparison.Ordinal) && group.Line.EndsWith(
            "Read the line char by char with Console.Read, into a span of a constant size (stackalloc char[64]).",
            StringComparison.Ordinal));
    }

    [Fact]
    public void A_target_without_them_refuses_each_class_and_says_why()
    {
        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load(EveryCall.Replace(
            "[Interrupt(Event.Uart)]", "", StringComparison.Ordinal)), Targets.Load("host"));

        var lines = analysis.Groups.Select(group => group.Line).ToList();
        const string ForAChip = "Serial ports, analog inputs and PWM outputs are a chip's: build the program for one " +
            "that has them, e.g. with -t pico.";
        Assert.Contains(lines, line => line.StartsWith(
            "ChipSharp.Hardware.Uart needs a serial port (UART), and the target 'host' has none: 8 places in Program.Main()",
            StringComparison.Ordinal) && line.EndsWith(ForAChip, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith(
            "ChipSharp.Hardware.Adc needs analog inputs (an ADC), and the target 'host' has none: 4 places",
            StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith(
            "ChipSharp.Hardware.Pwm needs PWM outputs, and the target 'host' has none: 4 places", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith(
            "Console.Read and Console.ReadLine need a console that reads, and the target 'host' has none: 2 places",
            StringComparison.Ordinal) && line.EndsWith(
            "On a chip the console reads a serial port (on the Pico, UART0's RX: GPIO 1, at 115200 baud): build the " +
            "program for one, e.g. with -t pico.", StringComparison.Ordinal));
        Assert.Equal("host", analysis.DetailsTarget);
    }

    [Fact]
    public void A_pin_or_port_given_as_a_constant_that_the_target_does_not_have_is_found_before_the_program_runs()
    {
        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load("""
            using ChipSharp.Hardware;

            class Program
            {
                const int Knob = 3;

                static void Main()
                {
                    Uart.Open(1, 6, 5, 9600);
                    Uart.Open(0, 0, 2, 115200);
                    Uart.Open(2, 4, 5, 9600);
                    Uart.WriteByte(-1, 0);
                    Adc.Input(Knob);
                    Adc.Read(25);
                    Pwm.Output(30, 1000);
                    Pwm.Duty(-1, 0.5);
                }
            }
            """), Targets.Load("pico"));

        Assert.Equal(
            [
                "Program.Main() at IL_0008 (call): GPIO 6 cannot be UART1's TX on the target 'pico'. UART1's TX can be " +
                    "GPIO 4, 8, 20 or 24.",
                "Program.Main() at IL_0015 (call): GPIO 2 cannot be UART0's RX on the target 'pico'. UART0's RX can be " +
                    "GPIO 1, 13, 17 or 29.",
                "Program.Main() at IL_0022 (call): the target 'pico' has no UART2. Its serial ports are UART0 and UART1.",
                "Program.Main() at IL_0029 (call): the target 'pico' has no UART-1. Its serial ports are UART0 and UART1.",
                "Program.Main() at IL_002f (call): GPIO 3 cannot be an analog input on the target 'pico'. An analog input " +
                    "can be GPIO 26 to 29.",
                "Program.Main() at IL_0036 (call): GPIO 25 cannot be an analog input on the target 'pico'. An analog " +
                    "input can be GPIO 26 to 29.",
                "Program.Main() at IL_0043 (call): GPIO 30 cannot be a PWM output on the target 'pico'. A PWM output can " +
                    "be GPIO 0 to 29.",
                "Program.Main() at IL_0052 (call): GPIO -1 cannot be a PWM output on the target 'pico'. A PWM output can " +
                    "be GPIO 0 to 29.",
            ],
            analysis.Problems.Select(problem => problem.Line));
        Assert.Equal("pico", analysis.DetailsTarget);
    }

    [Fact]
    public void Pins_known_only_when_the_program_runs_are_the_runtimes_to_check()
    {
        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load("""
            using ChipSharp.Hardware;

            class Program
            {
                static int pin = 3;

                static void Main()
                {
                    Adc.Input(pin);
                    Pwm.Output(pin > 2 ? 30 : 31, 1000);
                    Pwm.Duty(pin + 40, 0.5);
                    var port = 7;
                    for (var i = 0; i < 3; i++)
                    {
                        Uart.WriteByte(port, 1);
                    }
                }
            }
            """), Targets.Load("pico"));

        Assert.True(analysis.IsSupported, analysis.Summary);
    }

    [Fact]
    public void A_uart_event_needs_its_handler_and_the_handler_cannot_wait_for_the_console()
    {
        var analysis = WholeProgramPass.Check(InterruptCheckTests.Load("""
            using ChipSharp.Hardware;

            class Program
            {
                static volatile int last;

                static void Main()
                {
                    Uart.Open(1, 4, 5, 9600);
                    Uart.Watch(1);
                }

                [Interrupt(Event.Pin)]
                static void Changed() => last = System.Console.Read() + Uart.ReadByte(1);
            }
            """), Targets.Load("pico"));

        Assert.Equal(
            [
                "Program.Main() at IL_000e (call): the program starts the uart event, which no [Interrupt(Event.Uart)] " +
                    "method handles. Add one, e.g. [Interrupt(Event.Uart)] static void OnUart() { ... }: without it the " +
                    "chip stops in the interrupt.",
                "Program.Changed() at IL_0000 (call): an interrupt handler cannot wait (Timing.Delay, " +
                    "Timing.DelayMilliseconds, Timing.DelayMicroseconds, Console.Read, Console.ReadLine). The interrupt " +
                    "that ends the wait cannot come while a handler runs, so it would wait forever: keep handlers short, " +
                    "and wait in Main, e.g. for a static volatile field the handler sets.",
            ],
            analysis.Problems.Select(problem => problem.Line));
    }

    [Fact]
    public void The_Picos_target_file_says_what_it_has_and_which_pins_can_have_it()
    {
        var pico = Targets.Load("pico");

        Assert.Equal("2", pico.Capabilities["uart"]);
        Assert.Equal("4", pico.Capabilities["adc"]);
        Assert.Equal("16", pico.Capabilities["pwm"]);
        Assert.True(pico.HasCapability("consoleInput"));
        Assert.Equal("[0,12,16,28]", pico.Settings["pinFunctions"]!["uart0.tx"]!.ToJsonString());
        Assert.Equal("[5,9,21,25]", pico.Settings["pinFunctions"]!["uart1.rx"]!.ToJsonString());
        Assert.Equal("[26,27,28,29]", pico.Settings["pinFunctions"]!["adc"]!.ToJsonString());
        Assert.Equal(Enumerable.Range(0, 30),
            pico.Settings["pinFunctions"]!["pwm"]!.AsArray().Select(pin => pin!.GetValue<int>()));
        Assert.Equal(new TargetInterrupt("uart", 26, null), Interrupts.ForTarget(pico)["uart"]);
        foreach (var name in new[] { "armv6m", "host" })
        {
            var target = Targets.Load(name);
            Assert.False(target.Capabilities.ContainsKey("uart") || target.Capabilities.ContainsKey("adc")
                || target.Capabilities.ContainsKey("pwm") || target.HasCapability("consoleInput"));
        }
    }

    [Fact]
    public void A_pin_list_that_is_not_one_is_a_mistake_in_the_target_file()
    {
        var target = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("pico-odd", """{ "inherits": "pico", "pinFunctions": { "adc": "26-29" } }"""))).Load("pico-odd");

        var error = Assert.Throws<ChipSharpException>(() => WholeProgramPass.Check(
            InterruptCheckTests.Load("ChipSharp.Hardware.Adc.Input(26);"), target));

        Assert.Equal("Target 'pico-odd': 'pinFunctions.adc' must be a list of pin numbers, e.g. [26, 27, 28].",
            error.Message);
    }

    /// <summary>A Pico whose target file says it has no heap (but its console, serial ports, ADC and PWM).</summary>
    private TargetDescription NoHeap => new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
        ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");

    private static bool IsSerialAnalog(string name) =>
        name.StartsWith("ChipSharp.Runtime.Uart", StringComparison.Ordinal)
        || name.StartsWith("ChipSharp.Runtime.Adc", StringComparison.Ordinal)
        || name.StartsWith("ChipSharp.Runtime.Pwm", StringComparison.Ordinal)
        || name == "ChipSharp.Runtime.ConsoleRead";
}
