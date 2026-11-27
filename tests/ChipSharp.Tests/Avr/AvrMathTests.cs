using ChipSharp.Abstractions;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The transcendental functions of Math and MathF (the shared runtime's chipsharp_math*.c: musl's and AMD's C) on the
/// ATmega328P, called on <see cref="AvrCpu"/> as compiled code calls them, for the special values and hundreds of random
/// ones each: every result has the bits the same C gives on the PC with no float hardware (<see cref="MathLibrary.Software"/>,
/// which is what the Pico computes, and the PC's SSE2 too). Here C's int has 16 bits, and the functions' tables stay in
/// flash, read with LPM. Several functions together are larger than the ATmega328P's 32 KB, so these run on a core of
/// 64 KB (<see cref="AvrCoreProgram.BuildLargeAsync"/>), in five programs.
/// </summary>
public sealed class AvrMathTests
{
    private const int RandomCases = 300;

    private static readonly string[][] Groups =
    [
        ["Sin", "Cos", "Tan"],
        ["Asin", "Acos", "Atan", "Atan2"],
        ["Sinh", "Cosh", "Tanh"],
        ["Asinh", "Acosh", "Atanh"],
        ["Exp", "Log", "Log2", "Log10", "Pow", "Cbrt"],
    ];

    public static TheoryData<int> GroupNumbers => [.. Enumerable.Range(0, Groups.Length)];

    [AvrTheory]
    [RunsLong]
    [MemberData(nameof(GroupNumbers))]
    public async Task Every_function_gives_on_the_ATmega328P_the_bits_it_gives_in_software_on_the_PC(int group)
    {
        var functions = Groups[group].SelectMany(name => new[] { (Name: name, Float: false), (Name: name, Float: true) })
            .ToList();
        var program = await AvrCoreProgram.BuildLargeAsync(
            [.. AvrCoreProgram.RuntimeFiles, new GeneratedFile("math_test.c", "long chipsharp_entry(void) { return 0; }\n")],
            functions.Select(Symbol));
        var mismatches = new System.Collections.Concurrent.ConcurrentBag<string>();
        var count = 0;
        Parallel.ForEach(functions, function =>
        {
            var op = MathLibrary.Op(function.Name, function.Float);
            var arity = MathLibrary.Functions[op].Arity;
            var size = function.Float ? 4 : 8;
            var cpu = program.Load();
            var address = program.Address(Symbol(function));
            var random = new Random(op + 1);
            var wrong = 0;
            foreach (var (a, b) in Cases(function.Float, op))
            {
                var expected = MathLibrary.Software(op, a, b);
                var actual = arity == 2
                    ? AvrAbi.Call(cpu, address, size, random, (a, size), (b, size))
                    : AvrAbi.Call(cpu, address, size, random, (a, size));
                Interlocked.Increment(ref count);
                if (expected != actual && !(IsNaN(expected, function.Float) && IsNaN(actual, function.Float))
                    && wrong++ < 3)
                {
                    mismatches.Add($"{Symbol(function)}(0x{a:X}, 0x{b:X}): the PC 0x{expected:X}, the AVR 0x{actual:X}");
                }
            }
        });

        Assert.True(mismatches.IsEmpty, string.Join('\n', mismatches.Order()));
        Assert.True(count > functions.Count * RandomCases, $"{count} cases");
    }

    private static string Symbol((string Name, bool Float) function) =>
        $"ChipSharp_Runtime_{function.Name}{(function.Float ? "Float32" : "Float64")}";

    /// <summary>The special values (of a float or a double) paired with each other, then random ones of every range.</summary>
    private static IEnumerable<(ulong A, ulong B)> Cases(bool isFloat, int seed)
    {
        // and values whose range reductions count past 16 bits: exp's of 32 steps per ln 2 up to overflow, tan's
        // multiples of pi/2 up to 5e5
        ulong[] specials = isFloat
            ?
            [
                .. FloatMathProgram.SpecialFloats.Concat([88.7f, -87.3f, 89.4f, 499_999f, 65_535.9f, 1e5f, -30_000.5f])
                    .Select(value => (ulong)BitConverter.SingleToUInt32Bits(value)),
            ]
            :
            [
                .. FloatMathProgram.SpecialDoubles.Concat([700, -708.3, 709.7, 710.4, 499_999, 65_535.9, 1e5, -30_000.5])
                    .Select(BitConverter.DoubleToUInt64Bits),
            ];
        var randoms = isFloat
            ? MathProgram.RandomFloats(2 * RandomCases, seed).Select(value => (ulong)BitConverter.SingleToUInt32Bits(value))
            : MathProgram.RandomDoubles(2 * RandomCases, seed).Select(BitConverter.DoubleToUInt64Bits);
        return specials.SelectMany(a => specials.Take(8).Select(b => (a, b))).Concat(randoms.Chunk(2).Select(pair => (pair[0], pair[1])));
    }

    private static bool IsNaN(ulong bits, bool isFloat) =>
        isFloat ? float.IsNaN(BitConverter.UInt32BitsToSingle((uint)bits)) : double.IsNaN(BitConverter.UInt64BitsToDouble(bits));
}
