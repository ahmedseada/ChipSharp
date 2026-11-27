using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Build;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Llvm;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// LLVM 22, the one in Zig, accepts what the generator writes for an AVR, and compiles it for an ATmega328P: every
/// instruction kind on every type, and every sample that needs no heap, whose constant data all stays in flash.
/// </summary>
public sealed class AvrZigTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> SampleNames => [.. Samples.Names];

    // not -O0: there LLVM 22's AVR backend (its fast register allocator) runs out of registers in ordinary functions, such
    // as Program.SumOfSquares; ChipSharp always optimizes
    [AvrTheory]
    [InlineData("-Os")]
    [InlineData("-O2")]
    public async Task Zig_compiles_every_instruction_kind_on_every_type_for_an_atmega328p(string optimization)
    {
        var ll = Assert.Single(new LlvmCodeGenerator().Generate(LlvmSamples.Everything(), AvrCode.Uno()).Files);

        var objectFile = await AvrCode.CompileAsync(_scratch, ll.FileName, ll.Content, optimization);

        Assert.True(new FileInfo(objectFile).Length > 0);
    }

    [AvrTheory]
    [MemberData(nameof(SampleNames))]
    public async Task Every_sample_without_objects_compiles_for_an_atmega328p_with_its_constants_in_flash(string name)
    {
        var target = AvrCode.Uno();
        var dll = Samples.Compile(name, _scratch);
        var analysis = WholeProgramPass.Check(IlAssembly.Load(dll), target);
        if (!analysis.IsSupported)
        {
            // a sample that makes objects or keeps built text needs a heap, which the Uno does not have
            Assert.Contains("needs a heap", analysis.Summary, StringComparison.Ordinal);
            return;
        }

        await CompilesWithItsConstantsInFlash(dll, target);
    }

    [AvrFact]
    public async Task DateTime_formatting_and_its_name_tables_compile_for_an_atmega328p_with_the_tables_in_flash()
    {
        var dll = CSharpCompiler.CompileToFile("""
            using System;

            class Program
            {
                static void Main()
                {
                    var day = new DateTime(2026, 11, 27, 21, 5, 9);
                    Console.WriteLine($"{day:yyyy-MM-dd HH:mm:ss} {day:dddd} {day:MMMM}");
                    Console.WriteLine(day.DayOfWeek);
                    Console.WriteLine(day.AddDays(40).Month);
                }
            }
            """, Path.Combine(_scratch, "Days.dll"));

        await CompilesWithItsConstantsInFlash(dll, AvrCode.Uno());
    }

    /// <summary>
    /// Translates the program in <paramref name="dll"/>, generates its LLVM for <paramref name="target"/> and compiles it
    /// with Zig: nothing constant is copied to RAM (no .rodata; .data holds only static fields that start with a value),
    /// and its constant data is in flash's section.
    /// </summary>
    private async Task CompilesWithItsConstantsInFlash(string dll, TargetDescription target)
    {
        var module = ChipSharpBuilder.Translate(dll, target);
        var ll = Assert.Single(new LlvmCodeGenerator().Generate(module, target).Files);
        var objectFile = await AvrCode.CompileAsync(_scratch, ll.FileName, ll.Content, "-Os");

        var sections = await Sections(objectFile);
        Assert.DoesNotContain(sections, section => section.Name.StartsWith(".rodata", StringComparison.Ordinal));
        if (module.Globals.Any(global => global.IsConstant))
        {
            Assert.Contains(sections, section => section.Name.StartsWith(LlvmFlash.Section, StringComparison.Ordinal));
        }
    }

    /// <summary>The sections of an object file, with their sizes, as llvm-readobj lists them.</summary>
    private static async Task<List<(string Name, long Size)>> Sections(string objectFile)
    {
        var result = await LlvmTools.RunAsync(PicoTools.LlvmTool("llvm-readobj"), ["--sections", objectFile],
            Path.GetDirectoryName(objectFile)!);
        Assert.True(result.ExitCode == 0, result.ToString());
        var sections = new List<(string, long)>();
        string? name = null;
        foreach (var line in result.Output.Split('\n').Select(line => line.Trim()))
        {
            if (line.StartsWith("Name: ", StringComparison.Ordinal))
            {
                name = line["Name: ".Length..].Split(' ')[0];
            }
            else if (line.StartsWith("Size: ", StringComparison.Ordinal) && name is not null)
            {
                sections.Add((name, long.Parse(line["Size: ".Length..], System.Globalization.CultureInfo.InvariantCulture)));
                name = null;
            }
        }

        return sections;
    }
}
