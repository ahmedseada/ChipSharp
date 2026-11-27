using System.Text;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// C# for the Arduino Uno built the whole way, as <c>chipsharp build -t uno</c> builds it (the code generator, the
/// toolchain "zig", the runtimes "atmega328p" and "avr", the .hex), and run in the avr8js simulator (tools/avr-sim): a
/// blink, a button on D2 through the pin event, the timer event, the delays and the clock against the simulated time,
/// and the console both ways.
/// </summary>
public sealed class UnoCSharpTests : IDisposable
{
    private const string Finished = "the program has finished";

    /// <summary>
    /// Why the tests of text and numbers on the console wait: Console.WriteLine of a string or a number runs the shared
    /// runtime C (chipsharp_console.c, chipsharp_string.c), which reads a C# int as AVR's 16-bit int and the program's
    /// text with plain loads, where an AVR keeps it in flash; and the string descriptors are not in the AVR runtime yet.
    /// </summary>
    private const string WaitsForSharedRuntime = "Waits for the shared runtime C made AVR-safe (32-bit ints, the program's " +
        "data read through bit 15) and the string descriptors in the AVR runtime: then remove this Skip.";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [AvrSimulatorFact]
    public async Task Blink_toggles_D13_on_time_and_takes_a_little_flash_and_RAM()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "Blink", """
            using ChipSharp.Hardware;

            Pin.Output(13);
            for (int i = 0; i < 6; i++)
            {
                Pin.Toggle(13);
                Timing.DelayMilliseconds(100);
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        var led = run.Pins.Where(p => p.Pin == "D13").ToList();
        Assert.Equal(["low", "high", "low", "high", "low", "high", "low"], led.Select(p => p.State));
        for (var i = 2; i < led.Count; i++)
        {
            Assert.InRange(led[i].Milliseconds - led[i - 1].Milliseconds, 100, 100.020);
        }

        // the vectors, start-up, the clock's overflow interrupt, the runtime's interrupt entries, Main, the pin and delay
        // functions; in RAM the clock's rounds, the interrupt hooks, the exit code
        Assert.InRange(program.Memory.Code!.Used, 800UL, 1400UL);
        Assert.InRange(program.Memory.Data!.Used, 1UL, 24UL);
    }

    [AvrSimulatorFact]
    public async Task A_button_on_D2_runs_the_pin_events_method_for_each_press()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "Button", """
            using ChipSharp.Hardware;

            class Program
            {
                static volatile int presses;

                static void Main()
                {
                    Pin.Output(13);
                    Pin.Input(2);
                    Pin.WatchRising(2);
                    while (presses < 3)
                    {
                        Timing.DelayMilliseconds(1);
                    }

                    Pin.Unwatch(2);
                }

                [Interrupt(Event.Pin)]
                static void Pressed()
                {
                    presses++;
                    Pin.Toggle(13);
                }
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000, "--input", "D2=low",
            "--input", "D2=high@50ms", "--input", "D2=low@80ms", "--input", "D2=high@150ms", "--input", "D2=low@170ms",
            "--input", "D2=high@250ms", "--input", "D2=low@260ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        var toggles = run.Pins.Where(p => p.Pin == "D13").Skip(1).Select(p => p.Milliseconds).ToList();
        Assert.Equal(3, toggles.Count);
        double[] presses = [50, 150, 250];
        for (var i = 0; i < presses.Length; i++)
        {
            Assert.InRange(toggles[i], presses[i], presses[i] + 0.060);
        }
    }

    [AvrSimulatorFact]
    public async Task The_timer_event_blinks_every_period_while_Main_waits()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "TimerBlink", """
            using ChipSharp.Hardware;

            class Program
            {
                static volatile int ticks;

                static void Main()
                {
                    Pin.Output(13);
                    Timing.EveryMilliseconds(25);
                    while (ticks < 8)
                    {
                        Timing.DelayMilliseconds(1);
                    }
                }

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    Pin.Toggle(13);
                    if (++ticks == 8)
                    {
                        Timing.Stop();
                    }
                }
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        var toggles = run.Pins.Where(p => p.Pin == "D13").Skip(1).Select(p => p.Milliseconds).ToList();
        Assert.Equal(8, toggles.Count);
        for (var i = 1; i < toggles.Count; i++)
        {
            Assert.InRange(toggles[i] - toggles[i - 1], 25 - 0.002, 25 + 0.002);
        }
    }

    [AvrSimulatorFact]
    public async Task The_delays_and_the_clock_keep_the_simulated_time()
    {
        // D13 high: Stopwatch measured the 200 ms delay; D12 high: DateTime.Now moved on 1.5 s from Clock.Start's date;
        // D11 toggles around a Delay of a TimeSpan and DelayMicroseconds
        var program = UnoProgram.BuildCSharp(_scratch, "Clock", """
            using System;
            using System.Diagnostics;
            using ChipSharp.Hardware;

            long start = Stopwatch.GetTimestamp();
            Timing.DelayMilliseconds(200);
            long elapsed = Stopwatch.GetTimestamp() - start;
            Pin.Output(13);
            Pin.Write(13, Stopwatch.Frequency == 1000000 && elapsed >= 200000 && elapsed < 200100);
            Clock.Start(new DateTime(2026, 11, 27, 12, 0, 0));
            Timing.DelayMilliseconds(1500);
            DateTime now = DateTime.Now;
            Pin.Output(12);
            Pin.Write(12, now.Year == 2026 && now.Hour == 12 && now.Minute == 0 && now.Second == 1 && now.Millisecond >= 500
                && Stopwatch.GetElapsedTime(start) >= TimeSpan.FromMilliseconds(1700));
            Pin.Output(11);
            Pin.Toggle(11);
            Timing.Delay(TimeSpan.FromMilliseconds(30));
            Pin.Toggle(11);
            Timing.DelayMicroseconds(750);
            Pin.Toggle(11);
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 3000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        Assert.Equal("high", run.Pins.Last(p => p.Pin == "D13").State);
        Assert.Equal("high", run.Pins.Last(p => p.Pin == "D12").State);
        var d11 = run.Pins.Where(p => p.Pin == "D11").Skip(1).Select(p => p.Milliseconds).ToList();
        Assert.Equal(3, d11.Count);
        Assert.InRange(d11[1] - d11[0], 30, 30.100);   // Delay(TimeSpan): DelayMicroseconds of 30000 after the TimeSpan math
        Assert.InRange(d11[2] - d11[1], 0.750, 0.770);
    }

    [AvrSimulatorFact]
    public async Task Console_Read_reads_USART0_and_the_serial_port_writes_back_with_its_uart_event()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "Echo", """
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static volatile int received;

                static void Main()
                {
                    Uart.Open(0, 1, 0, 115200);
                    for (int c = Console.Read(); c != '\n'; c = Console.Read())
                    {
                        Uart.WriteByte(0, (byte)(c >= 'a' && c <= 'z' ? c - 32 : c));
                    }

                    Uart.WriteByte(0, (byte)'\n');
                    Uart.Watch(0);
                    while (received < 4)
                    {
                        Timing.DelayMilliseconds(1);
                    }

                    Span<byte> bytes = stackalloc byte[8];
                    Uart.Write(0, bytes.Slice(0, Uart.Read(0, bytes)));
                }

                [Interrupt(Event.Uart)]
                static void Received() => received++;
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000, "--send", "hello uno\\n@20ms", "--send", "abc\\n@100ms");

        // the line in capitals; then "abc\n", which the uart event counts and Uart.Read takes from the buffer
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        Assert.Equal("HELLO UNO\nabc\n", Encoding.ASCII.GetString(run.Usart0));
        Assert.Equal([117647], run.Usart0BaudRates);
    }

    [AvrSimulatorFact(Skip = WaitsForSharedRuntime)]
    public async Task Console_WriteLine_says_hello_and_writes_numbers_and_chars_on_USART0()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "Hello", """
            using System;

            Console.WriteLine("Hello, Uno!");
            Console.WriteLine(123456789);
            Console.WriteLine(-42L);
            Console.WriteLine(true);
            Console.Write('x');
            Console.WriteLine();
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("Hello, Uno!\r\n123456789\r\n-42\r\nTrue\r\nx\r\n", Encoding.ASCII.GetString(run.Usart0));
    }

    [AvrSimulatorFact]
    public async Task A_failure_prints_dotnets_message_on_USART0_and_stops()
    {
        var program = UnoProgram.BuildCSharp(_scratch, "Fails", """
            using System;

            class Program
            {
                static int index = 3;

                static void Main()
                {
                    Span<int> values = stackalloc int[3];
                    values[index] = 1;
                }
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Equal(["Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds of the array."],
            run.Usart0Lines);
    }
}
