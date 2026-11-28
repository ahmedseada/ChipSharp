using System.Globalization;
using System.Text;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// The arithmetic of every integer width, as clang compiles it for the ESP32-C5 (rv32imac: add, sub, mul, mulh, div,
/// rem, the shifts and their compressed forms; the 64-bit operations in pairs of registers with sltu's carries), run on
/// the simulator: each program computes a + b, a - b, a * b, a / b, a % b, a &lt;&lt; n and a &gt;&gt; n for 48 pairs
/// of edge and random values and reports every result, which must be what C's rules give (computed here: C's int is 32
/// bits, so the 8- and 16-bit operands are promoted to it). The 64-bit divisions are the program's own shift-and-subtract
/// (__udivdi3 and its kin: no compiler-rt here). 2,688 results in all.
/// </summary>
public sealed class Esp32c5ArithmeticTests : IDisposable
{
    private const int Pairs = 48;
    private static readonly string[] Operations = ["+", "-", "*", "/", "%", "<<", ">>"];

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => Scratch.Delete(_scratch);

    /// <summary>The 64-bit divisions clang calls for on rv32: shift and subtract, signed ones by their magnitudes.</summary>
    private const string Divisions = """
        typedef unsigned long long u64;
        typedef long long i64;
        static u64 divide(u64 n, u64 d, u64 *remainder)
        {
            u64 q = 0, r = 0;
            for (int bit = 63; bit >= 0; bit--)
            {
                r = (r << 1) | ((n >> bit) & 1);
                if (r >= d) { r -= d; q |= 1ull << bit; }
            }
            *remainder = r;
            return q;
        }
        u64 __udivdi3(u64 n, u64 d) { u64 r; return divide(n, d, &r); }
        u64 __umoddi3(u64 n, u64 d) { u64 r; divide(n, d, &r); return r; }
        i64 __divdi3(i64 n, i64 d)
        {
            u64 r, q = divide(n < 0 ? -(u64)n : (u64)n, d < 0 ? -(u64)d : (u64)d, &r);
            return (n < 0) != (d < 0) ? -(i64)q : (i64)q;
        }
        i64 __moddi3(i64 n, i64 d)
        {
            u64 r;
            divide(n < 0 ? -(u64)n : (u64)n, d < 0 ? -(u64)d : (u64)d, &r);
            return n < 0 ? -(i64)r : (i64)r;
        }
        """;

    [Esp32c5Theory]
    [InlineData("uint8_t")]
    [InlineData("int8_t")]
    [InlineData("uint16_t")]
    [InlineData("int16_t")]
    [InlineData("uint32_t")]
    [InlineData("int32_t")]
    [InlineData("uint64_t")]
    [InlineData("int64_t")]
    public async Task Every_operation_gives_what_C_gives(string type)
    {
        var bits = int.Parse(type.TrimStart('u', 'i', 'n', 't').TrimEnd('_', 't'), CultureInfo.InvariantCulture);
        var signed = type[0] == 'i';
        var (a, b, shifts) = Operands(bits, signed);
        var program = await Esp32c5Program.BuildAsync(Source(type, bits, signed, a, b, shifts), _scratch);

        var (run, values) = program.RunReports(500, bits == 64 ? 8 : 4);

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
        var random = new Random(bits * 2 + (signed ? 1 : 0));
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
        text.AppendLine("#include \"c5.h\"");
        if (bits == 64)
        {
            text.AppendLine(Divisions);
        }

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
            // an 8- or 16-bit operand becomes a 32-bit int: its shifts are by less than 32, and every result's low
            // bits are those of the 64-bit one
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
