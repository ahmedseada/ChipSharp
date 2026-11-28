using System.Text;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The Uno's runtime (atmega328p, on the AVR runtime) under C programs written by hand, which call its functions as a C#
/// program's code does, built for the Uno and run in the avr8js simulator (tools/avr-sim): the pins by the Uno's
/// numbers, the delays and the clock against the simulated time, the pin and timer events, USART0 both ways, and the
/// failures' messages.
/// </summary>
public sealed class UnoRuntimeSimulatorTests : IDisposable
{
    private const string Finished = "the program has finished";

    /// <summary>
    /// How long after its event, at most, a handler toggles a pin, in milliseconds: the interrupt's entry saves the
    /// registers, the runtime finds out whether the event happened (and sets the timer's next time), then calls it.
    /// </summary>
    private const double Latency = 0.060;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [AvrSimulatorFact]
    public async Task The_pins_go_by_the_Unos_numbers_D0_to_D13_then_A0_to_A5()
    {
        var program = UnoProgram.BuildC(_scratch, "Pins", """
            long chipsharp_entry(void)
            {
                for (i32 pin = 2; pin < 20; pin++)
                {
                    ChipSharp_Runtime_PinOutput(pin);
                    ChipSharp_Runtime_PinWrite(pin, 1);
                }

                ChipSharp_Runtime_PinToggle(13);
                ChipSharp_Runtime_PinWrite(19, 0);
                ChipSharp_Runtime_PinOutput(20); /* none: ignored */
                ChipSharp_Runtime_PinToggle(-1);
                ChipSharp_Runtime_PinInput(7);
                ChipSharp_Runtime_RegisterWrite(0x2b, ChipSharp_Runtime_RegisterRead(0x2b) | 0x80); /* PORTD7: D7's pull-up */
                print(ChipSharp_Runtime_PinRead(8) && !ChipSharp_Runtime_PinRead(19) && ChipSharp_Runtime_PinRead(7)
                    && !ChipSharp_Runtime_PinRead(20) ? "read\n" : "wrong\n");
                return 0;
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 100);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        string[] names = ["D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9", "D10", "D11", "D12", "D13", "A0", "A1", "A2", "A3", "A4", "A5"];
        Assert.All(names, name => Assert.Equal(["low", "high"], run.Pins.Where(p => p.Pin == name).Select(p => p.State).Take(2)));
        Assert.Equal(["low", "high", "low"], run.Pins.Where(p => p.Pin == "D13").Select(p => p.State));
        Assert.Equal(["low", "high", "low"], run.Pins.Where(p => p.Pin == "A5").Select(p => p.State));
        // Input lets go of D7 (high a moment more by its pull-up, PORTD7 still set), then Registers turns the pull-up on
        var d7 = run.Pins.Where(p => p.Pin == "D7").Select(p => p.State).ToList();
        Assert.Equal(["low", "high"], d7.Take(2));
        Assert.Equal(["input", "input pull-up"], d7.TakeLast(2));
        Assert.Equal(["read"], run.Usart0Lines);
    }

    [AvrSimulatorFact]
    public async Task The_delays_take_the_time_they_are_given_to_a_few_microseconds()
    {
        var program = UnoProgram.BuildC(_scratch, "Delays", """
            long chipsharp_entry(void)
            {
                ChipSharp_Runtime_PinOutput(13);
                ChipSharp_Runtime_PinOutput(12);
                ChipSharp_Runtime_PinOutput(11);
                for (i32 i = 0; i < 4; i++)
                {
                    ChipSharp_Runtime_PinToggle(13);
                    ChipSharp_Runtime_DelayMilliseconds(50);
                }

                for (i32 i = 0; i < 4; i++)
                {
                    ChipSharp_Runtime_PinToggle(12);
                    ChipSharp_Runtime_DelayMicroseconds(300);
                }

                for (i32 i = 0; i < 4; i++)
                {
                    ChipSharp_Runtime_PinToggle(11);
                    ChipSharp_Runtime_DelayMicroseconds(20);
                }

                ChipSharp_Runtime_DelayMicroseconds(0);
                ChipSharp_Runtime_DelayMilliseconds(-5);
                return 0;
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        AssertIntervals(run, "D13", 50, 0.020);
        AssertIntervals(run, "D12", 0.300, 0.020);
        AssertIntervals(run, "D11", 0.020, 0.020);
    }

    /// <summary>The times between the toggles of <paramref name="pin"/> (after it became an output): at least milliseconds, at most tolerance more.</summary>
    private static void AssertIntervals(AvrSimulatorRun run, string pin, double milliseconds, double tolerance)
    {
        var times = run.Pins.Where(p => p.Pin == pin).Skip(1).Select(p => p.Milliseconds).ToList();
        Assert.True(times.Count >= 4, run.ToString());
        for (var i = 1; i < times.Count; i++)
        {
            var interval = times[i] - times[i - 1];
            Assert.True(interval >= milliseconds && interval <= milliseconds + tolerance,
                $"{pin}: {interval} ms, not {milliseconds} ms\n{run}");
        }
    }

    [AvrSimulatorFact]
    public async Task Console_output_goes_to_USART0_at_115200_baud_with_U2X()
    {
        var program = UnoProgram.BuildC(_scratch, "Console", """
            static const __flash char in_flash[] = "from flash\n";

            long chipsharp_entry(void)
            {
                print("Hello\r\n");
                chipsharp_console_write((const char *)((unsigned int)in_flash | 0x8000u), sizeof in_flash - 1);
                print_number(-1234567);
                print("\n");
                return 0;
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 100);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        Assert.Equal("Hello\r\nfrom flash\n-1234567\n", Encoding.ASCII.GetString(run.Usart0));
        Assert.Equal([117647], run.Usart0BaudRates); // 16 MHz / (8 * 17), U2X0
    }

    [AvrSimulatorFact]
    public async Task The_clock_counts_microseconds_and_DateTime_ticks_as_the_time_passes()
    {
        var program = UnoProgram.BuildC(_scratch, "Clock", """
            long chipsharp_entry(void)
            {
                i64 start = ChipSharp_Runtime_ClockTimestamp();
                i64 ticks = ChipSharp_Runtime_ClockUtcTicks();
                ChipSharp_Runtime_DelayMilliseconds(250);
                i64 end = ChipSharp_Runtime_ClockTimestamp();
                i64 later = ChipSharp_Runtime_ClockUtcTicks();
                print_number(end - start);
                print("\n");
                print_number(later - ticks);
                print("\n");
                print_number(ChipSharp_Runtime_ClockElapsedTicks(start, end));
                print("\n");
                print_number(ChipSharp_Runtime_ClockTickCount64());
                print("\n");
                ChipSharp_Runtime_ClockStart(638000000000000000LL);
                ChipSharp_Runtime_DelayMilliseconds(100);
                print_number(ChipSharp_Runtime_ClockUtcTicks() - 638000000000000000LL);
                print("\n");
                return 0;
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        var lines = run.Usart0Lines.Select(long.Parse).ToList();
        Assert.InRange(lines[0], 250000, 250100);       // microseconds
        Assert.InRange(lines[1], 2500000, 2501000);     // ticks of 100 ns
        Assert.Equal(lines[0] * 10, lines[2]);
        Assert.InRange(lines[3], 250, 256);             // milliseconds since start-up (the lines before took some)
        Assert.InRange(lines[4], 1000000, 1001000);     // since Clock.Start, in ticks
    }

    [AvrSimulatorFact]
    public async Task The_pin_event_comes_for_the_edges_watched_on_INT0_INT1_and_the_pin_change_interrupts()
    {
        // each edge the handler runs for toggles D13; D12 says which pins are high then
        var program = UnoProgram.BuildC(_scratch, "PinEvent", """
            static volatile i32 count;

            void chipsharp_isr_irq26(void)
            {
                count++;
                ChipSharp_Runtime_PinToggle(13);
            }

            long chipsharp_entry(void)
            {
                ChipSharp_Runtime_PinOutput(13);
                ChipSharp_Runtime_PinInput(2);
                ChipSharp_Runtime_PinInput(3);
                ChipSharp_Runtime_PinInput(8);
                ChipSharp_Runtime_PinInput(14);
                ChipSharp_Runtime_PinInput(4);
                ChipSharp_Runtime_PinWatchRising(2);   /* INT0 */
                ChipSharp_Runtime_PinWatchFalling(3);  /* INT1 */
                ChipSharp_Runtime_PinWatch(8);         /* PCINT0: both edges */
                ChipSharp_Runtime_PinWatchRising(14);  /* A0, PCINT1 */
                ChipSharp_Runtime_PinWatchFalling(4);  /* PCINT2 */
                ChipSharp_Runtime_DelayMilliseconds(100);
                ChipSharp_Runtime_PinUnwatch(8);
                ChipSharp_Runtime_PinUnwatch(2);
                ChipSharp_Runtime_DelayMilliseconds(50);
                print_number(count);
                print("\n");
                return 0;
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 500,
            "--input", "D2=low", "--input", "D3=high", "--input", "D8=low", "--input", "A0=low", "--input", "D4=high",
            "--input", "D2=high@10ms", "--input", "D2=low@15ms",      // rising at 10 ms
            "--input", "D3=low@20ms", "--input", "D3=high@25ms",      // falling at 20 ms
            "--input", "D8=high@30ms", "--input", "D8=low@35ms",      // both, 30 and 35 ms
            "--input", "A0=high@40ms", "--input", "A0=low@45ms",      // rising at 40 ms
            "--input", "D4=low@50ms", "--input", "D4=high@55ms",      // falling at 50 ms
            "--input", "D2=high@110ms", "--input", "D8=high@120ms");  // no longer watched

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        Assert.Equal(["6"], run.Usart0Lines);
        var toggles = run.Pins.Where(p => p.Pin == "D13").Skip(1).Select(p => p.Milliseconds).ToList();
        double[] edges = [10, 20, 30, 35, 40, 50];
        Assert.Equal(edges.Length, toggles.Count);
        for (var i = 0; i < edges.Length; i++)
        {
            Assert.InRange(toggles[i], edges[i], edges[i] + Latency);
        }
    }

    [AvrSimulatorFact]
    public async Task The_timer_event_comes_every_period_on_time_and_once_after()
    {
        var program = UnoProgram.BuildC(_scratch, "TimerEvent", """
            static volatile i32 ticks;
            static volatile i32 once;

            void chipsharp_isr_irq27(void)
            {
                if (once)
                {
                    once = 0;
                    ChipSharp_Runtime_PinToggle(11);
                    return;
                }

                ChipSharp_Runtime_PinToggle(13);
                if (++ticks == 5)
                {
                    ChipSharp_Runtime_TimerStop();
                }
            }

            long chipsharp_entry(void)
            {
                ChipSharp_Runtime_PinOutput(13);
                ChipSharp_Runtime_PinOutput(12);
                ChipSharp_Runtime_PinOutput(11);
                ChipSharp_Runtime_TimerEveryMilliseconds(40);  /* longer than a round of Timer1, 32.768 ms */
                ChipSharp_Runtime_DelayMilliseconds(300);
                once = 1;
                ChipSharp_Runtime_PinToggle(12);
                ChipSharp_Runtime_TimerAfterMicroseconds(1500);
                ChipSharp_Runtime_DelayMilliseconds(10);
                ChipSharp_Runtime_TimerEveryMicroseconds(250);
                ChipSharp_Runtime_DelayMicroseconds(1100);
                ChipSharp_Runtime_TimerStop();
                print_number(ticks);
                print("\n");
                return 0;
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        // the handler toggles D13 some 40 microseconds after its time (the interrupt, the check of the time, the next time
        // set, the call); the times themselves are exact, so the toggles are as far apart as the period
        var led = run.Pins.Where(p => p.Pin == "D13").Select(p => p.Milliseconds).ToList();
        var start = led[0]; // Pin.Output, just before the timer starts
        Assert.True(led.Count == 1 + 5 + 4, run.ToString()); // every 40 ms five times, then every 250 microseconds four times
        Assert.InRange(led[1] - start, 40.0, 40.0 + Latency);
        for (var i = 2; i <= 5; i++)
        {
            Assert.InRange(led[i] - led[i - 1], 40.0 - 0.002, 40.0 + 0.002);
        }

        for (var i = 7; i < led.Count; i++)
        {
            Assert.InRange(led[i] - led[i - 1], 0.250 - 0.002, 0.250 + 0.002);
        }

        var after = run.Pins.Last(p => p.Pin == "D12").Milliseconds;
        Assert.InRange(run.Pins.Last(p => p.Pin == "D11").Milliseconds - after, 1.5, 1.5 + Latency);
        Assert.Equal(["9"], run.Usart0Lines);
    }

    [AvrSimulatorFact]
    public async Task Console_Read_and_the_serial_port_receive_through_the_buffer_and_the_uart_event()
    {
        var program = UnoProgram.BuildC(_scratch, "Receive", """
            static volatile i32 events;

            void chipsharp_isr_irq28(void)
            {
                events++;
            }

            long chipsharp_entry(void)
            {
                char line[16];
                unsigned int length = 0;
                for (i32 c = ChipSharp_Runtime_ConsoleRead(); c != '\n'; c = ChipSharp_Runtime_ConsoleRead())
                {
                    line[length++] = c >= 'a' && c <= 'z' ? (char)(c - 32) : (char)c;
                }

                chipsharp_console_write(line, length);
                print("\n");
                i32 euro = ChipSharp_Runtime_ConsoleRead();                     /* UTF-8, 3 bytes */
                print(euro == 0x20ac ? "euro\n" : "wrong\n");
                ChipSharp_Runtime_UartOpen(0, 1, 0, 9600);
                ChipSharp_Runtime_UartWatch(0);
                ChipSharp_Runtime_DelayMilliseconds(60);                        /* the program is busy */
                i32 waiting = ChipSharp_Runtime_UartAvailable(0);
                for (i32 c = ChipSharp_Runtime_UartReadByte(0); c >= 0; c = ChipSharp_Runtime_UartReadByte(0))
                {
                    ChipSharp_Runtime_UartWriteByte(0, c);
                }

                print_number(waiting);
                print(events > 0 ? " events\n" : " none\n");
                return 0;
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000,
            "--send", "hello\\n\\xE2\\x82\\xAC@5ms", "--send", "abc\\n@30ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains(Finished, run.Stopped);
        Assert.Equal(["HELLO", "euro", "abc", "4 events"], run.Usart0Lines);
        Assert.Equal([117647, 9615], run.Usart0BaudRates);
    }

    [AvrSimulatorFact]
    public async Task A_failure_says_what_dotnet_says_on_USART0_and_stops_at_BREAK()
    {
        var program = UnoProgram.BuildC(_scratch, "Failure", """
            void ChipSharp_Runtime_IndexOutOfRange(void);

            long chipsharp_entry(void)
            {
                print("before\n");
                ChipSharp_Runtime_IndexOutOfRange();
                return 0;
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 100);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains("breakpoint", run.Stopped);
        Assert.Equal(["before", "Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds of the array."],
            run.Usart0Lines);
    }

    [AvrSimulatorFact]
    public async Task The_serial_port_stops_the_program_for_a_pin_or_speed_it_cannot_have()
    {
        var program = UnoProgram.BuildC(_scratch, "WrongPin", """
            long chipsharp_entry(void)
            {
                ChipSharp_Runtime_UartOpen(0, 4, 0, 9600);
                return 0;
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 100);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Equal(["Unhandled exception. System.ArgumentException: Pin 4 cannot be UART0's TX: on the Arduino Uno it is pin 1 " +
            "(D1). (Parameter 'tx')"], run.Usart0Lines);
    }
}
