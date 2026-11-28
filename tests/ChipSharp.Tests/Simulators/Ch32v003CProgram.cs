using ChipSharp.Simulators;
using ChipSharp.Simulators.Ch32v003;
using ChipSharp.Tests.Llvm;
using ChipSharp.Tests.Pico;
using Xunit.Sdk;

namespace ChipSharp.Tests.Simulators;

/// <summary>A test that builds a program for the CH32V003 with clang and ld.lld: skipped, with the reason, without LLVM.</summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionFactDiscoverer", "ChipSharp.Tests")]
public sealed class Ch32v003FactAttribute : FactAttribute
{
    public Ch32v003FactAttribute()
    {
        if (PicoTools.MissingLlvm.Value is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>The same for a theory: each case in a collection of its own (they run side by side).</summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionTheoryDiscoverer", "ChipSharp.Tests")]
public sealed class Ch32v003TheoryAttribute : TheoryAttribute
{
    public Ch32v003TheoryAttribute()
    {
        if (PicoTools.MissingLlvm.Value is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// A program for a simulator test of the CH32V003, in C (and assembly), built with clang for the QingKe V2A
/// (rv32ec_zicsr, ilp32e; with XW, rv32ec_zicsr_xwchc, when asked) and linked by ld.lld into an ELF file for the flash
/// at 0 (WCH's linker script's origin, the alias of 0x0800_0000) and the 2 KB of SRAM: <see cref="StartUp"/> is WCH's
/// start-up's shape (startup_ch32v00x.S): `j handle_reset` and the vector table of addresses at 0 (mtvec 0 | 3), the
/// stack at the top of SRAM, .data copied from flash, .bss cleared, INTSYSCR 3 (the HPE and nesting on), then main with
/// interrupts off, which returns to halt (interrupts off, wfi). The handlers are weak: a program defines isr_systick,
/// isr_software, isr_wwdg, isr_exti, isr_usart1 or isr_exception; any other trap runs ebreak. soft.c has the helpers
/// clang calls for what the core has no instruction for (__mulsi3, __divsi3 and their kin, the 64-bit ones too), shift
/// and add, shift and subtract; main.c includes <see cref="Header"/> (ch32.h: the registers, the clocks, the pins, USART1).
/// </summary>
internal sealed class Ch32v003CProgram
{
    /// <summary>The flags of clang: the V2A's ISA, and no C library.</summary>
    public static readonly string[] CompileFlags =
        ["--target=riscv32-unknown-elf", "-march=rv32ec_zicsr", "-mabi=ilp32e", "-O2", "-ffreestanding", "-nostdlib",
        "-mno-relax", "-fwrapv"];

    public const string StartUp = """
            .section .init, "ax"
            .global _start
            .option push
            .option norvc
        _start:
            j handle_reset          /* 0 */
            .word 0                 /* 1 */
            .word unexpected        /* 2 NMI */
            .word isr_exception     /* 3 EXC */
            .fill 8, 4, 0           /* 4-11 */
            .word isr_systick       /* 12 */
            .word 0
            .word isr_software      /* 14 */
            .word 0
            .word isr_wwdg          /* 16 */
            .fill 3, 4, 0
            .word isr_exti          /* 20 EXTI7_0 */
            .fill 11, 4, 0
            .word isr_usart1        /* 32 */
            .fill 6, 4, 0           /* 33-38 */
            .option pop

            .text
            .weak isr_exception, isr_systick, isr_software, isr_wwdg, isr_exti, isr_usart1
        isr_exception:
        isr_systick:
        isr_software:
        isr_wwdg:
        isr_exti:
        isr_usart1:
        unexpected:
            ebreak

        handle_reset:
            la sp, __stack_top
            la a0, __data_load
            la a1, __data_start
            la a2, __data_end
        1:  bgeu a1, a2, 2f
            lw t0, 0(a0)
            sw t0, 0(a1)
            addi a0, a0, 4
            addi a1, a1, 4
            j 1b
        2:  la a0, __bss_start
            la a1, __bss_end
        3:  bgeu a0, a1, 4f
            sw zero, 0(a0)
            addi a0, a0, 4
            j 3b
        4:  li t0, 0x1800           /* mstatus: MPP machine, MPIE 0: main starts with interrupts off */
            csrw mstatus, t0
            li t0, 3                /* INTSYSCR: the HPE and nesting, as WCH's start-up */
            csrw 0x804, t0
            la t0, _start
            ori t0, t0, 3           /* mtvec: the vector table of addresses at 0 */
            csrw mtvec, t0
            call init
            la t0, main
            csrw mepc, t0
            la ra, halt
            mret

            .global halt
        halt:
            csrci mstatus, 8
        5:  wfi
            j 5b
        """;

    /// <summary>report.c: an empty report(), in a file of its own so that its calls stay, and the weak init.</summary>
    public const string Report = """
        void report(void) {}
        __attribute__((weak)) void init(void) {}
        """;

    /// <summary>soft.c: what clang calls for multiplications and divisions on a core without M.</summary>
    public const string Soft = """
        typedef unsigned int u32;
        typedef int i32;
        typedef unsigned long long u64;
        typedef long long i64;
        u32 __mulsi3(u32 a, u32 b) { u32 r = 0; while (b) { if (b & 1) r += a; a <<= 1; b >>= 1; } return r; }
        u64 __muldi3(u64 a, u64 b) { u64 r = 0; while (b) { if (b & 1) r += a; a <<= 1; b >>= 1; } return r; }
        static u32 udiv32(u32 n, u32 d, u32 *rem)
        {
            u32 q = 0, r = 0;
            for (int bit = 31; bit >= 0; bit--)
            {
                r = (r << 1) | ((n >> bit) & 1);
                if (r >= d) { r -= d; q |= 1u << bit; }
            }
            *rem = r;
            return q;
        }
        u32 __udivsi3(u32 n, u32 d) { u32 r; return udiv32(n, d, &r); }
        u32 __umodsi3(u32 n, u32 d) { u32 r; udiv32(n, d, &r); return r; }
        i32 __divsi3(i32 n, i32 d)
        {
            u32 r, q = udiv32(n < 0 ? -(u32)n : (u32)n, d < 0 ? -(u32)d : (u32)d, &r);
            return (n < 0) != (d < 0) ? -(i32)q : (i32)q;
        }
        i32 __modsi3(i32 n, i32 d)
        {
            u32 r;
            udiv32(n < 0 ? -(u32)n : (u32)n, d < 0 ? -(u32)d : (u32)d, &r);
            return n < 0 ? -(i32)r : (i32)r;
        }
        static u64 udiv64(u64 n, u64 d, u64 *rem)
        {
            u64 q = 0, r = 0;
            for (int bit = 63; bit >= 0; bit--)
            {
                r = (r << 1) | ((n >> bit) & 1);
                if (r >= d) { r -= d; q |= 1ull << bit; }
            }
            *rem = r;
            return q;
        }
        u64 __udivdi3(u64 n, u64 d) { u64 r; return udiv64(n, d, &r); }
        u64 __umoddi3(u64 n, u64 d) { u64 r; udiv64(n, d, &r); return r; }
        i64 __divdi3(i64 n, i64 d)
        {
            u64 r, q = udiv64(n < 0 ? -(u64)n : (u64)n, d < 0 ? -(u64)d : (u64)d, &r);
            return (n < 0) != (d < 0) ? -(i64)q : (i64)q;
        }
        i64 __moddi3(i64 n, i64 d)
        {
            u64 r;
            udiv64(n < 0 ? -(u64)n : (u64)n, d < 0 ? -(u64)d : (u64)d, &r);
            return n < 0 ? -(i64)r : (i64)r;
        }
        """;

    public const string LinkerScript = """
        MEMORY
        {
          flash (rx) : ORIGIN = 0x00000000, LENGTH = 16K
          ram (rwx) : ORIGIN = 0x20000000, LENGTH = 2K
        }
        ENTRY(_start)
        SECTIONS
        {
          .init : { KEEP(*(.init)) } > flash
          .text : { *(.text*) *(.rodata*) *(.srodata*) . = ALIGN(4); } > flash
          .data : { . = ALIGN(4); __data_start = .; *(.data*) *(.sdata*) . = ALIGN(4); __data_end = .; } > ram AT > flash
          __data_load = LOADADDR(.data);
          .bss (NOLOAD) : { . = ALIGN(4); __bss_start = .; *(.bss*) *(.sbss*) *(COMMON) . = ALIGN(4); __bss_end = .; } > ram
          __stack_top = ORIGIN(ram) + LENGTH(ram);
        }
        """;

    /// <summary>ch32.h: what the tests' programs share.</summary>
    public const string Header = """
        #include <stdint.h>
        #define REG(address) (*(volatile uint32_t *)(address))
        #define RCC_CTLR REG(0x40021000)
        #define RCC_CFGR0 REG(0x40021004)
        #define RCC_APB2PCENR REG(0x40021018)
        #define RCC_APB1PCENR REG(0x4002101C)
        #define FLASH_ACTLR REG(0x40022000)
        #define GPIO(port) (0x40010800 + 0x400 * (port)) /* port 0 A, 2 C, 3 D */
        #define PFIC_IENR(n) REG(0xE000E100 + 4 * ((n) / 32))
        #define PFIC_IPRIOR(n) (*(volatile uint8_t *)(0xE000E400 + (n)))
        #define STK_CTLR REG(0xE000F000)
        #define STK_SR REG(0xE000F004)
        #define STK_CNTR REG(0xE000F008)
        #define STK_CMPR REG(0xE000F010)
        #define USART1_STATR REG(0x40013800)
        #define USART1_DATAR REG(0x40013804)
        #define USART1_BRR REG(0x40013808)
        #define USART1_CTLR1 REG(0x4001380C)

        static inline void interrupts_on(void) { __asm__ volatile("csrsi mstatus, 8"); }
        static inline void interrupts_off(void) { __asm__ volatile("csrci mstatus, 8"); }
        static inline uint32_t mcause(void) { uint32_t c; __asm__ volatile("csrr %0, mcause" : "=r"(c)); return c; }
        static inline void wait(void) { __asm__ volatile("wfi"); }

        /* 48 MHz from the PLL (HSI x 2), with the flash's wait state first, as WCH's SetSysClockTo_48MHZ_HSI */
        static inline void clock_48mhz(void)
        {
            FLASH_ACTLR = 1;
            RCC_CFGR0 &= ~0xF0u;              /* HPRE: SYSCLK / 1 */
            RCC_CTLR |= 1u << 24;             /* PLLON */
            while ((RCC_CTLR & (1u << 25)) == 0) {}
            RCC_CFGR0 = (RCC_CFGR0 & ~3u) | 2u;
            while ((RCC_CFGR0 & 0xC) != 8) {}
        }

        /* a pin an output, push-pull: port 0 A, 2 C, 3 D */
        static inline void pin_output(int port, int pin)
        {
            RCC_APB2PCENR |= 1u << (port + 2);
            uint32_t cfg = REG(GPIO(port));
            REG(GPIO(port)) = (cfg & ~(0xFu << (4 * pin))) | (0x3u << (4 * pin));
        }

        static inline void pin_write(int port, int pin, int high)
        {
            REG(GPIO(port) + 0x10) = high ? 1u << pin : 1u << (pin + 16);
        }

        /* a pin an input with its pull-up (pull 1) or pull-down (0), or floating (-1) */
        static inline void pin_input(int port, int pin, int pull)
        {
            RCC_APB2PCENR |= 1u << (port + 2);
            uint32_t cfg = REG(GPIO(port)) & ~(0xFu << (4 * pin));
            REG(GPIO(port)) = cfg | ((pull < 0 ? 0x4u : 0x8u) << (4 * pin));
            if (pull >= 0) REG(GPIO(port) + 0x10) = pull ? 1u << pin : 1u << (pin + 16);
        }

        static inline int pin_read(int port, int pin) { return (REG(GPIO(port) + 0x08) >> pin) & 1; }

        /* USART1 on PD5 (TX, alternate push-pull) and PD6 (RX, floating) at a baud rate of HCLK hclk */
        static inline void usart_init(uint32_t hclk, uint32_t baud)
        {
            RCC_APB2PCENR |= (1u << 14) | (1u << 5);
            uint32_t cfg = REG(GPIO(3)) & ~(0xFFu << 20);
            REG(GPIO(3)) = cfg | (0xBu << 20) | (0x4u << 24);
            USART1_BRR = (hclk + baud / 2) / baud;
            USART1_CTLR1 = (1u << 13) | (1u << 3) | (1u << 2);
        }

        static inline void usart_put(char c) { while ((USART1_STATR & 0x80) == 0) {} USART1_DATAR = (uint8_t)c; }

        static inline void usart_print(const char *text) { while (*text) usart_put(*text++); }

        static inline void usart_flush(void) { while ((USART1_STATR & 0x40) == 0) {} }

        void report(void);
        """;

    private Ch32v003CProgram(string elf, IReadOnlyDictionary<string, uint> symbols)
    {
        Elf = elf;
        Symbols = symbols;
    }

    /// <summary>The ELF file.</summary>
    public string Elf { get; }

    public IReadOnlyDictionary<string, uint> Symbols { get; }

    /// <summary>
    /// Builds main.c (<paramref name="source"/>), report.c, soft.c and <paramref name="assembly"/> (more.S), in
    /// <paramref name="folder"/>, for <paramref name="march"/>.
    /// </summary>
    public static async Task<Ch32v003CProgram> BuildAsync(string source, string folder, string? assembly = null,
        string march = "rv32ec_zicsr")
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "start.S"), StartUp);
        await File.WriteAllTextAsync(Path.Combine(folder, "link.ld"), LinkerScript);
        await File.WriteAllTextAsync(Path.Combine(folder, "ch32.h"), Header);
        await File.WriteAllTextAsync(Path.Combine(folder, "main.c"), source);
        await File.WriteAllTextAsync(Path.Combine(folder, "report.c"), Report);
        await File.WriteAllTextAsync(Path.Combine(folder, "soft.c"), Soft);
        List<string> files = ["start.S", "main.c", "report.c", "soft.c"];
        if (assembly is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "more.S"), assembly);
            files.Add("more.S");
        }

        string[] flags = [.. CompileFlags.Select(flag => flag.StartsWith("-march=", StringComparison.Ordinal) ? "-march=" + march : flag)];
        var compiled = await Task.WhenAll(files.Select(file => LlvmTools.RunAsync(LlvmTools.Clang,
            [.. flags, "-c", file, "-o", file + ".o"], folder)));
        foreach (var (file, result) in files.Zip(compiled))
        {
            Assert.True(result.ExitCode == 0, $"clang {file} failed:\n{result}");
        }

        var link = await LlvmTools.RunAsync(PicoTools.LlvmTool("ld.lld"),
            ["-T", "link.ld", .. files.Select(file => file + ".o"), "-o", "program.elf"], folder);
        Assert.True(link.ExitCode == 0, $"ld.lld failed:\n{link}");
        var elf = Path.Combine(folder, "program.elf");
        return new Ch32v003CProgram(elf, Ch32v003Image.Load(elf).Symbols);
    }

    /// <summary>The program as Intel HEX (<paramref name="flashAddresses"/>: at 0x0800_0000, else at 0) or a .bin, by llvm-objcopy.</summary>
    public async Task<string> ConvertAsync(string format, bool flashAddresses = false)
    {
        var folder = Path.GetDirectoryName(Elf)!;
        var output = Path.Combine(folder, (flashAddresses ? "flash." : "program.") + (format == "ihex" ? "hex" : "bin"));
        List<string> arguments = ["-O", format];
        if (flashAddresses)
        {
            arguments.AddRange(["--change-section-lma", "*+0x08000000"]);
        }

        var result = await LlvmTools.RunAsync(PicoTools.LlvmTool("llvm-objcopy"), [.. arguments, Elf, output], folder);
        Assert.True(result.ExitCode == 0, $"llvm-objcopy failed:\n{result}");
        return output;
    }

    /// <summary>The address of <paramref name="symbol"/>; the test fails when the program has none.</summary>
    public uint Address(string symbol) => Symbols.TryGetValue(symbol, out var address)
        ? address
        : throw new XunitException($"The program has no '{symbol}'.");

    public SimulationResult Run(double milliseconds, IReadOnlyList<PinInput>? inputs = null,
        IReadOnlyList<SerialSend>? sends = null, Action<SimulatedChip>? setUp = null, string? image = null) =>
        Simulation.Run(new SimulationOptions
        {
            Chip = "ch32v003",
            Image = image ?? Elf,
            Milliseconds = milliseconds,
            Inputs = inputs ?? [],
            Sends = sends ?? [],
        }, setUp: setUp);

    /// <summary>Runs the program and reads its variable out (<paramref name="size"/> bytes) each time it calls report().</summary>
    public (SimulationResult Run, List<ulong> Values) RunReports(double milliseconds, int size = 4)
    {
        var values = new List<ulong>();
        var report = (int)Address("report");
        var output = Address("out");
        var run = Run(milliseconds, setUp: chip => chip.Hooks = new Dictionary<int, Action<SimulatedChip>>
        {
            [report] = c =>
            {
                var ch32 = (Ch32v003Chip)c;
                values.Add(size == 8
                    ? ch32.Read(output, 4) | ((ulong)ch32.Read(output + 4, 4) << 32)
                    : ch32.Read(output, size));
            },
        });
        return (run, values);
    }
}
