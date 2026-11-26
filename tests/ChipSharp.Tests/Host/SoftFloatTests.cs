using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Windows;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Host;

/// <summary>
/// The float math the runtimes ship, checked on the PC against .NET for millions of values: the Cortex-M0+'s
/// soft-float routines (compiler-rt's C, built with the software paths it takes on ARM, and the armv6m runtime's
/// comparisons) and the shared fmod, square roots, Abs, Min and Max. They are built for this PC into one DLL and
/// called directly; every result must have the bits .NET's has (any NaN for a NaN: NaN payloads differ between
/// processors, and .NET prints every NaN the same).
/// </summary>
public sealed class SoftFloatTests
{
    private const int RandomCases = 100_000;

    /// <summary>
    /// The DLL's one entry point: chipsharp_test_float(op, a, b) applies operation op (its place in
    /// <see cref="Operations"/>) to the bits a and b and returns the result's bits.
    /// </summary>
    private const string HarnessSource = """
        typedef unsigned int u32;
        typedef unsigned long long u64;

        int _fltused = 0; /* the MSVC target asks for it when C uses floating point */

        float __addsf3(float, float);
        float __subsf3(float, float);
        float __mulsf3(float, float);
        float __divsf3(float, float);
        double __adddf3(double, double);
        double __subdf3(double, double);
        double __muldf3(double, double);
        double __divdf3(double, double);
        double __extendsfdf2(float);
        float __truncdfsf2(double);
        int __fixsfsi(float);
        u32 __fixunssfsi(float);
        long long __fixsfdi(float);
        u64 __fixunssfdi(float);
        int __fixdfsi(double);
        u32 __fixunsdfsi(double);
        long long __fixdfdi(double);
        u64 __fixunsdfdi(double);
        float __floatsisf(int);
        float __floatunsisf(u32);
        float __floatdisf(long long);
        float __floatundisf(u64);
        double __floatsidf(int);
        double __floatunsidf(u32);
        double __floatdidf(long long);
        double __floatundidf(u64);
        int __aeabi_fcmpeq(float, float);
        int __aeabi_fcmplt(float, float);
        int __aeabi_fcmple(float, float);
        int __aeabi_fcmpge(float, float);
        int __aeabi_fcmpgt(float, float);
        long long __unordsf2(float, float);
        int __aeabi_dcmpeq(double, double);
        int __aeabi_dcmplt(double, double);
        int __aeabi_dcmple(double, double);
        int __aeabi_dcmpge(double, double);
        int __aeabi_dcmpgt(double, double);
        long long __unorddf2(double, double);
        float fmodf(float, float);
        double fmod(double, double);
        float ChipSharp_Runtime_SqrtFloat32(float);
        double ChipSharp_Runtime_SqrtFloat64(double);
        float ChipSharp_Runtime_AbsFloat32(float);
        double ChipSharp_Runtime_AbsFloat64(double);
        float ChipSharp_Runtime_MinFloat32(float, float);
        double ChipSharp_Runtime_MinFloat64(double, double);
        float ChipSharp_Runtime_MaxFloat32(float, float);
        double ChipSharp_Runtime_MaxFloat64(double, double);

        static float f(u64 bits) { union { u32 b; float v; } x; x.b = (u32)bits; return x.v; }
        static double d(u64 bits) { union { u64 b; double v; } x; x.b = bits; return x.v; }
        static u64 of(float v) { union { u32 b; float v; } x; x.v = v; return x.b; }
        static u64 od(double v) { union { u64 b; double v; } x; x.v = v; return x.b; }

        __declspec(dllexport) u64 chipsharp_test_float(int op, u64 a, u64 b)
        {
            switch (op)
            {
                case 0: return of(__addsf3(f(a), f(b)));
                case 1: return of(__subsf3(f(a), f(b)));
                case 2: return of(__mulsf3(f(a), f(b)));
                case 3: return of(__divsf3(f(a), f(b)));
                case 4: return od(__adddf3(d(a), d(b)));
                case 5: return od(__subdf3(d(a), d(b)));
                case 6: return od(__muldf3(d(a), d(b)));
                case 7: return od(__divdf3(d(a), d(b)));
                case 8: return od(__extendsfdf2(f(a)));
                case 9: return of(__truncdfsf2(d(a)));
                case 10: return (u64)(long long)__fixsfsi(f(a));
                case 11: return __fixunssfsi(f(a));
                case 12: return (u64)__fixsfdi(f(a));
                case 13: return __fixunssfdi(f(a));
                case 14: return (u64)(long long)__fixdfsi(d(a));
                case 15: return __fixunsdfsi(d(a));
                case 16: return (u64)__fixdfdi(d(a));
                case 17: return __fixunsdfdi(d(a));
                case 18: return of(__floatsisf((int)a));
                case 19: return of(__floatunsisf((u32)a));
                case 20: return of(__floatdisf((long long)a));
                case 21: return of(__floatundisf(a));
                case 22: return od(__floatsidf((int)a));
                case 23: return od(__floatunsidf((u32)a));
                case 24: return od(__floatdidf((long long)a));
                case 25: return od(__floatundidf(a));
                case 26: return __aeabi_fcmpeq(f(a), f(b));
                case 27: return __aeabi_fcmplt(f(a), f(b));
                case 28: return __aeabi_fcmple(f(a), f(b));
                case 29: return __aeabi_fcmpge(f(a), f(b));
                case 30: return __aeabi_fcmpgt(f(a), f(b));
                case 31: return __unordsf2(f(a), f(b)) != 0;
                case 32: return __aeabi_dcmpeq(d(a), d(b));
                case 33: return __aeabi_dcmplt(d(a), d(b));
                case 34: return __aeabi_dcmple(d(a), d(b));
                case 35: return __aeabi_dcmpge(d(a), d(b));
                case 36: return __aeabi_dcmpgt(d(a), d(b));
                case 37: return __unorddf2(d(a), d(b)) != 0;
                case 38: return of(fmodf(f(a), f(b)));
                case 39: return od(fmod(d(a), d(b)));
                case 40: return of(ChipSharp_Runtime_SqrtFloat32(f(a)));
                case 41: return od(ChipSharp_Runtime_SqrtFloat64(d(a)));
                case 42: return of(ChipSharp_Runtime_AbsFloat32(f(a)));
                case 43: return od(ChipSharp_Runtime_AbsFloat64(d(a)));
                case 44: return of(ChipSharp_Runtime_MinFloat32(f(a), f(b)));
                case 45: return od(ChipSharp_Runtime_MinFloat64(d(a), d(b)));
                case 46: return of(ChipSharp_Runtime_MaxFloat32(f(a), f(b)));
                case 47: return od(ChipSharp_Runtime_MaxFloat64(d(a), d(b)));
                default: return 0xDEAD;
            }
        }
        """;

    private static readonly Operation[] Operations =
    [
        new("__aeabi_fadd", Operands.Float32, Result.Float32, (a, b) => Of(F(a) + F(b))),
        new("__aeabi_fsub", Operands.Float32, Result.Float32, (a, b) => Of(F(a) - F(b))),
        new("__aeabi_fmul", Operands.Float32, Result.Float32, (a, b) => Of(F(a) * F(b))),
        new("__aeabi_fdiv", Operands.Float32, Result.Float32, (a, b) => Of(F(a) / F(b))),
        new("__aeabi_dadd", Operands.Float64, Result.Float64, (a, b) => Of(D(a) + D(b))),
        new("__aeabi_dsub", Operands.Float64, Result.Float64, (a, b) => Of(D(a) - D(b))),
        new("__aeabi_dmul", Operands.Float64, Result.Float64, (a, b) => Of(D(a) * D(b))),
        new("__aeabi_ddiv", Operands.Float64, Result.Float64, (a, b) => Of(D(a) / D(b))),
        new("__aeabi_f2d", Operands.Float32, Result.Float64, (a, _) => Of((double)F(a))),
        new("__aeabi_d2f", Operands.Float64, Result.Float32, (a, _) => Of((float)D(a))),
        new("__aeabi_f2iz", Operands.Float32, Result.Bits, (a, _) => (ulong)(long)(int)F(a), 32),
        new("__aeabi_f2uiz", Operands.Float32, Result.Bits, (a, _) => (uint)F(a), 32, Unsigned: true),
        new("__aeabi_f2lz", Operands.Float32, Result.Bits, (a, _) => (ulong)(long)F(a), 64),
        new("__aeabi_f2ulz", Operands.Float32, Result.Bits, (a, _) => (ulong)F(a), 64, Unsigned: true),
        new("__aeabi_d2iz", Operands.Float64, Result.Bits, (a, _) => (ulong)(long)(int)D(a), 32),
        new("__aeabi_d2uiz", Operands.Float64, Result.Bits, (a, _) => (uint)D(a), 32, Unsigned: true),
        new("__aeabi_d2lz", Operands.Float64, Result.Bits, (a, _) => (ulong)(long)D(a), 64),
        new("__aeabi_d2ulz", Operands.Float64, Result.Bits, (a, _) => (ulong)D(a), 64, Unsigned: true),
        new("__aeabi_i2f", Operands.Integer, Result.Float32, (a, _) => Of((float)(int)a)),
        new("__aeabi_ui2f", Operands.Integer, Result.Float32, (a, _) => Of((float)(uint)a)),
        new("__aeabi_l2f", Operands.Integer, Result.Float32, (a, _) => Of((float)(long)a)),
        new("__aeabi_ul2f", Operands.Integer, Result.Float32, (a, _) => Of((float)a)),
        new("__aeabi_i2d", Operands.Integer, Result.Float64, (a, _) => Of((double)(int)a)),
        new("__aeabi_ui2d", Operands.Integer, Result.Float64, (a, _) => Of((double)(uint)a)),
        new("__aeabi_l2d", Operands.Integer, Result.Float64, (a, _) => Of((double)(long)a)),
        new("__aeabi_ul2d", Operands.Integer, Result.Float64, (a, _) => Of((double)a)),
        new("__aeabi_fcmpeq", Operands.Float32, Result.Bits, (a, b) => Flag(F(a) == F(b))),
        new("__aeabi_fcmplt", Operands.Float32, Result.Bits, (a, b) => Flag(F(a) < F(b))),
        new("__aeabi_fcmple", Operands.Float32, Result.Bits, (a, b) => Flag(F(a) <= F(b))),
        new("__aeabi_fcmpge", Operands.Float32, Result.Bits, (a, b) => Flag(F(a) >= F(b))),
        new("__aeabi_fcmpgt", Operands.Float32, Result.Bits, (a, b) => Flag(F(a) > F(b))),
        new("__aeabi_fcmpun", Operands.Float32, Result.Bits, (a, b) => Flag(float.IsNaN(F(a)) || float.IsNaN(F(b)))),
        new("__aeabi_dcmpeq", Operands.Float64, Result.Bits, (a, b) => Flag(D(a) == D(b))),
        new("__aeabi_dcmplt", Operands.Float64, Result.Bits, (a, b) => Flag(D(a) < D(b))),
        new("__aeabi_dcmple", Operands.Float64, Result.Bits, (a, b) => Flag(D(a) <= D(b))),
        new("__aeabi_dcmpge", Operands.Float64, Result.Bits, (a, b) => Flag(D(a) >= D(b))),
        new("__aeabi_dcmpgt", Operands.Float64, Result.Bits, (a, b) => Flag(D(a) > D(b))),
        new("__aeabi_dcmpun", Operands.Float64, Result.Bits, (a, b) => Flag(double.IsNaN(D(a)) || double.IsNaN(D(b)))),
        new("fmodf", Operands.Float32, Result.Float32, (a, b) => Of(F(a) % F(b))),
        new("fmod", Operands.Float64, Result.Float64, (a, b) => Of(D(a) % D(b))),
        new("MathF.Sqrt", Operands.Float32, Result.Float32, (a, _) => Of(MathF.Sqrt(F(a)))),
        new("Math.Sqrt", Operands.Float64, Result.Float64, (a, _) => Of(Math.Sqrt(D(a)))),
        new("MathF.Abs", Operands.Float32, Result.Bits, (a, _) => Of(MathF.Abs(F(a)))),
        new("Math.Abs", Operands.Float64, Result.Bits, (a, _) => Of(Math.Abs(D(a)))),
        new("MathF.Min", Operands.Float32, Result.Float32, (a, b) => Of(MathF.Min(F(a), F(b)))),
        new("Math.Min", Operands.Float64, Result.Float64, (a, b) => Of(Math.Min(D(a), D(b)))),
        new("MathF.Max", Operands.Float32, Result.Float32, (a, b) => Of(MathF.Max(F(a), F(b)))),
        new("Math.Max", Operands.Float64, Result.Float64, (a, b) => Of(Math.Max(D(a), D(b)))),
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

    private static readonly Lazy<Harness> Built = new(Build);

    private delegate ulong Harness(int op, ulong a, ulong b);

    /// <summary>What an operation takes.</summary>
    private enum Operands
    {
        Float32,
        Float64,
        Integer,
    }

    /// <summary>How a result is compared: bit for bit, or any NaN for a NaN.</summary>
    private enum Result
    {
        Float32,
        Float64,
        Bits,
    }

    /// <summary>
    /// For each operation, every pair of the special values, then random ones: any bits, values a few exponents
    /// apart (where addition cancels and rounds), subnormals, and whole numbers. A conversion to an integer gets only
    /// values in the integer's range: beyond it, LLVM's saturating conversion decides without the routine's result.
    /// </summary>
    [HostFact]
    public void Every_routine_matches_dotnet_for_the_special_values_and_100_000_random_ones()
    {
        var harness = Built.Value;
        var mismatches = new List<string>();
        var count = 0;
        for (var op = 0; op < Operations.Length; op++)
        {
            var operation = Operations[op];
            var wrong = 0;
            foreach (var (a, b) in Cases(operation, new Random(op + 1)))
            {
                var expected = operation.Dotnet(a, b);
                var actual = harness(op, a, b);
                count++;
                var same = expected == actual || operation.Result switch
                {
                    Result.Float32 => float.IsNaN(F(expected)) && float.IsNaN(F(actual)),
                    Result.Float64 => double.IsNaN(D(expected)) && double.IsNaN(D(actual)),
                    _ => false,
                };
                if (!same && wrong++ < 3)
                {
                    mismatches.Add($"{operation.Name}(0x{a:X}, 0x{b:X}): .NET 0x{expected:X}, the runtime 0x{actual:X}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, string.Join('\n', mismatches));
        Assert.True(count > Operations.Length * RandomCases * 3 / 4, $"{count} cases");
    }

    private static IEnumerable<(ulong A, ulong B)> Cases(Operation operation, Random random)
    {
        if (operation.Operands == Operands.Integer)
        {
            return IntegerCases().Concat(Enumerable.Range(0, RandomCases).Select(_ => RandomInteger(random)))
                .Select(a => (a, 0UL));
        }

        var single = operation.Operands == Operands.Float32;
        var specials = single ? FloatSpecials : DoubleSpecials;
        var cases = specials.SelectMany(a => specials.Select(b => (a, b)))
            .Concat(Enumerable.Range(0, RandomCases).Select(_ =>
            {
                var a = RandomValue(random, single, 0);
                return (a, RandomValue(random, single, a));
            }));
        return operation.IntegerBits == 0
            ? cases
            : cases.Where(c => InRange(single ? F(c.a) : D(c.a), operation.IntegerBits, operation.Unsigned));
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

    /// <summary>
    /// A random value's bits: any bits; one a few exponents from <paramref name="other"/>'s; a subnormal; or a whole
    /// number.
    /// </summary>
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

    private static ulong RandomInteger(Random random) =>
        ((ulong)random.NextInt64() >> random.Next(0, 64)) ^ (random.Next(2) == 0 ? 0 : ulong.MaxValue);

    /// <summary>True when <paramref name="value"/> rounded toward zero fits an integer of that width and sign.</summary>
    private static bool InRange(double value, int bits, bool unsigned) => unsigned
        ? value > -1 && value < Math.ScaleB(1, bits)
        : value > -Math.ScaleB(1, bits - 1) - 1 && value < Math.ScaleB(1, bits - 1);

    private static float F(ulong bits) => BitConverter.UInt32BitsToSingle((uint)bits);

    private static double D(ulong bits) => BitConverter.UInt64BitsToDouble(bits);

    private static ulong Of(float value) => BitConverter.SingleToUInt32Bits(value);

    private static ulong Of(double value) => BitConverter.DoubleToUInt64Bits(value);

    private static ulong Flag(bool value) => value ? 1UL : 0UL;

    /// <summary>
    /// compiler-rt's files as a Pico build compiles them (in units), the armv6m comparisons, the shared float math and
    /// the harness, built for this PC into a DLL (no C runtime) with __SOFTFP__ defined, so compiler-rt takes the paths
    /// it takes on the Cortex-M0+. A loaded
    /// DLL cannot be deleted, so it lives in a folder named after the sources' hash, which later runs reuse.
    /// </summary>
    private static Harness Build()
    {
        List<(string Name, string Text)> files =
        [
            .. CompilerRt.Sources().Select(file => (file.FileName, file.Content)),
            (ArmV6MRuntime.FloatCompareFileName, ArmV6MRuntime.Source(ArmV6MRuntime.FloatCompareFileName).Content),
            (WindowsX64Runtime.FloatMathFileName, WindowsX64Runtime.FloatMath),
            ("harness.c", HarnessSource),
        ];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Concat(files.Select(file => file.Name + "\n" + file.Text)))))[..16];
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "soft-float-" + hash);
        var dll = Path.Combine(folder, "soft_float.dll");
        if (!File.Exists(dll))
        {
            Directory.CreateDirectory(folder);
            foreach (var (name, text) in files)
            {
                File.WriteAllText(Path.Combine(folder, name), text);
            }

            var built = $"soft_float-{Guid.NewGuid():N}.dll"; // then renamed: a half-written DLL is never loaded
            var sources = files.Select(file => file.Name).Where(name => name.EndsWith(".c", StringComparison.Ordinal));
            var clang = Llvm.LlvmTools.RunAsync(new ClangToolchain().FindClang(),
            [
                "--target=x86_64-pc-windows-msvc", "-O2", "-ffreestanding", "-nostdlib", "-fuse-ld=lld", "-shared",
                "-D__SOFTFP__", .. sources, "-o", built, "-Wl,-noentry",
            ], folder).GetAwaiter().GetResult();
            Assert.True(clang.ExitCode == 0, $"clang failed:\n{clang}");
            try
            {
                File.Move(Path.Combine(folder, built), dll);
            }
            catch (IOException) when (File.Exists(dll))
            {
                // another test run built it first
            }
        }

        var library = NativeLibrary.Load(dll);
        return Marshal.GetDelegateForFunctionPointer<Harness>(NativeLibrary.GetExport(library, "chipsharp_test_float"));
    }

    /// <summary>
    /// An operation of the harness: its name, what it takes and gives, what .NET computes, and for a conversion to
    /// an integer the integer's width and sign. The harness numbers them in the order of <see cref="Operations"/>.
    /// </summary>
    private sealed record Operation(string Name, Operands Operands, Result Result, Func<ulong, ulong, ulong> Dotnet,
        int IntegerBits = 0, bool Unsigned = false);
}
