using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ChipSharp.Runtime.Riscv32;
using ChipSharp.Tests.Host;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Ch32v003;

/// <summary>
/// The riscv32e runtime's multiplication and division (riscv32e_arithmetic.c: __mulsi3, __udivsi3, __umodsi3,
/// __divsi3, __modsi3, __muldi3), which every product and quotient of a CH32V003 program calls: the very C, built for
/// this PC into a DLL (no C runtime) and called with the edge cases and 200,000 random pairs, gives what .NET's
/// arithmetic gives, and for a divisor of 0 what RISC-V's M gives (quotient all ones, or -1; remainder the dividend).
/// That the RV32EC build calls none of them from themselves (a * or / in them would) is the link tests'.
/// </summary>
public sealed class Riscv32eArithmeticTests
{
    private static readonly Lazy<Helpers> Built = new(Build);

    private delegate uint Unsigned32(uint a, uint b);

    private delegate int Signed32(int a, int b);

    private delegate ulong Unsigned64(ulong a, ulong b);

    private sealed record Helpers(Unsigned32 Multiply, Unsigned32 Divide, Unsigned32 Remainder, Signed32 SignedDivide,
        Signed32 SignedRemainder, Unsigned64 Multiply64);

    [HostFact]
    public void The_helpers_multiply_and_divide_as_dotnet_does_and_divide_by_zero_as_M_does()
    {
        var helpers = Built.Value;
        uint[] edges = [0, 1, 2, 3, 7, 10, 0x7fff, 0x8000, 0xffff, 0x10000, 0x7fffffff, 0x80000000, 0x80000001, 0xfffffffe, 0xffffffff];
        var random = new Random(20261128);
        var pairs = edges.SelectMany(a => edges.Select(b => (a, b)))
            .Concat(Enumerable.Range(0, 200_000).Select(_ => ((uint)random.NextInt64(1L << 32) >> random.Next(32),
                (uint)random.NextInt64(1L << 32) >> random.Next(32))));
        var mismatches = new List<string>();
        void Check(string what, ulong expected, ulong actual)
        {
            if (expected != actual && mismatches.Count < 20)
            {
                mismatches.Add($"{what}: {actual:x}, .NET {expected:x}");
            }
        }

        foreach (var (a, b) in pairs)
        {
            var (x, y) = ((int)a, (int)b);
            Check($"__mulsi3({a:x}, {b:x})", unchecked(a * b), helpers.Multiply(a, b));
            Check($"__udivsi3({a:x}, {b:x})", b == 0 ? uint.MaxValue : a / b, helpers.Divide(a, b));
            Check($"__umodsi3({a:x}, {b:x})", b == 0 ? a : a % b, helpers.Remainder(a, b));
            Check($"__divsi3({x}, {y})", (uint)(y == 0 ? -1 : x == int.MinValue && y == -1 ? int.MinValue : x / y),
                (uint)helpers.SignedDivide(x, y));
            Check($"__modsi3({x}, {y})", (uint)(y == 0 ? x : x == int.MinValue && y == -1 ? 0 : x % y),
                (uint)helpers.SignedRemainder(x, y));
            var (wide, other) = ((ulong)a << (int)(b % 33) | b, (ulong)b * 0x9e3779b9u ^ a);
            Check($"__muldi3({wide:x}, {other:x})", unchecked(wide * other), helpers.Multiply64(wide, other));
        }

        Assert.True(mismatches.Count == 0, string.Join('\n', mismatches));
    }

    /// <summary>
    /// riscv32e_arithmetic.c built for this PC into a DLL and loaded; a loaded DLL cannot be deleted, so it lives in a
    /// folder named after the source's hash, which later runs reuse.
    /// </summary>
    private static Helpers Build()
    {
        var source = Riscv32Runtime.Source(Riscv32Runtime.ArithmeticFileName).Content;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "riscv32e-arithmetic-" + hash);
        var dll = Path.Combine(folder, "arithmetic.dll");
        string[] exports = ["__mulsi3", "__udivsi3", "__umodsi3", "__divsi3", "__modsi3", "__muldi3"];
        if (!File.Exists(dll))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "arithmetic.c"), source);
            var built = $"arithmetic-{Guid.NewGuid():N}.dll"; // then renamed: a half-written DLL is never loaded
            var clang = Llvm.LlvmTools.RunAsync(new ClangToolchain().FindClang(),
            [
                "--target=x86_64-pc-windows-msvc", "-O2", "-ffreestanding", "-fno-builtin", "-nostdlib", "-fuse-ld=lld",
                "-shared", "arithmetic.c", "-o", built, "-Wl,-noentry", .. exports.Select(name => "-Wl,-export:" + name),
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
        T Get<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        return new Helpers(Get<Unsigned32>("__mulsi3"), Get<Unsigned32>("__udivsi3"), Get<Unsigned32>("__umodsi3"),
            Get<Signed32>("__divsi3"), Get<Signed32>("__modsi3"), Get<Unsigned64>("__muldi3"));
    }
}
