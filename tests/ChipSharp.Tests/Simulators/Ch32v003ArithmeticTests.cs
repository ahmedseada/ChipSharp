using System.Globalization;
using System.Text;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// The arithmetic of every integer width, as clang compiles it for the CH32V003's QingKe V2A (rv32ec_zicsr, and with XW,
/// rv32ec_zicsr_xwchc: no M, so * / % are calls of soft.c's __mulsi3, __divsi3, __muldi3, __udivdi3 and their kin; the
/// shifts, the 64-bit operations in pairs of registers with sltu's carries, the compressed forms and XW's byte and
/// halfword loads and stores), run on the simulator: each program computes a + b, a - b, a * b, a / b, a % b, a &lt;&lt; n
/// and a &gt;&gt; n for 48 pairs of edge and random values and reports every result, which must be what C's rules give
/// (computed here: C's int is 32 bits, so the 8- and 16-bit operands are promoted to it). 2,688 results for the eight
/// widths, 1,344 more for the 8- and 16-bit ones with XW.
/// </summary>
public sealed class Ch32v003ArithmeticTests : IDisposable
{
    private const int Pairs = 48;
    private static readonly string[] Operations = ["+", "-", "*", "/", "%", "<<", ">>"];

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => Scratch.Delete(_scratch);

    [Ch32v003Theory]
    [InlineData("uint8_t", "rv32ec_zicsr")]
    [InlineData("int8_t", "rv32ec_zicsr")]
    [InlineData("uint16_t", "rv32ec_zicsr")]
    [InlineData("int16_t", "rv32ec_zicsr")]
    [InlineData("uint32_t", "rv32ec_zicsr")]
    [InlineData("int32_t", "rv32ec_zicsr")]
    [InlineData("uint64_t", "rv32ec_zicsr")]
    [InlineData("int64_t", "rv32ec_zicsr")]
    [InlineData("uint8_t", "rv32ec_zicsr_xwchc")]
    [InlineData("int8_t", "rv32ec_zicsr_xwchc")]
    [InlineData("uint16_t", "rv32ec_zicsr_xwchc")]
    [InlineData("int16_t", "rv32ec_zicsr_xwchc")]
    public async Task Every_operation_gives_what_C_gives(string type, string march)
    {
        var bits = int.Parse(type.TrimStart('u', 'i', 'n', 't').TrimEnd('_', 't'), CultureInfo.InvariantCulture);
        var signed = type[0] == 'i';
        var (a, b, shifts) = Operands(bits, signed);
        var program = await Ch32v003CProgram.BuildAsync(Source(type, bits, signed, a, b, shifts), _scratch, march: march);

        var (run, values) = program.RunReports(2000, size: bits == 64 ? 8 : 4);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("the program has finished", run.Stopped, StringComparison.Ordinal);
        Assert.Equal(Pairs * Operations.Length, values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var (pair, operation) = (i / Operations.Length, Operations[i % Operations.Length]);
            var expected = Expected(operation, a[pair], b[pair], shifts[pair], bits, signed);
            Assert.True(expected == values[i], $"({type})({a[pair]} {operation} " +
                $"{(operation is "<<" or ">>" ? shifts[pair] : b[pair])}): expected 0x{expected:x}, the chip gave 0x{values[i]:x}");
        }
    }

    /// <summary>The operands: edge values, then random ones (the same every run); a divisor is never 0 nor MIN / -1.</summary>
    private static (long[] A, long[] B, int[] Shifts) Operands(int bits, bool signed)
    {
        var random = new Random(bits * 2 + (signed ? 1 : 0) + 100);
        var min = signed ? (bits == 64 ? long.MinValue : -(1L << (bits - 1))) : 0;
        var max = signed ? (bits == 64 ? long.MaxValue : (1L << (bits - 1)) - 1) : (bits == 64 ? -1 : (1L << bits) - 1);
        long[] edges = [0, 1, 2, 3, 7, 10, 100, max, max - 1, signed ? min : max / 2, signed ? -1 : max / 3, signed ? -7 : 0x55,
            signed ? min + 1 : 0x8000, 0x7F, 0x80, 0xFF, 0x7FFF];
        long Random() => Wrap((random.NextInt64() << 1) ^ random.Next(2), bits, signed) >> random.Next(bits);
        var a = new long[Pairs];
        var b = new long[Pairs];
        var shifts = new int[Pairs];
        for (var i = 0; i < Pairs; i++)
        {
            a[i] = Wrap(i < edges.Length ? edges[i] : Random(), bits, signed);
            b[i] = Wrap(i < edges.Length ? edges[(i * 5 + 3) % edges.Length] : Random(), bits, signed);
            if (b[i] == 0 || (signed && b[i] == -1 && a[i] == min))
            {
                b[i] = 3;
            }

            shifts[i] = i < 4 ? i : random.Next(Math.Max(bits, 32));
        }

        return (a, b, shifts);
    }

    private static string Source(string type, int bits, bool signed, long[] a, long[] b, int[] shifts)
    {
        var wide = bits == 64 ? (signed ? "int64_t" : "uint64_t") : (signed ? "int32_t" : "uint32_t");
        var text = new StringBuilder();
        text.AppendLine("#include \"ch32.h\"");
        text.AppendLine(CultureInfo.InvariantCulture, $"volatile {(bits == 64 ? "uint64_t" : "uint32_t")} out;");
        text.AppendLine(CultureInfo.InvariantCulture, $"static const {type} as[] = {{ {string.Join(", ", a.Select(v => Literal(v, type, bits)))} }};");
        text.AppendLine(CultureInfo.InvariantCulture, $"static const {type} bs[] = {{ {string.Join(", ", b.Select(v => Literal(v, type, bits)))} }};");
        text.AppendLine(CultureInfo.InvariantCulture, $"static const uint8_t ns[] = {{ {string.Join(", ", shifts)} }};");
        text.AppendLine("int main(void) {");
        text.AppendLine(CultureInfo.InvariantCulture, $"  for (int i = 0; i < {Pairs}; i++) {{");
        text.AppendLine(CultureInfo.InvariantCulture, $"    {type} a = as[i], b = bs[i]; uint8_t n = ns[i];");
        foreach (var operation in Operations)
        {
            var right = operation is "<<" or ">>" ? "n" : "b";
            text.AppendLine(CultureInfo.InvariantCulture, $"    out = ({wide})({type})(a {operation} {right}); report();");
        }

        text.AppendLine("  }");
        text.AppendLine("  return 0;");
        text.AppendLine("}");
        return text.ToString();
    }

    private static string Literal(long value, string type, int bits)
    {
        var mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        return string.Create(CultureInfo.InvariantCulture, $"({type})0x{(ulong)value & mask:x}ull");
    }

    /// <summary>What C gives for <c>(wide)(type)(a op b)</c>, with C's 32-bit int (and its promotions) on riscv32.</summary>
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
            result = (ulong)(operation switch
            {
                "+" => unchecked(a + b),
                "-" => unchecked(a - b),
                "*" => unchecked(a * b),
                "/" => a / b,
                "%" => a % b,
                "<<" => bits == 64 ? a << shift : (long)(int)((uint)a << shift),
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
