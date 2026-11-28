using System.Text;

// a ChipTheory's cases, one per chip, are its discoverer's (ChipTheoryDiscoverer), not a data attribute's
#pragma warning disable xUnit1003

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// The conformance suite's serial ports: Console.Write and WriteLine as .NET writes them, Console.Read, a port of Uart
/// both ways at the speed asked for, and the uart event.
/// </summary>
public sealed class SerialConformanceTests : ConformanceTests
{
    [ChipTheory("console")]
    public async Task Console_writes_text_numbers_chars_bools_and_floats_as_dotnet_with_its_line_endings(string target)
    {
        var chip = ChipSimulators.Get(target);

        var run = await RunAsync(chip, "ConsoleWrite", """
            Console.Write("Hello, ");
            Console.WriteLine("chip");
            Console.WriteLine(-42);
            Console.WriteLine(3000000000u);
            Console.Write('x');
            Console.WriteLine(true);
            Console.WriteLine(1.5f);
            Console.WriteLine();
            """, new ChipStimulus(100));

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        Assert.Equal("Hello, chip\r\n-42\r\n3000000000\r\nxTrue\r\n1.5\r\n\r\n",
            Encoding.ASCII.GetString(run.SerialOf(chip.ConsolePort)));
    }

    [ChipTheory("consoleInput")]
    public async Task Console_Read_waits_for_each_char_of_the_input(string target)
    {
        var chip = ChipSimulators.Get(target);

        var run = await RunAsync(chip, "ConsoleRead", """
            Console.Write("?");
            for (var i = 0; i < 3; i++)
            {
                var c = Console.Read();
                Console.Write((char)(c + 1));
            }

            Console.WriteLine();
            """, new ChipStimulus(200) { Sends = [new(chip.ConsolePort, "abc"u8.ToArray(), 5)] });

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        Assert.Equal("?bcd\r\n", Encoding.ASCII.GetString(run.SerialOf(chip.ConsolePort)));
    }

    [ChipTheory("uart")]
    public async Task A_serial_port_sends_and_receives_at_the_speed_asked_for(string target)
    {
        var chip = ChipSimulators.Get(target);

        var run = await RunAsync(chip, "UartBothWays", """
            Check.Start();
            Uart.Open(0, UartTx, UartRx, 9600);
            Uart.Write(0, Program.Ready);
            while (Uart.Available(0) < 3)
            {
            }

            Span<byte> received = stackalloc byte[4];
            var count = Uart.Read(0, received);
            for (var i = 0; i < count; i++)
            {
                Uart.WriteByte(0, (byte)(received[i] + 1));
            }

            Check.That(count == 3);
            Check.That(Uart.ReadByte(0) == -1 && Uart.Available(0) == 0);
            """, new ChipStimulus(200) { Sends = [new(0, "abc"u8.ToArray(), 20)] },
            "    static ReadOnlySpan<byte> Ready => [(byte)'o', (byte)'k', (byte)'\\n'];");

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        AssertChecks(chip, run, 2);
        Assert.Equal("ok\nbcd", Encoding.ASCII.GetString(run.SerialOf(0)));
        var baud = run.BaudRates[0][^1];
        AssertIn(baud, 9600 * (1 - chip.Tolerances.Baud), 9600 * (1 + chip.Tolerances.Baud), "the port's speed", run);
    }

    [ChipTheory("uart", "interrupts.uart")]
    public async Task The_uart_event_runs_when_bytes_arrive_on_a_watched_port(string target)
    {
        var chip = ChipSimulators.Get(target);

        var run = await RunAsync(chip, "UartEvent", """
            Uart.Open(0, UartTx, UartRx, 9600);
            Uart.Watch(0);
            while (received < 3)
            {
            }

            Uart.Unwatch(0);
            """, new ChipStimulus(200) { Sends = [new(0, "xyz"u8.ToArray(), 10)] }, """
                static volatile int received;

                [Interrupt(Event.Uart)]
                static void Arrived()
                {
                    var next = Uart.ReadByte(0);
                    while (next >= 0)
                    {
                        Uart.WriteByte(0, (byte)(next + 1));
                        received++;
                        next = Uart.ReadByte(0);
                    }
                }
            """);

        Assert.True(run.Stop == ChipStop.Finished, run.ToString());
        Assert.Equal("yz{", Encoding.ASCII.GetString(run.SerialOf(0)));
    }
}
