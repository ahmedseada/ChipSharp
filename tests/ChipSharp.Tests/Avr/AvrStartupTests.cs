using System.Buffers.Binary;
using ChipSharp.Abstractions;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The avr runtime from reset on the ATmega328P (<see cref="AvrCpu"/>): the vector table, the reset handler's .data,
/// .rodata and .bss, the hooks of a chip's runtime, and how a program stops: SLEEP with interrupts off when it has
/// ended, BREAK when it failed (an exception .NET would throw, a stack overflow, an interrupt nobody handles). The test
/// program does what GPIOR0 says, a register the start-up leaves as it is.
/// </summary>
public sealed class AvrStartupTests
{
    private const int Gpior0 = 0x3E;

    private const string ProgramSource = """
        // chipsharp_entry checks what the start-up did, then does what GPIOR0 says: 0 returns, 1 fails, 2 overflows the
        // stack, 3 runs interrupt 1 (its handler is below) and then interrupt 2 (nobody's)
        #define GPIOR0 (*(volatile unsigned char *)0x3E)
        #define SREG (*(volatile unsigned char *)0x5F)
        #define SP (*(volatile unsigned int *)0x5D)

        void ChipSharp_Runtime_IndexOutOfRange(void);
        void chipsharp_stack_overflow(void);

        volatile long counter = 123456789;
        const volatile char text[] = "ChipSharp on AVR";
        volatile unsigned char zeros[64];
        volatile unsigned char chip_init_ran, before_halt_ran, irq1_ran, entry_sreg, entry_r1;
        volatile unsigned int entry_sp;

        void chipsharp_chip_init(void) { chip_init_ran++; }
        void chipsharp_before_halt(void) { before_halt_ran++; }
        __attribute__((signal)) void chipsharp_isr_irq1(void) { irq1_ran++; }

        long chipsharp_entry(void)
        {
            unsigned char r1;
            __asm__ volatile("mov %0, r1" : "=r"(r1));
            entry_r1 = r1;
            entry_sreg = SREG;
            entry_sp = SP;
            switch (GPIOR0)
            {
                case 1:
                    ChipSharp_Runtime_IndexOutOfRange();
                    break;
                case 2:
                    SP = 0x300;
                    chipsharp_stack_overflow();
                    break;
                case 3:
                    __asm__ volatile("call chipsharp_vectors + 4" ::: "memory");
                    __asm__ volatile("call chipsharp_vectors + 8" ::: "memory");
                    break;
            }

            for (int i = 0; i < 64; i++)
            {
                if (zeros[i] != 0)
                {
                    return 1;
                }
            }

            if (text[0] != 'C' || text[10] != 'o' || text[15] != 'R' || text[16] != 0)
            {
                return 2;
            }

            return counter;
        }
        """;

    private static readonly Lazy<Task<AvrProgram>> Built = new(() =>
        AvrProgram.BuildAsync([.. AvrProgram.RuntimeFiles, new GeneratedFile("startup_test.c", ProgramSource)]));

    [AvrFact]
    public async Task A_program_starts_as_compiled_code_expects_and_its_end_sleeps_with_interrupts_off()
    {
        var (program, cpu) = await StartAsync(mode: 0);

        Assert.Equal(AvrCpu.StopReason.Sleep, cpu.Run(1_000_000));

        Assert.Equal(123456789, Read32(program, cpu, "chipsharp_exit_code")); // .data, .rodata and .bss were right
        Assert.Equal(1, Byte(program, cpu, "chip_init_ran"));
        Assert.Equal(1, Byte(program, cpu, "before_halt_ran"));
        Assert.Equal(0, Byte(program, cpu, "entry_r1"));
        Assert.NotEqual(0, Byte(program, cpu, "entry_sreg") & 0x80); // interrupts on while the program runs
        Assert.InRange(Read16(program, cpu, "entry_sp"), 0x8F0, 0x8FE); // the stack starts at the end of RAM
        Assert.Equal(0, cpu.Data[AvrCpu.Sreg] & 0x80);
        Assert.Equal(0x9588, cpu.Program[cpu.Pc - 1]); // SLEEP
    }

    [AvrFact]
    public async Task A_failure_stops_at_BREAK_with_interrupts_off_and_stays_stopped()
    {
        var (program, cpu) = await StartAsync(mode: 1);

        Assert.Equal(AvrCpu.StopReason.Break, cpu.Run(1_000_000));

        Assert.Equal(0, cpu.Data[AvrCpu.Sreg] & 0x80);
        Assert.Equal(1, Byte(program, cpu, "before_halt_ran"));
        Assert.Equal(0, Read32(program, cpu, "chipsharp_exit_code"));
        Assert.Equal(AvrCpu.StopReason.Sleep, cpu.Run(1_000)); // without a debugger: SLEEP, forever
    }

    [AvrFact]
    public async Task A_stack_overflow_puts_the_stack_back_at_the_end_of_RAM_and_stops_as_a_failure()
    {
        var (_, cpu) = await StartAsync(mode: 2);

        Assert.Equal(AvrCpu.StopReason.Break, cpu.Run(1_000_000));

        Assert.InRange(cpu.Sp, 0x8F0, 0x8FF);
        Assert.Equal(0, cpu.Data[AvrCpu.Sreg] & 0x80);
    }

    [AvrFact]
    public async Task An_interrupt_runs_its_handler_and_one_nobody_handles_stops_as_a_failure()
    {
        var (program, cpu) = await StartAsync(mode: 3);

        Assert.Equal(AvrCpu.StopReason.Break, cpu.Run(1_000_000));

        Assert.Equal(1, Byte(program, cpu, "irq1_ran"));
        Assert.Equal(0, Read32(program, cpu, "chipsharp_exit_code"));
    }

    /// <summary>26 entries of two words at address 0, each a JMP: to the reset handler, a handler, or the failure stop.</summary>
    [AvrFact]
    public async Task The_vector_table_is_first_in_flash_a_jmp_for_each_of_the_chips_26_entries()
    {
        var program = await Built.Value;
        var cpu = program.Load();

        Assert.Equal(0, program.Address("chipsharp_vectors"));
        Assert.Equal(26 * 4, program.SizeOf("chipsharp_vectors"));
        for (var entry = 0; entry < 26; entry++)
        {
            Assert.Equal(0x940C, cpu.Program[entry * 2]);
            var expected = entry switch
            {
                0 => "chipsharp_reset",
                1 => "chipsharp_isr_irq1",
                _ => "chipsharp_unhandled_interrupt",
            };
            Assert.Equal(program.Address(expected) / 2, cpu.Program[entry * 2 + 1]);
        }
    }

    /// <summary>A core at reset with the test program, RAM and registers as anything, and <paramref name="mode"/> in GPIOR0.</summary>
    private static async Task<(AvrProgram Program, AvrCpu Cpu)> StartAsync(byte mode)
    {
        var program = await Built.Value;
        var cpu = program.Load();
        new Random(mode).NextBytes(cpu.Data);
        cpu.Data[AvrCpu.Sreg] = 0x80;
        cpu.Data[Gpior0] = mode;
        cpu.Pc = 0;
        return (program, cpu);
    }

    private static int Byte(AvrProgram program, AvrCpu cpu, string symbol) => cpu.Data[program.Address(symbol)];

    private static int Read16(AvrProgram program, AvrCpu cpu, string symbol) =>
        BinaryPrimitives.ReadUInt16LittleEndian(cpu.Data.AsSpan(program.Address(symbol)));

    private static int Read32(AvrProgram program, AvrCpu cpu, string symbol) =>
        BinaryPrimitives.ReadInt32LittleEndian(cpu.Data.AsSpan(program.Address(symbol)));
}
