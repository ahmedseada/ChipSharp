using System.Diagnostics;
using System.Globalization;
using System.Text;
using ChipSharp.Simulators;
using ChipSharp.Simulators.Ch32v003;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// The CH32V003 in chipsim, running C programs clang builds for its QingKe V2A (<see cref="Ch32v003CProgram"/>): the
/// clock (8 MHz after reset, 48 MHz from the PLL with the flash's wait state, and without it a stop); SysTick's
/// interrupt through the PFIC's vector table to a handler and back (mret), the HPE's saving of the registers for a
/// handler without its own, nesting and a VTF channel; GPIO's outputs, inputs and pulls, and EXTI's pin interrupts on an
/// input timeline; USART1's output, baud rate and input with its RXNE interrupt; the watchdogs' resets; the stops
/// (ebreak, a trap without a handler, wfi with interrupts off, and with none to come); a peripheral used with its clock
/// off, or not simulated yet; loading an ELF, Intel HEX at either base and a .bin; the simulator's speed.
/// </summary>
public sealed class Ch32v003SimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => Scratch.Delete(_scratch);

    private const string Hello = """
        #include "ch32.h"
        int main(void)
        {
            pin_output(2, 0);                 /* PC0 */
            pin_write(2, 0, 1);
            usart_init(8000000, 115200);      /* HCLK 8 MHz after reset: HSI / 3 */
            usart_print("Hello\n");
            usart_flush();
            pin_write(2, 0, 0);
            return 0;
        }
        """;

    [Ch32v003Fact]
    public async Task A_program_starts_at_8_MHz_prints_on_USART1_and_drives_its_pins_from_an_ELF_a_HEX_or_a_bin()
    {
        var program = await Ch32v003CProgram.BuildAsync(Hello, _scratch);
        string[] images = [program.Elf, await program.ConvertAsync("ihex"), await program.ConvertAsync("ihex", flashAddresses: true),
            await program.ConvertAsync("binary")];

        foreach (var image in images)
        {
            var run = program.Run(50, image: image);

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Contains("the program has finished: the CPU waits for an interrupt (wfi) with interrupts off", run.Stopped,
                StringComparison.Ordinal);
            Assert.Equal(["Hello"], run.SerialLines);
            Assert.Equal("Hello\n", Encoding.ASCII.GetString(run.Serial));
            Assert.Equal(["USART1 baud 115942"], run.After("USART1 baud")); // 8 MHz / 69
            var pc0 = run.Pins.Where(p => p.Pin == "PC0").Select(p => p.State);
            Assert.Equal(["low", "high", "low"], pc0);
            // 6 bytes of 10 bits at 115942 baud: 0.52 ms
            Assert.InRange(run.Times("USART1: Hello").Single(), 0.5, 0.7);
            Assert.Contains("PD5 high", run.Output, StringComparison.Ordinal); // TX idles high
        }
    }

    [Ch32v003Fact]
    public async Task The_simulator_runs_millions_of_instructions_a_second()
    {
        // a busy loop at 48 MHz, from flash with its wait state: 200 ms of it
        var program = await Ch32v003CProgram.BuildAsync("""
            #include "ch32.h"
            volatile uint32_t out;
            int main(void)
            {
                clock_48mhz();
                uint32_t x = 1;
                for (;;) { x = x * 5 + 1; out = x; }
            }
            """, _scratch);
        Ch32v003Chip? chip = null;

        var clock = Stopwatch.StartNew();
        var run = program.Run(200, setUp: c => chip = (Ch32v003Chip)c);
        clock.Stop();

        Assert.True(run.ExitCode == 0, run.ToString());
        var instructions = chip!.Cpu.InstructionsRetired;
        var rate = instructions / clock.Elapsed.TotalSeconds;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{instructions:N0} instructions ({chip.Cpu.Cycles:N0} cycles, 200 ms at 48 MHz) in {clock.Elapsed.TotalMilliseconds:N0} ms: {rate / 1e6:0.0} million a second"));
        Assert.True(rate > 5e6, $"{rate:N0} instructions a second");
    }
}
