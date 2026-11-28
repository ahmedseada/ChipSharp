using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// The ARMv6-M runtime's division, multiply and shift helpers. Their C is built for this PC as a DLL and called
/// with many values, each result checked against .NET's own arithmetic; and the Cortex-M0+ build is checked to call
/// no helper (a helper that calls itself, or one nothing provides, would hang or fail to link). The simulator tests
/// run them on the Cortex-M0+ itself (every sample divides).
/// </summary>
public sealed partial class ArmV6MHelperTests : IDisposable
{
    private static readonly Lazy<Helpers> Built = new(BuildHelpers);
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    private delegate uint UDiv(uint a, uint b);

    private delegate int SDiv(int a, int b);

    private delegate ulong UDivMod(uint a, uint b);

    private delegate ulong SDivMod(int a, int b);

    private delegate ulong DivMod64(ulong a, ulong b, out ulong remainder);

    private delegate ulong Multiply64(ulong a, ulong b);

    private delegate ulong Shift64(ulong value, int shift);

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [HostFact]
    public void Division_32_bit_matches_dotnet()
    {
        var helpers = Built.Value;
        foreach (var (a, b) in Pairs32())
        {
            Assert.Equal(a / b, helpers.UDiv(a, b));
            Assert.Equal(((ulong)(a % b) << 32) | (a / b), helpers.UDivMod(a, b));

            var (x, y) = ((int)a, (int)b);
            if (x == int.MinValue && y == -1)
            {
                // .NET throws OverflowException; the helper wraps, like the hardware divide of other ARM chips
                Assert.Equal(int.MinValue, helpers.SDiv(x, y));
                continue;
            }

            Assert.Equal(x / y, helpers.SDiv(x, y));
            Assert.Equal(((ulong)(uint)(x % y) << 32) | (uint)(x / y), helpers.SDivMod(x, y));
        }
    }

    [HostFact]
    public void Division_64_bit_matches_dotnet()
    {
        var helpers = Built.Value;
        foreach (var (a, b) in Pairs64())
        {
            Assert.Equal(a / b, helpers.UDivMod64(a, b, out var remainder));
            Assert.Equal(a % b, remainder);

            var (x, y) = ((long)a, (long)b);
            if (x == long.MinValue && y == -1)
            {
                Assert.Equal(long.MinValue, (long)helpers.SDivMod64(a, b, out _));
                continue;
            }

            Assert.Equal(x / y, (long)helpers.SDivMod64(a, b, out var signedRemainder));
            Assert.Equal(x % y, (long)signedRemainder);
        }
    }

    [HostFact]
    public void Multiply_and_shifts_64_bit_match_dotnet()
    {
        var helpers = Built.Value;
        foreach (var (a, b) in Pairs64())
        {
            Assert.Equal(unchecked(a * b), helpers.Multiply(a, b));
        }

        foreach (var value in Values64())
        {
            for (var shift = 0; shift < 64; shift++)
            {
                Assert.Equal(value << shift, helpers.ShiftLeft(value, shift));
                Assert.Equal(value >> shift, helpers.ShiftRightUnsigned(value, shift));
                Assert.Equal((ulong)((long)value >> shift), helpers.ShiftRightSigned(value, shift));
            }
        }
    }

    [PicoFact]
    public async Task On_the_Cortex_M0_plus_the_helpers_call_no_helper()
    {
        Directory.CreateDirectory(_scratch);
        var pico = PicoBuild.Pico;
        var flags = pico.Settings["clang"]!["flags"]!.AsArray().Select(f => f!.GetValue<string>())
            .Where(f => f != "-fuse-ld=lld" && f != "-nostdlib");
        await File.WriteAllTextAsync(Path.Combine(_scratch, "helpers.c"), ArmV6MRuntime.Source(ArmV6MRuntime.HelpersFileName).Content);
        Run(new ClangToolchain().FindClang(), [$"--target={pico.Triple}", $"-mcpu={pico.Cpu}", .. flags, "-c", "helpers.c", "-o", "helpers.o"]);

        var disassembly = Run(PicoTools.LlvmTool("llvm-objdump"), ["-dr", "helpers.o"]);

        var calls = CallRelocation().Matches(disassembly).Select(m => m.Groups[1].Value).Distinct().Order().ToList();
        Assert.Equal(["chipsharp_divide_by_zero", "udivmod32", "udivmod64"], calls);
    }

    /// <summary>Edge cases and random values, never a zero divisor.</summary>
    private static IEnumerable<(uint, uint)> Pairs32()
    {
        uint[] edges = [0, 1, 2, 3, 7, 10, 255, 256, 65535, 65536, 0x7fffffff, 0x80000000, 0x80000001, 0xfffffffe, 0xffffffff];
        var random = new Random(2040);
        var values = edges.Concat(Enumerable.Range(0, 200).Select(_ => (uint)random.NextInt64(0, 1L << 32)))
            .Concat(Enumerable.Range(0, 100).Select(_ => (uint)random.Next(0, 1000))).ToList();
        foreach (var a in values)
        {
            foreach (var b in values.Where(v => v != 0).Take(60))
            {
                yield return (a, b);
            }
        }

        for (var i = 0; i < 5000; i++)
        {
            var b = (uint)random.NextInt64(1, 1L << 32) >> random.Next(0, 32);
            yield return ((uint)random.NextInt64(0, 1L << 32), Math.Max(1, b));
        }
    }

    private static List<ulong> Values64()
    {
        ulong[] edges =
        [
            0, 1, 2, 3, 10, 0xffffffff, 0x100000000, 0x100000001, 0x7fffffffffffffff, 0x8000000000000000,
            0x8000000000000001, 0xfffffffffffffffe, 0xffffffffffffffff, 1234567890123, 0xdeadbeefcafef00d,
        ];
        var random = new Random(64);
        return [.. edges, .. Enumerable.Range(0, 150).Select(_ => (ulong)random.NextInt64() << random.Next(0, 2))];
    }

    private static IEnumerable<(ulong, ulong)> Pairs64()
    {
        var values = Values64();
        foreach (var a in values)
        {
            foreach (var b in values.Where(v => v != 0).Take(60))
            {
                yield return (a, b);
            }
        }

        var random = new Random(6464);
        for (var i = 0; i < 5000; i++)
        {
            var a = (ulong)random.NextInt64() * 2 + (ulong)random.Next(2);
            var b = Math.Max(1, ((ulong)random.NextInt64() * 2) >> random.Next(0, 64));
            yield return (a, b);
        }
    }

    private static string RunTool(string tool, IEnumerable<string> arguments, string folder)
    {
        var start = new ProcessStartInfo(tool)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{tool} {string.Join(' ', start.ArgumentList)} failed:\n{output}\n{errors.Result}");
        return output;
    }

    private string Run(string tool, IEnumerable<string> arguments) => RunTool(tool, arguments, _scratch);

    /// <summary>
    /// The helpers' C built for this PC into a DLL (no C runtime), loaded, with each function exported. A loaded DLL
    /// cannot be deleted, so it lives in a folder named after the source's hash, which later runs reuse.
    /// </summary>
    private static Helpers BuildHelpers()
    {
        var source = ArmV6MRuntime.Source(ArmV6MRuntime.HelpersFileName).Content;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "armv6m-helpers-" + hash);
        var dll = Path.Combine(folder, "helpers.dll");
        if (!File.Exists(dll))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "helpers.c"), source);
            string[] exports =
            [
                "__aeabi_uidiv", "__aeabi_idiv", "__aeabi_uidivmod", "__aeabi_idivmod", "chipsharp_udivmod64",
                "chipsharp_divmod64", "__aeabi_lmul", "__aeabi_llsl", "__aeabi_llsr", "__aeabi_lasr",
            ];
            var built = $"helpers-{Guid.NewGuid():N}.dll"; // then renamed: a half-written DLL is never loaded
            RunTool(new ClangToolchain().FindClang(),
            [
                "--target=x86_64-pc-windows-msvc", "-O2", "-ffreestanding", "-fno-builtin", "-nostdlib", "-fuse-ld=lld",
                "-shared", "helpers.c", "-o", built, "-Wl,-noentry", .. exports.Select(name => $"-Wl,-export:{name}"),
            ], folder);
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
        return new Helpers(
            Get<UDiv>("__aeabi_uidiv"), Get<SDiv>("__aeabi_idiv"), Get<UDivMod>("__aeabi_uidivmod"),
            Get<SDivMod>("__aeabi_idivmod"), Get<DivMod64>("chipsharp_udivmod64"), Get<DivMod64>("chipsharp_divmod64"),
            Get<Multiply64>("__aeabi_lmul"), Get<Shift64>("__aeabi_llsl"), Get<Shift64>("__aeabi_llsr"),
            Get<Shift64>("__aeabi_lasr"));
    }

    [GeneratedRegex(@"R_ARM_THM_CALL\s+(\S+)")]
    private static partial Regex CallRelocation();

    private sealed record Helpers(
        UDiv UDiv, SDiv SDiv, UDivMod UDivMod, SDivMod SDivMod, DivMod64 UDivMod64, DivMod64 SDivMod64,
        Multiply64 Multiply, Shift64 ShiftLeft, Shift64 ShiftRightUnsigned, Shift64 ShiftRightSigned);
}
