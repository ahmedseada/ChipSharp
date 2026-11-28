using System.Text;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The hand-written test program (<see cref="AvrTestProgram"/>), compiled and linked with Zig, written as .hex and run
/// in the avr8js simulator (tools/avr-sim/run.mjs): the whole chain to the Uno before the runtime "avr" is there, and
/// what the simulator does: pins by their Uno names, USART0 both ways, inputs on a timeline, and the ways a run stops.
/// </summary>
public sealed class AvrSimulatorTests : IDisposable
{
    private const string Finished =
        "stopped: the program has finished: the CPU sleeps with interrupts off, so nothing can wake it";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [AvrSimulatorFact]
    public async Task The_test_program_writes_to_USART0_toggles_D13_and_finishes()
    {
        var program = AvrTestProgram.Build(_scratch);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.EndsWith(Finished, run.Stopped);
        // the line from RAM (copied there from flash at start-up), the one read from flash, the count from .bss
        Assert.Equal("Hello AVR\nfrom flash\n6\n", Encoding.ASCII.GetString(run.Usart0));
        Assert.Equal(["Hello AVR", "from flash", "6"], run.Usart0Lines);
        Assert.Equal([9615], run.Usart0BaudRates); // 16 MHz / (16 * 104)
        // DDRB makes D13 an output, low; then six toggles, one per delay
        var led = run.Pins.Where(pin => pin.Pin == "D13").ToList();
        Assert.Equal(["low", "high", "low", "high", "low", "high", "low"], led.Select(pin => pin.State));
        for (var i = 2; i < led.Count; i++)
        {
            Assert.InRange(led[i].Milliseconds - led[i - 1].Milliseconds, 1, 5);
        }

        Assert.DoesNotContain(run.Pins, pin => pin.Pin != "D13");
        Assert.Matches(@"^\[\s+\d+\.\d{3} ms\] D13 high$", run.Output.Split('\n').First(line => line.Contains("D13 high")).TrimEnd('\r'));
    }

    [AvrSimulatorFact]
    public async Task Inputs_on_a_timeline_pull_ups_and_text_sent_to_USART0_reach_the_program()
    {
        var program = AvrTestProgram.Build(_scratch, extra: """
            #define PIND (*(volatile uint8_t *)0x29)
            #define PORTD (*(volatile uint8_t *)0x2B)
            void send(char c);
            char receive(void);

            void test_extra(void) {
              char line[16];
              uint8_t length = 0;
              PORTD |= 1 << 3;                                  /* D3: an input with its pull-up */
              send(PIND & (1 << 2) ? 'H' : 'L');                /* D2 */
              send(PIND & (1 << 3) ? 'H' : 'L');                /* D3 */
              send('\n');
              for (char c = receive(); c != '\n' && length < sizeof line; c = receive()) line[length++] = c;
              for (uint8_t i = 0; i < length; i++) send(line[i] >= 'a' && line[i] <= 'z' ? line[i] - 32 : line[i]);
              send('\n');
              while (!(PIND & (1 << 2))) {}                     /* until D2 goes high */
              send('!');
              send('\n');
            }
            """);

        var run = await AvrSimulator.RunAsync(program.Hex, 1000,
            "--input", "D2=low", "--input", "D2=high@200ms", "--send", "hello\\n@60ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.EndsWith(Finished, run.Stopped);
        // D2 low, D3 high by its pull-up; the line sent, in capitals; then D2 high at 200 ms
        Assert.Equal(["Hello AVR", "from flash", "6", "LH", "HELLO", "!"], run.Usart0Lines);
        Assert.Contains("[    60.000 ms] input: USART0 \"hello\\n\"", run.Output);
        Assert.Contains("[   200.000 ms] input: D2 high", run.Output);
        // a line's time is its line ending's: "!\n" is two bytes, about 1 ms each at 9600 baud
        var bang = run.Output.Split('\n').Single(line => line.Contains("USART0: !"));
        Assert.InRange(double.Parse(bang[1..11], System.Globalization.CultureInfo.InvariantCulture), 200, 203);
        Assert.Contains(run.Pins, pin => pin is { Pin: "D3", State: "input pull-up" });

        // driven from outside, D3 reads what drives it, pull-up or not
        var driven = await AvrSimulator.RunAsync(program.Hex, 1000,
            "--input", "D2=high", "--input", "D3=low", "--send", "x\\n@60ms");
        Assert.Equal(["Hello AVR", "from flash", "6", "HL", "X", "!"], driven.Usart0Lines);
    }

    [AvrSimulatorFact]
    public async Task A_break_stops_the_run_as_a_program_that_went_wrong()
    {
        var program = AvrTestProgram.Build(_scratch, extra: """void test_extra(void) { __asm__ volatile ("break"); }""");

        var run = await AvrSimulator.RunAsync(program.Hex, 1000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Matches(@"stopped: the program hit a breakpoint \(break\) at pc 0x[0-9a-f]{4}, e\.g\. a division by zero$",
            run.Stopped);
        Assert.Equal(["Hello AVR", "from flash", "6"], run.Usart0Lines);
    }

    [AvrSimulatorFact]
    public async Task A_jump_into_flash_the_image_does_not_fill_stops_the_run()
    {
        // a function pointer on an AVR holds a word address: 0x1800 is byte 0x3000
        var program = AvrTestProgram.Build(_scratch, extra: "void test_extra(void) { ((void (*)(void))0x1800)(); }");

        var run = await AvrSimulator.RunAsync(program.Hex, 1000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.EndsWith("stopped: the program ran into flash the image does not fill, at pc 0x3000", run.Stopped);
    }

    [AvrSimulatorFact]
    public async Task A_program_that_never_finishes_runs_for_the_time_given()
    {
        var program = AvrTestProgram.Build(_scratch, extra: "void test_extra(void) { for (;;) {} }");

        var run = await AvrSimulator.RunAsync(program.Hex, 80);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("[    80.000 ms] stopped: 80 ms of simulated time have passed", run.Stopped);
        Assert.Equal(["Hello AVR", "from flash", "6"], run.Usart0Lines);
    }

    [AvrSimulatorFact]
    public async Task A_record_with_a_wrong_checksum_or_a_wrong_pin_name_is_refused()
    {
        var program = AvrTestProgram.Build(_scratch);
        var lines = await File.ReadAllLinesAsync(program.Hex);
        lines[1] = lines[1][..^2] + (lines[1][^2..] == "00" ? "01" : "00");
        var broken = Path.Combine(_scratch, "Broken.hex");
        await File.WriteAllLinesAsync(broken, lines);

        var run = await AvrSimulator.RunAsync(broken, 100);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("line 2 of", run.Errors);
        Assert.Contains("has a wrong checksum", run.Errors);

        var pin = await AvrSimulator.RunAsync(program.Hex, 100, "--input", "D14=high");
        Assert.Equal(2, pin.ExitCode);
        Assert.Contains("--input takes an Uno pin (D0 to D13, A0 to A5)", pin.Errors);
    }
}
