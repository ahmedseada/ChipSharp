using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Build;
using ChipSharp.Image.Hex;

using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// C# built for the Arduino Uno the whole way, with a small runtime written here until the runtime "avr" is there: its
/// literals, tables and spans of constant data stay in flash, which it reads through addresses with bit 15 set as the
/// runtime is to (<see cref="LlvmFlash"/>), and in the avr8js simulator it prints what .NET prints.
/// </summary>
public sealed class AvrProgramTests : IDisposable
{
    /// <summary>Text, tables, a span of constant data and one of RAM, a struct through <c>this</c>, a field holding a literal.</summary>
    private const string Program = """
        using System;

        class Program
        {
            static readonly byte[] Digits = { 0x3F, 0x06, 0x5B, 0x4F };
            static string title = "Uno";

            static ReadOnlySpan<byte> Table => new byte[] { 10, 20, 30 };

            struct Counter
            {
                public int Value;

                public void Add(int n) { Value += n; }
            }

            static string Name(int i) => i switch { 0 => "zero", 1 => "one", _ => "many" };

            static int Sum(ReadOnlySpan<byte> values)
            {
                int sum = 0;
                foreach (var value in values)
                {
                    sum += value;
                }

                return sum;
            }

            static void Main()
            {
                Console.WriteLine(title);
                for (int i = 0; i < Digits.Length; i++)
                {
                    Console.WriteLine(Digits[i]);
                }

                Console.WriteLine(Sum(Table));
                Span<byte> ram = stackalloc byte[2];
                ram[0] = 1;
                ram[1] = 2;
                Console.WriteLine(Sum(ram));
                var counter = new Counter();
                counter.Add(5);
                counter.Add(7);
                Console.WriteLine(counter.Value);
                for (int i = 0; i < 3; i++)
                {
                    Console.Write(Name(i));
                    Console.Write(" ");
                }

                Console.WriteLine();
            }
        }
        """;

    /// <summary>What .NET prints for <see cref="Program"/>.</summary>
    private const string Expected = "Uno\n63\n6\n91\n79\n60\n3\n12\nzero one many \n";

    /// <summary>
    /// The runtime functions the program calls, writing to USART0 at 9600 baud as \n-ended lines, and the string
    /// descriptors: an address the program passes may have bit 15 set, and then is one in flash (read with LPM).
    /// </summary>
    private const string Runtime = """
        #include <stdint.h>

        #define REG(address) (*(volatile uint8_t *)(address))
        #define UCSR0A REG(0xC0)
        #define UCSR0B REG(0xC1)
        #define UCSR0C REG(0xC2)
        #define UBRR0L REG(0xC4)
        #define UBRR0H REG(0xC5)
        #define UDR0 REG(0xC6)

        typedef struct chipsharp_type
        {
            uint32_t size, element_size, flags, reference_count;
            const void *references, *base, *name, *interfaces;
            uint32_t interface_count, virtual_count;
        } chipsharp_type;

        const chipsharp_type chipsharp_string_type = {8, 1, 1, 0, 0, 0, 0, 0, 0, 0};
        const chipsharp_type chipsharp_wide_string_type = {8, 2, 1, 0, 0, 0, 0, 0, 0, 0};

        int chipsharp_entry(void);

        /* a byte the program's address points to: in flash when bit 15 is set, else in RAM */
        static uint8_t read8(const void *address)
        {
            uint16_t bits = (uint16_t)address;
            return bits & 0x8000u ? *(const __flash uint8_t *)(bits & 0x7FFFu) : *(const uint8_t *)address;
        }

        static uint16_t read16(const void *address)
        {
            return read8(address) | (uint16_t)read8((const uint8_t *)address + 1) << 8;
        }

        static void send(char c)
        {
            while (!(UCSR0A & (1 << 5))) {}
            UDR0 = c;
        }

        void ChipSharp_Runtime_WriteLine(void) { send('\n'); }

        void ChipSharp_Runtime_WriteString(const void *s)
        {
            if (!s) return;
            const chipsharp_type *type = (const chipsharp_type *)read16(s);
            uint16_t length = read16((const uint8_t *)s + 2);
            for (uint16_t i = 0; i < length; i++)
            {
                send(type->element_size == 2 ? (char)read16((const uint8_t *)s + 8 + 2 * i) : read8((const uint8_t *)s + 8 + i));
            }
        }

        void ChipSharp_Runtime_WriteLineString(const void *s) { ChipSharp_Runtime_WriteString(s); send('\n'); }

        void ChipSharp_Runtime_WriteInt32(int32_t value)
        {
            static const __flash uint32_t tens[] = {1000000000, 100000000, 10000000, 1000000, 100000, 10000, 1000, 100, 10, 1};
            uint32_t magnitude = value < 0 ? 0u - (uint32_t)value : (uint32_t)value;
            if (value < 0) send('-');
            uint8_t started = 0;
            for (uint8_t i = 0; i < 10; i++)
            {
                char digit = '0';
                while (magnitude >= tens[i]) { magnitude -= tens[i]; digit++; }
                if (digit != '0' || started || i == 9) { send(digit); started = 1; }
            }
        }

        void ChipSharp_Runtime_WriteLineInt32(int32_t value) { ChipSharp_Runtime_WriteInt32(value); send('\n'); }

        static void stop(void) { for (;;) __asm__ volatile("break"); }
        void ChipSharp_Runtime_IndexOutOfRange(void) { stop(); }
        void ChipSharp_Runtime_ArgumentOutOfRange(void) { stop(); }
        void ChipSharp_Runtime_Overflow(void) { stop(); }
        void ChipSharp_Runtime_DivideByZero(void) { stop(); }
        void ChipSharp_Runtime_LengthOutOfRange(void) { stop(); }

        void test_main(void)
        {
            UBRR0H = 0;
            UBRR0L = 103;
            UCSR0C = 3 << 1;
            UCSR0B = (1 << 4) | (1 << 3);
            chipsharp_entry();
        }
        """;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [AvrFact]
    public void The_program_links_with_its_constant_data_in_flash_below_0x8000_and_only_its_fields_in_RAM()
    {
        var (binary, memory, ll) = Build();

        // the literals and tables: in flash's section, read through flash addresses; no constant is copied to RAM
        Assert.Contains("section \".progmem.data\"", ll, StringComparison.Ordinal);
        Assert.Contains("ptr addrspace(1)", ll, StringComparison.Ordinal);
        var symbols = Symbols(binary.Path);
        var flashData = symbols.Where(symbol => symbol.Name.StartsWith("Program_Digits", StringComparison.Ordinal)
            || symbol.Name.StartsWith("_text_", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(flashData);
        Assert.All(flashData, symbol => Assert.InRange(symbol.Address, 0x68UL, 0x7FFFUL)); // after the 26 vectors
        // RAM holds only the runtime's string descriptor (32 bytes, a C constant: copied there at start-up): the field
        // title only ever holds one literal, so LLVM makes it a constant too
        Assert.Equal(32UL, memory.Data!.Used);
        Assert.Equal(["chipsharp_string_type"], symbols
            .Where(symbol => symbol.Address >= 0x800000 && !symbol.Name.StartsWith("__chipsharp_", StringComparison.Ordinal))
            .Select(symbol => symbol.Name));
    }

    [AvrSimulatorFact]
    public async Task The_program_prints_over_USART0_what_dotnet_prints()
    {
        var (binary, _, _) = Build();
        var hex = Path.ChangeExtension(binary.Path, ".hex");
        await using (var output = File.Create(hex))
        {
            new IntelHexImageFormat().Package(binary, AvrCode.Uno(), output);
        }

        var run = await AvrSimulator.RunAsync(hex, 2000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(Expected, Encoding.ASCII.GetString(run.Usart0));
    }

    private (LinkedBinary Binary, MemoryReport Memory, string Ll) Build()
    {
        var target = AvrCode.Uno();
        var dll = CSharpCompiler.CompileToFile(Program, Path.Combine(_scratch, "UnoText.dll"));
        var module = ChipSharpBuilder.Translate(dll, target);
        var ll = Assert.Single(new LlvmCodeGenerator().Generate(module, target).Files);
        var toolchain = new ClangToolchain(new ClangToolchainOptions
        {
            UseCache = false, Driver = ClangDriver.Zig, OutputFolder = _scratch, ProgramName = "UnoText",
        });
        var binary = toolchain.Build(new GeneratedCode(
        [
            new GeneratedFile("startup.S", AvrTestProgram.Startup),
            new GeneratedFile("runtime.c", Runtime),
            ll,
        ]), target);
        return (binary, MemoryReport.Read(binary, target), ll.Content);
    }

    /// <summary>The symbols of an .elf and their addresses, as llvm-nm lists them.</summary>
    private static List<(string Name, ulong Address)> Symbols(string elf)
    {
        var result = ChipSharp.Tests.Llvm.LlvmTools.RunAsync(PicoTools.LlvmTool("llvm-nm"), [elf], Path.GetDirectoryName(elf)!)
            .GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0, result.ToString());
        return
        [
            .. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' '))
                .Where(parts => parts.Length == 3)
                .Select(parts => (parts[2], Convert.ToUInt64(parts[0], 16))),
        ];
    }
}
