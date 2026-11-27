using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// Float and double arithmetic on the ATmega328P: compiler-rt's soft-float routines as the avr runtime ships them
/// (__addsf3, __muldf3, __ltdf2, __fixdfsi, __floatsidf, ...) and the shared float math (fmod, Math.Sqrt, Math.Round,
/// ...), called the way LLVM calls them, from functions in LLVM IR as ChipSharp's code generator writes them (<c>fadd double</c>, <c>fcmp olt</c>, <c>fptosi</c>, the saturating
/// <c>llvm.fptosi.sat</c> of C#'s casts), run on <see cref="AvrCpu"/> and checked against .NET for the special values
/// and random ones: every result must have the bits .NET's has (any NaN for a NaN). This is where AVR differs from the
/// PC the same files are checked on (SoftFloatTests): a 16-bit int, comparisons that return a byte, and a double that
/// is 64 bits only with -mdouble=64.
/// </summary>
public sealed class AvrSoftFloatTests
{
    private const int RandomCases = 1_500;

    private static readonly Operation[] Operations =
    [
        Binary("fadd", "float", (a, b) => Of(F(a) + F(b))),
        Binary("fsub", "float", (a, b) => Of(F(a) - F(b))),
        Binary("fmul", "float", (a, b) => Of(F(a) * F(b))),
        Binary("fdiv", "float", (a, b) => Of(F(a) / F(b))),
        Binary("fadd", "double", (a, b) => Of(D(a) + D(b))),
        Binary("fsub", "double", (a, b) => Of(D(a) - D(b))),
        Binary("fmul", "double", (a, b) => Of(D(a) * D(b))),
        Binary("fdiv", "double", (a, b) => Of(D(a) / D(b))),
        Compare("oeq", "float", (a, b) => F(a) == F(b)),
        Compare("one", "float", (a, b) => F(a) < F(b) || F(a) > F(b)),
        Compare("une", "float", (a, b) => F(a) != F(b)),
        Compare("olt", "float", (a, b) => F(a) < F(b)),
        Compare("ole", "float", (a, b) => F(a) <= F(b)),
        Compare("ogt", "float", (a, b) => F(a) > F(b)),
        Compare("oge", "float", (a, b) => F(a) >= F(b)),
        Compare("ult", "float", (a, b) => !(F(a) >= F(b))),
        Compare("uno", "float", (a, b) => float.IsNaN(F(a)) || float.IsNaN(F(b))),
        Compare("oeq", "double", (a, b) => D(a) == D(b)),
        Compare("one", "double", (a, b) => D(a) < D(b) || D(a) > D(b)),
        Compare("une", "double", (a, b) => D(a) != D(b)),
        Compare("olt", "double", (a, b) => D(a) < D(b)),
        Compare("ole", "double", (a, b) => D(a) <= D(b)),
        Compare("ogt", "double", (a, b) => D(a) > D(b)),
        Compare("oge", "double", (a, b) => D(a) >= D(b)),
        Compare("ult", "double", (a, b) => !(D(a) >= D(b))),
        Compare("uno", "double", (a, b) => double.IsNaN(D(a)) || double.IsNaN(D(b))),
        Convert("fpext", "float", "double", a => Of((double)F(a))),
        Convert("fptrunc", "double", "float", a => Of((float)D(a))),
        Convert("sitofp", "i32", "float", a => Of((float)(int)a)),
        Convert("uitofp", "i32", "float", a => Of((float)(uint)a)),
        Convert("sitofp", "i64", "float", a => Of((float)(long)a)),
        Convert("uitofp", "i64", "float", a => Of((float)a)),
        Convert("sitofp", "i32", "double", a => Of((double)(int)a)),
        Convert("uitofp", "i32", "double", a => Of((double)(uint)a)),
        Convert("sitofp", "i64", "double", a => Of((double)(long)a)),
        Convert("uitofp", "i64", "double", a => Of((double)a)),
        Convert("sitofp", "i16", "double", a => Of((double)(short)a)),
        Convert("uitofp", "i8", "float", a => Of((float)(byte)a)),
        // C#'s casts to integers saturate (NaN is 0): llvm.fpto[su]i.sat, as the code generator writes them
        Saturate("fptosi", "float", "i32", a => (uint)(int)F(a)),
        Saturate("fptoui", "float", "i32", a => (uint)F(a)),
        Saturate("fptosi", "float", "i64", a => (ulong)(long)F(a)),
        Saturate("fptoui", "float", "i64", a => (ulong)F(a)),
        Saturate("fptosi", "double", "i32", a => (uint)(int)D(a)),
        Saturate("fptoui", "double", "i32", a => (uint)D(a)),
        Saturate("fptosi", "double", "i64", a => (ulong)(long)D(a)),
        Saturate("fptoui", "double", "i64", a => (ulong)D(a)),
        Saturate("fptosi", "double", "i16", a => (ushort)(short)Math.Clamp(double.IsNaN(D(a)) ? 0 : Math.Truncate(D(a)), short.MinValue, short.MaxValue)),
        Saturate("fptoui", "double", "i8", a => (byte)Math.Clamp(double.IsNaN(D(a)) ? 0 : Math.Truncate(D(a)), 0, 255)),
        // the shared float math (chipsharp_float_math.c), called as the code generator calls it
        Runtime("fmod", "double", 2, (a, b) => Of(D(a) % D(b))),
        Runtime("fmodf", "float", 2, (a, b) => Of(F(a) % F(b))),
        Runtime("ChipSharp_Runtime_SqrtFloat64", "double", 1, (a, _) => Of(Math.Sqrt(D(a)))),
        Runtime("ChipSharp_Runtime_SqrtFloat32", "float", 1, (a, _) => Of(MathF.Sqrt(F(a)))),
        Runtime("ChipSharp_Runtime_AbsFloat64", "double", 1, (a, _) => Of(Math.Abs(D(a)))),
        Runtime("ChipSharp_Runtime_MinFloat64", "double", 2, (a, b) => Of(Math.Min(D(a), D(b)))),
        Runtime("ChipSharp_Runtime_MaxFloat32", "float", 2, (a, b) => Of(MathF.Max(F(a), F(b)))),
        Runtime("ChipSharp_Runtime_FloorFloat64", "double", 1, (a, _) => Of(Math.Floor(D(a)))),
        Runtime("ChipSharp_Runtime_CeilingFloat32", "float", 1, (a, _) => Of(MathF.Ceiling(F(a)))),
        Runtime("ChipSharp_Runtime_TruncateFloat64", "double", 1, (a, _) => Of(Math.Truncate(D(a)))),
        Runtime("ChipSharp_Runtime_RoundFloat64", "double", 1, (a, _) => Of(Math.Round(D(a)))),
        Runtime("ChipSharp_Runtime_RoundFloat32", "float", 1, (a, _) => Of(MathF.Round(F(a)))),
        Runtime("ChipSharp_Runtime_BitIncrementFloat64", "double", 1, (a, _) => Of(Math.BitIncrement(D(a)))),
    ];

    private static readonly ulong[] DoubleSpecials =
    [
        .. FloatMathProgram.SpecialDoubles.Select(BitConverter.DoubleToUInt64Bits),
        0x7FF0000000000001, 0xFFF8000000000001, 0x0000000000000001, 0x8000000000000001, 0x7FEFFFFFFFFFFFFF,
        0x3FF0000000000001, 0x3FEFFFFFFFFFFFFF, 0x4330000000000000, 0x4330000000000001, 0x0010000000000001,
    ];

    private static readonly ulong[] FloatSpecials =
    [
        .. FloatMathProgram.SpecialFloats.Select(value => (ulong)BitConverter.SingleToUInt32Bits(value)),
        0x7F800001, 0xFFC00001, 0x00000001, 0x80000001, 0x7F7FFFFF, 0x3F800001, 0x3F7FFFFF, 0x4B000000, 0x4B000001,
        0x00800001,
    ];

    /// <summary>Two programs, as all of it is more than the 32 KB of flash: the arithmetic of LLVM IR, and the float math.</summary>
    private static readonly Lazy<Task<AvrCoreProgram>> Arithmetic = new(() => AvrCoreProgram.BuildAsync(
        [.. AvrCoreProgram.RuntimeFiles, new GeneratedFile("float_test.ll", HarnessIr())],
        Operations.Where(operation => operation.Ir.Length > 0).Select(operation => operation.Name)));

    private static readonly Lazy<Task<AvrCoreProgram>> FloatMath = new(() => AvrCoreProgram.BuildAsync(
        [.. AvrCoreProgram.RuntimeFiles, new GeneratedFile("float_test.ll", HarnessIr())],
        Operations.Where(operation => operation.Ir.Length == 0).Select(operation => operation.Name)));

    [AvrFact]
    [RunsLong]
    public async Task Every_float_and_double_operation_matches_dotnet_on_the_ATmega328P()
    {
        var (arithmetic, floatMath) = (await Arithmetic.Value, await FloatMath.Value);
        var mismatches = new System.Collections.Concurrent.ConcurrentBag<string>();
        var count = 0;
        Parallel.For(0, Operations.Length, op =>
        {
            var operation = Operations[op];
            var program = operation.Ir.Length > 0 ? arithmetic : floatMath;
            var cpu = program.Load();
            var random = new Random(op + 1);
            var address = program.Address(operation.Name);
            var wrong = 0;
            foreach (var (a, b) in Cases(operation, new Random(op + 100)))
            {
                var expected = operation.Dotnet(a, b) & Mask(operation.ResultSize);
                ulong actual = operation.Operands == 2
                    ? AvrAbi.Call(cpu, address, operation.ResultSize, random, (a, operation.OperandSize), (b, operation.OperandSize))
                    : AvrAbi.Call(cpu, address, operation.ResultSize, random, (a, operation.OperandSize));
                Interlocked.Increment(ref count);
                var same = expected == actual || operation.ResultKind switch
                {
                    "float" => float.IsNaN(F(expected)) && float.IsNaN(F(actual)),
                    "double" => double.IsNaN(D(expected)) && double.IsNaN(D(actual)),
                    _ => false,
                };
                if (!same && wrong++ < 3)
                {
                    mismatches.Add($"{operation.Name}(0x{a:X}, 0x{b:X}): .NET 0x{expected:X}, the runtime 0x{actual:X}");
                }
            }
        });

        Assert.True(mismatches.IsEmpty, string.Join('\n', mismatches.Order()));
        Assert.True(count > Operations.Length * RandomCases, $"{count} cases");
    }

    /// <summary>The special values (every pair for two operands), then random ones (see SoftFloatTests).</summary>
    private static IEnumerable<(ulong A, ulong B)> Cases(Operation operation, Random random)
    {
        if (operation.OperandKind is not ("float" or "double"))
        {
            var mask = Mask(operation.OperandSize);
            return IntegerCases().Concat(Enumerable.Range(0, RandomCases).Select(_ =>
                    ((ulong)random.NextInt64() >> random.Next(0, 64)) ^ (random.Next(2) == 0 ? 0 : ulong.MaxValue)))
                .Select(a => (a & mask, 0UL));
        }

        var single = operation.OperandKind == "float";
        var specials = single ? FloatSpecials : DoubleSpecials;
        var randoms = Enumerable.Range(0, RandomCases).Select(_ =>
        {
            var a = RandomValue(random, single, 0);
            return (a, RandomValue(random, single, a));
        });
        return operation.Operands == 2
            ? specials.SelectMany(a => specials.Select(b => (a, b))).Concat(randoms)
            : specials.Select(a => (a, 0UL)).Concat(randoms);
    }

    private static IEnumerable<ulong> IntegerCases()
    {
        foreach (var value in FloatMathProgram.SpecialInt64s)
        {
            yield return (ulong)value;
        }

        foreach (var value in FloatMathProgram.SpecialInt32s)
        {
            yield return (ulong)value;
            yield return (uint)value;
        }

        for (var bit = 0; bit < 64; bit++)
        {
            yield return 1UL << bit;
            yield return (1UL << bit) - 1;
            yield return (1UL << bit) + 1;
            yield return ~(1UL << bit);
        }
    }

    /// <summary>Any bits; a value a few exponents from <paramref name="other"/>'s; a subnormal; or a whole number.</summary>
    private static ulong RandomValue(Random random, bool single, ulong other)
    {
        var (fraction, exponentBits) = single ? (23, 8) : (52, 11);
        var bits = (ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63);
        if (single)
        {
            bits >>= 32;
        }

        var exponentMask = (1UL << exponentBits) - 1;
        var sign = bits & (1UL << (fraction + exponentBits));
        var fractionBits = bits & ((1UL << fraction) - 1);
        switch (random.Next(4))
        {
            case 1:
                var exponent = (long)((other >> fraction) & exponentMask) + random.Next(-30, 31);
                return sign | ((ulong)Math.Clamp(exponent, 0, (long)exponentMask - 1) << fraction) | fractionBits;
            case 2:
                return sign | (fractionBits >> random.Next(0, fraction));
            case 3:
                var whole = random.NextInt64() >> random.Next(0, 63);
                return single ? BitConverter.SingleToUInt32Bits(whole) : BitConverter.DoubleToUInt64Bits(whole);
            default:
                return bits;
        }
    }

    /// <summary>A function of LLVM IR per operation, named after it, e.g. <c>@fadd_double</c>.</summary>
    private static string HarnessIr()
    {
        var text = new StringBuilder("target triple = \"avr\"\n\ndefine i32 @chipsharp_entry() {\n  ret i32 0\n}\n\n");
        foreach (var operation in Operations)
        {
            text.Append(operation.Ir).Append('\n');
        }

        foreach (var declaration in Operations.Select(operation => operation.Declaration).OfType<string>().Distinct())
        {
            text.Append(declaration).Append('\n');
        }

        return text.ToString();
    }

    private static Operation Binary(string instruction, string type, Func<ulong, ulong, ulong> dotnet) =>
        new($"{instruction}_{type}", type, 2, type, dotnet,
            $"define {type} @{instruction}_{type}({type} %a, {type} %b) {{\n  %r = {instruction} {type} %a, %b\n  ret {type} %r\n}}\n");

    private static Operation Compare(string predicate, string type, Func<ulong, ulong, bool> dotnet) =>
        new($"fcmp_{predicate}_{type}", type, 2, "i8", (a, b) => dotnet(a, b) ? 1UL : 0UL,
            $"define i8 @fcmp_{predicate}_{type}({type} %a, {type} %b) {{\n  %c = fcmp {predicate} {type} %a, %b\n" +
            "  %r = zext i1 %c to i8\n  ret i8 %r\n}\n");

    private static Operation Convert(string instruction, string from, string to, Func<ulong, ulong> dotnet) =>
        new($"{instruction}_{from}_{to}", from, 1, to, (a, _) => dotnet(a),
            $"define {to} @{instruction}_{from}_{to}({from} %a) {{\n  %r = {instruction} {from} %a to {to}\n  ret {to} %r\n}}\n");

    /// <summary>A function of the runtime, called directly: its result has the type of its operands.</summary>
    private static Operation Runtime(string symbol, string type, int operands, Func<ulong, ulong, ulong> dotnet) =>
        new(symbol, type, operands, type, dotnet, "");

    private static Operation Saturate(string instruction, string from, string to, Func<ulong, ulong> dotnet)
    {
        var intrinsic = $"llvm.{instruction}.sat.{to}.f{(from == "float" ? 32 : 64)}";
        return new($"{instruction}_sat_{from}_{to}", from, 1, to, (a, _) => dotnet(a),
            $"define {to} @{instruction}_sat_{from}_{to}({from} %a) {{\n  %r = call {to} @{intrinsic}({from} %a)\n" +
            $"  ret {to} %r\n}}\n",
            $"declare {to} @{intrinsic}({from})");
    }

    private static int SizeOf(string type) => type switch
    {
        "float" => 4,
        "double" => 8,
        _ => int.Parse(type[1..], System.Globalization.CultureInfo.InvariantCulture) / 8,
    };

    private static ulong Mask(int bytes) => bytes == 8 ? ulong.MaxValue : (1UL << (8 * bytes)) - 1;

    private static float F(ulong bits) => BitConverter.UInt32BitsToSingle((uint)bits);

    private static double D(ulong bits) => BitConverter.UInt64BitsToDouble(bits);

    private static ulong Of(float value) => BitConverter.SingleToUInt32Bits(value);

    private static ulong Of(double value) => BitConverter.DoubleToUInt64Bits(value);

    /// <summary>
    /// A function of the harness: its name, what it takes (the type of its operands, one or two) and gives, what .NET
    /// computes, its IR, and the intrinsic it calls.
    /// </summary>
    private sealed record Operation(string Name, string OperandKind, int Operands, string ResultKind,
        Func<ulong, ulong, ulong> Dotnet, string Ir, string? Declaration = null)
    {
        public int OperandSize => SizeOf(OperandKind);

        public int ResultSize => SizeOf(ResultKind);
    }
}
