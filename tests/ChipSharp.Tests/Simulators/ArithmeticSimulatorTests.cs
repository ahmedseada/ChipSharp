using System.Globalization;
using System.Text;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// The arithmetic of every integer width, as SDCC compiles it for each chip (inline, or by its library's helpers:
/// _mulint, _divslong, _modulong, _rlulong, ...), run on the simulators: each program computes a + b, a - b, a * b,
/// a / b, a % b, a &lt;&lt; n and a &gt;&gt; n for pairs of edge and random values, and reports every result, which
/// must be what C's rules give (computed here: C's int is 16 bits on both chips, so 8-bit operands are promoted to it).
/// </summary>
public sealed class ArithmeticSimulatorTests : IDisposable
{
    private const int Pairs = 20;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private const string All = "+ - * / % << >>";

    // the PFS154's 128 bytes of RAM hold the parameters of one 32-bit helper at a time (SDCC gives each its own)
    [SdccTheory]
    [InlineData("pfs154-bare", "uint8_t", All)]
    [InlineData("pfs154-bare", "int8_t", All)]
    [InlineData("pfs154-bare", "uint16_t", All)]
    [InlineData("pfs154-bare", "int16_t", All)]
    [InlineData("pfs154-bare", "uint32_t", "+ - *")]
    [InlineData("pfs154-bare", "uint32_t", "/")]
    [InlineData("pfs154-bare", "uint32_t", "%")]
    [InlineData("pfs154-bare", "uint32_t", "<< >>")]
    [InlineData("pfs154-bare", "int32_t", "+ - *")]
    [InlineData("pfs154-bare", "int32_t", "/")]
    [InlineData("pfs154-bare", "int32_t", "%")]
    [InlineData("pfs154-bare", "int32_t", "<< >>")]
    [InlineData("stm8s103-board", "uint8_t", All)]
    [InlineData("stm8s103-board", "int8_t", All)]
    [InlineData("stm8s103-board", "uint16_t", All)]
    [InlineData("stm8s103-board", "int16_t", All)]
    [InlineData("stm8s103-board", "uint32_t", All)]
    [InlineData("stm8s103-board", "int32_t", All)]
    [InlineData("stm8s103-board", "uint64_t", All)]
    [InlineData("stm8s103-board", "int64_t", All)]
    public void Every_operation_gives_what_C_gives(string board, string type, string operations)
    {
        var ops = operations.Split(' ');
        var bits = int.Parse(type.TrimStart('u', 'i', 'n', 't').TrimEnd('_', 't'), CultureInfo.InvariantCulture);
        var signed = type[0] == 'i';
        var (a, b, shifts) = Operands(bits, signed);
        var program = ChipProgram.Build(board, Source(board, type, bits, signed, a, b, shifts, ops), _scratch);

        var (run, values) = program.RunReports(20_000, bits == 64 ? 8 : 4);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("the program has finished", run.Stopped, StringComparison.Ordinal);
        var expected = new List<ulong>();
        for (var i = 0; i < Pairs; i++)
        {
            expected.AddRange(ops.Select(operation => Expected(operation, a[i], b[i], shifts[i], bits, signed)));
        }

        Assert.Equal(expected.Count, values.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var (pair, operation) = (i / ops.Length, ops[i % ops.Length]);
            Assert.True(expected[i] == values[i], $"({type})({a[pair]} {operation} " +
                $"{(operation is "<<" or ">>" ? shifts[pair] : b[pair])}): expected 0x{expected[i]:x}, the chip gave 0x{values[i]:x}");
        }
    }

    /// <summary>The operands: edge values, then random ones (the same every run); a divisor is never 0 nor MIN / -1.</summary>
    private static (long[] A, long[] B, int[] Shifts) Operands(int bits, bool signed)
    {
        var random = new Random(bits * 2 + (signed ? 1 : 0));
        var min = signed ? (bits == 64 ? long.MinValue : -(1L << (bits - 1))) : 0;
        var max = signed ? (bits == 64 ? long.MaxValue : (1L << (bits - 1)) - 1) : (bits == 64 ? -1 : (1L << bits) - 1);
        long[] edges = [0, 1, 2, 3, 7, 10, 100, max, max - 1, signed ? min : max / 2, signed ? -1 : max / 3, signed ? -7 : 0x55];
        long Random() => Wrap((random.NextInt64() << 1) ^ random.Next(2), bits, signed);
        var a = new long[Pairs];
        var b = new long[Pairs];
        var shifts = new int[Pairs];
        for (var i = 0; i < Pairs; i++)
        {
            a[i] = i < edges.Length ? edges[i] : Random();
            b[i] = i < edges.Length ? edges[(i * 5 + 3) % edges.Length] : Random();
            if (b[i] == 0 || (signed && b[i] == -1 && a[i] == min && bits >= 16))
            {
                b[i] = 3;
            }

            shifts[i] = i < 4 ? i : random.Next(Math.Max(bits, 16));
        }

        return (a, b, shifts);
    }

    /// <summary>The program: the operands in constant tables, each result stored in out, then report() called.</summary>
    private static string Source(string board, string type, int bits, bool signed, long[] a, long[] b, int[] shifts,
        string[] operations)
    {
        var wide = bits == 64 ? (signed ? "int64_t" : "uint64_t") : (signed ? "int32_t" : "uint32_t");
        var pdk = board.StartsWith("pfs154", StringComparison.Ordinal);
        var text = new StringBuilder();
        text.AppendLine("#include <stdint.h>");
        if (pdk)
        {
            // the watchdog off, the system clock IHRC / 8 (2 MHz), as the pfs154-bare board's
            text.AppendLine("__sfr __at(0x03) CLKMD;");
            text.AppendLine("unsigned char __sdcc_external_startup(void) { CLKMD = 0x3c; return 0; }");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"volatile {(bits == 64 ? "uint64_t" : "uint32_t")} out;");
        text.AppendLine("void report(void);");
        text.AppendLine(CultureInfo.InvariantCulture, $"static const {type} as[] = {{ {string.Join(", ", a.Select(v => Literal(v, type, bits)))} }};");
        text.AppendLine(CultureInfo.InvariantCulture, $"static const {type} bs[] = {{ {string.Join(", ", b.Select(v => Literal(v, type, bits)))} }};");
        text.AppendLine(CultureInfo.InvariantCulture, $"static const uint8_t ns[] = {{ {string.Join(", ", shifts)} }};");
        text.AppendLine("void main(void) {");
        text.AppendLine("  uint8_t i;");
        text.AppendLine(CultureInfo.InvariantCulture, $"  for (i = 0; i < {Pairs}; i++) {{");
        text.AppendLine(CultureInfo.InvariantCulture, $"    {type} a = as[i], b = bs[i]; uint8_t n = ns[i];");
        foreach (var operation in operations)
        {
            var right = operation is "<<" or ">>" ? "n" : "b";
            text.AppendLine(CultureInfo.InvariantCulture, $"    out = ({wide})({type})(a {operation} {right}); report();");
        }

        text.AppendLine("  }");
        text.AppendLine(pdk ? "  __asm__(\"disgint\\n stopsys\");" : "  __asm__(\"sim\\n halt\");");
        text.AppendLine("}");
        return text.ToString();
    }

    private static string Literal(long value, string type, int bits)
    {
        var mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        return string.Create(CultureInfo.InvariantCulture, $"({type})0x{(ulong)value & mask:x}ull");
    }

    /// <summary>What C gives for <c>(wide)(type)(a op b)</c>, with C's 16-bit int (and its promotions) on these chips.</summary>
    private static ulong Expected(string operation, long a, long b, int shift, int bits, bool signed)
    {
        ulong result;
        if (bits == 64 && !signed)
        {
            var (x, y) = ((ulong)a, (ulong)b);
            result = operation switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                "/" => x / y,
                "%" => x % y,
                "<<" => x << shift,
                _ => x >> shift,
            };
        }
        else
        {
            // every narrower type's result fits a long before it is cut to its width (8-bit operands become 16-bit
            // ints, whose shifts are by less than 16)
            result = (ulong)(operation switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => unchecked(a * b),
                "/" => a / b,
                "%" => a % b,
                "<<" => a << shift,
                _ => a >> shift,
            });
        }

        var cut = Wrap((long)result, bits, signed);
        return bits == 64 ? (ulong)cut : signed ? (uint)(int)cut : (uint)cut;
    }

    private static long Wrap(long value, int bits, bool signed) => bits switch
    {
        8 => signed ? (sbyte)value : (byte)value,
        16 => signed ? (short)value : (ushort)value,
        32 => signed ? (int)value : (uint)value,
        _ => value,
    };
}
