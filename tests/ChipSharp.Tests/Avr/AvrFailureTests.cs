using System.Text;
using ChipSharp.Abstractions;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The avr runtime's failures on the ATmega328P (<see cref="AvrCpu"/>): each says .NET's message through the hook
/// chipsharp_failure_write (a chip runtime's console; here the test program's, which keeps what it is given) and stops
/// at BREAK. The text the program gives may be in RAM or in flash (an address with bit 15 set). The test program does
/// what GPIOR0 says, a register the start-up leaves as it is.
/// </summary>
public sealed class AvrFailureTests
{
    private const string ProgramSource = """
        // chipsharp_failure_write keeps the message; chipsharp_entry fails as GPIOR0 says
        #define GPIOR0 (*(volatile unsigned char *)0x3E)

        void ChipSharp_Runtime_IndexOutOfRange(void);
        void ChipSharp_Runtime_LengthBelowZero(long value);
        void ChipSharp_Runtime_MinMaxInt64(long long min, long long max);
        void ChipSharp_Runtime_InvalidCast(const char *from, const char *to);
        void ChipSharp_Runtime_RoundingDigits(long maximum);
        void ChipSharp_Runtime_StackOverflow(void);
        void ChipSharp_Runtime_Unhandled(const void *text);
        void chipsharp_fail(const char *message);

        char message[600];
        unsigned int message_count;

        void chipsharp_failure_write(const char *text, unsigned int count)
        {
            while (count-- > 0 && message_count < sizeof message)
            {
                message[message_count++] = *text++;
            }
        }

        static const __flash char in_flash[] = "System.Collections.Generic.List`1[[Program+Node, Program]]";
        static char in_ram[] = "Program+Node";

        long chipsharp_entry(void)
        {
            switch (GPIOR0)
            {
                case 1: ChipSharp_Runtime_IndexOutOfRange(); break;
                case 2: ChipSharp_Runtime_LengthBelowZero(-12345); break;
                case 3: ChipSharp_Runtime_MinMaxInt64(-9223372036854775807LL - 1, 1234567890123LL); break;
                case 4: ChipSharp_Runtime_InvalidCast(in_ram, (const char *)(0x8000u | (unsigned int)in_flash)); break;
                case 5: chipsharp_fail("System.FormatException: The input string 'x' was not in a correct format."); break;
                case 6: ChipSharp_Runtime_StackOverflow(); break;
                case 7: ChipSharp_Runtime_RoundingDigits(15); break;
                case 8: ChipSharp_Runtime_Unhandled(0); break;
            }

            return 0;
        }
        """;

    private static readonly Lazy<Task<AvrCoreProgram>> Built = new(() =>
        AvrCoreProgram.BuildAsync([.. AvrCoreProgram.RuntimeFiles, new GeneratedFile("failure_test.c", ProgramSource)]));

    [AvrFact]
    public async Task Every_failure_says_dotnets_message_through_the_hook_and_stops_at_BREAK()
    {
        (byte Mode, string Message)[] cases =
        [
            (1, "Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds of the array.\r\n"),
            (2, "Unhandled exception. System.ArgumentOutOfRangeException: length ('-12345') must be a non-negative value. " +
                "(Parameter 'length')\r\nActual value was -12345.\r\n"),
            (3, "Unhandled exception. System.ArgumentException: '-9223372036854775808' cannot be greater than 1234567890123.\r\n"),
            (4, "Unhandled exception. System.InvalidCastException: Unable to cast object of type 'Node' to type " +
                "'System.Collections.Generic.List`1[[Program+Node, Program]]'.\r\n"),
            (5, "Unhandled exception. System.FormatException: The input string 'x' was not in a correct format.\r\n"),
            (6, "Stack overflow.\r\n"),
            (7, "Unhandled exception. System.ArgumentOutOfRangeException: Rounding digits must be between 0 and 15, " +
                "inclusive. (Parameter 'digits')\r\n"),
            (8, "Unhandled exception.\r\n"),
        ];
        var program = await Built.Value;
        foreach (var (mode, expected) in cases)
        {
            var cpu = program.Load();
            cpu.Data[0x3E] = mode;
            Assert.Equal(AvrCpu.StopReason.Break, cpu.Run(1_000_000));

            var count = cpu.Data[program.Address("message_count")] | (cpu.Data[program.Address("message_count") + 1] << 8);
            Assert.Equal(expected, Encoding.ASCII.GetString(cpu.Data, program.Address("message"), count));
        }
    }

    /// <summary>A failure's message is in flash, in a section of its own: a program has only those it can fail with.</summary>
    [AvrFact]
    public async Task A_program_has_the_messages_of_its_failures_only()
    {
        var program = await Built.Value;

        var flash = Encoding.ASCII.GetString(program.Flash);
        Assert.Contains("Index was outside the bounds of the array.", flash);
        Assert.DoesNotContain("Attempted to divide by zero.", flash);

        var cpu = program.Load();
        Assert.Equal(AvrCpu.StopReason.Sleep, cpu.Run(1_000_000)); // GPIOR0 0: no failure
        Assert.Contains("Program+Node", Encoding.ASCII.GetString(cpu.Data)); // .data, copied to RAM
        Assert.DoesNotContain("Index was outside", Encoding.ASCII.GetString(cpu.Data)); // the messages stay in flash
    }
}
