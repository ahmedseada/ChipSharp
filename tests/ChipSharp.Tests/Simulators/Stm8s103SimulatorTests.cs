using System.Text;
using ChipSharp.Simulators;
using ChipSharp.Tests.Sdcc;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// C programs built with SDCC for the stm8s103-board, run on the STM8S103 simulator: TIM4's interrupt and its timing,
/// UART1 both ways (by interrupt, at 115200 baud and at the reset clock's 9600), a pin's external interrupt waking the CPU,
/// TIM2's PWM with TIM1's update interrupt, the independent watchdog, the ways a run stops, and C's calls, recursion,
/// structs, strings and the library.
/// </summary>
public sealed class Stm8s103SimulatorTests : IDisposable
{
    /// <summary>The registers the programs use (RM0016, the STM8S103's datasheet).</summary>
    private const string Registers = """
        #include <stdint.h>
        #define REG(address) (*(volatile uint8_t *)(address))
        #define PB_ODR REG(0x5005)
        #define PB_DDR REG(0x5007)
        #define PB_CR1 REG(0x5008)
        #define PC_ODR REG(0x500a)
        #define PC_DDR REG(0x500c)
        #define PC_CR1 REG(0x500d)
        #define PD_ODR REG(0x500f)
        #define PD_DDR REG(0x5011)
        #define PD_CR1 REG(0x5012)
        #define PD_CR2 REG(0x5013)
        #define EXTI_CR1 REG(0x50a0)
        #define CLK_CKDIVR REG(0x50c6)
        #define IWDG_KR REG(0x50e0)
        #define UART1_SR REG(0x5230)
        #define UART1_DR REG(0x5231)
        #define UART1_BRR1 REG(0x5232)
        #define UART1_BRR2 REG(0x5233)
        #define UART1_CR2 REG(0x5235)
        #define TIM1_CR1 REG(0x5250)
        #define TIM1_IER REG(0x5254)
        #define TIM1_SR1 REG(0x5255)
        #define TIM1_PSCRH REG(0x5260)
        #define TIM1_PSCRL REG(0x5261)
        #define TIM1_ARRH REG(0x5262)
        #define TIM1_ARRL REG(0x5263)
        #define TIM2_CR1 REG(0x5300)
        #define TIM2_CCMR1 REG(0x5307)
        #define TIM2_CCER1 REG(0x530a)
        #define TIM2_PSCR REG(0x530e)
        #define TIM2_ARRH REG(0x530f)
        #define TIM2_ARRL REG(0x5310)
        #define TIM2_CCR1H REG(0x5311)
        #define TIM2_CCR1L REG(0x5312)
        #define TIM4_CR1 REG(0x5340)
        #define TIM4_IER REG(0x5343)
        #define TIM4_SR REG(0x5344)
        #define TIM4_PSCR REG(0x5347)
        #define TIM4_ARR REG(0x5348)

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
    public void Tim4s_update_interrupt_toggles_a_pin_every_millisecond()
    {
        var program = ChipProgram.Build("stm8s103-board", Registers + """
            volatile uint8_t ticks;
            void tim4(void) __interrupt(23) { TIM4_SR = 0; PC_ODR ^= 0x08; ticks++; }
            void main(void) {
              CLK_CKDIVR = 0;                 /* 16 MHz */
              PC_DDR = 0x08; PC_CR1 = 0x08;   /* PC3, push-pull */
              TIM4_PSCR = 7; TIM4_ARR = 124;  /* 16 MHz / 128 / 125: 1 kHz */
              TIM4_IER = 1; TIM4_CR1 = 1;
              __asm__("rim");
              while (ticks < 10) { __asm__("wfi"); }
              __asm__("sim\n halt");
            }
            """, _scratch);

        var run = program.Run(1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("stopped: the program has finished: the CPU halted (halt), and nothing is left to wake it " +
            "(no external interrupt to come)", run.Stopped);
        var times = run.Times("PC3");
        Assert.Equal(11, times.Count); // low as it becomes an output, then 10 toggles
        for (var i = 2; i < times.Count; i++)
        {
            Assert.InRange(times[i] - times[i - 1], 0.999, 1.001);
        }
    }

    [SdccFact]
    public void Uart1_receives_by_interrupt_and_sends_at_115200_baud()
    {
        var program = ChipProgram.Build("stm8s103-board", Registers + """
            volatile char line[16];
            volatile uint8_t length, lines;
            void rx(void) __interrupt(18) {
              char c = UART1_DR;
              if (c == '\n') { lines++; } else if (length < sizeof line) { line[length++] = c; }
            }
            void send(char c) { while (!(UART1_SR & 0x80)) {} UART1_DR = c; }
            void print(const char *s) { while (*s) send(*s++); }
            void main(void) {
              uint8_t i, done = 0;
              CLK_CKDIVR = 0;
              UART1_BRR2 = 0x0b; UART1_BRR1 = 0x08;  /* 16 MHz / 139 */
              UART1_CR2 = 0x2c;                      /* TX, RX, the RX interrupt */
              __asm__("rim");
              print("ready\n");
              while (done < 2) {
                if (lines) {
                  __asm__("sim");
                  for (i = 0; i < length; i++) send(line[i] >= 'a' && line[i] <= 'z' ? line[i] - 32 : line[i]);
                  send(' '); send('0' + length); send('\n');
                  length = 0; lines = 0; done++;
                  __asm__("rim");
                }
              }
              while (!(UART1_SR & 0x40)) {}
              __asm__("sim\n halt");
            }
            """, _scratch);

        var run = program.Run(1000, sends: [new SerialSend("hello\n"u8.ToArray(), 2), new SerialSend("world!\n"u8.ToArray(), 10)]);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["UART1 baud 115108", "UART1: ready", "UART1: HELLO 5", "UART1: WORLD! 6"], run.After("UART1"));
        Assert.Equal(["input: UART1 \"hello\\n\"", "input: UART1 \"world!\\n\""], run.After("input:"));
        Assert.Equal("ready\nHELLO 5\nWORLD! 6\n", Encoding.ASCII.GetString(run.Serial));
        // "ready\n": 6 bytes of 10 bits at 115108 baud
        Assert.InRange(run.Times("UART1: ready")[0], 0.52, 0.6); // after SDCC's start-up at the reset clock, 2 MHz
        // the reply goes after the line's last byte has arrived: 7 bytes after 10 ms, then 9 bytes out
        Assert.InRange(run.Times("UART1: WORLD!")[0], 10 + 6 * 10 / 115.108, 10 + 17 * 10 / 115.108);
    }

    [SdccFact]
    public void Uart1_at_the_reset_clock_sends_at_its_divider_of_2_MHz()
    {
        var program = ChipProgram.Build("stm8s103-board", Registers + """
            void send(char c) { while (!(UART1_SR & 0x80)) {} UART1_DR = c; }
            void main(void) {
              const char *s = "slow\n";
              UART1_BRR2 = 0x00; UART1_BRR1 = 0x0d;  /* fMASTER after reset, HSI / 8 = 2 MHz, / 208 */
              UART1_CR2 = 0x08;
              while (*s) send(*s++);
              while (!(UART1_SR & 0x40)) {}
              __asm__("sim\n halt");
            }
            """, _scratch);

        var run = program.Run(1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["UART1 baud 9615", "UART1: slow"], run.After("UART1"));
        Assert.InRange(run.Times("UART1: slow")[0], 5.19, 5.25); // 50 bits at 9615 baud
    }

    [SdccFact]
    public void A_pins_external_interrupt_wakes_the_CPU()
    {
        var program = ChipProgram.Build("stm8s103-board", Registers + """
            volatile uint8_t presses;
            void button(void) __interrupt(6) { presses++; PC_ODR ^= 0x10; }
            void main(void) {
              PC_DDR = 0x10; PC_CR1 = 0x10;   /* PC4, push-pull */
              PD_CR1 = 0x04; PD_CR2 = 0x04;   /* PD2: an input with its pull-up and its interrupt */
              EXTI_CR1 = 2 << 6;              /* port D: falling edges */
              __asm__("rim");
              while (presses < 2) { __asm__("wfi"); }
              __asm__("halt");                /* PD2 could wake it: until its last input */
            }
            """, _scratch);

        var run = program.Run(100,
            [new PinInput("PD2", false, 5), new PinInput("PD2", true, 6), new PinInput("PD2", false, 9), new PinInput("PD2", true, 12)]);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["PC4 low", "PC4 high", "PC4 low"], run.After("PC4"));
        var times = run.Times("PC4");
        Assert.InRange(times[1], 5, 5.05);
        Assert.InRange(times[2], 9, 9.05);
        Assert.StartsWith("stopped: the program has finished: the CPU halted (halt)", run.Stopped, StringComparison.Ordinal);
        Assert.InRange(run.Times("stopped")[0], 12, 12.05); // the rising edge at 12 ms wakes nothing
    }

    [SdccFact]
    public void Tim2s_PWM_drives_its_pin_while_tim1s_update_interrupt_counts()
    {
        var program = ChipProgram.Build("stm8s103-board", Registers + """
            volatile uint8_t updates;
            void tim1(void) __interrupt(11) { TIM1_SR1 = 0; updates++; }
            void main(void) {
              CLK_CKDIVR = 0;
              TIM2_PSCR = 4;                            /* 16 MHz / 16: 1 MHz */
              TIM2_ARRH = 999 >> 8; TIM2_ARRL = 999 & 0xff;
              TIM2_CCR1H = 0; TIM2_CCR1L = 250;         /* 25% */
              TIM2_CCMR1 = 0x68;                        /* PWM mode 1, preloaded */
              TIM2_CCER1 = 0x01;                        /* channel 1 on PD4 */
              TIM2_CR1 = 0x81;
              TIM1_PSCRH = 15999 >> 8; TIM1_PSCRL = 15999 & 0xff;  /* 16 MHz / 16000: 1 kHz */
              TIM1_ARRH = 0; TIM1_ARRL = 9;             /* an update every 10 ms */
              TIM1_IER = 1; TIM1_CR1 = 1;
              __asm__("rim");
              while (updates < 3) { __asm__("wfi"); }
              __asm__("sim\n halt");
            }
            """, _scratch);

        var run = program.Run(1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("PD4 PWM 1000.00 Hz, duty 25.00% (250.00 us high)", run.After("PD4"));
        // the prescaler written is loaded at the first update, 10 counts of 16 MHz after TIM1 starts (as on the chip,
        // without UG): then every 10 ms
        Assert.InRange(run.Times("stopped")[0], 20, 20.1);
    }

    [SdccFact]
    public void The_independent_watchdog_resets_a_program_that_does_not_refresh_it()
    {
        var program = ChipProgram.Build("stm8s103-board", Registers + """
            void main(void) { IWDG_KR = 0xcc; for (;;) {} }
            """, _scratch);

        var run = program.Run(1000);

        Assert.Equal(3, run.ExitCode);
        Assert.StartsWith("stopped: the independent watchdog reset the chip", run.Stopped, StringComparison.Ordinal);
        Assert.InRange(run.Times("stopped")[0], 7.9, 8.1); // 256 counts of the LSI / 4: 8 ms
    }

    [SdccTheory]
    [InlineData("__asm__(\"break\");", 3, "stopped: the program hit a breakpoint (break) at pc 0x")]
    [InlineData("TIM4_IER = 1; TIM4_CR1 = 1; __asm__(\"rim\"); for (;;) {}", 3,
        "stopped: interrupt 23 (TIM4 update) was requested, but its vector at 0x8064 has no handler")]
    [InlineData("((void (*)(void))0x9000)();", 3, "stopped: the program ran into flash the image does not fill, at pc 0x9000")]
    [InlineData("return;", 3, "stopped: the program popped more than it pushed")]
    [InlineData("__asm__(\"wfi\");", 0, "stopped: the program has finished: the CPU waits for an interrupt (wfi) that nothing can raise any more")]
    public void The_ways_a_run_stops(string statement, int exitCode, string stopped)
    {
        var program = ChipProgram.Build("stm8s103-board", Registers + $$"""
            void main(void) { PB_DDR = 0x20; PB_ODR = 0x00; {{statement}} for (;;) {} }
            """, _scratch);

        var run = program.Run(1000);

        Assert.True(exitCode == run.ExitCode, run.ToString());
        Assert.StartsWith(stopped, run.Stopped, StringComparison.Ordinal);
        Assert.Equal(["PB5 low"], run.After("PB5")); // the board's LED, on
    }

    [SdccFact]
    public void Calls_recursion_structs_strings_and_the_library_run()
    {
        var program = ChipProgram.Build("stm8s103-board", """
            #include <stdint.h>
            #include <string.h>
            #include <stdlib.h>
            volatile uint32_t out;
            void report(void);
            struct point { int16_t x, y; };
            static struct point add(struct point a, struct point b) { struct point r; r.x = a.x + b.x; r.y = a.y + b.y; return r; }
            uint32_t fib(uint8_t n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
            int32_t accumulate(int32_t (*f)(int32_t), int32_t x, uint8_t times) { while (times--) x = f(x); return x; }
            int32_t triple_minus_one(int32_t x) { return 3 * x - 1; }
            int compare(const void *a, const void *b) { return *(const int16_t *)a - *(const int16_t *)b; }
            const char *days[] = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            void main(void) {
              struct point p = { 3, -4 }, q = { 100, 200 }, r;
              int16_t numbers[] = { 42, -7, 1000, 3, -300, 0, 17 };
              char text[24];
              uint8_t i;
              for (i = 0; i < 20; i += 4) { out = fib(i); report(); }
              r = add(p, q); out = (uint16_t)r.x; report(); out = (uint16_t)r.y; report();
              out = accumulate(triple_minus_one, 2, 10); report();
              qsort(numbers, 7, sizeof numbers[0], compare);
              for (i = 0; i < 7; i++) { out = (uint16_t)numbers[i]; report(); }
              strcpy(text, days[2]); strcat(text, "+"); strcat(text, days[4]);
              out = strlen(text); report();
              out = strcmp(days[0], days[1]) < 0; report();
              memset(text, 'x', 5); text[5] = 0;
              out = atoi("-12345"); report();
              out = text[4] + text[6]; report();
              __asm__("sim\n halt");
            }
            """, _scratch);

        var (run, values) = program.RunReports(5000, 4);

        Assert.True(run.ExitCode == 0, run.ToString());
        ulong[] fibs = [0, 3, 21, 144, 987];
        long accumulated = 2;
        for (var i = 0; i < 10; i++)
        {
            accumulated = 3 * accumulated - 1;
        }

        short[] sorted = [-300, -7, 0, 3, 17, 42, 1000];
        ulong[] expected =
        [
            .. fibs, 103, 196, (ulong)(uint)(int)accumulated, .. sorted.Select(n => (ulong)(ushort)n), 7, 1,
            unchecked((uint)-12345), 'x' + 'i',
        ];
        Assert.Equal(expected, values);
    }
}
