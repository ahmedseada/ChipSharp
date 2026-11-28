using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;
using ChipSharp.Runtime.Windows;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Host;

/// <summary>
/// Console.WriteLine(double) and Console.WriteLine(float): the runtime functions they become, and the text the
/// runtimes' shared C (chipsharp_float_text.c) writes, which must be exactly what .NET writes. The programs that
/// print are hand-built TIR with float constants: thousands of calls, with no C# to compile.
/// </summary>
public sealed class FloatPrintingTests : IDisposable
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private static readonly Lazy<(Float64Text, Float32Text)> Built = new(BuildFloatText);
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    private delegate int Float64Text(ulong bits, byte[] text);

    private delegate int Float32Text(uint bits, byte[] text);

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void Console_WriteLine_of_a_float_or_a_double_becomes_a_runtime_function_that_takes_it_as_it_is()
    {
        var assembly = IlAssembly.Load(CSharpCompiler.Compile("""
            class Program
            {
                static void Main()
                {
                    System.Console.WriteLine(1.5f);
                    System.Console.WriteLine(2.5);
                }
            }
            """), "Test.dll");
        var calls = Assert.Single(assembly.FindMethods("Program.Main")).Body!.Instructions
            .Select(instruction => instruction.Operand).OfType<IlMethodOperand>().Select(operand => operand.Method).ToList();

        Assert.Equal(2, calls.Count);
        Assert.True(KnownExternals.TryMap(calls[0], out var single));
        Assert.True(KnownExternals.TryMap(calls[1], out var @double));
        Assert.Equal(("ChipSharp.Runtime.WriteLineFloat32", TirType.Void, "System.Console.WriteLine(float)"),
            (single.DisplayName, single.ReturnType, single.LibraryMethod));
        Assert.Equal([TirType.F32], single.ParameterTypes);
        Assert.Equal(("ChipSharp.Runtime.WriteLineFloat64", TirType.Void, "System.Console.WriteLine(double)"),
            (@double.DisplayName, @double.ReturnType, @double.LibraryMethod));
        Assert.Equal([TirType.F64], @double.ParameterTypes);
        Assert.Equal("ChipSharp_Runtime_WriteLineFloat32", LlvmCodeGenerator.SymbolName(single.DisplayName));
        Assert.Equal("ChipSharp_Runtime_WriteLineFloat64", LlvmCodeGenerator.SymbolName(@double.DisplayName));
        Assert.Contains(single, KnownExternals.All);
        Assert.Contains(@double, KnownExternals.All);

        // the float constants (ldc.r4, ldc.r8) are supported too
        Assert.True(WholeProgramPass.Check(assembly).IsSupported);
    }

    /// <summary>
    /// One PC program prints thousands of doubles and floats through the real windows-x64 runtime: the edge cases,
    /// every power of 2 and of 10 (and the values next to each power of 10), and random values from a fixed seed.
    /// Every line must be what .NET writes for the value in the invariant culture.
    /// </summary>
    [HostFact]
    public async Task Thousands_of_doubles_and_floats_print_exactly_what_dotnet_prints()
    {
        double[] doubles =
            [.. FloatPrinting.DoubleCases(), .. FloatPrinting.DoublePowers(), .. FloatPrinting.RandomDoubles(3000, 2026)];
        float[] floats =
            [.. FloatPrinting.FloatCases(), .. FloatPrinting.FloatPowers(), .. FloatPrinting.RandomFloats(3000, 2027)];
        var host = Targets.Load("host");
        IArchRuntime runtime = new WindowsX64Runtime();
        var code = new GeneratedCode(
        [
            .. new LlvmCodeGenerator().Generate(FloatPrinting.Program("Floats", doubles, floats), host).Files,
            .. runtime.Startup(host).Files,
            .. runtime.InterruptEntry(host).Files,
            .. runtime.Atomics(host).Files,
        ]);
        var program = new ClangToolchain(new ClangToolchainOptions { OutputFolder = _scratch }).Build(code, host);

        var result = await Llvm.LlvmTools.RunAsync(program.Path, [], _scratch);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.True(doubles.Length > 7000 && floats.Length > 3500, $"{doubles.Length} doubles, {floats.Length} floats");
        FloatPrinting.AssertLines(FloatPrinting.Expected(doubles, floats), result.Output);
    }

    /// <summary>
    /// The shared C built for this PC as a DLL and called directly: at every exponent, its first two values and the
    /// last one below it, and 100,000 random bit patterns of each type, many more values than a program prints in a
    /// test's time.
    /// </summary>
    [HostFact]
    public void The_float_text_matches_dotnet_at_every_exponent_and_for_200_000_random_values()
    {
        var (float64, float32) = Built.Value;
        var text = new byte[32];
        var random = new Random(754);
        var mismatches = new List<string>();
        void Check(string kind, ulong bits, string expected, int length)
        {
            var written = Encoding.ASCII.GetString(text, 0, length);
            if (written != expected && mismatches.Count < 20)
            {
                mismatches.Add($"{kind} 0x{bits:X}: .NET writes {expected}, the runtime wrote {written}");
            }
        }

        void CheckDouble(ulong bits) => Check("double", bits,
            BitConverter.UInt64BitsToDouble(bits).ToString(CultureInfo.InvariantCulture), float64(bits, text));

        void CheckFloat(uint bits) => Check("float", bits,
            BitConverter.UInt32BitsToSingle(bits).ToString(CultureInfo.InvariantCulture), float32(bits, text));

        for (var exponent = 0UL; exponent < 2048; exponent++)
        {
            CheckDouble(exponent << 52);
            CheckDouble((exponent << 52) + 1);
            CheckDouble((exponent << 52) - 1);
        }

        for (var exponent = 0u; exponent < 256; exponent++)
        {
            CheckFloat(exponent << 23);
            CheckFloat((exponent << 23) + 1);
            CheckFloat((exponent << 23) - 1);
        }

        for (var i = 0; i < 100_000; i++)
        {
            CheckDouble((ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63));
            CheckFloat((uint)random.Next() ^ ((uint)random.Next(2) << 31));
        }

        Assert.True(mismatches.Count == 0, string.Join('\n', mismatches));
    }

    /// <summary>
    /// chipsharp_float_text.c built for this PC into a DLL (no C runtime) and loaded. A loaded DLL cannot be deleted,
    /// so it lives in a folder named after the source's hash, which later runs reuse.
    /// </summary>
    private static (Float64Text, Float32Text) BuildFloatText()
    {
        var source = WindowsX64Runtime.FloatText;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "float-text-" + hash);
        var dll = Path.Combine(folder, "float_text.dll");
        if (!File.Exists(dll))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "float_text.c"), source);
            var built = $"float_text-{Guid.NewGuid():N}.dll"; // then renamed: a half-written DLL is never loaded
            var clang = Llvm.LlvmTools.RunAsync(new ClangToolchain().FindClang(),
            [
                "--target=x86_64-pc-windows-msvc", "-O2", "-nostdlib", "-fuse-ld=lld", "-shared", "float_text.c",
                "-o", built, "-Wl,-noentry", "-Wl,-export:chipsharp_float64_text", "-Wl,-export:chipsharp_float32_text",
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
        return (Get<Float64Text>("chipsharp_float64_text"), Get<Float32Text>("chipsharp_float32_text"));
    }
}
