using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Build;

/// <summary>What <see cref="ChipSharpBuilder"/> says when a build cannot be done. None of these needs LLVM.</summary>
public sealed class BuildErrorTests : IDisposable
{
    private static readonly string ShippedTargets = Path.Combine(AppContext.BaseDirectory, "targets");
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void An_incomplete_chip_names_what_is_missing_and_the_boards_to_build_instead()
    {
        var error = Assert.Throws<ChipSharpException>(() => ChipSharpBuilder.Build(Samples.Compile("Square", _scratch),
            new BuildOptions { Target = "rp2040", TargetsFolder = ShippedTargets, OutputFolder = _scratch }));

        Assert.StartsWith(
            "Target 'rp2040' is incomplete (memory.flash.size); build a board that inherits it, such as pico",
            error.Message);
        Assert.Contains("pico-w", error.Message);
        Assert.EndsWith(".", error.Message);
    }

    [Fact]
    public void An_incomplete_target_that_no_complete_target_inherits_says_how_to_make_one()
    {
        var targets = TargetFiles.Write(Path.Combine(_scratch, "targets"),
            ("lonely", """
                {
                  "backend": "llvm", "toolchain": "clang", "runtime": "windows-x64", "pointerSize": 4, "endian": "little",
                  "memory": { "flash": { "origin": "0x0" }, "ram": { "origin": "0x20000000" } }
                }
                """),
            // inherits it, but is still incomplete: not worth suggesting
            ("half", """{ "inherits": "lonely", "memory": { "flash": { "size": "64K" } } }"""),
            // a broken file elsewhere in the folder does not get in the way of the message
            ("broken", "{ not json"));

        var error = Assert.Throws<ChipSharpException>(() => ChipSharpBuilder.Build(Path.Combine(_scratch, "never-read.dll"),
            new BuildOptions { Target = "lonely", TargetsFolder = targets }));

        Assert.Equal(
            "Target 'lonely' is incomplete (memory.flash.size, memory.ram.size); a target that inherits it must set " +
            "those: write a board file with \"inherits\": \"lonely\" and build that.",
            error.Message);
    }

    [Fact]
    public void An_unknown_target_lists_the_known_ones()
    {
        var error = Assert.Throws<ChipSharpException>(() => ChipSharpBuilder.Build(Samples.Compile("Square", _scratch),
            new BuildOptions { Target = "arduino-uno", TargetsFolder = ShippedTargets }));

        Assert.StartsWith($"Target 'arduino-uno' not found in '{ShippedTargets}'. Known targets: ", error.Message);
        Assert.Contains("host", error.Message);
    }

    [Theory]
    [InlineData("native-board", "Target 'native-board' cannot be built: Code generator 'native' is not registered.")]
    [InlineData("sh-board", "Target 'sh-board' cannot be built: Toolchain 'gcc-sh' is not registered.")]
    [InlineData("esp-board", "Target 'esp-board' cannot be built: Image format 'esp-image' is not registered.")]
    [InlineData("z80-board", "Target 'z80-board' cannot be built: Architecture runtime 'z80' is not registered.")]
    [InlineData("bare-board", "Target 'bare-board' cannot be built: it does not say which runtime to use (\"runtime\" is missing).")]
    public void A_plug_in_nobody_registered_is_named_with_the_target_that_asks_for_it(string target, string message)
    {
        var targets = TargetFiles.Write(Path.Combine(_scratch, "targets"),
            ("pc", """{ "backend": "llvm", "toolchain": "clang", "runtime": "windows-x64", "pointerSize": 8, "endian": "little" }"""),
            ("native-board", """{ "inherits": "pc", "backend": "native" }"""),
            ("sh-board", """{ "inherits": "pc", "toolchain": "gcc-sh" }"""),
            ("esp-board", """{ "inherits": "pc", "image": "esp-image" }"""),
            ("z80-board", """{ "inherits": "pc", "runtime": "z80" }"""),
            ("bare-board", """{ "backend": "llvm", "toolchain": "clang", "pointerSize": 4, "endian": "little" }"""));
        var dll = Samples.Compile("Square", _scratch);
        var output = Path.Combine(_scratch, "out");

        var error = Assert.Throws<ChipSharpException>(() => ChipSharpBuilder.Build(dll,
            new BuildOptions { Target = target, TargetsFolder = targets, OutputFolder = output }));

        // the registered names follow (e.g. "Registered: llvm."); they grow as plug-ins are added
        Assert.StartsWith(message, error.Message);
        Assert.False(Directory.Exists(output), "Nothing is written before every part is found.");
    }

    [Fact]
    public void A_program_name_that_cannot_be_a_file_name_is_rejected()
    {
        var dll = Samples.Compile("Square", _scratch);

        var error = Assert.Throws<ChipSharpException>(() => ChipSharpBuilder.Build(dll,
            new BuildOptions { TargetsFolder = ShippedTargets, ProgramName = @"out\Square" }));

        Assert.StartsWith(@"The program name 'out\Square' cannot be a file name", error.Message);
    }

    [Fact]
    public void A_missing_assembly_and_a_missing_targets_folder_are_named()
    {
        var missing = Path.Combine(_scratch, "missing.dll");
        var error = Assert.Throws<ChipSharpException>(() =>
            ChipSharpBuilder.Build(missing, new BuildOptions { TargetsFolder = ShippedTargets }));
        Assert.StartsWith($"Cannot read '{missing}'", error.Message);

        var noTargets = Path.Combine(_scratch, "no-targets");
        error = Assert.Throws<ChipSharpException>(() =>
            ChipSharpBuilder.Build(missing, new BuildOptions { TargetsFolder = noTargets }));
        Assert.Equal($"Targets folder '{noTargets}' does not exist.", error.Message);
    }

    [Fact]
    public void A_program_outside_the_supported_subset_fails_with_the_whole_program_pass_message()
    {
        var dll = CSharpCompiler.CompileToFile("""
            using System;

            class Program
            {
                static string Format() => "Hello {0}";

                static void Main() => Console.WriteLine(new System.Text.StringBuilder().AppendFormat(Format(), 5).ToString());
            }
            """, Path.Combine(_scratch, "Hello.dll"));
        var output = Path.Combine(_scratch, "out");

        var error = Assert.Throws<ChipSharpException>(() => ChipSharpBuilder.Build(dll,
            new BuildOptions { TargetsFolder = ShippedTargets, OutputFolder = output, KeepIntermediates = true }));

        Assert.Equal(WholeProgramPass.Check(IlAssembly.Load(dll)).Summary, error.Message);
        Assert.StartsWith($"{dll} uses 1 C# feature that ChipSharp does not compile yet (1 place):\n  building text "
            + "($\"...\", +, ToString, string.Format and the other methods of string) arrives later: "
            + "1 place in Program.Main() (IL_0010).", error.Message);
        Assert.EndsWith($"\nFor every place, run: chipsharp check {ChipSharpCli.Quoted(dll)} --details", error.Message);
        Assert.False(Directory.Exists(output));
    }
}
