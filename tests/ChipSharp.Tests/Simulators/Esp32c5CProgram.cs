using ChipSharp.Simulators;
using ChipSharp.Simulators.Esp32c5;
using ChipSharp.Tests.Llvm;
using ChipSharp.Tests.Pico;
using Xunit.Sdk;

namespace ChipSharp.Tests.Simulators;

/// <summary>A test that builds a program for the ESP32-C5 with clang and ld.lld: skipped, with the reason, without LLVM.</summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionFactDiscoverer", "ChipSharp.Tests")]
public sealed class Esp32c5FactAttribute : FactAttribute
{
    public Esp32c5FactAttribute()
    {
        if (PicoTools.MissingLlvm.Value is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// A program for a simulator test of the ESP32-C5, in C (and assembly), built with clang for riscv32 (the target's rv32imac_zicsr_zifencei, ilp32)
/// and linked by ld.lld into an ELF file in HP SRAM, as the ROM would load it: <see cref="StartUp"/> sets the stack,
/// clears .bss, calls init (report.c's weak one turns the watchdogs off; a program can have its own), then main, and
/// halts as ChipSharp's runtimes do (interrupts off, wfi); main.c includes
/// <see cref="Header"/> (c5.h: the registers, the watchdogs turned off, the interrupt matrix and the CLIC set up, the
/// UART's and the USB port's output).
/// </summary>
internal sealed class Esp32c5CProgram
{
    /// <summary>The flags of clang: the C5's ISA as ChipSharp builds for it, and no C library.</summary>
    public static readonly string[] CompileFlags =
        ["--target=riscv32-unknown-elf", "-march=rv32imac_zicsr_zifencei", "-mabi=ilp32", "-O2", "-ffreestanding", "-nostdlib",
        "-mno-relax", "-fwrapv"];

    public const string StartUp = """
            .section .text.start, "ax"
            .global _start
        _start:
            la sp, __stack_top
            la t0, __bss_start
            la t1, __bss_end
        1:  bgeu t0, t1, 2f
            sw zero, 0(t0)
            addi t0, t0, 4
            j 1b
        2:  call init
            call main
            .global halt
        halt:
            csrci mstatus, 8
        3:  wfi
            j 3b
        """;

    /// <summary>report.c: an empty report(), in a file of its own so that its calls stay, and the weak init.</summary>
    public const string Report = """
        #include "c5.h"
        void report(void) {}
        __attribute__((weak)) void init(void) { watchdogs_off(); }
        """;

    public const string LinkerScript = """
        MEMORY { sram (rwx) : ORIGIN = 0x40800000, LENGTH = 0x5E000 }
        ENTRY(_start)
        SECTIONS
        {
          .text : { *(.text.start) *(.text*) } > sram
          .rodata : { *(.rodata*) *(.srodata*) } > sram
          .data : { *(.data*) *(.sdata*) } > sram
          .bss (NOLOAD) : { . = ALIGN(4); __bss_start = .; *(.bss*) *(.sbss*) *(COMMON) . = ALIGN(4); __bss_end = .; } > sram
          __stack_top = ORIGIN(sram) + LENGTH(sram);
        }
        """;

    /// <summary>c5.h: what the tests' programs share.</summary>
    public const string Header = """
        #include <stdint.h>
        #define REG(address) (*(volatile uint32_t *)(address))
        #define CLIC_IP(id) (*(volatile uint8_t *)(0x20801000 + 4 * (id)))
        #define CLIC_IE(id) (*(volatile uint8_t *)(0x20801001 + 4 * (id)))
        #define CLIC_ATTR(id) (*(volatile uint8_t *)(0x20801002 + 4 * (id)))
        #define CLIC_CTL(id) (*(volatile uint8_t *)(0x20801003 + 4 * (id)))
        #define WDT_KEY 0x50D83AA1u

        /* MWDT0's flash boot protection, RWDT's and the super watchdog off, as ESP-IDF's start-up does */
        static inline void watchdogs_off(void)
        {
            REG(0x60008064) = WDT_KEY;
            REG(0x60008048) = (REG(0x60008048) & ~((1u << 14) | (1u << 31))) | (1u << 22);
            REG(0x60008064) = 0;
            REG(0x600B1C18) = WDT_KEY;
            REG(0x600B1C00) = 0;
            REG(0x600B1C18) = 0;
            REG(0x600B1C20) = WDT_KEY;
            REG(0x600B1C1C) |= 1u << 18;
            REG(0x600B1C20) = 0;
        }

        /* a source of the interrupt matrix to CLIC interrupt id, at a level, vectored or not, edge or level */
        static inline void route(int source, int id, int level, int vectored, int trigger)
        {
            REG(0x60010000 + 4 * source) = id;
            CLIC_ATTR(id) = (uint8_t)((trigger << 1) | vectored);
            CLIC_CTL(id) = (uint8_t)(level << 5);
            CLIC_IE(id) = 1;
        }

        static inline void uart_put(char c)
        {
            while (((REG(0x6000001C) >> 16) & 0xFF) >= 120) {}
            REG(0x60000000) = (uint8_t)c;
        }

        static inline void uart_print(const char *text)
        {
            while (*text) uart_put(*text++);
        }

        /* waits until UART0 has sent everything: TX_DONE */
        static inline void uart_flush(void)
        {
            while (((REG(0x6000001C) >> 16) & 0xFF) != 0 || (REG(0x60000004) & (1u << 14)) == 0) {}
        }

        static inline void usb_print(const char *text)
        {
            while (*text)
            {
                while ((REG(0x6000F004) & 2) == 0) {}
                REG(0x6000F000) = (uint8_t)*text++;
            }
            REG(0x6000F004) = 1;
        }

        static inline void gpio_output(int pin)
        {
            REG(0x60091AD4 + 4 * pin) = 0x100;
            REG(0x60090000 + 4 * pin) = (REG(0x60090000 + 4 * pin) & ~(7u << 12)) | (1u << 12);
            REG(0x60091038) = 1u << pin;
        }

        static inline void gpio_write(int pin, int high)
        {
            REG(high ? 0x60091008 : 0x6009100C) = 1u << pin;
        }

        /* SYSTIMER unit 0 now, in 16 MHz counts */
        static inline uint64_t systimer_now(void)
        {
            REG(0x6000A004) = 1u << 30;
            while ((REG(0x6000A004) & (1u << 29)) == 0) {}
            return ((uint64_t)REG(0x6000A040) << 32) | REG(0x6000A044);
        }

        void report(void);
        """;

    private Esp32c5CProgram(string elf, IReadOnlyDictionary<string, uint> symbols)
    {
        Elf = elf;
        Symbols = symbols;
    }

    /// <summary>The ELF file.</summary>
    public string Elf { get; }

    public IReadOnlyDictionary<string, uint> Symbols { get; }

    /// <summary>
    /// Builds main.c (<paramref name="source"/>), report.c (<see cref="Report"/>) and <paramref name="assembly"/>
    /// (more.S), in <paramref name="folder"/>.
    /// </summary>
    public static async Task<Esp32c5CProgram> BuildAsync(string source, string folder, string? assembly = null,
        string march = "rv32imac_zicsr_zifencei")
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "start.S"), StartUp);
        await File.WriteAllTextAsync(Path.Combine(folder, "link.ld"), LinkerScript);
        await File.WriteAllTextAsync(Path.Combine(folder, "c5.h"), Header);
        await File.WriteAllTextAsync(Path.Combine(folder, "main.c"), source);
        await File.WriteAllTextAsync(Path.Combine(folder, "report.c"), Report);
        List<string> files = ["start.S", "main.c", "report.c"];
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
        return new Esp32c5CProgram(elf, Esp32c5Image.Load(elf).Symbols);
    }

    /// <summary>The address of <paramref name="symbol"/>; the test fails when the program has none.</summary>
    public uint Address(string symbol) => Symbols.TryGetValue(symbol, out var address)
        ? address
        : throw new XunitException($"The program has no '{symbol}'.");

    public SimulationResult Run(double milliseconds, IReadOnlyList<PinInput>? inputs = null,
        IReadOnlyList<SerialSend>? sends = null, Action<SimulatedChip>? setUp = null) =>
        Simulation.Run(new SimulationOptions
        {
            Chip = "esp32c5",
            Image = Elf,
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
                var esp = (Esp32c5Chip)c;
                values.Add(size == 8
                    ? esp.Read(output, 4) | ((ulong)esp.Read(output + 4, 4) << 32)
                    : esp.Read(output, size));
            },
        });
        return (run, values);
    }
}
