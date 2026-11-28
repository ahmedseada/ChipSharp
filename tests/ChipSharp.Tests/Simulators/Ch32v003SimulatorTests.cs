using System.Diagnostics;
using System.Globalization;
using System.Text;
using ChipSharp.Simulators;
using ChipSharp.Simulators.Ch32v003;
using ChipSharp.Tests.Ch32v003;
using ChipSharp.Tests.Pico;
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

    /// <summary>
    /// The start-up of ChipSharp's ch32v003 target (milestone M5, wave 0), in its shape: linked at 0x0800_0000, words 0 and
    /// 1 lui and jalr to the start-up from 0, where the chip boots (the flash's alias); mtvec 0x0800_0003 (the table of
    /// addresses at the flash itself, exceptions through entry 3); INTSYSCR 0 (no HPE, no nesting: a handler saves its
    /// registers itself); gp; sp at 0x2000_0800; 48 MHz from the PLL with the flash's wait state; then main with
    /// interrupts on. Its .bin from 0x0800_0000 (the image format bin's) runs as its ELF does.
    /// </summary>
    [Ch32v003Fact]
    public async Task The_target_s_start_up_linked_at_0x08000000_runs_from_the_alias_at_0_and_its_table_at_the_flash()
    {
        const string startUp = """
                .section .init, "ax"
                .global _start
                .option push
                .option norvc
            _start:
                lui t0, %hi(reset)          /* 0 and 1: from the alias at 0 to the flash at 0x08000000 */
                jalr zero, %lo(reset)(t0)
                .word unexpected            /* 2 NMI */
                .word unexpected            /* 3 EXC */
                .fill 8, 4, 0
                .word isr_systick           /* 12 */
                .fill 26, 4, 0              /* 13-38 */
                .option pop

                .text
                .weak isr_systick
            isr_systick:
            unexpected:
                ebreak
            reset:
                .option push
                .option norelax
                la gp, __global_pointer$
                .option pop
                li sp, 0x20000800
                la a0, __bss_start
                la a1, __bss_end
            1:  bgeu a0, a1, 2f
                sw zero, 0(a0)
                addi a0, a0, 4
                j 1b
            2:  li t0, 0x08000003
                csrw mtvec, t0
                csrw 0x804, zero
                li a0, 0x40022000
                li a1, 1
                sw a1, 0(a0)                /* FLASH_ACTLR: LATENCY 1 */
                li a0, 0x40021000
                lw a1, 4(a0)
                li a2, 0xFFFEFF0F
                and a1, a1, a2
                sw a1, 4(a0)                /* RCC_CFGR0: HPRE /1, PLLSRC HSI */
                lw a1, 0(a0)
                li a2, 0x01000000
                or a1, a1, a2
                sw a1, 0(a0)                /* RCC_CTLR: PLLON */
            3:  lw a1, 0(a0)
                srli a1, a1, 25
                andi a1, a1, 1
                beqz a1, 3b                 /* PLLRDY */
                lw a1, 4(a0)
                andi a1, a1, -4
                ori a1, a1, 2
                sw a1, 4(a0)                /* SW: the PLL */
            4:  lw a1, 4(a0)
                andi a1, a1, 0xC
                li a2, 8
                bne a1, a2, 4b              /* SWS: the PLL */
                csrsi mstatus, 8
                call main
                .global halt
            halt:
                csrci mstatus, 8
            5:  wfi
                j 5b
            """;
        const string linkerScript = """
            MEMORY
            {
              flash (rx) : ORIGIN = 0x08000000, LENGTH = 16K
              ram (rwx) : ORIGIN = 0x20000000, LENGTH = 2K
            }
            ENTRY(_start)
            SECTIONS
            {
              .init : { KEEP(*(.init)) } > flash
              .text : { *(.text*) *(.rodata*) *(.srodata*) . = ALIGN(4); } > flash
              .bss (NOLOAD) : { . = ALIGN(4); __bss_start = .; *(.bss*) *(.sbss*) *(COMMON) . = ALIGN(4); __bss_end = .; } > ram
              __global_pointer$ = ORIGIN(ram) + 0x800;
            }
            """;
        var program = await Ch32v003CProgram.BuildAsync("""
            #include "ch32.h"
            volatile uint32_t out;
            volatile int count;
            __attribute__((interrupt("machine"))) void isr_systick(void)
            {
                STK_SR = 0;
                if (++count == 2) STK_CTLR = 0;
            }
            int main(void)
            {
                uint32_t mtvec, intsyscr;
                __asm__ volatile("csrr %0, mtvec" : "=r"(mtvec));
                __asm__ volatile("csrr %0, 0x804" : "=r"(intsyscr));
                usart_init(48000000, 115200);
                usart_print("up\n");
                STK_CMPR = 48000 - 1;
                STK_CTLR = 0xF;
                PFIC_IENR(12) = 1u << 12;
                while (count < 2) wait();
                usart_flush();
                out = mtvec;
                report();
                out = intsyscr;
                report();
                return 0;
            }
            """, _scratch, startUp: startUp, linkerScript: linkerScript);
        Assert.Equal(0x0800_0000u, Ch32v003Image.Load(program.Elf).Symbols["_start"]);

        foreach (var image in new[] { program.Elf, await program.ConvertAsync("binary") })
        {
            var reports = new List<uint>();
            var run = program.Run(20, image: image, setUp: chip => chip.Hooks = new Dictionary<int, Action<SimulatedChip>>
            {
                [(int)program.Address("report")] = c => reports.Add(((Ch32v003Chip)c).Read(program.Address("out"), 4)),
            });

            Assert.True(run.ExitCode == 0 && run.Stopped.Contains("has finished", StringComparison.Ordinal), run.ToString());
            Assert.Equal(["CPU clock 24 MHz", "CPU clock 48 MHz"], run.After("CPU clock")); // HPRE /1 first, then the PLL
            Assert.Equal(["up"], run.SerialLines);
            Assert.Equal([0x0800_0003u, 0], reports);
        }
    }

    /// <summary>
    /// C# built for ch32v003-evt as the build pipeline builds it (the target's start-up, the riscv32 runtime on RV32E,
    /// its multiply and divide helpers): it runs from 0 to its end at 48 MHz, the start-up's clock, its .bin as its ELF;
    /// what it writes (the console, on USART1) is what .NET writes; and a failure stops at ebreak, saying why first.
    /// </summary>
    [Ch32v003Fact]
    public void A_C_sharp_program_runs_its_target_s_start_up_to_its_end_and_a_failure_stops_at_ebreak()
    {
        var ended = Ch32v003Build.Build("""
            using System;
            static class Program
            {
                static volatile int seven = 7;
                static int Main()
                {
                    long big = 1234567L * (89 + seven - 7);
                    Console.Write(big / 13);
                    Console.Write(' ');
                    Console.Write(7.25 / (seven - 5));
                    Console.Write(' ');
                    Console.Write(-1234567 % (1000 + seven - 7));
                    return 0;
                }
            }
            """, "ended", Path.Combine(_scratch, "ended"));
        var failed = Ch32v003Build.Build("""
            using System;
            class Program { static int Main() { Span<int> values = stackalloc int[3]; int i = 3; return values[i]; } }
            """, "failed", Path.Combine(_scratch, "failed"));
        var bin = Path.Combine(_scratch, "ended.bin");
        File.WriteAllBytes(bin, ChipSharp.Image.Bin.BinImageFormat.Make(ended.File, Ch32v003Build.Board, bin));

        foreach (var image in new[] { ended.Elf, bin })
        {
            var run = Simulation.Run(new SimulationOptions { Chip = "ch32v003", Image = image, Milliseconds = 200 });

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.StartsWith("stopped: the program has finished", run.Stopped, StringComparison.Ordinal);
            Assert.Equal(["CPU clock 24 MHz", "CPU clock 48 MHz"], run.After("CPU clock")); // HPRE /1 first, then the PLL
            var chip = (Ch32v003Chip)run.Chip;
            Assert.Equal(0u, chip.Read(ended.File.Symbols["chipsharp_exit_code"], 4));
            Assert.Equal($"{1234567L * 89 / 13} {7.25 / 2} {-1234567 % 1000}", Encoding.ASCII.GetString(run.SerialOf("USART1")));
        }

        var failure = Simulation.Run(new SimulationOptions { Chip = "ch32v003", Image = failed.Elf, Milliseconds = 100 });
        Assert.True(failure.ExitCode == 3, failure.ToString());
        Assert.StartsWith("stopped: the program hit a breakpoint (ebreak)", failure.Stopped, StringComparison.Ordinal);
        Assert.Equal("Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds of the array.\r\n",
            Encoding.ASCII.GetString(failure.SerialOf("USART1")));

        // chipsharp build -t ch32v003-evt: its Name.bin, as chipsharp flash writes it; a wrong sum would fail at ebreak
        var dll = Path.Combine(_scratch, "Sums.dll");
        var expected = Enumerable.Range(1, 1000).Sum(i => (long)i * i / 3);
        File.WriteAllBytes(dll, CSharpCompiler.Compile([$$"""
            class Program
            {
                static void Main()
                {
                    long sum = 0;
                    for (int i = 1; i <= 1000; i++) sum += (long)i * i / 3;
                    System.Span<int> one = stackalloc int[1];
                    one[sum == {{expected}}L ? 0 : 1] = 1; // out of its range, a failure, unless the sum is right
                }
            }
            """], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], "Sums"));
        var built = ChipSharp.Build.ChipSharpBuilder.Build(dll, new ChipSharp.Build.BuildOptions
        {
            Target = "ch32v003-evt",
            TargetsFolder = Path.Combine(AppContext.BaseDirectory, "targets"),
            OutputFolder = Path.Combine(_scratch, "out"),
        });
        Assert.EndsWith("Sums.bin", built.OutputPath, StringComparison.Ordinal);
        var sums = Simulation.Run(new SimulationOptions { Chip = "ch32v003", Image = built.OutputPath, Milliseconds = 200 });
        Assert.True(sums.ExitCode == 0, sums.ToString());
        Assert.StartsWith("stopped: the program has finished", sums.Stopped, StringComparison.Ordinal);
        Assert.Contains("CPU clock 48 MHz", sums.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_simulator_s_addresses_interrupts_memories_and_pins_are_WCH_s_SVD_s_and_the_chip_and_target_files()
    {
        var svd = ChipSharp.Chips.Svd.Read(Path.Combine(Repository.Root, "chips", "svd", "CH32V003xx.svd"));
        foreach (var (name, address) in Ch32v003Map.Peripherals)
        {
            var peripheral = svd.Peripherals.Single(p => p.Name == name);
            Assert.True(peripheral.Base == address, $"{name}: the simulator has 0x{address:x8}, the SVD 0x{peripheral.Base:x8}");
        }

        foreach (var (name, number) in Ch32v003Map.Interrupts)
        {
            Assert.True(svd.Interrupts.First(i => i.Name == name).Number == number, name);
        }

        Assert.Equal((ulong)Ch32v003Map.SysTick, svd.Registers.Single(r => r.Name == "STK_CTLR").Address);
        var chip = ChipSharp.Chips.ChipCatalog.Read(Path.Combine(Repository.Root, "chips", "ch32v003.json"));
        Assert.Equal((Ch32v003Map.SysTickNumber, Ch32v003Map.SoftwareNumber),
            (chip.Interrupts.Single(i => i.Name == "SysTick").Number, chip.Interrupts.Single(i => i.Name == "SW").Number));
        Assert.Equal(((ulong)Ch32v003Map.FlashStart, (ulong)Ch32v003Map.FlashSize),
            (chip.Memory.Single(m => m.Name == "flash").Origin, chip.Memory.Single(m => m.Name == "flash").Size));
        Assert.Equal(((ulong)Ch32v003Map.SramStart, (ulong)Ch32v003Map.SramSize),
            (chip.Memory.Single(m => m.Name == "sram").Origin, chip.Memory.Single(m => m.Name == "sram").Size));
        Assert.Equal((ulong)Ch32v003Map.OptionBytes, chip.Memory.Single(m => m.Name == "option").Origin);

        // the pins: the chip file's, by ChipSharp's numbers (8 * the port's letter + the bit), are the simulator's
        var simulated = new Ch32v003Chip(Ch32v003Image.Load(WriteBin([0x6F, 0x00, 0x00, 0x00])));
        var pins = simulated.Ports.SelectMany(port => Enumerable.Range(0, 8).Where(bit => (port.Pins & (1 << bit)) != 0)
            .Select(bit => (Name: port.PinName(bit), Number: 8 * (port.Letter - 'A') + bit))).ToList();
        Assert.Equal(chip.Pins.Select(pin => (pin.Name, pin.Number)).OrderBy(pin => pin.Number), pins);

        var target = Ch32v003Build.Board;
        Assert.Equal(Ch32v003Map.InterruptCount, (int)target.Settings["vectors"]!);
        // the pin event is EXTI7_0's; the timer's and the uart's are past the PFIC's numbers, called by the runtime's
        // handlers of SysTick and USART1
        Assert.Equal(Ch32v003Map.Exti7To0Number, (int)target.Settings["interrupts"]!["pin"]!["irq"]!);
        Assert.All((string[])["timer", "uart"], name =>
            Assert.True((int)target.Settings["interrupts"]![name]!["irq"]! >= Ch32v003Map.InterruptCount, name));
    }

    /// <summary>A .bin of <paramref name="bytes"/> in the scratch folder.</summary>
    private string WriteBin(byte[] bytes)
    {
        Directory.CreateDirectory(_scratch);
        var path = Path.Combine(_scratch, "tiny.bin");
        File.WriteAllBytes(path, bytes);
        return path;
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
        program.Run(1); // warm up

        var clock = Stopwatch.StartNew();
        var run = program.Run(1000, setUp: c => chip = (Ch32v003Chip)c);
        clock.Stop();

        Assert.True(run.ExitCode == 0, run.ToString());
        var instructions = chip!.Cpu.InstructionsRetired;
        var rate = instructions / clock.Elapsed.TotalSeconds;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{instructions:N0} instructions ({chip.Cpu.Cycles:N0} cycles of the core, the flash's wait states besides, in 1000 ms " +
            $"at 48 MHz) in {clock.Elapsed.TotalMilliseconds:N0} ms: {rate / 1e6:0.0} million a second"));
        Assert.True(instructions > 20_000_000, $"{instructions} instructions");
        Assert.True(rate > 3e6, $"{rate:N0} instructions a second");
    }

    [Ch32v003Fact]
    public async Task SysTick_s_interrupt_comes_through_the_PFIC_s_vector_table_to_its_handler_and_mret_goes_back()
    {
        var program = await Ch32v003CProgram.BuildAsync("""
            #include "ch32.h"
            volatile int count;
            volatile uint32_t out, cause, stack;
            __attribute__((interrupt("machine"))) void isr_systick(void)
            {
                STK_SR = 0;                       /* CNTIF */
                cause = mcause();
                count++;
                pin_write(2, 0, count & 1);
            }
            int main(void)
            {
                clock_48mhz();
                pin_output(2, 0);
                STK_CMPR = 48000 - 1;             /* 1 ms of HCLK, counting from 0 again (STRE) */
                STK_CNTR = 0;
                STK_CTLR = 0xF;                   /* STE, STIE, STCLK: HCLK, STRE */
                PFIC_IENR(12) = 1u << 12;
                uint32_t sp;
                __asm__ volatile("mv %0, sp" : "=r"(sp));
                interrupts_on();
                while (count < 3) wait();
                STK_CTLR = 0;
                __asm__ volatile("mv %0, sp" : "=r"(stack));
                out = cause;
                report();
                out = sp - stack;                 /* the HPE gave back what it took */
                report();
                return 0;
            }
            """, _scratch);

        var (run, values) = program.RunReports(100);

        Assert.True(run.ExitCode == 0 && run.Stopped.Contains("has finished", StringComparison.Ordinal), run.ToString());
        Assert.Equal([0x8000_000CUL, 0], values); // an interrupt, number 12
        Assert.Contains("CPU clock 48 MHz", run.Output, StringComparison.Ordinal);
        var edges = run.Pins.Where(p => p.Pin == "PC0").ToList();
        Assert.Equal(["low", "high", "low", "high"], edges.Select(p => p.State));
        for (var k = 1; k <= 3; k++)
        {
            Assert.InRange(edges[k].Milliseconds, k, k + 0.05); // the clock's switch at 8 MHz first, a few cycles of entry
        }
    }

    /// <summary>
    /// A handler with no prologue of its own, as WCH's "WCH-Interrupt-fast" ones: it clobbers ra, t0-t2 and a0-a5, and
    /// returns with mret; the HPE (INTSYSCR's HWSTKEN) saves and restores them around it, so that a loop that keeps its
    /// sums in them gives what C# computes. Without the HPE ra is lost, and the run ends at the HardFault's ebreak.
    /// </summary>
    [Ch32v003Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_HPE_saves_the_registers_a_handler_without_its_own_prologue_clobbers(bool hpe)
    {
        var program = await Ch32v003CProgram.BuildAsync($$"""
            #include "ch32.h"
            volatile uint32_t out, ticks;
            uint32_t spin(uint32_t n);
            int main(void)
            {
                __asm__ volatile("csrw 0x804, %0" : : "r"({{(hpe ? 3 : 2)}}));
                STK_CMPR = 400;                   /* every 50 us at 8 MHz */
                STK_CTLR = 0xF;
                PFIC_IENR(12) = 1u << 12;
                interrupts_on();
                out = spin(5000);
                interrupts_off();
                report();
                out = ticks;
                report();
                return 0;
            }
            """, _scratch, """
                .text
                .global isr_systick, spin
            isr_systick:
                li t0, 0xE000F004
                sw zero, 0(t0)          /* STK_SR: CNTIF */
                la a0, ticks
                lw a1, 0(a0)
                addi a1, a1, 1
                sw a1, 0(a0)
                li a2, -1
                li a3, -1
                li a4, -1
                li a5, -1
                li t1, -1
                li t2, -1
                li ra, -1
                mret
            spin:                       /* a0 rounds of sums in t0-t2 and a1-a5 */
                li t0, 0
                li t1, 1
                li t2, 2
                li a1, 3
                li a2, 4
                li a3, 5
                li a4, 6
                li a5, 7
            1:  add t0, t0, t1
                add t1, t1, t2
                add t2, t2, a1
                add a1, a1, a2
                add a2, a2, a3
                add a3, a3, a4
                add a4, a4, a5
                addi a5, a5, 1
                addi a0, a0, -1
                bnez a0, 1b
                xor a0, t0, t1
                xor a0, a0, t2
                xor a0, a0, a1
                xor a0, a0, a2
                xor a0, a0, a3
                xor a0, a0, a4
                xor a0, a0, a5
                ret
            """);

        var (run, values) = program.RunReports(100);

        uint t0 = 0, t1 = 1, t2 = 2, a1 = 3, a2 = 4, a3 = 5, a4 = 6, a5 = 7;
        for (var i = 0; i < 5000; i++)
        {
            (t0, t1, t2, a1, a2, a3, a4, a5) = (t0 + t1, t1 + t2, t2 + a1, a1 + a2, a2 + a3, a3 + a4, a4 + a5, a5 + 1);
        }

        var sum = t0 ^ t1 ^ t2 ^ a1 ^ a2 ^ a3 ^ a4 ^ a5;
        if (!hpe)
        {
            // the loop's count (a0) and its sums are lost: it does not end in time, or ends with another sum
            Assert.False(run.ExitCode == 0 && values.Count == 2 && values[0] == sum, run.ToString());
            return;
        }

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(sum, values[0]);
        Assert.InRange(values[1], 20UL, 200UL); // interrupts came while it summed
    }

    /// <summary>
    /// SysTick at a low preemption priority (IPRIOR 0x80) sets the software interrupt (STK_CTLR's SWIE), of a high one
    /// (0x00): with nesting (INTSYSCR's INESTEN) it runs at once, inside SysTick's handler (GISR's NESTSTA 3: level 2);
    /// without, after it returns (level 1); a VTF channel takes it to a handler of its own, not the vector table's.
    /// </summary>
    [Ch32v003Theory]
    [InlineData(3, false, new ulong[] { 1, 3, 2, 0x103 })]
    [InlineData(1, false, new ulong[] { 1, 2, 3, 0x101 })]
    [InlineData(3, true, new ulong[] { 1, 4, 2, 0x103 })]
    public async Task Nesting_lets_a_higher_preemption_priority_in_and_a_VTF_channel_goes_straight_to_its_handler(int intsyscr,
        bool vtf, ulong[] expected)
    {
        var program = await Ch32v003CProgram.BuildAsync($$"""
            #include "ch32.h"
            #define PFIC_GISR REG(0xE000E04C)
            volatile uint32_t out, seq[8], gisr;
            volatile int n;
            __attribute__((interrupt("machine"))) void isr_systick(void)
            {
                STK_SR = 0;
                STK_CTLR &= ~2u;                  /* STIE off: once */
                seq[n++] = 1;
                STK_CTLR |= 1u << 31;             /* SWIE: the software interrupt */
                seq[n++] = 2;
            }
            __attribute__((interrupt("machine"))) void isr_software(void)
            {
                STK_CTLR &= ~(1u << 31);
                gisr = PFIC_GISR;
                seq[n++] = 3;
            }
            __attribute__((interrupt("machine"))) void direct(void)
            {
                STK_CTLR &= ~(1u << 31);
                gisr = PFIC_GISR;
                seq[n++] = 4;
            }
            int main(void)
            {
                __asm__ volatile("csrw 0x804, %0" : : "r"({{intsyscr}}));
                if ({{(vtf ? 1 : 0)}})
                {
                    REG(0xE000E050) = 14;          /* VTFIDR: channel 0 for interrupt 14 */
                    REG(0xE000E060) = (uint32_t)direct | 1;
                }
                PFIC_IPRIOR(12) = 0x80;
                PFIC_IPRIOR(14) = 0x00;
                PFIC_IENR(12) = (1u << 12) | (1u << 14);
                STK_CMPR = 1000;
                STK_CTLR = 0x7;                   /* STE, STIE, STCLK */
                interrupts_on();
                while (n < 3) wait();
                interrupts_off();
                for (int i = 0; i < 3; i++) { out = seq[i]; report(); }
                out = gisr;
                report();
                return 0;
            }
            """, _scratch);

        var (run, values) = program.RunReports(100);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(expected, values);
    }

    [Ch32v003Fact]
    public async Task A_pin_interrupt_follows_the_input_s_edges_on_its_timeline_through_AFIO_and_EXTI()
    {
        var program = await Ch32v003CProgram.BuildAsync("""
            #include "ch32.h"
            volatile int edges;
            volatile uint32_t out;
            __attribute__((interrupt("machine"))) void isr_exti(void)
            {
                REG(0x40010414) = 1u << 4;        /* EXTI_INTFR: line 4's flag */
                edges++;
                pin_write(2, 0, edges & 1);
            }
            int main(void)
            {
                pin_output(2, 0);
                pin_input(2, 4, 1);               /* PC4, its pull-up */
                RCC_APB2PCENR |= 1;               /* AFIO */
                REG(0x40010008) = 2u << 8;        /* AFIO_EXTICR: line 4 from PC4 */
                REG(0x40010400) = 1u << 4;        /* INTENR */
                REG(0x40010408) = 1u << 4;        /* RTENR */
                REG(0x4001040C) = 1u << 4;        /* FTENR */
                PFIC_IENR(20) = 1u << 20;
                out = pin_read(2, 4);
                report();
                interrupts_on();
                while (edges < 3) wait();
                out = pin_read(2, 4);
                report();
                return 0;
            }
            """, _scratch);

        var (run, values) = program.RunReports(10, [new PinInput("PC4", false, 1), new PinInput("PC4", true, 2),
            new PinInput("PC4", false, 3)]);

        Assert.True(run.ExitCode == 0 && run.Stopped.Contains("has finished", StringComparison.Ordinal), run.ToString());
        Assert.Equal([1UL, 0], values); // the pull-up, then the input's low
        Assert.Contains(run.Pins, p => p.Pin == "PC4" && p.State == "input pull-up");
        var edges = run.Pins.Where(p => p.Pin == "PC0").ToList();
        Assert.Equal(["low", "high", "low", "high"], edges.Select(p => p.State));
        for (var k = 1; k <= 3; k++)
        {
            Assert.InRange(edges[k].Milliseconds, k, k + 0.02); // a few microseconds at 8 MHz
        }
    }

    [Ch32v003Fact]
    public async Task An_input_reads_its_pull_up_or_pull_down_or_what_drives_it_and_an_open_drain_output_lets_go_at_1()
    {
        var program = await Ch32v003CProgram.BuildAsync("""
            #include "ch32.h"
            volatile uint32_t out;
            int main(void)
            {
                pin_input(2, 1, 1);               /* PC1 pull-up */
                pin_input(2, 2, 0);               /* PC2 pull-down */
                pin_input(2, 3, -1);              /* PC3 floating, driven high from outside */
                out = pin_read(2, 1) | (pin_read(2, 2) << 1) | (pin_read(2, 3) << 2);
                report();
                REG(GPIO(2)) = (REG(GPIO(2)) & ~(0xFu << 24)) | (0x7u << 24); /* PC6 open-drain output */
                pin_write(2, 6, 0);
                pin_write(2, 6, 1);
                REG(GPIO(2)) &= ~(0xFu << 20);    /* PC5 analog: INDR reads 0 */
                out = (REG(GPIO(2) + 8) >> 5) & 1;
                report();
                return 0;
            }
            """, _scratch);

        var (run, values) = program.RunReports(10, [new PinInput("PC3", true, null), new PinInput("PC5", true, null)]);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal([0b101UL, 0], values);
        Assert.Equal(["input pull-up", "input pull-down", "low", "input"], // PC6 open-drain: released at 1
            run.Pins.Where(p => p.Pin is "PC1" or "PC2" or "PC6").Select(p => p.State));
    }

    [Ch32v003Fact]
    public async Task USART1_prints_its_baud_rate_and_lines_and_its_RXNE_interrupt_echoes_what_is_sent()
    {
        var program = await Ch32v003CProgram.BuildAsync("""
            #include "ch32.h"
            volatile int done;
            __attribute__((interrupt("machine"))) void isr_usart1(void)
            {
                char c = (char)USART1_DATAR;      /* RXNE cleared */
                usart_put(c == '\n' ? '\n' : c - 32);
                if (c == '\n') done = 1;
            }
            int main(void)
            {
                clock_48mhz();
                usart_init(48000000, 115200);
                USART1_CTLR1 |= 1u << 5;          /* RXNEIE */
                PFIC_IENR(32) = 1u;               /* interrupt 32: IENR2's bit 0 */
                usart_print("ready\n");
                interrupts_on();
                while (!done) wait();
                usart_flush();
                return 0;
            }
            """, _scratch);

        var run = program.Run(20, sends: [new SerialSend("hi\n"u8.ToArray(), 2)]);

        Assert.True(run.ExitCode == 0 && run.Stopped.Contains("has finished", StringComparison.Ordinal), run.ToString());
        Assert.Equal(["ready", "HI"], run.SerialLines);
        Assert.Equal(["USART1 baud 115108"], run.After("USART1 baud")); // 48 MHz / 417
        Assert.Contains("input: USART1 \"hi\\n\"", run.Output, StringComparison.Ordinal);
    }

    [Ch32v003Fact]
    public async Task USART1_s_bytes_stay_in_the_chip_unless_its_TX_pin_is_an_alternate_function_output()
    {
        var program = await Ch32v003CProgram.BuildAsync("""
            #include "ch32.h"
            int main(void)
            {
                usart_init(8000000, 115200);
                REG(GPIO(3)) = (REG(GPIO(3)) & ~(0xFu << 20)) | (0x3u << 20); /* PD5 a plain output */
                usart_print("lost\n");
                usart_flush();
                return 0;
            }
            """, _scratch);

        var run = program.Run(20);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Empty(run.Serial);
        Assert.Contains("USART1 TX (PD5) is not an alternate-function output", run.Output, StringComparison.Ordinal);
    }

    [Ch32v003Theory]
    [InlineData("iwdg", false, "the independent watchdog (IWDG) reset the chip: the program did not feed it (IWDG_CTLR 0xAAAA) within its 10 ms")]
    [InlineData("iwdg", true, null)]
    [InlineData("wwdg", false, "the window watchdog (WWDG) reset the chip: its counter went from 0x40 to 0x3F")]
    [InlineData("wwdg", true, null)]
    [InlineData("wwdg-early", true, "the window watchdog (WWDG) reset the chip: the program fed it with its counter at 0x7e, above the window (W 0x50)")]
    public async Task A_watchdog_stops_the_run_unless_fed(string watchdog, bool feed, string? why)
    {
        var program = await Ch32v003CProgram.BuildAsync($$"""
            #include "ch32.h"
            int main(void)
            {
                int iwdg = {{(watchdog == "iwdg" ? 1 : 0)}}, feed = {{(feed ? 1 : 0)}};
                if (iwdg)
                {
                    REG(0x40003000) = 0x5555;     /* IWDG: unlock */
                    REG(0x40003004) = 0;          /* LSI / 4 */
                    REG(0x40003008) = 319;        /* 320 counts: 10 ms */
                    REG(0x40003000) = 0xCCCC;     /* start */
                }
                else
                {
                    RCC_APB1PCENR |= 1u << 11;    /* WWDG's clock */
                    REG(0x40002C04) = (3u << 7) | {{(watchdog == "wwdg-early" ? "0x50" : "0x7F")}}; /* HCLK / 4096 / 8, W */
                    REG(0x40002C00) = 0x80 | 0x7F; /* WDGA, T 0x7F: 64 counts of 4.096 ms to the reset */
                }
                STK_CTLR = 0x5;                   /* SysTick at HCLK, 8 MHz */
                for (uint32_t ms = 0; ms < 400; ms++)
                {
                    uint32_t start = STK_CNTR;
                    while (STK_CNTR - start < 8000) {}
                    if (feed)
                    {
                        if (iwdg) REG(0x40003000) = 0xAAAA;
                        else if ((REG(0x40002C00) & 0x7F) < 0x7F) REG(0x40002C00) = 0x7F;
                    }
                }
                return 0;
            }
            """, _scratch);

        var run = program.Run(600);

        if (why is null)
        {
            Assert.True(run.ExitCode == 0 && run.Stopped.Contains("has finished", StringComparison.Ordinal), run.ToString());
            return;
        }

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.StartsWith(why, run.Stopped["stopped: ".Length..], StringComparison.Ordinal);
        var at = run.Times("stopped").Single();
        if (watchdog == "iwdg")
        {
            Assert.InRange(at, 10, 10.1);
        }
        else if (watchdog == "wwdg")
        {
            Assert.InRange(at, 262, 263); // 64 x 4096 x 8 cycles of 8 MHz: 262.1 ms
        }
    }

    [Ch32v003Theory]
    [InlineData("__asm__ volatile(\"ebreak\");", "the program hit a breakpoint (ebreak) at pc 0x")]
    [InlineData("__asm__ volatile(\"csrw mtvec, zero\\n .word 0\");", "the program caused an illegal instruction 0x00000000 at pc 0x")]
    [InlineData("PFIC_IENR(21) = 1u << 21; REG(0xE000E200) = 1u << 21; interrupts_on(); wait();", // AWU, pending by IPSR
        "the program caused interrupt 21, and its vector table's entry at 0x00000054 (mtvec 0x00000003) is 0: no handler")]
    [InlineData("__asm__ volatile(\"ecall\");", "the program hit a breakpoint (ebreak)")]
    [InlineData("REG(0xE000E048) = 0xBEEF0080;", "the program reset the chip (PFIC_CFGR's SYSRESET, with KEY3)")]
    [InlineData("REG(GPIO(2)) = 0;", "the program used GPIOC's register at 0x40011000 with its clock off (RCC_APB2PCENR's IOPCEN 0)")]
    [InlineData("REG(0x40012C00) = 1;", "the program used TIM1 (its register at 0x40012c00), which the CH32V003 simulator does not model yet")]
    [InlineData("pin_output(3, 7);", "the program made PD7 an output, but PD7 is NRST")]
    [InlineData("pin_output(3, 1);", "the program made PD1 an output, but PD1 is SWIO")]
    [InlineData("RCC_CTLR |= 1u << 16;", "the program turned HSE on")]
    [InlineData("RCC_CTLR |= 1u << 24; RCC_CFGR0 = (RCC_CFGR0 & ~0xF3u) | 2;", "SYSCLK is 48 MHz with no flash wait state (FLASH_ACTLR's LATENCY 0)")]
    [InlineData("((void (*)(void))0x3F00)();", "the program ran into flash the image does not fill, at pc 0x00003f00")]
    [InlineData("*(volatile uint32_t *)0x20000101 = 1;", "the program hit a breakpoint")] // a misaligned store: the HardFault's ebreak
    public async Task The_failure_stops_say_what_happened(string statement, string why)
    {
        var program = await Ch32v003CProgram.BuildAsync($$"""
            #include "ch32.h"
            int main(void)
            {
                {{statement}}
                return 0;
            }
            """, _scratch);

        var run = program.Run(50);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains(why, run.Stopped, StringComparison.Ordinal);
    }

    [Ch32v003Fact]
    public async Task With_interrupts_on_and_none_to_come_wfi_ends_the_run_and_with_one_to_come_it_waits()
    {
        var program = await Ch32v003CProgram.BuildAsync("""
            #include "ch32.h"
            volatile int fired;
            __attribute__((interrupt("machine"))) void isr_systick(void) { STK_CTLR = 0; fired = 1; }
            int main(void)
            {
                PFIC_IENR(12) = 1u << 12;
                STK_CMPR = 8000 * 5;              /* 5 ms at 8 MHz */
                STK_CTLR = 0x7;
                interrupts_on();
                while (!fired) wait();
                pin_output(2, 0);
                wait();                           /* nothing can come any more */
                return 1;
            }
            """, _scratch);

        var run = program.Run(100);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("the program has finished: the CPU waits for an interrupt (wfi) that nothing can raise any more",
            run.Stopped, StringComparison.Ordinal);
        Assert.InRange(run.Pins.Single(p => p.Pin == "PC0").Milliseconds, 5, 5.05);
    }

    [Ch32v003Fact]
    public async Task The_HSI_without_its_divider_runs_the_core_at_24_MHz_and_the_PLL_at_48()
    {
        var program = await Ch32v003CProgram.BuildAsync("""
            #include "ch32.h"
            volatile uint32_t out;
            static uint32_t loop(void)
            {
                STK_CTLR = 0x5;                   /* SysTick at HCLK */
                uint32_t start = STK_CNTR;
                for (volatile int i = 0; i < 1000; i++) {}
                return STK_CNTR - start;
            }
            int main(void)
            {
                RCC_CFGR0 &= ~0xF0u;              /* HPRE /1: 24 MHz */
                pin_output(2, 0);
                pin_write(2, 0, 1);
                out = loop();
                report();
                clock_48mhz();
                pin_write(2, 0, 0);
                out = loop();
                report();
                return 0;
            }
            """, _scratch);

        var (run, values) = program.RunReports(100);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["CPU clock 24 MHz", "CPU clock 48 MHz"], run.After("CPU clock"));
        // the same loop in HCLK cycles: a few more at 48 MHz, for the flash's wait state
        Assert.InRange((double)values[1] / values[0], 1.0, 1.6);
        output.WriteLine($"the loop: {values[0]} cycles at 24 MHz, {values[1]} at 48 MHz");
    }

    [Ch32v003Fact]
    public async Task Chipsim_takes_the_CH32V003_s_pins_and_USART1_and_says_what_it_does_not_have()
    {
        var program = await Ch32v003CProgram.BuildAsync(Hello, _scratch);
        var bytes = Path.Combine(_scratch, "usart1.bin");
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(0, Simulation.Command(["ch32v003", program.Elf, "--ms", "5", "--usart1", bytes, "--input", "PA1=high"],
            output, errors));
        Assert.Equal("Hello\n", Encoding.ASCII.GetString(File.ReadAllBytes(bytes)));
        Assert.Contains("on the CH32V003 (", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("USART1: Hello", output.ToString(), StringComparison.Ordinal);

        using var wrong = new StringWriter();
        Assert.Equal(2, Simulation.Command(["ch32v003", program.Elf, "--input", "PB0=high"], output, wrong));
        Assert.Contains("the CH32V003 has no pin PB0; it has PA1, PA2, PC0, PC1, PC2, PC3, PC4, PC5, PC6, PC7, PD0, PD1",
            wrong.ToString(), StringComparison.Ordinal);
        using var none = new StringWriter();
        Assert.Equal(2, Simulation.Command(["ch32v003"], output, none));
        Assert.Contains("which image (.hex, .bin or .elf)?", none.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_with_bytes_where_the_chip_has_no_flash_is_refused()
    {
        Directory.CreateDirectory(_scratch);
        var big = Path.Combine(_scratch, "big.bin");
        File.WriteAllBytes(big, new byte[16 * 1024 + 1]);
        var hex = Path.Combine(_scratch, "ram.hex");
        File.WriteAllText(hex, ":020000042000DA\n:0400000001020304F2\n:00000001FF\n"); // 4 bytes at 0x20000000

        Assert.Contains("a .bin for the CH32V003 has 1 to 16384", Assert.Throws<ImageException>(() => Ch32v003Chip.Load(big)).Message,
            StringComparison.Ordinal);
        Assert.Contains("outside the CH32V003's 16 KB of flash", Assert.Throws<ImageException>(() => Ch32v003Chip.Load(hex)).Message,
            StringComparison.Ordinal);
    }
}
