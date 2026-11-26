using System.Text;
using ChipSharp.Abstractions;

namespace ChipSharp.Runtime.ArmV6M;

/// <summary>
/// Float arithmetic in software for chips with no float hardware: LLVM's compiler-rt builtins, the functions LLVM
/// calls for float and double operations on ARM (__aeabi_fadd, __aeabi_dmul, __aeabi_f2iz, __aeabi_l2d, ...), each
/// rounded exactly as IEEE 754 says, with subnormal numbers, infinities and NaN. The files are compiler-rt's
/// (llvmorg-23.1.6, compiler-rt/lib/builtins), unchanged, in CompilerRt/ with their license (Apache-2.0 WITH
/// LLVM-exception, CompilerRt/LICENSE.TXT).
/// </summary>
/// <remarks>
/// A build compiles the 31 .c files in the six <see cref="Units"/>: each unit is one C file with the text of its .c
/// files one after the other (with <c>#line</c>, so that a message names the file it is about). Files that set up a
/// header differently (float or double, the integer a conversion makes) are in different units. That compiles in a
/// third of the time of 31 files, into the same code.
/// </remarks>
public static class CompilerRt
{
    /// <summary>What the names of the compiler-rt files start with among the assembly's resources.</summary>
    private const string Prefix = "compiler-rt/";

    /// <summary>The name of every compiler-rt file: the .c files and the headers (.h, .inc) they include.</summary>
    public static IReadOnlyList<string> FileNames { get; } =
    [
        .. typeof(CompilerRt).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(name => name[Prefix.Length..])
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The units a build compiles, each with the compiler-rt .c files it holds, in order.</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Files)> Units { get; } =
    [
        ("compiler_rt_float32.c",
            ["addsf3.c", "subsf3.c", "mulsf3.c", "divsf3.c", "comparesf2.c", "fixsfsi.c", "fixunssfsi.c", "floatsisf.c",
                "floatunsisf.c", "floatdisf.c"]),
        ("compiler_rt_float64.c",
            ["adddf3.c", "subdf3.c", "muldf3.c", "divdf3.c", "comparedf2.c", "fixdfsi.c", "fixunsdfsi.c", "floatsidf.c",
                "floatunsidf.c", "floatdidf.c"]),
        ("compiler_rt_float32_int64.c", ["fixsfdi.c", "fixunssfdi.c", "floatundisf.c", "clzsi2.c", "clzdi2.c", "fp_mode.c"]),
        ("compiler_rt_float64_int64.c", ["fixdfdi.c", "fixunsdfdi.c", "floatundidf.c"]),
        ("compiler_rt_extend.c", ["extendsfdf2.c"]),
        ("compiler_rt_trunc.c", ["truncdfsf2.c"]),
    ];

    /// <summary>What a build compiles: the headers as they are, and the <see cref="Units"/>.</summary>
    public static IReadOnlyList<GeneratedFile> Sources() =>
    [
        .. FileNames.Where(name => !name.EndsWith(".c", StringComparison.Ordinal)).Select(File),
        .. Units.Select(unit => Unit(unit.Name, unit.Files)),
    ];

    /// <summary>One compiler-rt file, unchanged, e.g. "adddf3.c" or "fp_lib.h".</summary>
    public static GeneratedFile File(string fileName) =>
        new(fileName, EmbeddedText.Read(typeof(CompilerRt), Prefix + fileName));

    private static GeneratedFile Unit(string name, IEnumerable<string> files)
    {
        var text = new StringBuilder("// compiler-rt's ")
            .Append(string.Join(", ", files))
            .Append(" (ChipSharp.Runtime.ArmV6M/CompilerRt), compiled as one file\n");
        foreach (var file in files)
        {
            text.Append("#line 1 \"").Append(file).Append("\"\n").Append(File(file).Content).Append('\n');
        }

        return new GeneratedFile(name, text.ToString());
    }
}
