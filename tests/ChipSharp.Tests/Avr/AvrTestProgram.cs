using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Image.Hex;
using ChipSharp.Targets;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Avr;

/// <summary>A program built for the Arduino Uno: the linked .elf, the .hex made from it, and its memory report.</summary>
internal sealed record AvrProgram(string Elf, string Hex, MemoryReport Memory)
{
    public string Folder => Path.GetDirectoryName(Elf)!;
}

/// <summary>
/// A small program for the Arduino Uno written by hand, start-up and all, until the runtime "avr" is there: it proves the
/// chain from C to the simulator (the toolchain "zig", the linker script, the .hex, tools/avr-sim) without C#.
/// <see cref="Startup"/> is the vectors and the reset handler (the stack, .data copied from flash, .bss zeroed, then
/// test_main, then the stop ChipSharp's AVR programs end with: cli, sleep); <see cref="Main"/> writes a line from
/// .data (an ordinary constant, in RAM) and one from flash (__flash, address space 1) to USART0 at 9600 baud, toggles
/// D13 (PB5) six times, writes the count, and calls test_extra, which <see cref="NoExtra"/> leaves empty and a test may
/// give a body.
/// </summary>
internal static class AvrTestProgram
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    public static TargetDescription Uno => Targets.Load("uno");

    public const string Startup = """
        ; the interrupt vectors: the reset vector first (address 0), then the ATmega328P's 25 interrupts, none used
          .section .vectors,"ax",@progbits
          .global chipsharp_vectors
        chipsharp_vectors:
          jmp chipsharp_reset
          .rept 25
          jmp unused_interrupt
          .endr

          .section .text.chipsharp_reset,"ax",@progbits
          .global chipsharp_reset
        chipsharp_reset:
          clr r1                                  ; __zero_reg__, which clang's code takes to be 0
          out 0x3f, r1                            ; SREG: interrupts off
          ldi r28, lo8(__chipsharp_stack_top - 1) ; SP = RAMEND: push stores, then decrements
          ldi r29, hi8(__chipsharp_stack_top - 1)
          out 0x3e, r29
          out 0x3d, r28
          ; .data's starting values, from flash (LPM) to RAM
          ldi r26, lo8(__chipsharp_data_start)
          ldi r27, hi8(__chipsharp_data_start)
          ldi r30, lo8(__chipsharp_data_load)
          ldi r31, hi8(__chipsharp_data_load)
          ldi r24, lo8(__chipsharp_data_end)
          ldi r25, hi8(__chipsharp_data_end)
          rjmp 2f
        1:
          lpm r0, Z+
          st X+, r0
        2:
          cp r26, r24
          cpc r27, r25
          brne 1b
          ; .bss zeroed
          ldi r26, lo8(__chipsharp_bss_start)
          ldi r27, hi8(__chipsharp_bss_start)
          ldi r24, lo8(__chipsharp_bss_end)
          ldi r25, hi8(__chipsharp_bss_end)
          rjmp 4f
        3:
          st X+, r1
        4:
          cp r26, r24
          cpc r27, r25
          brne 3b
          call test_main
          ; the program has finished: sleep with interrupts off, which nothing wakes up from
          cli
          sleep
        5:
          rjmp 5b

          .section .text.unused_interrupt,"ax",@progbits
        unused_interrupt:
          break
          rjmp unused_interrupt
        """;

    public const string Main = """
        #include <stdint.h>

        #define REG(address) (*(volatile uint8_t *)(address))
        #define PIND REG(0x29)
        #define DDRB REG(0x24)
        #define PORTB REG(0x25)
        #define UCSR0A REG(0xC0)
        #define UCSR0B REG(0xC1)
        #define UCSR0C REG(0xC2)
        #define UBRR0L REG(0xC4)
        #define UBRR0H REG(0xC5)
        #define UDR0 REG(0xC6)

        static char greeting[] = "Hello AVR\n";              /* .data, or .rodata as nothing writes it: in RAM */
        static const __flash char flashText[] = "from flash\n"; /* address space 1: .progmem.data, in flash */
        static uint8_t toggles;                                /* .bss */

        void test_extra(void);

        void send(char c) {
          while (!(UCSR0A & (1 << 5))) {}                     /* UDRE0 */
          UDR0 = c;
        }

        char receive(void) {
          while (!(UCSR0A & (1 << 7))) {}                     /* RXC0 */
          return UDR0;
        }

        static void delay(void) {
          for (volatile uint16_t i = 0; i < 2000; i++) {}
        }

        void test_main(void) {
          UBRR0H = 0;
          UBRR0L = 103;                                       /* 9600 baud at 16 MHz: 16e6 / (16 * 104) */
          UCSR0C = 3 << 1;                                    /* 8N1 */
          UCSR0B = (1 << 4) | (1 << 3);                       /* RXEN0, TXEN0 */
          for (const char *c = greeting; *c; c++) send(*c);
          for (const __flash char *c = flashText; *c; c++) send(*c);
          DDRB |= 1 << 5;                                     /* PB5 = D13: an output, low */
          while (toggles < 6) {
            PORTB ^= 1 << 5;
            toggles++;
            delay();
          }

          send('0' + toggles);
          send('\n');
          test_extra();
        }
        """;

    public const string NoExtra = "void test_extra(void) {}\n";

    /// <summary>
    /// Builds the program with <paramref name="extra"/> as test_extra's file into <paramref name="folder"/>, as
    /// &lt;name&gt;.elf and &lt;name&gt;.hex, with the toolchain "zig" for the Uno (or <paramref name="target"/>).
    /// </summary>
    public static AvrProgram Build(string folder, string name = "Blink", string extra = NoExtra,
        TargetDescription? target = null, ClangToolchainOptions? options = null)
    {
        target ??= Uno;
        var toolchain = new ClangToolchain((options ?? new ClangToolchainOptions { UseCache = false }) with
        {
            Driver = ClangDriver.Zig, OutputFolder = folder, ProgramName = name,
        });
        var binary = toolchain.Build(new GeneratedCode(
        [
            new GeneratedFile("startup.S", Startup),
            new GeneratedFile("main.c", Main),
            new GeneratedFile("extra.c", "#include <stdint.h>\n" + extra),
        ]), target);

        var hex = Path.ChangeExtension(binary.Path, ".hex");
        using (var output = File.Create(hex))
        {
            new IntelHexImageFormat().Package(binary, target, output);
        }

        return new AvrProgram(binary.Path, hex, MemoryReport.Read(binary, target));
    }
}
