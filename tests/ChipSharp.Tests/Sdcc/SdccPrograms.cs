using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Targets;
using ChipSharp.Toolchain.Sdcc;

namespace ChipSharp.Tests.Sdcc;

/// <summary>
/// Hand-written C programs for the SDCC chips, as the C code generator and a runtime will give the toolchain: a main
/// file with an interrupt handler, a second C file with a variable that has a starting value, and an assembly file;
/// and their build with the toolchain "sdcc".
/// </summary>
internal static partial class SdccPrograms
{
    public static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    /// <summary>The second C file: a table in program memory and a variable set before main() runs.</summary>
    public const string Steps = """
        #include <stdint.h>

        static const uint8_t steps[] = { 1, 2, 4, 8, 16 };
        uint8_t offset = 3;

        uint8_t next_step(uint8_t i)
        {
            return steps[(uint8_t)(i + offset) % 5];
        }
        """;

    /// <summary>A PFS154 program: PA4 as an output, the interrupt counting ticks.</summary>
    public const string PfsBlink = """
        #include <stdint.h>

        __sfr __at(0x05) INTRQ;
        __sfr __at(0x10) PA;
        __sfr __at(0x11) PAC;

        volatile uint8_t ticks;
        uint16_t pattern[4] = { 0x0110, 0x0220, 0x0440, 0x0880 };

        extern uint8_t next_step(uint8_t i);
        extern uint8_t answer(void);

        void interrupt(void) __interrupt(0)
        {
            INTRQ = 0;
            ticks++;
        }

        void main(void)
        {
            PAC = 0x10;
            for (;;)
            {
                PA = next_step(ticks) ^ (uint8_t)pattern[ticks & 3] ^ answer();
            }
        }
        """;

    /// <summary>The assembly file for the PDK14: answer() returns 42.</summary>
    public const string PdkAnswer = """
            .module answer
            .globl _answer
            .area CODE
        _answer:
            ret #0x2a
        """;

    /// <summary>An STM8S103 program: the LED on PB5 as an output, TIM4's update interrupt counting ticks.</summary>
    public const string StmBlink = """
        #include <stdint.h>

        #define PB_ODR (*(volatile uint8_t *)0x5005)
        #define PB_DDR (*(volatile uint8_t *)0x5007)
        #define PB_CR1 (*(volatile uint8_t *)0x5008)
        #define TIM4_SR (*(volatile uint8_t *)0x5344)

        volatile uint16_t ticks;
        uint16_t pattern[4] = { 0x0110, 0x0220, 0x0440, 0x0880 };

        extern uint8_t next_step(uint8_t i);
        extern uint8_t answer(void);

        void tim4_update(void) __interrupt(23)
        {
            TIM4_SR = 0;
            ticks++;
        }

        void main(void)
        {
            PB_DDR |= 1 << 5;
            PB_CR1 |= 1 << 5;
            for (;;)
            {
                PB_ODR = next_step((uint8_t)ticks) ^ (uint8_t)pattern[ticks & 3] ^ answer();
            }
        }
        """;

    /// <summary>The assembly file for the STM8: answer() returns 42 in A.</summary>
    public const string StmAnswer = """
            .module answer
            .globl _answer
            .area CODE
        _answer:
            ld a, #0x2a
            ret
        """;

    public static TargetDescription Pfs154 => Shipped.Load("pfs154-bare");

    public static TargetDescription Stm8s103 => Shipped.Load("stm8s103-board");

    /// <summary>
    /// The files of the program for <paramref name="target"/>, main()'s last (the toolchain links it first anyway),
    /// with <paramref name="extra"/> at the end of main()'s file.
    /// </summary>
    public static GeneratedCode Code(TargetDescription target, string extra = "")
    {
        var pdk = target.Setting("sdcc.port") == "pdk14";
        return new GeneratedCode(
        [
            new GeneratedFile("steps.c", Steps),
            new GeneratedFile("answer.s", pdk ? PdkAnswer : StmAnswer),
            new GeneratedFile("blink.c", (pdk ? PfsBlink : StmBlink) + "\n" + extra),
        ]);
    }

    /// <summary>Builds <paramref name="code"/> for <paramref name="target"/> as Blink in <paramref name="folder"/>, without the cache.</summary>
    public static LinkedBinary Build(TargetDescription target, string folder, GeneratedCode? code = null,
        SdccToolchainOptions? options = null) =>
        new SdccToolchain((options ?? new SdccToolchainOptions { UseCache = false }) with
        {
            OutputFolder = folder,
            ProgramName = "Blink",
        }).Build(code ?? Code(target), target);

    /// <summary>SDCC's own symbols in a map (s_CODE, l_CODE: an area's start and length), by name.</summary>
    public static Dictionary<string, ulong> MapSymbols(string map) => Symbol().Matches(File.ReadAllText(map))
        .GroupBy(match => match.Groups["name"].Value)
        .ToDictionary(group => group.Key,
            group => ulong.Parse(group.First().Groups["value"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    // "     00008038  _main                              main"
    [GeneratedRegex(@"^\s+(?<value>[0-9A-F]{8})\s+(?<name>[A-Za-z_.$][\w.$]*)", RegexOptions.Multiline)]
    private static partial Regex Symbol();
}
