using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Tests.Build;
using ChipSharp.Toolchain.Clang;
using ChipSharp.Toolchain.Sdcc;

namespace ChipSharp.Tests.Sdcc;

/// <summary>
/// The toolchain "sdcc" without running SDCC: where it looks for SDCC, the command lines it gives it for each port, its
/// build cache's keys, what it reads from the target file and the errors it gives before compiling.
/// </summary>
public sealed class SdccToolchainTests : IDisposable
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
    public void The_build_pipeline_has_the_toolchain_sdcc_working_in_the_builds_folder()
    {
        var target = SdccPrograms.Stm8s103;
        var options = new BuildOptions { Target = target.Name, SdccPath = @"D:\sdcc\bin", UseCache = false };

        var registry = DefaultComponents.CreateRegistry(new BuildContext(options, target, "Blink", _scratch));

        var sdcc = Assert.IsType<SdccToolchain>(registry.Get<IToolchain>("sdcc"));
        Assert.Equal("sdcc", sdcc.Name);
        Assert.Equal((_scratch, "Blink", @"D:\sdcc\bin", false), (sdcc.Options.OutputFolder, sdcc.Options.ProgramName,
            sdcc.Options.SdccPath, sdcc.Options.UseCache));
        Assert.Null(sdcc.CacheFolder);
        Assert.Contains("sdcc", registry.Names<IToolchain>());
    }

    [Fact]
    public void SDCC_not_where_CHIPSHARP_SDCC_says_is_an_error_that_says_how_to_get_it()
    {
        var missing = Path.Combine(_scratch, "no-sdcc", "sdcc.exe");
        var toolchain = new SdccToolchain(new SdccToolchainOptions(), name => name == SdccToolchain.SdccVariable ? missing : null);

        var error = Assert.Throws<ChipSharpException>(toolchain.FindSdcc);

        Assert.StartsWith($"SDCC was not found at '{missing}' (set by the environment variable CHIPSHARP_SDCC). ", error.Message);
        Assert.Contains("Install SDCC 4.6 or later (https://sdcc.sourceforge.net/", error.Message);
        Assert.Contains("set CHIPSHARP_SDCC to its sdcc", error.Message);
        var option = new SdccToolchain(new SdccToolchainOptions { SdccPath = missing }, _ => null);
        Assert.Contains("(set by the SdccPath option)", Assert.Throws<ChipSharpException>(option.FindSdcc).Message);
    }

    [Fact]
    public void CHIPSHARP_SDCC_may_name_sdcc_or_its_folder_and_the_option_comes_first()
    {
        var folder = Path.Combine(_scratch, "SDCC 4.6", "bin");
        var other = Path.Combine(_scratch, "other");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(other);
        var sdcc = Path.Combine(folder, "sdcc.exe");
        File.WriteAllBytes(sdcc, []);
        File.WriteAllBytes(Path.Combine(other, "sdcc.exe"), []);

        Assert.Equal(sdcc, new SdccToolchain(new SdccToolchainOptions(), name =>
            name == SdccToolchain.SdccVariable ? sdcc : null).FindSdcc());
        Assert.Equal(sdcc, new SdccToolchain(new SdccToolchainOptions(), name =>
            name == SdccToolchain.SdccVariable ? $"\"{folder}\"" : null).FindSdcc());
        Assert.Equal(sdcc, new SdccToolchain(new SdccToolchainOptions { SdccPath = folder }, name =>
            name == SdccToolchain.SdccVariable ? other : null).FindSdcc());
    }

    [Fact]
    public void Without_a_setting_SDCC_is_looked_for_in_its_install_folder_then_on_the_PATH()
    {
        var toolchain = new SdccToolchain(new SdccToolchainOptions(), name => name == "PATH" ? Path.Combine(_scratch, "none") : null);

        string found;
        try
        {
            found = toolchain.FindSdcc();
        }
        catch (ChipSharpException e)
        {
            // no SDCC installed: the message names the install folder it looked in
            Assert.StartsWith($"SDCC was not found (looked in '{SdccTools.InstallFolder}' and on the PATH). ", e.Message);
            return;
        }

        Assert.Equal(Path.Combine(SdccTools.InstallFolder!, "sdcc.exe"), found);
    }

    [Fact]
    public void The_command_lines_are_the_port_the_targets_flags_and_for_the_STM8_the_start_of_its_flash()
    {
        var pfs = SdccSettings.Read(SdccPrograms.Pfs154);
        var stm = SdccSettings.Read(SdccPrograms.Stm8s103);
        string[] objects = ["blink.c.rel", "steps.c.rel", "answer.s.rel"];

        Assert.Equal(["-mpdk14", "--opt-code-size"], SdccToolchain.CompileFlags(pfs, []));
        Assert.Equal(["-mstm8", "--opt-code-size", "-DDEBUG"], SdccToolchain.CompileFlags(stm, ["-DDEBUG"]));
        Assert.Equal(["-mpdk14", "--opt-code-size", "--out-fmt-ihx", .. objects, "-o", "Blink.ihx"],
            SdccToolchain.LinkArguments(pfs, [], objects, "Blink.ihx"));
        Assert.Equal(["-mstm8", "--opt-code-size", "--code-loc", "0x8000", "--out-fmt-ihx", .. objects, "-o", "Blink.ihx"],
            SdccToolchain.LinkArguments(stm, [], objects, "Blink.ihx"));
        // the library of the runtime's files the program may use nothing of, in the build's folder
        Assert.Equal(["-mpdk14", "--opt-code-size", "--out-fmt-ihx", .. objects, "-L", ".", "-l", "Blink.lib", "-o", "Blink.ihx"],
            SdccToolchain.LinkArguments(pfs, [], objects, "Blink.ihx", "Blink.lib"));
    }

    [Fact]
    public void A_cache_key_is_made_of_SDCC_itself_its_arguments_and_the_text()
    {
        var first = Path.Combine(_scratch, "4.6", "sdcc.exe");
        var second = Path.Combine(_scratch, "4.7", "sdcc.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [1, 2]);
        string[] arguments = ["-mstm8", "--opt-code-size", "-c", "blink.c", "-o", "blink.c.rel"];
        string Key(string sdcc, string[] args, string text) => ObjectCache.Key(sdcc, ".rel", [.. args, text]);

        var key = Key(first, arguments, SdccPrograms.StmBlink);

        Assert.EndsWith(".rel", key);
        Assert.Equal(key, Key(first, arguments, SdccPrograms.StmBlink));
        Assert.NotEqual(key, Key(second, arguments, SdccPrograms.StmBlink));
        Assert.NotEqual(key, Key(first, ["-mpdk14", .. arguments[1..]], SdccPrograms.StmBlink));
        Assert.NotEqual(key, Key(first, [.. arguments, "-DDEBUG"], SdccPrograms.StmBlink));
        Assert.NotEqual(key, Key(first, arguments, SdccPrograms.StmBlink + " "));
    }

    [Fact]
    public void A_file_name_with_a_space_or_a_build_without_C_fails_before_SDCC_runs()
    {
        var toolchain = new SdccToolchain(new SdccToolchainOptions { OutputFolder = _scratch, SdccPath = Path.Combine(_scratch, "none") });
        var target = SdccPrograms.Stm8s103;

        var space = Assert.Throws<ChipSharpException>(() => toolchain.Build(
            new GeneratedCode([new GeneratedFile("my blink.c", SdccPrograms.StmBlink)]), target));
        var none = Assert.Throws<ChipSharpException>(() => toolchain.Build(
            new GeneratedCode([new GeneratedFile("answer.s", SdccPrograms.StmAnswer)]), target));

        Assert.Equal("Generated file name 'my blink.c' has a space, which SDCC cannot take in a file name.", space.Message);
        Assert.Equal("There is nothing to compile: the generated code has no .c file.", none.Message);
    }

    [Theory]
    [InlineData("""{ "sdcc": { "port": "stm 8" } }""", "'sdcc.port' must be SDCC's name of the processor (sdcc's -m), e.g. \"stm8\" or \"pdk14\".")]
    [InlineData("""{ "sdcc": { "port": "stm8", "flags": "--opt-code-size" } }""", "'sdcc.flags' must be a list of text, e.g. [\"--opt-code-size\"].")]
    [InlineData("""{ "link": { "stack": "1K" } }""", "'link.stack' is 1 KB, which leaves nothing of the 1 KB of ram ('link.data') for the variables.")]
    [InlineData("""{ "memory": { "flash": { "wordSize": 3 } } }""", "'memory.flash.wordSize' is 3; it must be 1, 2 or 4 (the bytes of the .ihx a word takes), and the region's origin and size whole words.")]
    public void A_wrong_SDCC_setting_says_which_and_what_it_must_be(string json, string message)
    {
        var folder = TargetFiles.WithShipped(_scratch, ("odd", $$"""{ "inherits": "stm8s103-board", {{json.Trim()[1..^1]}} }"""));
        var target = new Targets.TargetLoader(folder).Load("odd");

        var error = Assert.Throws<ChipSharpException>(() => SdccSettings.Read(target));

        Assert.Equal($"Target 'odd': {message}", error.Message);
    }

    [Fact]
    public void A_target_without_an_sdcc_section_is_not_for_this_toolchain()
    {
        var error = Assert.Throws<ChipSharpException>(() => SdccSettings.Read(SdccPrograms.Shipped.Load("uno")));

        Assert.Equal("Target 'uno': 'sdcc' is missing; the toolchain \"sdcc\" needs SDCC's port, e.g. " +
            "{ \"sdcc\": { \"port\": \"stm8\" } }.", error.Message);
    }
}
