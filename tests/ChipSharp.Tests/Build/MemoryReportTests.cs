using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Build;

/// <summary>
/// How much of the target's memory a built program takes, read from its .elf (<see cref="MemoryReport"/>), and what a
/// build says when the program does not fit (<see cref="MemoryOverflow"/>): with both sizes and the target, not
/// ld.lld's own words.
/// </summary>
public sealed class MemoryReportTests : IDisposable
{
    private const uint FlashStart = 0x10000000;
    private const uint RamStart = 0x20000000;

    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [PicoFact]
    public void The_report_of_a_chip_program_is_what_its_elf_sections_take_in_flash_and_RAM()
    {
        var program = PicoBuild.Build(PicoBuild.BlinkSource, "PicoBlink", _scratch);
        var elf = ElfFile.Read(program.Elf);
        ElfSection Section(string name) => Assert.Single(elf.Sections, section => section.Name == name);

        var report = MemoryReport.Read(new LinkedBinary(program.Elf), PicoBuild.Pico);

        // flash: boot2, the vector table, code and constants, then the starting values of .data, one after another
        var flash = new[] { ".boot2", ".vectors", ".text", ".ARM.exidx", ".data" }.Sum(name => (long)Section(name).Size);
        Assert.Equal(new RegionUse("flash", (ulong)flash, 2 << 20), report.Code);
        Assert.Equal((ulong)(elf.Symbols["__chipsharp_data_load"] + Section(".data").Size - FlashStart), report.Code!.Used);
        // RAM: the variables, .data then .bss; the stack is kept apart, at the end
        Assert.Equal(new RegionUse("ram", Section(".data").Size + Section(".bss").Size, 264 << 10), report.Data);
        Assert.Equal((ulong)(elf.Symbols["__chipsharp_bss_end"] - RamStart), report.Data!.Used);
        Assert.Equal(8UL << 10, report.Stack);
        Assert.Equal(("PicoBlink.elf", (ulong)new FileInfo(program.Elf).Length), (report.FileName, report.FileSize));
        Assert.Equal(string.Create(CultureInfo.InvariantCulture,
            $"flash {MemorySize.Text((ulong)flash)} of 2048 KB ({100.0 * flash / (2 << 20):0.0}%), " +
            $"RAM {MemorySize.Text(report.Data.Used)} of 264 KB (stack 8 KB reserved)"), report.Text);
    }

    [Fact]
    public void The_report_of_a_program_for_a_target_without_memory_regions_is_its_file_size()
    {
        var file = Path.Combine(_scratch, "Square.exe");
        Directory.CreateDirectory(_scratch);
        File.WriteAllBytes(file, new byte[5632]);

        var report = MemoryReport.Read(new LinkedBinary(file), Shipped.Load("host"));

        Assert.Equal(new MemoryReport("Square.exe", 5632, null, null, 0), report);
        Assert.Equal("Square.exe: 5.5 KB", report.Text);
    }

    [Fact]
    public void A_flash_overflow_is_told_with_what_the_program_needs_and_what_the_target_has()
    {
        var target = Board("""{ "inherits": "pico", "memory": { "flash": { "size": "1K" } } }""");
        const string Lld = """
            clang failed (exit code 1) in 'C:\temp':
              clang.exe --target=thumbv6m-none-eabi ...
            ld.lld: error: section '.text' will not fit in region 'flash': overflowed by 136 bytes
            ld.lld: error: section '.ARM.exidx' will not fit in region 'flash': overflowed by 152 bytes
            ld.lld: error: section '.data' will not fit in region 'flash': overflowed by 152 bytes
            clang: error: linker command failed with exit code 1 (use -v to see invocation)
            """;

        var message = MemoryOverflow.Message(Lld, "Blink", target, ChipSettings.Read(target));

        Assert.Equal("Blink does not fit in target 'board': its code, constants and the starting values of its variables " +
            "need 1.1 KB of flash, 152 bytes more than the target's 1 KB. Make the program smaller, or build it for a " +
            "chip with more memory.", message);
    }

    [Fact]
    public void A_RAM_overflow_counts_the_stack_the_target_keeps()
    {
        var target = Board("""{ "inherits": "pico", "memory": { "ram": { "size": "10K" } }, "capabilities": { "heap": false } }""");
        const string Lld = "ld.lld: error: section '.bss' will not fit in region 'ram': overflowed by 2148 bytes\n" +
            "ld.lld: error: section '.text' will not fit in region 'flash': overflowed by 5 bytes";

        var message = MemoryOverflow.Message(Lld, "Big", target, ChipSettings.Read(target));

        // the variables have the 2 KB the stack leaves, and need 2148 bytes more
        Assert.Equal("Big does not fit in target 'board': its code, constants and the starting values of its variables " +
            "need 2048.0 KB of flash, 5 bytes more than the target's 2048 KB; its variables (4.1 KB) and the stack " +
            "(8 KB, link.stack) need 12.1 KB of RAM, 2.1 KB more than the target's 10 KB. Make the program smaller, or " +
            "build it for a chip with more memory.", message);
    }

    [Fact]
    public void A_RAM_overflow_without_a_stack_is_the_variables_alone()
    {
        var target = Board("""
            { "inherits": "pico", "memory": { "ram": { "size": "4" } }, "link": { "stack": 0 }, "capabilities": { "heap": false } }
            """);

        var message = MemoryOverflow.Message(
            "ld.lld: error: section '.bss' will not fit in region 'ram': overflowed by 4 bytes", "Blink", target,
            ChipSettings.Read(target));

        Assert.Equal("Blink does not fit in target 'board': its variables need 8 bytes of RAM, 4 bytes more than the " +
            "target's 4 bytes. Make the program smaller, or build it for a chip with more memory.", message);
    }

    [Fact]
    public void A_link_that_failed_for_another_reason_keeps_the_linkers_words()
    {
        var pico = Shipped.Load("pico");

        Assert.Null(MemoryOverflow.Message("ld.lld: error: undefined symbol: chipsharp_entry", "Blink", pico,
            ChipSettings.Read(pico)));
    }

    [PicoFact]
    public void A_program_too_big_for_the_flash_fails_the_build_with_both_sizes_and_the_target()
    {
        var dll = BlinkDll();
        var targets = TinyChip.Targets(_scratch);
        var pico = ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = "pico", TargetsFolder = targets, OutputFolder = Path.Combine(_scratch, "pico"),
        });

        var error = Assert.Throws<ChipSharpException>(() => ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = TinyChip.TinyFlash, TargetsFolder = targets, OutputFolder = Path.Combine(_scratch, "tiny"),
        }));

        // the same program needs the same flash as on the pico
        var needs = pico.Memory.Code!.Used;
        Assert.Equal($"PicoBlink does not fit in target 'tiny-flash': its code, constants and the starting values of " +
            $"its variables need {MemorySize.Text(needs)} of flash, {MemorySize.Text(needs - 512)} more than the " +
            "target's 512 bytes. Make the program smaller, or build it for a chip with more memory.", error.Message);
        Assert.Contains("will not fit in region 'flash'", error.InnerException!.Message);
        Assert.False(Directory.Exists(Path.Combine(_scratch, "tiny")));
    }

    [PicoFact]
    public async Task Build_for_a_chip_prints_the_image_then_what_the_program_takes_of_the_chips_memory()
    {
        var dll = BlinkDll();
        var output = Path.Combine(_scratch, "out");

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", "pico", "-o", output, "--keep");

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        var lines = BuildCliTests.Lines(printed);
        var elf = Path.Combine(output, "PicoBlink.elf");
        Assert.Equal([Path.Combine(output, "PicoBlink.uf2"), Path.Combine(output, "PicoBlink.tir"),
            Path.Combine(output, "PicoBlink.ll"), elf], lines[..4]);
        Assert.Equal(MemoryReport.Read(new LinkedBinary(elf), Shipped.Load("pico")).Text, lines[4]);
        Assert.StartsWith("flash 1.", lines[4]);
        Assert.EndsWith(" of 264 KB (stack 8 KB reserved)", lines[4]);
        Assert.Equal(5, lines.Length);
    }

    [PicoFact]
    public async Task Build_for_a_chip_without_a_heap_or_a_console_compiles_a_program_that_needs_neither()
    {
        var dll = CSharpCompiler.CompileToFile("""
            class Program
            {
                static int total;

                static void Main()
                {
                    for (var i = 1; i <= 10; i++)
                    {
                        total += i * i;
                    }
                }
            }
            """, Path.Combine(_scratch, "Sum.dll"));
        var output = Path.Combine(_scratch, "out");

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", TinyChip.Name,
            "--targets", TinyChip.Targets(_scratch), "-o", output);

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        var lines = BuildCliTests.Lines(printed);
        var elf = Path.Combine(output, "Sum.elf");
        Assert.Equal(elf, lines[0]);
        var report = MemoryReport.Read(new LinkedBinary(elf), new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name));
        Assert.Equal(16UL << 10, report.Code!.Size);
        Assert.InRange(report.Data!.Used, 4UL, 256UL);
        Assert.Equal(report.Text, lines[1]);
        Assert.Matches(@"^flash \d+(\.\d)? (bytes|KB) of 16 KB \(\d+\.\d%\), RAM \d+ bytes of 2 KB \(stack 512 bytes reserved\)$",
            lines[1]);
    }

    [Fact]
    public async Task Check_for_a_target_judges_the_program_for_that_chip()
    {
        var dll = CSharpCompiler.CompileToFile("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(new Counter().Count);
                }
            }

            class Counter
            {
                public int Count = 3;
            }
            """, Path.Combine(_scratch, "Counter.dll"));
        var targets = TinyChip.Targets(_scratch);

        var tiny = await ChipSharpCli.RunAsync("check", dll, "-t", TinyChip.Name, "--targets", targets);
        var pico = await ChipSharpCli.RunAsync("check", dll, "-t", "pico", "--targets", targets);
        var neutral = await ChipSharpCli.RunAsync("check", dll, "--targets", targets);

        Assert.Equal((1, ""), (tiny.ExitCode, tiny.Output));
        Assert.Equal($"""
            error: {dll} uses 3 C# features that ChipSharp does not compile yet (3 places):
              creating objects (new ...) needs a heap, and the target 'tiny' (2 KB RAM) has no heap: 1 place in Program.Main() (IL_0000). Use structs and static methods.
              instance fields of classes (fields without static) need a heap, and the target 'tiny' (2 KB RAM) has no heap: 1 place in Program.Main() (IL_0005). Make the fields static, or make the class a struct.
              Console.Write and Console.WriteLine need a console, and the target 'tiny' has none: 1 place in Program.Main() (IL_000a). Show what the program does another way, e.g. on a pin with ChipSharp.Hardware.Pin.
            For every place, run: chipsharp check {ChipSharpCli.Quoted(dll)} -t tiny --details

            """.ReplaceLineEndings("\n"), tiny.Error.ReplaceLineEndings("\n"));

        // a target with a heap and a console, and no target: the objects arrive with M3, the console is there
        Assert.Equal(1, pico.ExitCode);
        Assert.Equal(neutral.Error, pico.Error);
        Assert.Contains("creating objects (new ...) arrives with M3 (the heap): 1 place in Program.Main() (IL_0000). " +
            "For now, use structs and static methods.", pico.Error);
        Assert.DoesNotContain("console", pico.Error);
    }

    [Fact]
    public async Task Check_for_a_target_says_the_program_compiles_for_it()
    {
        var dll = Samples.Compile("Square", _scratch);

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("check", dll, "-t", "pico");
        var tiny = await ChipSharpCli.RunAsync("check", dll, "-t", TinyChip.Name, "--targets", TinyChip.Targets(_scratch),
            "--details");

        Assert.Equal((0, ""), (exitCode, error));
        Assert.StartsWith($"{dll}: ChipSharp compiles it for target 'pico'. The program uses 2 methods, entry point first:",
            printed);
        Assert.Equal(1, tiny.ExitCode);
        Assert.Contains("Program.Main() at IL_0007 (call): Console.Write and Console.WriteLine need a console, and the " +
            "target 'tiny' has none.", tiny.Error);
    }

    /// <summary>A target "board" written next to the shipped ones, loaded.</summary>
    private TargetDescription Board(string json) =>
        new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"), ("board", json))).Load("board");

    /// <summary>The blink example compiled against ChipSharp.Hardware, as PicoBlink.dll in the scratch folder.</summary>
    private string BlinkDll()
    {
        var dll = Path.Combine(_scratch, "PicoBlink.dll");
        Directory.CreateDirectory(_scratch);
        File.WriteAllBytes(dll, CSharpCompiler.Compile([PicoBuild.BlinkSource], [PicoBuild.HardwareAssembly], "PicoBlink"));
        return dll;
    }
}
