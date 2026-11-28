using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ChipSharp.Chips;
using ChipSharp.Simulators;
using ChipSharp.Simulators.Esp32c5;
using ChipSharp.Tests.Esp32c5;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// The ESP32-C5 in chipsim, running C programs clang builds for it (<see cref="Esp32c5CProgram"/>): SYSTIMER's alarms and
/// the CLINT's timer through the interrupt matrix and the CLIC to a handler and back (mret), vectored and not; GPIO's
/// outputs and its pin interrupts on an input timeline; UART0's output, baud rate and input with its RX interrupt; the
/// USB Serial/JTAG port's output and input; the watchdogs the ROM leaves running stopping the run, unless fed or turned
/// off; ebreak's failure stop, an exception without a trap handler, the ROM's functions; the CPU clock (PCR); an ESP
/// image; the riscv32 and esp32c5 runtimes' start-up, built from C#; the simulator's speed; and its addresses, which
/// are the SVD's and the chip file's.
/// </summary>
public sealed class Esp32c5SimulatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => Scratch.Delete(_scratch);

    /// <summary>What the interrupt tests share: the trap entry in CLIC mode, interrupts on.</summary>
    private const string Interrupts = """
        #include "c5.h"
        volatile uint32_t out;
        static inline void set_mtvec(void (*entry)(void)) { __asm__ volatile("csrw mtvec, %0" : : "r"((uint32_t)entry | 3)); }
        static inline void interrupts_on(void) { __asm__ volatile("csrsi mstatus, 8"); }
        static inline uint32_t mcause(void) { uint32_t c; __asm__ volatile("csrr %0, mcause" : "=r"(c)); return c; }
        static inline void wait(void) { __asm__ volatile("wfi"); }
        """;

    [Esp32c5Fact]
    public async Task A_periodic_SYSTIMER_alarm_comes_through_the_matrix_and_the_CLIC_and_mret_goes_back()
    {
        var program = await Esp32c5CProgram.BuildAsync(Interrupts + """
            volatile int count;
            volatile uint32_t first;
            __attribute__((interrupt("machine"), aligned(64))) void trap(void)
            {
                uint32_t cause = mcause();
                if ((cause & 0x80000FFF) != 0x80000000 + 17) __asm__ volatile("ebreak");
                REG(0x6000A06C) = 1;              /* SYSTIMER INT_CLR: target 0 */
                if (count++ == 0) first = cause;
                gpio_write(6, count & 1);
            }
            int main(void)
            {
                gpio_output(6);
                route(61, 17, 1, 0, 0);           /* SYSTIMER_TARGET0 to CLIC id 17, level 1, not vectored */
                set_mtvec(trap);
                REG(0x6000A034) = (1u << 30) | 16000; /* TARGET0_CONF: period mode, 1 ms, unit 0 */
                REG(0x6000A050) = 1;              /* COMP0_LOAD */
                REG(0x6000A064) = 1;              /* INT_ENA */
                REG(0x6000A000) |= 1u << 24;      /* TARGET0_WORK_EN */
                interrupts_on();
                while (count < 3) wait();
                REG(0x6000A064) = 0;
                out = first;
                report();
                return 0;
            }
            """, _scratch);

        var (run, values) = program.RunReports(100);

        Assert.True(run.ExitCode == 0 && run.Stopped.Contains("has finished", StringComparison.Ordinal), run.ToString());
        // the interrupt bit, MPP (machine) and MPIE (interrupts were on) as mstatus has them, MPIL 0, the id
        Assert.Equal([0x8000_0000UL | (3UL << 28) | (1UL << 27) | 17], values);
        var edges = run.Pins.Where(p => p.Pin == "GPIO6").ToList();
        Assert.Equal(["low", "high", "low", "high"], edges.Select(p => p.State));
        for (var k = 1; k <= 3; k++)
        {
            Assert.InRange(edges[k].Milliseconds, k, k + 0.01); // a few microseconds of entry at 48 MHz
        }
    }

    [Esp32c5Fact]
    public async Task The_CLINT_timer_takes_a_vectored_interrupt_through_mtvt()
    {
        var program = await Esp32c5CProgram.BuildAsync(Interrupts + """
            volatile int count;
            volatile uint32_t at;
            uint32_t table[48] __attribute__((aligned(64)));
            __attribute__((interrupt("machine"), aligned(64))) void trap(void) { __asm__ volatile("ebreak"); }
            __attribute__((interrupt("machine"))) void timer(void)
            {
                at = REG(0x2000BFF8);
                REG(0x20004004) = 0xFFFFFFFF;     /* MTIMECMP far away: the interrupt goes */
                REG(0x20004000) = 0xFFFFFFFF;
                count++;
            }
            int main(void)
            {
                table[7] = (uint32_t)timer;
                __asm__ volatile("csrw 0x307, %0" : : "r"(table)); /* mtvt */
                set_mtvec(trap);
                CLIC_ATTR(7) = 1;                 /* vectored */
                CLIC_CTL(7) = 0xE0;
                CLIC_IE(7) = 1;
                uint32_t now = REG(0x2000BFF8);
                REG(0x20004004) = 0;
                REG(0x20004000) = now + 8000;     /* half a millisecond at 16 MHz */
                interrupts_on();
                while (count == 0) wait();
                out = at - now;
                report();
                return 0;
            }
            """, _scratch);

        var (run, values) = program.RunReports(100);

        Assert.True(run.ExitCode == 0 && run.Stopped.Contains("has finished", StringComparison.Ordinal), run.ToString());
        Assert.InRange(Assert.Single(values), 8000UL, 8000UL + 160); // within 10 microseconds
    }

    [Esp32c5Fact]
    public async Task A_pin_interrupt_follows_the_input_s_edges_on_its_timeline()
    {
        var program = await Esp32c5CProgram.BuildAsync(Interrupts + """
            volatile int edges;
            __attribute__((interrupt("machine"), aligned(64))) void trap(void)
            {
                if ((mcause() & 0xFFF) != 18) __asm__ volatile("ebreak");
                uint32_t status = REG(0x60091074);
                REG(0x6009107C) = status;         /* GPIO_STATUS_W1TC */
                edges++;
                gpio_write(6, edges & 1);
            }
            int main(void)
            {
                gpio_output(6);
                gpio_output(8);
                REG(0x60091000 + 0xD4 + 4 * 7) = (3u << 7) | (1u << 13); /* GPIO7: both edges, to the CPU */
                route(31, 18, 2, 0, 0);
                set_mtvec(trap);
                interrupts_on();
                while (edges < 3) wait();
                REG(0x60091000 + 0xD4 + 4 * 7) = 0;  /* its interrupt off */
                while ((REG(0x60091064) & (1u << 7)) != 0) {} /* GPIO_IN: GPIO7 until it is low */
                gpio_write(8, 1);
                return 0;
            }
            """, _scratch);

        var run = program.Run(100, inputs: [new PinInput("GPIO7", true, 1), new PinInput("GPIO7", false, 2),
            new PinInput("GPIO7", true, 3), new PinInput("GPIO7", false, 4.5)]);

        Assert.True(run.ExitCode == 0 && run.Stopped.Contains("has finished", StringComparison.Ordinal), run.ToString());
        var edges = run.Pins.Where(p => p.Pin == "GPIO6").ToList();
        Assert.Equal(["low", "high", "low", "high"], edges.Select(p => p.State));
        for (var k = 1; k <= 3; k++)
        {
            Assert.InRange(edges[k].Milliseconds, k, k + 0.01);
        }

        var last = run.Pins.Last(p => p.Pin == "GPIO8");
        Assert.Equal("high", last.State);
        Assert.InRange(last.Milliseconds, 4.5, 4.51);
    }

    [Esp32c5Fact]
    public async Task An_input_reads_its_pad_s_pull_up_or_pull_down_and_what_drives_it_whatever_its_pulls()
    {
        var program = await Esp32c5CProgram.BuildAsync("""
            #include "c5.h"
            volatile uint32_t out;
            int main(void)
            {
                out = REG(0x60091064) & 0x3F0; report();                    /* GPIO4: its pull-up from reset */
                REG(0x60090000 + 4 * 4) &= ~(1u << 8);                       /* GPIO4: no pull */
                REG(0x60090000 + 4 * 5) |= 1u << 7;                          /* GPIO5: FUN_WPD */
                REG(0x60090000 + 4 * 6) |= 1u << 8;                          /* GPIO6: FUN_WPU */
                REG(0x60090000 + 4 * 8) |= (1u << 7);                        /* GPIO8: a pull-down, driven high */
                REG(0x60090000 + 4 * 9) |= (1u << 8);                        /* GPIO9: a pull-up, driven low */
                out = REG(0x60091064) & 0x3F0; report();
                return 0;
            }
            """, _scratch);

        var (run, values) = program.RunReports(10, 4);
        var inputs = (IReadOnlyList<PinInput>)[new("GPIO8", true, null), new("GPIO9", false, null)];
        var driven = Simulation.Run(new SimulationOptions { Chip = "esp32c5", Image = program.Elf, Milliseconds = 10, Inputs = inputs },
            setUp: chip => chip.Hooks = new Dictionary<int, Action<SimulatedChip>>
            {
                [(int)program.Address("report")] = c => values.Add(((Esp32c5Chip)c).Read(program.Address("out"), 4)),
            });

        Assert.True(run.ExitCode == 0 && driven.ExitCode == 0, run.ToString());
        Assert.Equal(0x10UL, values[0]); // only GPIO4's pull-up
        Assert.Equal(0x40UL | 0x200, values[1]); // GPIO6's and GPIO9's pull-ups; GPIO5's and GPIO8's pull-downs, GPIO4 floating
        Assert.Equal(0x40UL | 0x100, values[3]); // driven, GPIO8 high and GPIO9 low, whatever their pulls
        Assert.Contains(run.Pins, p => p is { Pin: "GPIO5", State: "input pull-down" });
        Assert.Contains(run.Pins, p => p is { Pin: "GPIO4", State: "input" });
    }

    [Esp32c5Fact]
    public async Task UART0_prints_its_baud_rate_and_lines_and_its_RX_interrupt_echoes_what_is_sent()
    {
        var program = await Esp32c5CProgram.BuildAsync(Interrupts + """
            volatile int done;
            __attribute__((interrupt("machine"), aligned(64))) void trap(void)
            {
                if ((mcause() & 0xFFF) != 19) __asm__ volatile("ebreak");
                while (REG(0x6000001C) & 0xFF)   /* STATUS: RXFIFO_CNT */
                {
                    char c = (char)REG(0x60000000);
                    uart_put(c >= 'a' && c <= 'z' ? (char)(c - 32) : c);
                    if (c == '\n') done = 1;
                }
                REG(0x60000010) = 1;              /* INT_CLR: RXFIFO_FULL */
            }
            int main(void)
            {
                REG(0x60096004) = (REG(0x60096004) & ~(0xFFu << 12)) | (1u << 12); /* PCR: XTAL / 2 */
                REG(0x60000014) = 2500;           /* CLKDIV: 24 MHz / 2500, 9600 baud */
                REG(0x60000098) = 1;              /* REG_UPDATE */
                while (REG(0x60000098) & 1) {}
                REG(0x60000024) = (REG(0x60000024) & ~0xFFu) | 1; /* RXFIFO_FULL_THRHD 1 */
                REG(0x6000000C) = 1;              /* INT_ENA: RXFIFO_FULL */
                route(47, 19, 1, 0, 0);
                set_mtvec(trap);
                interrupts_on();
                uart_print("ready\n");
                while (!done) wait();
                uart_flush();
                return 0;
            }
            """, _scratch);

        var run = program.Run(100, sends: [new SerialSend("abc\n"u8.ToArray(), 10)]);

        Assert.True(run.ExitCode == 0 && run.Stopped.Contains("has finished", StringComparison.Ordinal), run.ToString());
        Assert.Equal(["UART0 baud 9600"], run.After("UART0 baud"));
        Assert.Equal(["ready", "ABC"], run.SerialLines);
        Assert.Equal("ready\nABC\n", Encoding.ASCII.GetString(run.SerialOf("UART0")));
        Assert.InRange(run.Times("UART0: ready")[0], 6.2, 6.3); // 6 bytes of 10 bits at 9600 baud
        Assert.InRange(run.Times("UART0: ABC")[0], 10 + 4 * 1.04, 10 + 5 * 1.05); // the echo of the 4th byte
    }

    [Esp32c5Fact]
    public async Task The_USB_Serial_JTAG_port_sends_what_WR_DONE_flushes_and_reads_what_the_host_sends()
    {
        var program = await Esp32c5CProgram.BuildAsync("""
            #include "c5.h"
            int main(void)
            {
                usb_print("usb hi\n");
                char line[8];
                int n = 0;
                while (n < 3)
                {
                    if (REG(0x6000F004) & 4) line[n++] = (char)REG(0x6000F000); /* SERIAL_OUT_EP_DATA_AVAIL */
                }
                line[n] = 0;
                usb_print(line);
                usb_print("\n");
                REG(0x6000F000) = 'x';           /* never sent: no WR_DONE */
                return 0;
            }
            """, _scratch);

        var run = program.Run(100, sends: [new SerialSend("xyz"u8.ToArray(), 1, "USB")]);

        Assert.True(run.ExitCode == 0 && run.Stopped.Contains("has finished", StringComparison.Ordinal), run.ToString());
        Assert.Equal(["usb hi", "xyz"], run.SerialLines);
        Assert.Equal("usb hi\nxyz\n", Encoding.ASCII.GetString(run.SerialOf("USB")));
        Assert.Empty(run.SerialOf("UART0"));
        Assert.InRange(run.Times("USB: usb hi")[0], 0.05, 0.06); // the host takes the packet 50 microseconds on
    }

    [Esp32c5Theory]
    [InlineData("", 541.667, "the timer group 0 watchdog (MWDT0) reset the chip (stage 0: core reset)")]
    [InlineData("MWDT0", 900, "the super watchdog (SWD) reset the chip")]
    [InlineData("MWDT0 SWD", 2941.176, "the RTC watchdog (RWDT) reset the chip (stage 0: system reset)")]
    [InlineData("feed", 0, null)]
    public async Task The_watchdogs_the_ROM_leaves_running_stop_the_run_unless_turned_off_or_fed(string off, double at, string? why)
    {
        var program = await Esp32c5CProgram.BuildAsync($$"""
            #include "c5.h"
            typedef void (*delay_t)(uint32_t);
            void init(void)
            {
                if ({{(off.Contains("MWDT0", StringComparison.Ordinal) ? 1 : 0)}})
                {
                    REG(0x60008064) = WDT_KEY;
                    REG(0x60008048) = (REG(0x60008048) & ~(1u << 14)) | (1u << 22);
                    REG(0x60008064) = 0;
                }
                if ({{(off.Contains("SWD", StringComparison.Ordinal) ? 1 : 0)}})
                {
                    REG(0x600B1C20) = WDT_KEY;
                    REG(0x600B1C1C) |= 1u << 18;
                    REG(0x600B1C20) = 0;
                }
            }
            int main(void)
            {
                for (;;)
                {
                    if ({{(off == "feed" ? 1 : 0)}})
                    {
                        REG(0x60008064) = WDT_KEY; REG(0x60008060) = 1; REG(0x60008064) = 0;
                        REG(0x600B1C18) = WDT_KEY; REG(0x600B1C14) = 1u << 31; REG(0x600B1C18) = 0;
                        REG(0x600B1C20) = WDT_KEY; REG(0x600B1C1C) |= 1u << 31; REG(0x600B1C20) = 0;
                    }
                    ((delay_t)0x4000003C)(100000); /* the ROM's ets_delay_us: 100 ms */
                }
            }
            """, _scratch);

        var run = program.Run(4000);

        if (why is null)
        {
            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Equal("stopped: 4000 ms of simulated time have passed", run.Stopped);
            return;
        }

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.StartsWith("stopped: " + why, run.Stopped, StringComparison.Ordinal);
        Assert.Equal(at, run.Times("stopped:")[0], 0.002);
    }

    [Esp32c5Theory]
    [InlineData("__asm__ volatile(\"ebreak\");", 3, "the program hit a breakpoint (ebreak) at pc 0x408")]
    [InlineData("__asm__ volatile(\".word 0xffffffff\");", 3,
        "the program caused an illegal instruction 0xffffffff at pc 0x408")]
    [InlineData("((void (*)(void))0x40000100)();", 3, "the program called the ROM at 0x40000100 (from 0x408")]
    [InlineData("((void (*)(void))0x40000094)();", 3, "the program reset the chip (software_reset in the ROM")]
    [InlineData("REG(0x70000000) = 1;", 3, "the program caused a store access fault: nothing at 0x70000000, written at pc 0x408")]
    [InlineData("((int (*)(const char *, ...))0x40000024)(\"n=%d %s %04x\\n\", -42, \"ok\", 0xbe); ((void (*)(int))0x40000074)(0);",
        0, "the program has finished")]
    public async Task The_failure_stops_and_the_ROM_s_functions_say_what_happened(string statement, int exitCode, string why)
    {
        var program = await Esp32c5CProgram.BuildAsync($$"""
            #include "c5.h"
            int main(void)
            {
                {{statement}}
                return 0;
            }
            """, _scratch);

        var run = program.Run(100);

        Assert.True(run.ExitCode == exitCode, run.ToString());
        Assert.StartsWith("stopped: " + why, run.Stopped, StringComparison.Ordinal);
        if (exitCode == 0)
        {
            Assert.Equal(["n=-42 ok 00be"], run.SerialLines);
        }
    }

    [Esp32c5Fact]
    public async Task With_interrupts_on_and_none_to_come_wfi_ends_the_run_and_with_one_to_come_it_waits()
    {
        var program = await Esp32c5CProgram.BuildAsync(Interrupts + """
            __attribute__((interrupt("machine"), aligned(64))) void trap(void) { REG(0x6000A064) = 0; }
            int main(void)
            {
                set_mtvec(trap);
                route(61, 17, 1, 0, 0);
                REG(0x6000A020) = 16000 * 30;     /* TARGET0_LO: 30 ms on, target mode */
                REG(0x6000A050) = 1;
                REG(0x6000A064) = 1;
                REG(0x6000A000) |= 1u << 24;
                interrupts_on();
                for (;;) wait();
            }
            """, _scratch);

        var run = program.Run(100);

        // the alarm at 30 ms wakes it; its handler turns the alarm's interrupt off, and nothing is left to come
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("stopped: the program has finished: the CPU waits for an interrupt (wfi) that nothing can raise any more",
            run.Stopped);
        Assert.InRange(run.Times("stopped:")[0], 30, 30.01);
    }

    [Esp32c5Fact]
    public async Task PCR_s_clock_switch_makes_the_CPU_five_times_faster_at_240_MHz()
    {
        var program = await Esp32c5CProgram.BuildAsync("""
            #include "c5.h"
            volatile uint32_t out;
            static uint32_t timed(void)
            {
                uint32_t start = (uint32_t)systimer_now();
                for (volatile int i = 0; i < 20000; i++) {}
                return (uint32_t)systimer_now() - start;
            }
            int main(void)
            {
                out = timed(); report();
                REG(0x60096110) = (REG(0x60096110) & ~(3u << 16)) | (3u << 16); /* SOC_CLK_SEL: PLL_F240M */
                REG(0x60096118) = 0;              /* CPU_DIV_NUM */
                REG(0x60096144) = 1;              /* BUS_CLOCK_UPDATE */
                while (REG(0x60096144) & 1) {}
                out = timed(); report();
                return 0;
            }
            """, _scratch);

        var (run, values) = program.RunReports(1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Single(run.After("CPU clock 240 MHz"));
        Assert.Equal(2, values.Count);
        Assert.InRange((double)values[0] / values[1], 4.9, 5.1);
    }

    [Esp32c5Fact]
    public async Task The_simulator_runs_tens_of_millions_of_instructions_a_second()
    {
        var program = await Esp32c5CProgram.BuildAsync("""
            #include "c5.h"
            volatile uint32_t out;
            int main(void)
            {
                uint32_t a = 1, b = 0;
                for (int i = 0; i < 4000000; i++) { a = a * 1664525u + 1013904223u; b ^= a >> (i & 7); }
                out = b;
                report();
                return 0;
            }
            """, _scratch);
        program.Run(1); // warm up

        var watch = Stopwatch.StartNew();
        ulong instructions = 0;
        var run = program.Run(10_000, setUp: chip => chip.Log += _ => instructions = ((Esp32c5Chip)chip).Cpu.InstructionsRetired);
        watch.Stop();

        Assert.True(run.ExitCode == 0 && run.Stopped.Contains("has finished", StringComparison.Ordinal), run.ToString());
        var mips = instructions / watch.Elapsed.TotalSeconds / 1e6;
        var simulated = run.Times("stopped:")[0];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{instructions} instructions in {watch.Elapsed.TotalSeconds:F2} s: {mips:F1} million a second, " +
            $"{simulated:F1} ms of simulated time at 48 MHz ({simulated / watch.Elapsed.TotalMilliseconds:F2} x real time)"));
        Assert.True(instructions > 20_000_000, $"{instructions} instructions");
        Assert.True(mips > 5, $"{mips:F1} million instructions a second");
    }

    [Esp32c5Fact]
    public async Task An_ESP_image_runs_as_its_ELF_does_and_one_with_a_wrong_checksum_is_refused()
    {
        var program = await Esp32c5CProgram.BuildAsync("""
            #include "c5.h"
            int main(void) { uart_print("from the image\n"); uart_flush(); return 0; }
            """, _scratch);
        var image = EspImage(Esp32c5Image.Load(program.Elf));
        var path = Path.Combine(_scratch, "program.bin");
        await File.WriteAllBytesAsync(path, image);

        var run = Simulation.Run(new SimulationOptions { Chip = "esp32c5", Image = path, Milliseconds = 100 });

        Assert.True(run.ExitCode == 0 && run.Stopped.Contains("has finished", StringComparison.Ordinal), run.ToString());
        Assert.Equal(["from the image"], run.SerialLines);

        image[24 + 8] ^= 1; // a byte of the first segment: the checksum and the SHA-256 no longer agree
        await File.WriteAllBytesAsync(path, image);
        var error = Assert.Throws<ImageException>(() => Simulation.Run(new SimulationOptions { Chip = "esp32c5", Image = path }));
        Assert.Contains("checksum is wrong", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An ESP image of <paramref name="elf"/>'s segments, as esptool's format has it, with its SHA-256.</summary>
    private static byte[] EspImage(Esp32c5Image elf)
    {
        var bytes = new List<byte> { 0xE9, (byte)elf.Segments.Count, 2, 0x20 };
        bytes.AddRange(BitConverter.GetBytes(elf.Entry));
        bytes.AddRange([0xEE, 0, 0, 0, (byte)Esp32c5Image.EspChipId, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0, 1]);
        byte checksum = 0xEF;
        foreach (var segment in elf.Segments)
        {
            var data = segment.Bytes.Concat(new byte[(4 - segment.Bytes.Length % 4) % 4]).ToArray();
            bytes.AddRange(BitConverter.GetBytes(segment.Address));
            bytes.AddRange(BitConverter.GetBytes(data.Length));
            bytes.AddRange(data);
            checksum = data.Aggregate(checksum, (sum, b) => (byte)(sum ^ b));
        }

        while (bytes.Count % 16 != 15)
        {
            bytes.Add(0);
        }

        bytes.Add(checksum);
        bytes.AddRange(SHA256.HashData([.. bytes]));
        return [.. bytes];
    }

    [Esp32c5Fact]
    public void The_simulator_s_addresses_and_interrupts_are_Espressif_s_SVD_s_and_the_chip_file_s()
    {
        var svd = Svd.Read(Path.Combine(Repository.Root, "chips", "svd", "esp32c5.svd"));
        foreach (var (name, address) in Esp32c5Map.Peripherals)
        {
            var peripheral = svd.Peripherals.Single(p => p.Name == name);
            Assert.True(peripheral.Base == address, $"{name}: the simulator has 0x{address:x8}, the SVD 0x{peripheral.Base:x8}");
        }

        foreach (var (name, source) in Esp32c5Map.Interrupts)
        {
            Assert.True(svd.Interrupts.First(i => i.Name == name).Number == source, name);
        }

        var chip = ChipCatalog.Read(Path.Combine(Repository.Root, "chips", "esp32c5.json"));
        var map = chip.Peripherals.Single(p => p.Name == "INTERRUPT_CORE0");
        Assert.Equal((ulong)Esp32c5Map.InterruptMatrix, map.Base);
        Assert.Equal((ulong)Esp32c5Map.InterruptMatrix + 4 * Esp32c5Map.Uart0Source,
            map.Registers.Single(r => r.Name == "INTR_MAP" + Esp32c5Map.Uart0Source.ToString(CultureInfo.InvariantCulture)).Address);
        Assert.Equal((ulong)Esp32c5Map.Clic, chip.Peripherals.Single(p => p.Name == "CLIC").Base);
        var sram = chip.Memory.Single(m => m.Origin == Esp32c5Map.SramStart);
        Assert.InRange(sram.Origin + sram.Size, Esp32c5Map.SramStart, Esp32c5Map.SramEnd);

        var target = Esp32c5Build.Board;
        Assert.Equal(48, (int)target.Settings["vectors"]!);
        foreach (var (name, line) in new[] { ("timer", 17), ("pin", 18), ("uart", 19) })
        {
            Assert.Equal(line, (int)target.Settings["interrupts"]![name]!["irq"]!);
        }
    }

    [Esp32c5Fact]
    public void A_C_sharp_program_s_start_up_stops_the_watchdogs_and_runs_to_its_end_and_a_failure_stops_at_ebreak()
    {
        var ended = Esp32c5Build.Build("""
            class Program { static int Main() { int sum = 0; for (int i = 0; i < 100; i++) sum += i; return sum == 4950 ? 0 : 1; } }
            """, "ended", Path.Combine(_scratch, "ended"));
        var failed = Esp32c5Build.Build("""
            using System;
            class Program { static int Main() { Span<int> values = stackalloc int[3]; int i = 3; return values[i]; } }
            """, "failed", Path.Combine(_scratch, "failed"));

        var run = Simulation.Run(new SimulationOptions { Chip = "esp32c5", Image = ended.Elf, Milliseconds = 2000 });

        // past MWDT0's 0.54 s and the super watchdog's 0.9 s: the start-up stopped them, and the program ended at once
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.StartsWith("stopped: the program has finished", run.Stopped, StringComparison.Ordinal);
        Assert.InRange(run.Times("stopped:")[0], 0, 1);
        var chip = (Esp32c5Chip)run.Chip;
        Assert.Equal(0u, chip.Read(ended.File.Symbols["chipsharp_exit_code"], 4));

        run = Simulation.Run(new SimulationOptions { Chip = "esp32c5", Image = failed.Elf, Milliseconds = 100 });
        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.StartsWith("stopped: the program hit a breakpoint (ebreak)", run.Stopped, StringComparison.Ordinal);
    }

    [Esp32c5Fact]
    public async Task Chipsim_takes_the_ESP32_C5_s_pins_and_ports_and_says_what_it_does_not_have()
    {
        var program = await Esp32c5CProgram.BuildAsync("""
            #include "c5.h"
            int main(void) { gpio_output(5); gpio_write(5, 1); return 0; }
            """, _scratch);
        var text = Path.Combine(_scratch, "text.elf");
        await File.WriteAllTextAsync(text, "not an image");

        var ok = Command("esp32c5", program.Elf, "--ms", "10", "--input", "GPIO9=high");
        var noPin = Command("esp32c5", program.Elf, "--input", "GPIO29=high");
        var noPort = Command("esp32c5", program.Elf, "--send", "UART1=hi");
        var notAnImage = Command("esp32c5", text);
        var noImage = Command("esp32c5");

        Assert.True(ok.ExitCode == 0, ok.Output);
        Assert.Contains("] GPIO5 high", ok.Output, StringComparison.Ordinal);
        Assert.Contains("on the ESP32-C5 (", ok.Output, StringComparison.Ordinal);
        Assert.Equal(2, noPin.ExitCode);
        Assert.Contains("the ESP32-C5 has no pin GPIO29; it has GPIO0, GPIO1", noPin.Errors, StringComparison.Ordinal);
        Assert.Equal(2, noPort.ExitCode);
        Assert.Contains("the ESP32-C5 has no UART1; its serial ports: UART0, USB.", noPort.Errors, StringComparison.Ordinal);
        Assert.Equal(1, notAnImage.ExitCode);
        Assert.Contains("is neither an ELF file nor an ESP image", notAnImage.Errors, StringComparison.Ordinal);
        Assert.Equal(2, noImage.ExitCode);
        Assert.Contains("which image (.elf, or an ESP image)?", noImage.Errors, StringComparison.Ordinal);

        static (int ExitCode, string Output, string Errors) Command(params string[] arguments)
        {
            var (output, errors) = (new StringWriter(), new StringWriter());
            var exitCode = Simulation.Command(arguments, output, errors);
            return (exitCode, output.ToString(), errors.ToString());
        }
    }

    [Fact]
    public void An_image_with_bytes_where_the_chip_has_no_memory_is_refused()
    {
        var image = new Esp32c5Image { Segments = [new(0x3000_0000, [1, 2, 3, 4])], Entry = 0x3000_0000, Format = "ELF" };

        var error = Assert.Throws<ImageException>(() => new Esp32c5Chip(image));

        Assert.Contains("4 bytes at 0x30000000, which is not memory the ESP32-C5 loads", error.Message, StringComparison.Ordinal);
    }
}
