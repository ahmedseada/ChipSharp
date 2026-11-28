using System.Diagnostics;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Chips;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;
using ChipSharp.Toolchain.Sdcc;

namespace ChipSharp.Tests.Chips;

/// <summary>
/// The C made from the chips' descriptions: the kept headers are what the generator makes of chips/ today (with
/// CHIPSHARP_REGENERATE=1 the test writes them instead); they compile, and link, with SDCC for the PFS154 and the
/// STM8S103, the RP2040's (from its SVD) with clang for Arm and RISC-V; and what they say is what the description says.
/// </summary>
public sealed partial class ChipHeaderTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Theory]
    [InlineData("pfs154")]
    [InlineData("stm8s103f3")]
    [InlineData("esp32c5")]
    [InlineData("ch32v003")]
    public void The_kept_headers_are_what_the_generator_makes_of_the_chips_description(string chip)
    {
        var headers = ChipFiles.Headers(chip);
        var regenerate = Environment.GetEnvironmentVariable(ChipFiles.RegenerateVariable) == "1";

        // a chip clang builds has the table of its interrupts' handlers too
        string[] vectors = ChipCatalog.DialectOf(ChipFiles.Targets.Load(chip)) == CDialect.Gnu ? [$"{chip}_vectors.c"] : [];
        Assert.Equal([$"{chip}_registers.h", $"{chip}_interrupts.h", $"{chip}_pins.h", .. vectors],
            headers.Select(h => h.FileName));
        foreach (var header in headers)
        {
            var path = Path.Combine(ChipFiles.GeneratedFolder, header.FileName);
            if (regenerate)
            {
                Directory.CreateDirectory(ChipFiles.GeneratedFolder);
                File.WriteAllText(path, header.Content);
            }

            Assert.True(File.Exists(path), $"{path} is missing: run the tests with {ChipFiles.RegenerateVariable}=1 to make it.");
            Assert.True(header.Content == File.ReadAllText(path).ReplaceLineEndings("\n"),
                $"{path} is not what chips/{chip}.json makes: run the tests with {ChipFiles.RegenerateVariable}=1 to make it again.");
        }
    }

    [Fact]
    public void The_STM8s_registers_header_has_each_register_and_field_by_its_C_name()
    {
        var header = ChipFiles.Headers("stm8s103f3")[0].Content;

        Assert.Contains("#define UART1_SR_ADDRESS 0x5230u\n", header);
        Assert.Contains("#define UART1_SR (*(volatile uint8_t *)0x5230u)\n", header);
        Assert.Contains("#define UART1_SR_TXE_POS 7   // the data register can take a byte (read-only)\n", header);
        Assert.Contains("#define UART1_SR_TXE_MASK 0x80u\n", header);
        Assert.Contains("#define PD_DDR (*(volatile uint8_t *)0x5011u)\n", header);   // derived from port A, 0x500f + 2
        Assert.Contains("#define TIM4_SR (*(volatile uint8_t *)0x5344u)\n", header);
        Assert.Contains("#define CLK_CKDIVR_HSIDIV(value) ((((unsigned int)(value)) << 3) & CLK_CKDIVR_HSIDIV_MASK)\n", header);
        Assert.Contains("#define CLK_CKDIVR_HSIDIV_DIV8 (0x3u << 3)\n", header);
        Assert.Contains("#define ADC1_CSR (*(volatile uint8_t *)0x5400u)\n", header);
        Assert.Contains("#define CLK_SWR_HSI 0xe1u", header);
    }

    [Fact]
    public void The_Padauks_registers_are_SDCCs_sfrs_in_the_IO_space_and_its_interrupts_bits_of_INTRQ()
    {
        var headers = ChipFiles.Headers("pfs154");
        var registers = headers[0].Content;
        var interrupts = headers[1].Content;
        var pins = headers[2].Content;

        Assert.Contains("#define PA_ADDRESS 0x10u\n__sfr __at(0x10) PA;\n", registers);
        Assert.Contains("__sfr __at(0x04) INTEN;\n", registers);
        Assert.DoesNotContain("volatile", registers);
        Assert.Contains("#define TM2C_OUTPUT_PA3 (0x2u << 2)", registers);
        Assert.Contains("#define CLKMD_IHRC_DIV8 0x3cu", registers);
        Assert.Contains("#define PFS154_INTERRUPT_T16_MASK 0x4u\n#define PFS154_INTERRUPT_T16_REQUEST INTRQ\n" +
            "#define PFS154_INTERRUPT_T16_ENABLE INTEN\n", interrupts);
        Assert.Contains("#define PFS154_HANDLER void pfs154_interrupt(void) __interrupt(0)\n", interrupts);
        Assert.Contains("#define PFS154_PIN_PB5 13   // TM3_PWM_PB5\n", pins);
        Assert.Contains("#define PFS154_PINS 0xfff9u\n", pins);
        Assert.Contains("#define PFS154_PORT_A_DATA PA\n", pins);
        Assert.DoesNotContain("_ADDRESSES", pins); // the I/O space has no addresses a pointer reaches
    }

    [Fact]
    public void The_STM8s_pins_header_maps_numbers_ports_and_functions_and_its_interrupts_SDCCs_handlers()
    {
        var headers = ChipFiles.Headers("stm8s103f3");

        Assert.Contains("#define STM8S103F3_PIN_PD5 29   // UART1_TX, AIN5\n#define STM8S103F3_PIN_PD5_PORT 3\n" +
            "#define STM8S103F3_PIN_PD5_BIT 5\n#define STM8S103F3_PIN_PD5_MASK 0x20u\n", headers[2].Content);
        Assert.Contains("#define STM8S103F3_PORT_OUT_ADDRESSES { 0x5000u, 0x5005u, 0x500au, 0x500fu }\n", headers[2].Content);
        Assert.Contains("#define STM8S103F3_UART1_TX_PIN 29   // PD5\n", headers[2].Content);
        Assert.Contains("#define STM8S103F3_IRQ_TIM4_UPD_OVF 23   // TIM4 update, overflow\n", headers[1].Content);
        Assert.Contains("#define STM8S103F3_IRQ_COUNT 25\n", headers[1].Content);
        Assert.Contains("#define STM8S103F3_HANDLER(name) void stm8s103f3_isr_##name(void) __interrupt(STM8S103F3_IRQ_##name)\n",
            headers[1].Content);
    }

    [SdccFact]
    public void The_SDCC_chips_headers_compile_and_link_with_SDCC_and_a_handler_lands_in_its_vector()
    {
        var programs = new Dictionary<string, (string Target, string Main)>
        {
            ["pfs154"] = ("pfs154-bare", """
                volatile unsigned char ticks;

                PFS154_HANDLER
                {
                    if (INTRQ & PFS154_INTERRUPT_T16_MASK)
                    {
                        ticks++;
                    }

                    INTRQ = 0;
                }

                void main(void)
                {
                    CLKMD = CLKMD_IHRC_DIV8;
                    PAC |= PFS154_PIN_PA4_MASK;
                    T16M = T16M_CLOCK_SYSCLK | T16M_DIVIDER_DIV64 | T16M_INTERRUPT(7);
                    PFS154_INTERRUPT_T16_ENABLE |= PFS154_INTERRUPT_T16_MASK;
                    for (;;)
                    {
                        PA = (ticks & 1) ? PFS154_PIN_PA4_MASK : 0;
                    }
                }
                """),
            ["stm8s103f3"] = ("stm8s103-board", """
                volatile unsigned char ticks;

                STM8S103F3_HANDLER(TIM4_UPD_OVF)
                {
                    TIM4_SR &= (unsigned char)~TIM4_SR_UIF_MASK;
                    ticks++;
                }

                void main(void)
                {
                    static const unsigned int outs[STM8S103F3_PORT_COUNT] = STM8S103F3_PORT_OUT_ADDRESSES;
                    CLK_CKDIVR = CLK_CKDIVR_HSIDIV_DIV1 | CLK_CKDIVR_CPUDIV_DIV1;
                    PB_DDR |= STM8S103F3_PIN_PB5_MASK;
                    PB_CR1 |= STM8S103F3_PIN_PB5_MASK;
                    TIM4_PSCR = TIM4_PSCR_PSC_DIV128;
                    TIM4_ARR = 124;
                    TIM4_IER = TIM4_IER_UIE_MASK;
                    TIM4_CR1 = TIM4_CR1_CEN_MASK;
                    __asm__("rim");
                    for (;;)
                    {
                        *(volatile unsigned char *)outs[STM8S103F3_PIN_PB5_PORT] = (ticks & 1) ? STM8S103F3_PIN_PB5_MASK : 0;
                    }
                }
                """),
        };
        foreach (var (chip, (target, main)) in programs)
        {
            var headers = ChipFiles.Headers(chip);
            var code = new GeneratedCode(
            [
                .. headers,
                new GeneratedFile("main.c", string.Concat(headers.Select(h => $"#include \"{h.FileName}\"\n")) + "\n" + main),
            ]);
            var folder = Path.Combine(_scratch, chip);

            new SdccToolchain(new SdccToolchainOptions { OutputFolder = folder, ProgramName = "Generated", UseCache = false })
                .Build(code, ChipFiles.Targets.Load(target));

            var symbols = SdccPrograms.MapSymbols(Path.Combine(folder, "Generated.map"));
            Assert.Contains(chip == "pfs154" ? "_pfs154_interrupt" : "_stm8s103f3_isr_TIM4_UPD_OVF", symbols.Keys);
        }
    }

    [Fact]
    public void The_RP2040s_headers_from_its_SVD_compile_with_clang_for_Arm_and_RISC_V_and_its_vector_table_links_by_number()
    {
        if (PicoTools.MissingLlvm.Value is not null)
        {
            return;
        }

        var rp2040 = Svd.Read(ChipFiles.Svd);
        var files = ChipHeaders.All(rp2040, CDialect.Gnu, "chips/svd/RP2040.svd");
        Directory.CreateDirectory(_scratch);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(_scratch, file.FileName), file.Content);
        }

        File.WriteAllText(Path.Combine(_scratch, "use.c"), """
            #include "rp2040_registers.h"
            #include "rp2040_interrupts.h"
            #include "rp2040_pins.h"

            _Static_assert(TIMER_TIMERAWL_ADDRESS == 0x40054028ul, "TIMER_TIMERAWL");
            _Static_assert(RP2040_IRQ_TIMER_IRQ_1 == 1 && RP2040_IRQ_IO_IRQ_BANK0 == 13 && RP2040_IRQ_COUNT == 26, "IRQs");
            _Static_assert(UART0_UARTLCR_H_WLEN_MASK == (0x3u << 5), "a field");

            unsigned int now(void)
            {
                return TIMER_TIMERAWL;
            }

            void rp2040_irq_TIMER_IRQ_1(void)
            {
                TIMER_INTR = TIMER_INTR_ALARM_1_MASK;
            }
            """);
        foreach (var triple in (string[])["thumbv6m-none-eabi", "riscv32-unknown-elf"])
        {
            var (exitCode, output) = Run(new ClangToolchain().FindClang(),
                [$"--target={triple}", "-ffreestanding", "-std=c11", "-Wall", "-Werror", "-c", "use.c", "-o", $"use.{triple}.o"]);
            Assert.True(exitCode == 0, $"{triple}: {output}");
            (exitCode, output) = Run(new ClangToolchain().FindClang(),
                [$"--target={triple}", "-ffreestanding", "-std=c11", "-Wall", "-Werror", "-c", "rp2040_vectors.c", "-o", $"vectors.{triple}.o"]);
            Assert.True(exitCode == 0, $"{triple}: {output}");
        }

        // the table: a handler a program defines is its entry, the others the default
        var vectors = files.Single(f => f.FileName == "rp2040_vectors.c").Content;
        Assert.Contains("    rp2040_irq_TIMER_IRQ_1,   // 1\n", vectors);
        Assert.Contains("void rp2040_irq_IO_IRQ_BANK0(void) __attribute__((weak, alias(\"rp2040_irq_unhandled\")));\n", vectors);
        Assert.Contains("void (*const rp2040_irq_handlers[26])(void) =\n", vectors);
    }

    private (int ExitCode, string Output) Run(string tool, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(tool)
        {
            WorkingDirectory = _scratch, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result + errors);
    }

    [GeneratedRegex(@"^#define (\w+) ", RegexOptions.Multiline)]
    private static partial Regex Defined();

    [Fact]
    public void No_macro_is_defined_twice_in_a_header()
    {
        IEnumerable<GeneratedFile> all =
        [
            .. ChipFiles.Generated.SelectMany(ChipFiles.Headers),
            .. ChipHeaders.All(Svd.Read(ChipFiles.Svd), CDialect.Gnu, "RP2040.svd"),
        ];
        Assert.All(all.Where(f => f.FileName.EndsWith(".h", StringComparison.Ordinal)), file =>
        {
            var names = Defined().Matches(file.Content).Select(m => m.Groups[1].Value).ToList();
            Assert.True(names.Count == names.Distinct().Count(), $"{file.FileName}: " +
                string.Join(", ", names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).Take(5)));
        });
    }

    /// <summary>
    /// Espressif's SVD breaks a description's lines with two backslashes; one at the end of a line of C would make the next
    /// line part of its comment (the next field's mask, gone): the reader makes them "; ", and no line ends with one.
    /// </summary>
    [Fact]
    public void No_line_of_a_header_ends_with_a_backslash()
    {
        IEnumerable<GeneratedFile> all =
        [
            .. ChipFiles.Generated.SelectMany(ChipFiles.Headers),
            .. ChipHeaders.All(Svd.Read(ChipFiles.Svd), CDialect.Gnu, "RP2040.svd"),
        ];
        Assert.All(all, file => Assert.DoesNotContain(file.Content.Split('\n'), line => line.EndsWith('\\')));

        var registers = ChipFiles.Headers("esp32c5")[0].Content;
        Assert.Contains("#define LP_WDT_CONFIG0_WDT_EN_POS 31   // Configure whether or not to enable RWDT. 0:Disable; " +
            "1:Enable\n#define LP_WDT_CONFIG0_WDT_EN_MASK 0x80000000ul\n", registers);
    }

    /// <summary>
    /// The ESP32-C5's description: Espressif's SVD (its peripherals, registers and the interrupt matrix's sources), and
    /// what chips/esp32c5.json adds to it (the CLIC, the pins and their GPIO port, the constants); the headers compile with
    /// clang for the chip's RISC-V, and say what the start-up uses (the watchdogs).
    /// </summary>
    [Fact]
    public void The_ESP32_C5s_headers_from_its_SVD_and_description_compile_with_clang_for_its_RISC_V()
    {
        if (PicoTools.MissingLlvm.Value is not null)
        {
            return;
        }

        var files = ChipFiles.Headers("esp32c5");
        Directory.CreateDirectory(_scratch);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(_scratch, file.FileName), file.Content);
        }

        File.WriteAllText(Path.Combine(_scratch, "use.c"), """
            #include "esp32c5_registers.h"
            #include "esp32c5_interrupts.h"
            #include "esp32c5_pins.h"

            _Static_assert(LP_WDT_CONFIG0_ADDRESS == 0x600b1c00ul && LP_WDT_WPROTECT_ADDRESS == 0x600b1c18ul, "LP_WDT");
            _Static_assert(LP_WDT_SWD_CONFIG_ADDRESS == 0x600b1c1cul && LP_WDT_SWD_CONFIG_SWD_AUTO_FEED_EN_POS == 18, "SWD");
            _Static_assert(TIMG0_WDTCONFIG0_ADDRESS == 0x60008048ul && TIMG1_WDTWPROTECT_ADDRESS == 0x60009064ul, "TIMG");
            _Static_assert(TIMG0_WDTCONFIG0_WDT_FLASHBOOT_MOD_EN_MASK == (1ul << 14), "flash boot");
            _Static_assert(CLIC_INT_CTRL0_ADDRESS == 0x20801000ul && CLIC_INT_CTRL47_ADDRESS == 0x208010bcul, "CLIC");
            _Static_assert(CLIC_INT_CTRL17_IE_MASK == 0x100u && CLIC_INT_CTRL17_TRIG_FALLING == (0x3u << 17), "CLIC fields");
            _Static_assert(INTERRUPT_CORE0_INTR_MAP0_ADDRESS + 4 * ESP32C5_IRQ_SYSTIMER_TARGET1 == 0x600100f8ul && INTERRUPT_CORE0_INTR_MAP83_ADDRESS == 0x6001014cul,
                "a source's map register");
            _Static_assert(ESP32C5_IRQ_COUNT <= INTERRUPT_SOURCES && INTERRUPT_CORE0_INT_STATUS2_ADDRESS == 0x60010158ul, "the interrupt matrix");
            _Static_assert(ESP32C5_PIN_GPIO11 == 11 && ESP32C5_U0TXD_PIN == 11 && ESP32C5_U0RXD_PIN == 12, "UART0");
            _Static_assert(ESP32C5_PINS == 0x1ffffffful, "pins");

            void stop_the_watchdogs(void)
            {
                LP_WDT_WPROTECT = LP_WDT_WKEY_VALUE;
                LP_WDT_CONFIG0 &= ~(LP_WDT_CONFIG0_WDT_EN_MASK | LP_WDT_CONFIG0_WDT_FLASHBOOT_MOD_EN_MASK);
                LP_WDT_WPROTECT = 0;
                GPIO_OUT_W1TS = ESP32C5_PIN_GPIO27_MASK;
            }

            void esp32c5_irq_GPIO(void)
            {
                GPIO_STATUS_W1TC = GPIO_STATUS;
            }
            """);
        foreach (var source in (string[])["use.c", "esp32c5_vectors.c"])
        {
            var (exitCode, output) = Run(new ClangToolchain().FindClang(),
                ["--target=riscv32-unknown-elf", "-march=rv32imac_zicsr_zifencei", "-mabi=ilp32", "-ffreestanding",
                    "-std=c11", "-Wall", "-Werror", "-c", source, "-o", source + ".o"]);
            Assert.True(exitCode == 0, $"{source}: {output}");
        }

        var chip = ChipFiles.Read("esp32c5");
        Assert.Equal(("esp32c5", "rv32imac", true), (chip.Name, chip.Cpu, chip.InterruptMatrix));
        Assert.Equal("ESPRESSIF SYSTEMS (SHANGHAI) CO., LTD.", chip.Vendor);
        Assert.Equal(0x20800000UL, chip.Peripheral("CLIC")!.Base);
        Assert.Equal(0x60008000UL, chip.Peripheral("TIMG0")!.Base);
        Assert.Equal(0x60009048UL, chip.Register("TIMG1_WDTCONFIG0")!.Address); // TIMG1 is derived from TIMG0
        Assert.Equal(29, chip.Pins.Count);
        Assert.Equal(["GPIO_OUT", "GPIO_OUT_W1TS", "GPIO_OUT_W1TC", "GPIO_ENABLE", "GPIO_ENABLE_W1TS", "GPIO_ENABLE_W1TC",
            "GPIO_IN"], Assert.Single(chip.Ports).Registers.Values);
        Assert.Equal(82, chip.Interrupts.Max(i => i.Number)); // ECDSA: the SVD has no KM (83)
        Assert.DoesNotContain(chip.Registers, r => r.FullName == "INTERRUPT_CORE0_SYSTIMER_TARGET1_INTR_MAP"); // replaced
        Assert.Contains("    esp32c5_irq_GPIO,   // ", files.Single(f => f.FileName == "esp32c5_vectors.c").Content);
    }
}
