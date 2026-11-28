using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Backend.C;
using ChipSharp.Ir;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Simulators;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.CBackend;

/// <summary>
/// C# programs through the c code generator and SDCC, run on the simulated chips (chipsim, in process), with a stand-in
/// runtime whose Console.WriteLine(int) reports each number: what they print must be what .NET prints. On the PFS154 the
/// recursive function keeps its locals on the stack only because it is __reentrant; on the STM8, big endian, every
/// struct field and table entry is read back as the program wrote it.
/// </summary>
public sealed class CSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>
    /// For the PFS154's 128 bytes of RAM: recursion (Fib is __reentrant there, 8 bytes of stack a call, and each call
    /// checks that the next one has room: Fib(5) goes 5 calls deep), a struct copied and changed, and bytes at their edges
    /// (a static array's helpers pass it by value, 6 bytes a copy: the STM8's programs have tables).
    /// </summary>
    private const string Tiny = """
        using System;

        static class Program
        {
            struct Pair { public byte Low; public sbyte High; }

            static byte Fib(byte n) => n < 2 ? n : (byte)(Fib((byte)(n - 1)) + Fib((byte)(n - 2)));

            static Pair Make(byte i) => new Pair { Low = (byte)(i * 100), High = (sbyte)(i - 100) };

            static void Main()
            {
                Console.WriteLine(Fib(5));
                var p = Make(2);
                var q = p;
                q.Low += 100;
                Console.WriteLine(p.Low);
                Console.WriteLine(q.Low);
                Console.WriteLine(p.High);
                byte b = 200;
                b += 100;
                Console.WriteLine(b);
                sbyte s = -128;
                s--;
                Console.WriteLine(s);
                Console.WriteLine(p.High / 3);
                Console.WriteLine(p.High % 3);
                Console.WriteLine(p.High >> 1);
            }
        }
        """;

    /// <summary>Recursion, a struct copied and changed, a table of shorts, and bytes, shorts and ints at their edges.</summary>
    private const string Small = """
        using System;

        static class Program
        {
            struct Pair { public byte Low; public short Mid; public int High; }

            static readonly short[] Table = { -3, 1000, -32768, 32767 };

            static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

            static Pair Make(int i) => new Pair { Low = (byte)(i * 37), Mid = Table[i & 3], High = i * -100000 };

            static void Main()
            {
                Console.WriteLine(Fib(9));
                var p = Make(3);
                var q = p;
                q.Mid += 5;
                Console.WriteLine(p.Low);
                Console.WriteLine(p.Mid);
                Console.WriteLine(q.Mid);
                Console.WriteLine(p.High);
                int x = -7, y = 2;
                Console.WriteLine(x / y);
                Console.WriteLine(x % y);
                Console.WriteLine(x >> 1);
                Console.WriteLine((int)((uint)x >> 28));
                Console.WriteLine(int.MaxValue + y);
                byte b = 200;
                b += 100;
                Console.WriteLine(b);
                sbyte s = -128;
                s--;
                Console.WriteLine(s);
                ushort u = 65535;
                u++;
                Console.WriteLine(u);
                Console.WriteLine(x < y ? 1 : 0);
                Console.WriteLine((uint)x < (uint)y ? 1 : 0);
            }
        }
        """;

    /// <summary>Longs (64-bit arithmetic through SDCC's helpers), a struct with a long in it, and a loop over a table.</summary>
    private const string Longs = """
        using System;

        static class Program
        {
            struct Account { public byte Kind; public long Balance; public ushort Code; }

            static readonly int[] Steps = { 7, -11, 13, int.MinValue, int.MaxValue };

            static Account Open(byte kind, long balance) => new Account { Kind = kind, Balance = balance, Code = (ushort)(balance >> 20) };

            static void Divide(long big)
            {
                Console.WriteLine((int)(big >> 32));
                Console.WriteLine((int)big);
                Console.WriteLine((int)(big / -7));
                Console.WriteLine((int)(big % 1000));
            }

            static void Main()
            {
                long big = 3_000_000_000L * 3;
                Divide(big);
                var account = Open(2, big);
                account.Balance -= 1L << 40;
                Console.WriteLine(account.Kind);
                Console.WriteLine((int)(account.Balance >> 8));
                Console.WriteLine(account.Code);
                long sum = 0;
                for (var i = 0; i < Steps.Length; i++)
                {
                    sum += Steps[i] * (long)(i + 1);
                }

                Console.WriteLine((int)sum);
                Console.WriteLine((int)(sum >> 32));
                ulong mask = ulong.MaxValue >> 3;
                Console.WriteLine((int)(mask >> 40));
            }
        }
        """;

    public static TheoryData<string, string> Programs => new()
    {
        { "pfs154-bare", nameof(Tiny) },
        { "stm8s103-board", nameof(Small) },
        { "stm8s103-board", nameof(Longs) },
    };

    [SdccTheory]
    [MemberData(nameof(Programs))]
    public async Task A_program_through_C_prints_on_the_simulated_chip_what_dotnet_prints(string board, string name)
    {
        var source = name switch { nameof(Tiny) => Tiny, nameof(Small) => Small, _ => Longs };
        var target = CSdcc.With(board == "pfs154-bare" ? CSdcc.Pfs154 : CSdcc.Stm8s103, "console", "true");
        var (module, c) = CSdcc.Required(source, name, target);
        if (board == "pfs154-bare")
        {
            Assert.Contains("static uint8_t Program_Fib(uint8_t p_n) CS_REENTRANT;", c);
        }

        var program = ChipProgram.Build(board, Runtime(module), Path.Combine(_scratch, board), new GeneratedFile(name + ".c", c));
        var (run, values) = program.RunReports(5_000, 4);

        Assert.True(run.ExitCode == 0, run.ToString());
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        var expected = dotnet.Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, values.Select(value => ((int)(uint)value).ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// main(), which runs the program, and Console.WriteLine of an int, which puts the number in <c>out</c> and calls
    /// report() (where the test reads it); every other external function of the program does nothing.
    /// </summary>
    private static string Runtime(TirModule module)
    {
        var text = new StringBuilder("""
            #include <stdint.h>

            volatile uint32_t out;
            void report(void);
            int chipsharp_entry(void);

            void ChipSharp_Runtime_WriteLineInt32(uint32_t value)
            {
                out = value;
                report();
            }

            void chipsharp_stack_overflow(void)
            {
                for (;;)
                {
                }
            }

            """.ReplaceLineEndings("\n"));
        var types = new CTypes(new TirLayout(2, 1));
        types.Name(module, new HashSet<string>(StringComparer.Ordinal));
        foreach (var function in module.Functions.Where(f => f.Kind == TirFunctionKind.External))
        {
            var symbol = CText.Symbol(function.Name);
            if (symbol is "ChipSharp_Runtime_WriteLineInt32" or "memset" or "memcpy" or "memmove")
            {
                continue;
            }

            var names = function.Parameters.Select((_, i) => "p" + i.ToString(CultureInfo.InvariantCulture)).ToList();
            text.Append(CFunctionWriter.Signature(function, types, symbol, names, reentrant: false)).Append("\n{\n");
            text.Append(string.Concat(names.Select(parameter => $"    (void){parameter};\n")));
            text.Append(function.ReturnType == TirType.Void ? "" : "    return 0;\n").Append("}\n\n");
        }

        // on the PFS154, the watchdog off and the system clock IHRC / 8 (2 MHz), as the board's, before main(); and after
        // the program the chip stops (interrupts off: nothing wakes it), which the simulator takes as the program's end
        return text.Append("#ifdef __SDCC_pdk14\n__sfr __at(0x03) CLKMD;\n" +
            "unsigned char __sdcc_external_startup(void) { CLKMD = 0x3c; return 0; }\n#endif\n\n" +
            "void main(void)\n{\n    chipsharp_entry();\n#ifdef __SDCC_pdk14\n" +
            "    __asm__(\"disgint\\n stopsys\");\n#else\n    __asm__(\"sim\\n halt\");\n#endif\n}\n").ToString();
    }
}
