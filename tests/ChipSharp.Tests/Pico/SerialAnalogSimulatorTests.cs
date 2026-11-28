using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Image.Uf2;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// Serial ports, the console's input, analog inputs and PWM outputs on the simulated Pico: text sent to UART0 and UART1
/// (run.mjs --send) read with Console.ReadLine, Console.Read and Uart, also while the program is busy; voltages
/// (--adc) read with Adc; PWM outputs measured from their edges; and the messages a pin or port used the wrong way
/// stops the program with.
/// </summary>
public sealed partial class SerialAnalogSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [SimulatorFact]
    public async Task Console_ReadLine_ends_lines_as_dotnet_does_and_Console_Read_gives_every_char_UTF_8_decoded()
    {
        var program = PicoBuild.Build("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine("ready");
                    for (var i = 0; i < 4; i++)
                    {
                        var line = Console.ReadLine()!;
                        Console.WriteLine($"[{line}] {line.Length}");
                    }

                    for (var i = 0; i < 4; i++)
                    {
                        Console.Write(Console.Read());
                        Console.Write(' ');
                    }

                    Console.WriteLine();
                    foreach (var c in Console.ReadLine()!)
                    {
                        Console.Write((int)c);
                        Console.Write(' ');
                    }

                    Console.WriteLine();
                }
            }
            """, "ReadLines", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 200,
            "--send", @"0=one\r\ntwo\rthree\n\r\nab\r\n@10ms", "--send", @"0=\xc3\xa9\xe2\x82\xac\xf0\x9f\x98\x80\xff!\n@50ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(@"input: UART0 ""one\r\ntwo\rthree\n\r\nab\r\n""", run.Output);
        // .NET's ReadLine: "\r\n", "\r" and "\n" each end a line; Read gives the chars as they are (a '\n' right after
        // the '\r' that ended a line belongs to that line's end)
        Assert.Equal(
            ["ready", "[one] 3", "[two] 3", "[three] 5", "[] 0", "97 98 13 10 ", "233 8364 55357 56832 65533 33 "],
            Uart0Lines(run));
        Assert.EndsWith("stopped: the program has finished: the core sleeps and nothing is scheduled to wake it",
            run.Stopped);
    }

    [SimulatorTheory]
    [InlineData(200, 200)]
    [InlineData(300, 256)]
    public async Task Bytes_that_arrive_while_the_program_is_busy_wait_in_a_buffer_of_256_bytes(int sent, int kept)
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static volatile int spin;

                static void Main()
                {
                    Uart.Open(1, 4, 5, 115200);
                    for (var i = 0; i < 1000000; i++)
                    {
                        spin += i; // 50 ms of work, reading nothing
                    }

                    Console.WriteLine($"busy until now, {Uart.Available(1)} bytes waiting");
                    Span<byte> buffer = stackalloc byte[400];
                    var count = Uart.Read(1, buffer);
                    var sum = 0;
                    for (var i = 0; i < count; i++)
                    {
                        sum += buffer[i];
                    }

                    Console.WriteLine($"{count} bytes, sum {sum}, first {buffer[0]}, last {buffer[count - 1]}, then {Uart.ReadByte(1)}");
                }
            }
            """, "Busy", _scratch);
        var text = string.Concat(Enumerable.Range(0, sent).Select(i => (char)('0' + i % 10)));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 200, "--send", $"1={text}@1ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        // 10 bits a byte at 115200 baud: the last byte is in by 1 ms + sent * 86.8 us, long before the work is done
        var busy = Assert.Single(UartLines(run, 0), line => line.Text.StartsWith("busy", StringComparison.Ordinal));
        Assert.InRange(busy.Milliseconds, 1 + sent * 10_000.0 / 115_200 + 1, 200);
        Assert.Equal($"busy until now, {kept} bytes waiting", busy.Text);
        Assert.Equal(
            $"{kept} bytes, sum {text[..kept].Sum(c => c)}, first {(int)'0'}, last {(int)text[kept - 1]}, then -1",
            Uart0Lines(run)[1]);
    }

    [SimulatorFact]
    public async Task UART1_sends_and_receives_at_its_speed_and_the_uart_event_runs_when_bytes_arrive()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static volatile int count;
                static volatile int sum;
                static volatile int events;

                static void Main()
                {
                    Uart.Open(1, 8, 9, 9600);
                    Uart.Write(1, "hello, UART1\r\n"u8);
                    Uart.WriteByte(1, (byte)'!');
                    Uart.WriteByte(1, (byte)'\n');
                    Console.WriteLine($"{Uart.ReadByte(1)} {Uart.Available(1)}");
                    Uart.Watch(1);
                    while (count < 5)
                    {
                        Timing.DelayMilliseconds(1);
                    }

                    Console.WriteLine($"{count} bytes, sum {sum}, {events} events");
                    Uart.Unwatch(1);
                    Timing.DelayMilliseconds(20);
                    Console.WriteLine($"{events} events, {Uart.Available(1)} waiting, then {Uart.ReadByte(1)}");
                }

                [Interrupt(Event.Uart)]
                static void Received()
                {
                    events++;
                    for (var value = Uart.ReadByte(1); value >= 0; value = Uart.ReadByte(1))
                    {
                        count++;
                        sum += value;
                    }
                }
            }
            """, "Uart1", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 200, "--send", "1=hello@10ms", "--send", "1=z@30ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("UART1 baud 9600", run.Output);
        Assert.Equal(["hello, UART1", "!"], UartLines(run, 1).Select(line => line.Text));
        // at 9600 baud a byte takes 1.04 ms: the event runs for each, and the handler reads each
        Assert.Equal(["-1 0", $"5 bytes, sum {"hello".Sum(c => c)}, 5 events", "5 events, 1 waiting, then 122"],
            Uart0Lines(run));
        Assert.InRange(UartLines(run, 0)[1].Milliseconds, 10 + 4 * 1.04, 10 + 5 * 1.05 + 1);
    }

    [SimulatorFact]
    public async Task UART0_opened_on_other_pins_and_at_another_speed_is_the_consoles_from_then_on()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            Console.WriteLine("at 115200");
            Uart.Open(0, 12, 13, 9600);
            Console.WriteLine("at 9600");
            Console.WriteLine(Console.ReadLine());
            """, "Console9600", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 100, "--send", @"0=typed\r@5ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["at 115200", "at 9600", "typed"], Uart0Lines(run));
        Assert.Equal(9600, run.Uart0BaudRates[^1]);
        Assert.Contains("GPIO12 ", run.Output);
    }

    [SimulatorTheory]
    [InlineData("Uart.WriteByte(Value(1), 0);",
        "System.InvalidOperationException: UART1 is not open: call Uart.Open(1, tx, rx, baud) first.")]
    [InlineData("Uart.Open(Value(1), Value(6), 5, 9600);",
        "System.ArgumentException: GPIO 6 cannot be UART1's TX: on the RP2040 it is GPIO 4, 8, 20 or 24. (Parameter 'tx')")]
    [InlineData("Uart.Open(Value(0), 0, Value(5), 9600);",
        "System.ArgumentException: GPIO 5 cannot be UART0's RX: on the RP2040 it is GPIO 1, 13, 17 or 29. (Parameter 'rx')")]
    [InlineData("Uart.Open(Value(2), 4, 5, 9600);",
        "System.ArgumentOutOfRangeException: The RP2040 has UART0 and UART1; there is no UART2. (Parameter 'port')")]
    [InlineData("Uart.Open(1, 4, 5, Value(100));",
        "System.ArgumentOutOfRangeException: UART1 cannot run at 100 baud: the RP2040's UARTs run at 120 to 7812500 " +
        "baud. (Parameter 'baud')")]
    [InlineData("Adc.Read(Value(26));",
        "System.InvalidOperationException: GPIO 26 is not an analog input yet: call Adc.Input(26) first.")]
    [InlineData("Adc.Input(Value(25));",
        "System.ArgumentException: GPIO 25 is not an analog input: on the RP2040 they are GPIO 26, 27, 28 and 29. " +
        "(Parameter 'pin')")]
    [InlineData("Pwm.Duty(Value(15), 0.5);",
        "System.InvalidOperationException: GPIO 15 is not a PWM output: call Pwm.Output(15, frequency) first.")]
    [InlineData("Pwm.Output(Value(30), 1000);",
        "System.ArgumentOutOfRangeException: The RP2040 has no GPIO 30: its pins are GPIO 0 to 29. (Parameter 'pin')")]
    [InlineData("Pwm.Output(15, Value(5));",
        "System.ArgumentOutOfRangeException: The RP2040 cannot make a PWM frequency of 5 Hz: it makes 8 Hz to 62500000 " +
        "Hz. (Parameter 'frequency')")]
    [InlineData("Pwm.Output(0, 1000); Pwm.Output(Value(17), 50);",
        "System.InvalidOperationException: GPIO 17 shares PWM slice 0 with GPIO 0, which runs at 1000 Hz, and a slice " +
        "runs at one frequency. Give both the same frequency, stop GPIO 0 first, or use a pin of another slice.")]
    [InlineData("Pwm.Output(2, 1000); Pwm.Output(Value(18), 1000);",
        "System.InvalidOperationException: GPIO 18 and GPIO 2 are the same PWM output (slice 1): GPIO 2 is a PWM output " +
        "already. Stop GPIO 2 first, or use a pin of another slice.")]
    [InlineData("Pwm.Output(15, 1000); Pwm.Duty(15, Value(3) / 2.0);",
        "System.ArgumentOutOfRangeException: A duty cycle is from 0 (always low) to 1 (always high). (Parameter 'duty')")]
    [InlineData("Pwm.Output(15, 50); Pwm.PulseMicroseconds(15, Value(-1));",
        "System.ArgumentOutOfRangeException: A pulse of -1 microseconds is shorter than none. (Parameter 'microseconds')")]
    public async Task A_pin_port_or_value_the_hardware_cannot_take_stops_the_program_saying_what_to_do(string code,
        string message)
    {
        // (the values come from a method the program calls, so that chipsharp check cannot see them first)
        var program = PicoBuild.Build($$"""
            using System;
            using System.Runtime.CompilerServices;
            using ChipSharp.Hardware;

            Console.WriteLine("start");
            {{code}}
            Console.WriteLine("not here");

            partial class Program
            {
                [MethodImpl(MethodImplOptions.NoInlining)]
                static int Value(int value) => value;
            }
            """, "Wrong", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 100);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Equal(["start", $"Unhandled exception. {message}"], Uart0Lines(run));
        Assert.Contains("the program hit a breakpoint", run.Stopped);
    }

    [SimulatorFact]
    public async Task The_ADC_reads_the_voltages_and_the_temperature_the_simulator_gives()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            for (var pin = 26; pin <= 29; pin++)
            {
                Adc.Input(pin);
            }

            Show();
            Timing.DelayMilliseconds(50);
            Show();

            static void Show()
            {
                for (var pin = 26; pin <= 29; pin++)
                {
                    Console.WriteLine($"GPIO{pin} {Adc.Read(pin)}");
                }

                Console.WriteLine(Adc.Volts(27));
                Console.WriteLine(Adc.Temperature());
            }
            """, "Analog", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 200,
            "--adc", "26=0V", "--adc", "27=1.65V", "--adc", "28=3.3V", "--adc", "29=1234",
            "--adc", "27=1V@20ms", "--adc", "temp=30C@20ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("input: GPIO27 1.000 V (1241)", run.Output);
        Assert.Contains("input: temperature 30 C (870)", run.Output);
        // the simulator's readings: volts / 3.3 V * 4096, rounded, at most 4095; 27 C is 0.706 V, 1.721 mV less a degree
        static string Volts(int reading) => (reading * 3.3 / 4096).ToString(CultureInfo.InvariantCulture);
        static string Celsius(int reading) =>
            (27 - (reading * 3.3 / 4096 - 0.706) / 0.001721).ToString(CultureInfo.InvariantCulture);
        Assert.Equal(
            [
                "GPIO26 0", "GPIO27 2048", "GPIO28 4095", "GPIO29 1234", Volts(2048), Celsius(876),
                "GPIO26 0", "GPIO27 1241", "GPIO28 4095", "GPIO29 1234", Volts(1241), Celsius(870),
            ],
            Uart0Lines(run));
        Assert.Equal("1.65", Volts(2048));
        Assert.InRange(double.Parse(Celsius(870), CultureInfo.InvariantCulture), 29.9, 30.1);
    }

    [SimulatorFact]
    public async Task PWM_outputs_run_at_their_frequency_and_duty_cycle_as_their_edges_show()
    {
        var program = PicoBuild.Build("""
            using ChipSharp.Hardware;

            Pwm.Output(2, 1000);            // slice 1, A
            Pwm.Output(3, 1000);            // slice 1, B: the same frequency, another duty cycle
            Pwm.Duty(2, 0.25);
            Pwm.Duty(3, 0.75);
            Pwm.Output(15, 50);             // a servo's
            Pwm.PulseMicroseconds(15, 1500);
            Pwm.Output(20, 20000);          // slice 2, A
            Pwm.Duty(20, 0.5);
            Pwm.Output(27, 8);              // the slowest
            Pwm.Duty(27, 0.1);
            Timing.DelayMilliseconds(300);
            Pwm.Duty(2, 0);
            Pwm.Duty(3, 1);
            Pwm.Stop(15);
            Pwm.Stop(20);
            Pwm.Output(21, 440);            // slice 2 again, now free for another frequency
            Pwm.Duty(21, 0.5);
            Timing.DelayMilliseconds(100);
            """, "Pwm", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 450);

        Assert.True(run.ExitCode == 0, run.ToString());
        var pwm = PwmLines(run);
        // each output's first period, as measured: within 0.01% of the frequency asked for, the duty cycle within 0.01%
        // of the period
        void Measured(int pin, double hertz, double duty, double from = 0)
        {
            var line = pwm.First(line => line.Pin == pin && line.Milliseconds >= from && line.Hertz is not null);
            Assert.InRange(line.Hertz!.Value, hertz * 0.9999, hertz * 1.0001);
            Assert.InRange(line.Duty!.Value, duty - 0.01, duty + 0.01);
        }

        Measured(2, 1000, 25);
        Measured(3, 1000, 75);
        Measured(15, 50, 7.5);
        Measured(20, 20000, 50);
        Measured(27, 8, 10);
        Measured(21, 440, 50, from: 300);
        Assert.InRange(pwm.Single(line => line.Pin == 15).HighMicroseconds!.Value, 1499.9, 1500.1);
        Assert.Single(pwm, line => line.Pin == 20); // one line: every period alike
        Assert.Contains(pwm, line => line is { Pin: 2, Text: "steady low" } && line.Milliseconds > 300);
        Assert.Contains(pwm, line => line is { Pin: 3, Text: "steady high" } && line.Milliseconds > 300);
        // stopped: no longer driven
        Assert.Contains(run.Pins, change => change is { Pin: 15, State: "input pull-down" } && change.Milliseconds > 300);
        Assert.Contains(run.Pins, change => change is { Pin: 20, State: "input pull-down" } && change.Milliseconds > 300);
    }

    [PicoFact]
    public void Receiving_puts_the_runtimes_handlers_in_a_vector_table_in_RAM_and_only_a_program_that_receives_has_it()
    {
        var uart = ElfFile.Read(PicoBuild.Build("""
            using ChipSharp.Hardware;

            Uart.Open(1, 4, 5, 9600);
            Pin.Output(25);
            Pin.Write(25, Uart.ReadByte(1) > 0);
            """, "Receives", _scratch).Elf);
        var blink = ElfFile.Read(PicoBuild.Build(PicoBuild.BlinkSource, "Blink", _scratch).Elf);

        var table = uart.Symbols["vectors_in_ram"];
        Assert.InRange(table, 0x20000000u, 0x20042000u);
        Assert.Equal(0u, table % 256);
        Assert.Contains("ChipSharp_Runtime_UartOpen", uart.Symbols.Keys);
        Assert.DoesNotContain("vectors_in_ram", blink.Symbols.Keys);
        Assert.DoesNotContain(blink.Symbols.Keys, name => name.StartsWith("ChipSharp_Runtime_Uart", StringComparison.Ordinal)
            || name.StartsWith("ChipSharp_Runtime_Adc", StringComparison.Ordinal)
            || name.StartsWith("ChipSharp_Runtime_Pwm", StringComparison.Ordinal));
    }

    /// <summary>What the program wrote to UART0, line by line.</summary>
    private static List<string> Uart0Lines(SimulatorRun run) => [.. UartLines(run, 0).Select(line => line.Text)];

    /// <summary>The lines run.mjs printed for what the program wrote to UART0 or UART1, with when.</summary>
    private static List<(double Milliseconds, string Text)> UartLines(SimulatorRun run, int port) =>
    [
        .. run.Output.Split('\n').Select(line => UartLine().Match(line.TrimEnd('\r')))
            .Where(match => match.Success && match.Groups[2].Value == port.ToString(CultureInfo.InvariantCulture))
            .Select(match => (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[3].Value)),
    ];

    /// <summary>The PWM outputs run.mjs measured: a period's frequency, duty cycle (%) and high time, or a steady level.</summary>
    private static List<PwmLine> PwmLines(SimulatorRun run) =>
    [
        .. run.Output.Split('\n').Select(line => PwmLinePattern().Match(line.TrimEnd('\r'))).Where(match => match.Success)
            .Select(match =>
            {
                var text = match.Groups[3].Value;
                var period = PwmPeriod().Match(text);
                double? Number(int group) => period.Success
                    ? double.Parse(period.Groups[group].Value, CultureInfo.InvariantCulture)
                    : null;
                return new PwmLine(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), text, Number(1), Number(2), Number(3));
            }),
    ];

    private sealed record PwmLine(double Milliseconds, int Pin, string Text, double? Hertz, double? Duty,
        double? HighMicroseconds);

    [GeneratedRegex(@"^\[\s*([0-9.]+) ms\] UART([01]): (.*)$")]
    private static partial Regex UartLine();

    [GeneratedRegex(@"^\[\s*([0-9.]+) ms\] GPIO(\d+) PWM (.+)$")]
    private static partial Regex PwmLinePattern();

    [GeneratedRegex(@"^([0-9.]+) Hz, duty ([0-9.]+)% \(([0-9.]+) us high\)$")]
    private static partial Regex PwmPeriod();
}
