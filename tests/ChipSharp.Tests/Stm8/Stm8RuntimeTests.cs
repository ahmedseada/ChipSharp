using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Runtime.Stm8;
using ChipSharp.Runtime.Stm8s103;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Toolchain.Sdcc;

namespace ChipSharp.Tests.Stm8;

/// <summary>
/// The STM8 runtimes' files: the architecture's ("stm8") and the STM8S103F3's, which the build pipeline finds by the
/// target's "runtime"; the modules a file is cut into, so that SDCC's linker takes only what a program uses; the
/// defaults of the program's event handlers; and that no two modules of the program's library define a symbol, which
/// would make the module the linker takes depend on the order it looks in.
/// </summary>
public sealed partial class Stm8RuntimeTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void The_board_builds_with_the_STM8S103F3s_runtime_on_the_STM8_runtime()
    {
        var board = Stm8sProgram.Board;
        var build = new BuildContext(new BuildOptions(), board, "Program", _scratch);

        Assert.IsType<Stm8s103Runtime>(DefaultComponents.CreateRegistry(build).Get<IArchRuntime>(board.Runtime!));
        Assert.IsType<Stm8Runtime>(DefaultComponents.CreateRegistry(build).Get<IArchRuntime>("stm8"));
    }

    [Fact]
    public void A_file_with_module_lines_is_one_module_for_each_with_the_text_before_the_first()
    {
        var modules = Stm8Runtime.Modules(new GeneratedFile("parts.c", """
            // the parts
            typedef long s32;

            //@module first
            s32 one(void) { return 1; }

            //@module second_part
            s32 two(void) { return 2; }
            """));

        Assert.Equal(["parts_first.c", "parts_second_part.c"], modules.Select(module => module.FileName));
        Assert.All(modules, module =>
        {
            Assert.Contains("#define CHIPSHARP_MODULES 1\n", module.Content, StringComparison.Ordinal);
            Assert.Contains("typedef long s32;", module.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("//@module", module.Content, StringComparison.Ordinal);
        });
        Assert.Contains("s32 one(void)", modules[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("s32 two(void)", modules[0].Content, StringComparison.Ordinal);
        Assert.Contains("s32 two(void)", modules[1].Content, StringComparison.Ordinal);

        // a file without such a line is itself; a part without a name of its own is a mistake
        var whole = new GeneratedFile("whole.c", "int x;\n");
        Assert.Same(whole, Assert.Single(Stm8Runtime.Modules(whole)));
        Assert.Throws<ChipSharpException>(() => Stm8Runtime.Modules(new GeneratedFile("twice.c", "//@module a\n//@module a\n")));
    }

    [Fact]
    public void The_chip_runtime_replaces_the_architectures_vectors_and_defaults_and_ships_the_chips_headers()
    {
        var board = Stm8sProgram.Board;
        var runtime = new Stm8s103Runtime();

        var files = runtime.Startup(board).Files;

        Assert.Equal(files.Count, files.Select(f => f.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(files, f => Stm8Runtime.DefaultModuleNames.Contains(f.FileName));
        Assert.Contains("STM8S103F3_HANDLER(TIM1_UPD_OVF);",
            Assert.Single(files, f => f.FileName == Stm8Runtime.VectorsFileName).Content, StringComparison.Ordinal);
        Assert.All(Stm8s103Runtime.ChipHeaderFileNames, header => Assert.Contains(files, f => f.FileName == header));
        Assert.Contains(files, f => f.FileName == "stm8_defaults_local_utc_offset.c");
        Assert.Contains(files, f => f.FileName == "chipsharp_console_float32.c");
        Assert.Contains(files, f => f.FileName == "chipsharp_string_types.c");
        Assert.Contains(files, f => f.FileName == "stm8_failures_index_out_of_range.c");

        // each event of the target's interrupts table has the default of the program's method, a module of its own
        Assert.Equal(["stm8_isr_irq30.c", "stm8_isr_irq31.c", "stm8_isr_irq32.c"],
            runtime.InterruptEntry(board).Files.Select(f => f.FileName));
        Assert.Contains("void chipsharp_isr_irq31(void)\n{\n}\n", runtime.InterruptEntry(board).Files[1].Content,
            StringComparison.Ordinal);
        Assert.Empty(runtime.Atomics(board).Files);
    }

    [SdccFact]
    public void No_two_modules_of_the_runtime_define_the_same_symbol()
    {
        var board = Stm8sProgram.Board;
        var runtime = new Stm8s103Runtime();
        var program = new GeneratedFile("Program.c", "long chipsharp_entry(void)\n{\n    return 0;\n}\n");

        new SdccToolchain(new SdccToolchainOptions { OutputFolder = _scratch, ProgramName = "Program", UseCache = false })
            .Build(new GeneratedCode([program, .. runtime.Startup(board).Files, .. runtime.InterruptEntry(board).Files]),
                board);

        // every module was compiled (a .rel each), and each global symbol is defined in one of them
        var definitions = Directory.GetFiles(_scratch, "*.rel")
            .Where(rel => !Path.GetFileName(rel).StartsWith("Program.", StringComparison.Ordinal))
            .SelectMany(rel => Definition().Matches(File.ReadAllText(rel)).Select(m => (Symbol: m.Groups[1].Value,
                Module: Path.GetFileName(rel))))
            .ToList();
        Assert.True(definitions.Count > 200, $"{definitions.Count} symbols");
        var twice = definitions.GroupBy(d => d.Symbol).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(d => d.Module))}").ToList();
        Assert.True(twice.Count == 0, string.Join("\n", twice));
    }

    [GeneratedRegex(@"^S (_\w+) Def[0-9A-Fa-f]+\s*$", RegexOptions.Multiline)]
    private static partial Regex Definition();
}
