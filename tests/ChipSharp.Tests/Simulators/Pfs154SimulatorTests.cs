using ChipSharp.Simulators;
using ChipSharp.Simulators.Pdk14;
using ChipSharp.Tests.Sdcc;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// C programs built with SDCC for the pfs154-bare board, run on the PFS154 simulator: Timer16's interrupt and its
/// timing, inputs on a timeline with the pull-ups, Timer2's PWM and Timer3's period mode, PA0's interrupt waking the
/// chip from stopexe, the ways a run stops (stopsys, the watchdog, reset, memory the image does not fill), and calls,
/// recursion, switches, pointers and tables in program memory.
/// </summary>
public sealed class Pfs154SimulatorTests : IDisposable
{
    /// <summary>The registers, and the start-up every program has: the watchdog off, IHRC / 8 (2 MHz), as the board's.</summary>
    private const string Prelude = """
        #include <stdint.h>
        __sfr __at(0x03) CLKMD;
        __sfr __at(0x04) INTEN;
        __sfr __at(0x05) INTRQ;
        __sfr __at(0x06) T16M;
        __sfr __at(0x09) TM2B;
        __sfr __at(0x0c) INTEGS;
        __sfr __at(0x10) PA;
        __sfr __at(0x11) PAC;
        __sfr __at(0x12) PAPH;
        __sfr __at(0x14) PB;
        __sfr __at(0x15) PBC;
        __sfr __at(0x17) TM2S;
        __sfr __at(0x1c) TM2C;
        __sfr __at(0x32) TM3C;
        __sfr __at(0x34) TM3S;
        __sfr __at(0x35) TM3B;
        unsigned char __sdcc_external_startup(void) { CLKMD = 0x3c; return 0; }

        """;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [SdccFact]
    public void Timer16s_interrupt_toggles_a_pin_every_time_its_bit_rises()
    {
        var program = ChipProgram.Build("pfs154-bare", Prelude + """
            volatile uint8_t ticks;
            void interrupt(void) __interrupt(0) { if (INTRQ & 4) { INTRQ = 0; PA ^= 0x10; ticks++; } }
            void main(void) {
              PAC = 0x10;
              T16M = (4 << 5) | (2 << 3) | 3; /* the IHRC / 16, 1 MHz: bit 11 rises every 4096 us */
              INTEN = 4;
              __asm__("engint");
              while (ticks < 10) {}
              __asm__("disgint\n stopsys");
            }
            """, _scratch);

        var run = program.Run(1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("stopped: the program has finished: stopsys stopped the clocks, and no input is left to wake the chip", run.Stopped);
        Assert.Equal(["PA4 low", "PA4 high", "PA4 low", "PA4 high", "PA4 low", "PA4 high", "PA4 low", "PA4 high", "PA4 low",
            "PA4 high", "PA4 low"], run.After("PA4"));
        var times = run.Times("PA4");
        Assert.InRange(times[1] - times[0], 2.04, 2.06); // the first rise: 2048 counts after T16M is set
        for (var i = 2; i < times.Count; i++)
        {
            Assert.InRange(times[i] - times[i - 1], 4.090, 4.102);
        }
    }

    [SdccFact]
    public void Inputs_on_a_timeline_and_the_pull_ups_reach_the_program()
    {
        var program = ChipProgram.Build("pfs154-bare", Prelude + """
            void main(void) {
              PAC = 0x50;   /* PA4 and PA6 outputs */
              PAPH = 0x08;  /* PA3 with its pull-up; PB0 without */
              for (;;) { PA = (PA & 0x08 ? 0x10 : 0) | (PB & 0x01 ? 0x40 : 0); }
            }
            """, _scratch);

        var run = program.Run(30, [new PinInput("PA3", false, 10), new PinInput("PB0", true, 15), new PinInput("PA3", true, 20)]);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("stopped: 30 ms of simulated time have passed", run.Stopped);
        Assert.Equal(["PA3 input pull-up"], run.After("PA3"));
        Assert.Equal(["input: PA3 low", "input: PB0 high", "input: PA3 high"], run.After("input:"));
        // PA4 follows PA3 (high by its pull-up at first), PA6 follows PB0 (low with no pull-up)
        Assert.Equal(["PA4 low", "PA4 high", "PA4 low", "PA4 high"], run.After("PA4"));
        Assert.Equal(["PA6 low", "PA6 high"], run.After("PA6"));
        var pa4 = run.Times("PA4");
        Assert.InRange(pa4[2], 10, 10.05);
        Assert.InRange(pa4[3], 20, 20.05);
        Assert.InRange(run.Times("PA6")[1], 15, 15.05);
    }

    [SdccFact]
    public void Timer2s_PWM_and_timer3s_period_mode_drive_their_pins_and_timer3_interrupts()
    {
        var program = ChipProgram.Build("pfs154-bare", Prelude + """
            volatile uint8_t ticks;
            void interrupt(void) __interrupt(0) { if (INTRQ & 0x80) { INTRQ = 0; ticks++; } }
            void main(void) {
              TM2B = 64; TM2S = 0;                   /* 8-bit PWM, no prescaler: 16 MHz / 256, 25% high */
              TM2C = (2 << 4) | (3 << 2) | 0x02;     /* the IHRC, on PB4, PWM mode */
              TM3B = 124; TM3S = 3 << 5;             /* 16 MHz / 64 / 125: toggles every 0.5 ms */
              TM3C = (2 << 4) | (2 << 2);            /* the IHRC, on PB6, period mode */
              INTEN = 0x80;
              __asm__("engint");
              while (ticks < 20) {}
              __asm__("disgint\n stopsys");     /* the clocks stop: the timers and their pins too */
            }
            """, _scratch);

        var run = program.Run(1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.StartsWith("stopped: the program has finished: stopsys stopped the clocks", run.Stopped, StringComparison.Ordinal);
        Assert.Contains("PB4 PWM 62500.00 Hz, duty 25.00% (4.00 us high)", run.After("PB4"));
        Assert.Contains("PB6 PWM 1000.00 Hz, duty 50.00% (500.00 us high)", run.After("PB6"));
        Assert.DoesNotContain(run.After("PB4"), line => line is "PB4 high" or "PB4 low"); // measured, not printed
        // 20 of timer3's interrupts, each at its bound: 10 ms
        Assert.InRange(run.Times("stopped")[0], 10, 10.2);
    }

    [SdccFact]
    public void PA0s_interrupt_wakes_the_chip_from_stopexe()
    {
        var program = ChipProgram.Build("pfs154-bare", Prelude + """
            volatile uint8_t presses;
            void interrupt(void) __interrupt(0) { if (INTRQ & 1) { INTRQ = 0; presses++; PA ^= 0x10; } }
            void main(void) {
              PAC = 0x10;
              PAPH = 0x01;       /* PA0, a button to ground, with its pull-up */
              INTEGS = 2;        /* PA0's falling edges */
              INTRQ = 0;
              INTEN = 1;
              __asm__("engint");
              while (presses < 3) { __asm__("stopexe"); }
              __asm__("disgint\n stopsys");
            }
            """, _scratch);

        var run = program.Run(100,
        [
            new PinInput("PA0", false, 5), new PinInput("PA0", true, 6), new PinInput("PA0", false, 10),
            new PinInput("PA0", true, 11), new PinInput("PA0", false, 15),
        ]);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["PA4 low", "PA4 high", "PA4 low", "PA4 high"], run.After("PA4"));
        var times = run.Times("PA4");
        Assert.InRange(times[1], 5, 5.05);
        Assert.InRange(times[2], 10, 10.05);
        Assert.InRange(times[3], 15, 15.05);
        Assert.StartsWith("stopped: the program has finished: stopsys", run.Stopped, StringComparison.Ordinal);
    }

    [SdccFact]
    public void The_watchdog_resets_a_program_that_leaves_it_on()
    {
        var program = ChipProgram.Build("pfs154-bare", """
            void main(void) { for (;;) {} }
            """, _scratch);

        var run = program.Run(1000);

        Assert.Equal(3, run.ExitCode);
        Assert.StartsWith("stopped: the watchdog reset the chip", run.Stopped, StringComparison.Ordinal);
        Assert.InRange(run.Times("stopped")[0], 148.5, 149.5); // 8192 ILRC clocks of 54.98 kHz
    }

    [SdccTheory]
    [InlineData("__asm__(\"reset\");", "stopped: the program reset the chip (reset) at pc 0x")]
    // a function pointer holds a word address on the PDK14: word 0x380 is byte 0x0700
    [InlineData("((void (*)(void))0x0380)();", "stopped: the program ran into program memory the image does not fill, at pc 0x0700 (word 0x380)")]
    public void A_program_that_goes_wrong_stops_the_run_as_a_failure(string statement, string stopped)
    {
        var program = ChipProgram.Build("pfs154-bare", Prelude + $$"""
            void main(void) { PAC = 0x10; PA = 0x10; {{statement}} for (;;) {} }
            """, _scratch);

        var run = program.Run(1000);

        Assert.Equal(3, run.ExitCode);
        Assert.StartsWith(stopped, run.Stopped, StringComparison.Ordinal);
        Assert.Equal(["PA4 low", "PA4 high"], run.After("PA4"));
    }

    [SdccFact]
    public void Calls_recursion_switches_pointers_and_tables_in_program_memory_run()
    {
        var program = ChipProgram.Build("pfs154-bare", Prelude + """
            #include <string.h>
            volatile uint32_t out;
            void report(void);
            uint16_t fib(uint8_t n) __reentrant { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
            uint8_t classify(uint8_t x) {
              switch (x) { case 0: return 10; case 1: return 20; case 2: return 30; case 3: return 45;
                           case 4: return 50; case 5: return 61; case 6: return 77; default: return 99; }
            }
            uint8_t twice(uint8_t x) __reentrant { return x * 2; } /* called through a pointer: reentrant, on the PDK14 */
            uint8_t plus3(uint8_t x) __reentrant { return x + 3; }
            static const uint8_t squares[] = { 0, 1, 4, 9, 16, 25, 36, 49 };
            static const char text[] = "Padauk";
            uint8_t buffer[8];
            void main(void) {
              uint8_t i, *p = buffer;
              uint8_t (*operation)(uint8_t) __reentrant;
              for (i = 0; i < 9; i++) { out = fib(i); report(); }
              for (i = 0; i < 9; i++) { out = classify(i); report(); }
              for (i = 0; i < 8; i++) { operation = i & 1 ? plus3 : twice; out = operation(i); report(); }
              for (i = 0; i < 8; i++) { *p++ = squares[i] + i; }
              for (i = 0, out = 0; i < 8; i++) { out += buffer[7 - i] * (i + 1); }
              report();
              out = strlen(text); report();
              out = text[2]; report();
              out = ((uint8_t (*)(void))0x07ed)(); report(); /* the factory's IHRC trim: a ret k at word 0x7ed */
              __asm__("stopsys");
            }
            """, _scratch);

        var (run, values) = program.RunReports(1000, 4);

        Assert.True(run.ExitCode == 0, run.ToString());
        ulong[] fib = [0, 1, 1, 2, 3, 5, 8, 13, 21];
        ulong[] classes = [10, 20, 30, 45, 50, 61, 77, 99, 99];
        var operations = Enumerable.Range(0, 8).Select(i => (ulong)(i % 2 == 1 ? i + 3 : i * 2));
        var weighted = (ulong)Enumerable.Range(0, 8).Sum(i => (i * i + i) * (8 - i));
        Assert.Equal([.. fib, .. classes, .. operations, weighted, 6, 'd', Pfs154.FactoryTrim], values);
    }
}
