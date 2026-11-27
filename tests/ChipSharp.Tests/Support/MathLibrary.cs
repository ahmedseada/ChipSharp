using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Windows;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Support;

/// <summary>
/// The math the runtimes ship (chipsharp_float_math.c and chipsharp_math*.c) built for this PC into a DLL with one
/// entry point, which tests call for hundreds of thousands of values: <c>chipsharp_test_math(op, a, b, c)</c> applies
/// the function at index op of <see cref="Functions"/> to the bits a, b and c (as doubles or floats) and returns the
/// result's bits. Two builds: <see cref="Hardware"/>, compiled as a PC program compiles the runtime (SSE2), and
/// <see cref="Software"/>, with no float hardware at all (-mno-sse -mno-80387: every float operation a call of
/// compiler-rt's, built as the Pico builds it), which is what the Pico computes.
/// </summary>
internal static class MathLibrary
{
    /// <summary>The runtime functions, in the order the harness numbers them: name, arity, float (or double).</summary>
    public static IReadOnlyList<(string Name, int Arity, bool Float)> Functions { get; } =
    [
        .. new[]
            {
                "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Sinh", "Cosh", "Tanh", "Asinh", "Acosh", "Atanh", "Exp",
                "Log", "Log2", "Log10", "Cbrt", "Floor", "Ceiling", "Truncate", "Round", "BitIncrement", "BitDecrement",
                "ReciprocalEstimate", "ReciprocalSqrtEstimate", "Sqrt",
            }
            .SelectMany(name => new[] { (name, 1, false), (name, 1, true) }),
        .. new[] { "Atan2", "Pow", "CopySign", "IEEERemainder", "MinMagnitude", "MaxMagnitude", "ScaleB" }
            .SelectMany(name => new[] { (name, 2, false), (name, 2, true) }),
        ("FusedMultiplyAdd", 3, false), ("FusedMultiplyAdd", 3, true),
        ("ILogB", 1, false), ("ILogB", 1, true),
    ];

    private delegate ulong Entry(int op, ulong a, ulong b, ulong c);

    private static readonly Lazy<Entry> HardwareEntry = new(() => Build(soft: false));
    private static readonly Lazy<Entry> SoftwareEntry = new(() => Build(soft: true));

    /// <summary>The functions as a PC program computes them.</summary>
    public static ulong Hardware(int op, ulong a, ulong b = 0, ulong c = 0) => HardwareEntry.Value(op, a, b, c);

    /// <summary>The functions computed in software, as a chip without float hardware (the Pico) computes them.</summary>
    public static ulong Software(int op, ulong a, ulong b = 0, ulong c = 0) => SoftwareEntry.Value(op, a, b, c);

    /// <summary>The index of a function in <see cref="Functions"/>.</summary>
    public static int Op(string name, bool isFloat) =>
        Functions.Select((f, i) => (f, i)).Single(p => p.f.Name == name && p.f.Float == isFloat).i;

    private static string HarnessSource()
    {
        var text = new StringBuilder("""
            typedef unsigned int u32;
            typedef unsigned long long u64;

            int _fltused = 0; /* the MSVC target asks for it when C uses floating point */

            static double d(u64 bits) { union { u64 u; double d; } v = { bits }; return v.d; }
            static float f(u64 bits) { union { u32 u; float f; } v = { (u32)bits }; return v.f; }
            static u64 bd(double value) { union { double d; u64 u; } v = { value }; return v.u; }
            static u64 bf(float value) { union { float f; u32 u; } v = { value }; return v.u; }

            """);
        foreach (var (name, arity, isFloat) in Functions)
        {
            var (type, suffix) = isFloat ? ("float", "Float32") : ("double", "Float64");
            var returns = name == "ILogB" ? "int" : type;
            var parameters = name == "ScaleB" ? $"{type}, int" : string.Join(", ", Enumerable.Repeat(type, arity));
            text.Append($"{returns} ChipSharp_Runtime_{name}{suffix}({parameters});\n");
        }

        text.Append("__declspec(dllexport) u64 chipsharp_test_math(int op, u64 a, u64 b, u64 c)\n{\n    switch (op)\n    {\n");
        for (var i = 0; i < Functions.Count; i++)
        {
            var (name, arity, isFloat) = Functions[i];
            var (read, write, suffix) = isFloat ? ("f", "bf", "Float32") : ("d", "bd", "Float64");
            var arguments = name == "ScaleB" ? $"{read}(a), (int)b" : string.Join(", ", new[] { "a", "b", "c" }
                .Take(arity).Select(x => $"{read}({x})"));
            var call = $"ChipSharp_Runtime_{name}{suffix}({arguments})";
            text.Append(name == "ILogB"
                ? $"    case {i}: return (u64)(u32){call};\n"
                : $"    case {i}: return {write}({call});\n");
        }

        return text.Append("    default: return 0;\n    }\n}\n").ToString();
    }

    /// <summary>
    /// Builds the DLL, in a folder named after the sources' hash (a loaded DLL cannot be deleted; later runs reuse it).
    /// </summary>
    private static Entry Build(bool soft)
    {
        List<(string Name, string Text)> files =
        [
            (WindowsX64Runtime.FloatMathFileName, WindowsX64Runtime.FloatMath),
            .. WindowsX64Runtime.MathFileNames.Select(name => (name, WindowsX64Runtime.Read(name))),
            ("harness.c", HarnessSource()),
            .. soft ? CompilerRt.Sources().Select(file => (file.FileName, file.Content)) : [],
        ];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Concat(files.Select(file => file.Name + "\n" + file.Text)))))[..16];
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", $"math-{(soft ? "soft" : "hard")}-{hash}");
        var dll = Path.Combine(folder, "math.dll");
        if (!File.Exists(dll))
        {
            Directory.CreateDirectory(folder);
            foreach (var (name, text) in files)
            {
                File.WriteAllText(Path.Combine(folder, name), text);
            }

            var built = $"math-{Guid.NewGuid():N}.dll"; // then renamed: a half-written DLL is never loaded
            var sources = files.Select(file => file.Name).Where(name => name.EndsWith(".c", StringComparison.Ordinal));
            var clang = Llvm.LlvmTools.RunAsync(new ClangToolchain().FindClang(),
            [
                "--target=x86_64-pc-windows-msvc", "-O2", "-ffreestanding", "-nostdlib", "-fuse-ld=lld", "-shared",
                "-ffunction-sections", "-fdata-sections",
                .. soft ? new[] { "-mno-sse", "-mno-80387", "-D__SOFTFP__" } : [],
                .. sources, "-o", built, "-Wl,-noentry",
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
        return Marshal.GetDelegateForFunctionPointer<Entry>(NativeLibrary.GetExport(library, "chipsharp_test_math"));
    }
}
