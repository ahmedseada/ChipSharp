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
    public void The_kept_headers_are_what_the_generator_makes_of_the_chips_description(string chip)
    {
        var headers = ChipFiles.Headers(chip);
        var regenerate = Environment.GetEnvironmentVariable(ChipFiles.RegenerateVariable) == "1";

        Assert.Equal([$"{chip}_registers.h", $"{chip}_interrupts.h", $"{chip}_pins.h"], headers.Select(h => h.FileName));
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
}
