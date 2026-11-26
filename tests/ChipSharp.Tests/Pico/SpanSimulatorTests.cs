using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// Spans on a chip: the programs of <see cref="SpanPrograms"/> built for the Pico and run in the simulator print over
/// UART0 what .NET prints, and stop with .NET's message where it throws; MemoryMarshal reads and writes values at any
/// offset on the Cortex-M0+, which faults on a misaligned word, and a MemoryMarshal.Cast that would give such words
/// stops with a message instead; constant data is read from flash; and a chip without a heap has all of it.
/// </summary>
public sealed class SpanSimulatorTests : IDisposable
{
    private const uint FlashStart = 0x10000000;
    private const uint FlashSize = 2 << 20;

    private const string Misaligned = "Unhandled exception. System.DataMisalignedException: A datatype misalignment was " +
        "detected in a load or store instruction.\r\n";

    /// <summary>A Pico without a heap: what a chip of a few KB of RAM (a CH32V003) has, and the Pico's console.</summary>
    private const string NoHeapJson = """{ "inherits": "pico", "capabilities": { "heap": false } }""";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> ProgramNames => [.. SpanPrograms.All.Select(program => program.Name)];

    public static TheoryData<string> FailureNames => [.. SpanPrograms.Failures.Select(failure => failure.Name)];

    [SimulatorTheory]
    [MemberData(nameof(ProgramNames))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name)
    {
        var source = SpanPrograms.All.Single(program => program.Name == name).Source;
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name,
            Path.Combine(_scratch, "dotnet"));
        var program = PicoBuild.Build(source, name, Path.Combine(_scratch, name));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 30_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Output, run.Uart0);
    }

    [SimulatorTheory]
    [MemberData(nameof(FailureNames))]
    public async Task Where_dotnet_throws_the_program_says_so_on_UART0_and_stops_at_a_breakpoint(string name)
    {
        var (_, source, printed, error) = SpanPrograms.Failures.Single(failure => failure.Name == name);
        var program = PicoBuild.Build(source, name, Path.Combine(_scratch, name));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
        Assert.Equal(printed + error, Encoding.ASCII.GetString(run.Uart0));
    }

    [SimulatorFact]
    public async Task MemoryMarshal_reads_and_writes_words_at_every_offset_without_a_fault()
    {
        // every offset from 0 to 7, with an offset the compiler cannot know: an aligned load would fault on 1, 2 and 3
        const string source = """
            using System;
            using System.Runtime.InteropServices;

            struct Sample
            {
                public short Raw;
                public int Time;
                public long Total;
            }

            class Program
            {
                static volatile int start = 0;

                static void Main()
                {
                    Span<byte> bytes = stackalloc byte[32];
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        bytes[i] = (byte)(i * 7 + 1);
                    }

                    for (int offset = start; offset < 8; offset++)
                    {
                        var sample = MemoryMarshal.Read<Sample>(bytes.Slice(offset));
                        Console.WriteLine(MemoryMarshal.Read<int>(bytes.Slice(offset)) + MemoryMarshal.Read<long>(bytes.Slice(offset + 1)));
                        Console.WriteLine(sample.Raw + sample.Time + sample.Total);
                        sample.Time = -offset;
                        MemoryMarshal.Write(bytes.Slice(offset + 8), in sample);
                        Console.WriteLine(MemoryMarshal.Read<uint>(bytes.Slice(offset + 12)));
                    }
                }
            }
            """;
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Unaligned"), "Unaligned",
            Path.Combine(_scratch, "dotnet"));
        var program = PicoBuild.Build(source, "Unaligned", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 10_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Output, run.Uart0);
    }

    [SimulatorFact]
    public async Task A_cast_to_words_that_start_where_this_chip_cannot_read_them_stops_with_a_message()
    {
        // an aligned cast works as on the PC; from an odd offset its ints would be misaligned, which the Cortex-M0+
        // cannot read: the program stops with DataMisalignedException rather than fault (on the PC, .NET reads them)
        const string source = """
            using System;
            using System.Runtime.InteropServices;

            class Program
            {
                static void Main()
                {
                    Span<int> words = stackalloc int[] { 1, 2, 3 };
                    Span<byte> bytes = MemoryMarshal.AsBytes(words);
                    Console.WriteLine(MemoryMarshal.Cast<byte, int>(bytes)[2]);
                    Console.WriteLine(MemoryMarshal.Cast<byte, short>(bytes.Slice(2))[1]);
                    Console.WriteLine(MemoryMarshal.Cast<byte, int>(bytes.Slice(1)).Length);
                }
            }
            """;
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Cast"), "Cast",
            Path.Combine(_scratch, "dotnet"));
        Assert.Equal("3\r\n2\r\n2\r\n", dotnet.Text);
        var program = PicoBuild.Build(source, "Cast", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Equal("3\r\n2\r\n" + Misaligned, Encoding.ASCII.GetString(run.Uart0));
    }

    [PicoFact]
    public void Constant_data_is_a_table_in_flash_with_its_elements()
    {
        // a volatile index keeps LLVM from reading the table while it compiles (and leaving it out)
        var program = PicoBuild.Build("""
            using System;

            class Program
            {
                static ReadOnlySpan<int> Steps => new int[] { 0x11223344, -2, 3, 4 };
                static ReadOnlySpan<byte> Segments => new byte[] { 0x3F, 0x06, 0x5B, 0x4F, 0x66 };
                static volatile int index = 1;

                static void Main() => Console.WriteLine(Steps[index] + Segments[index + 2]);
            }
            """, "Tables", _scratch);
        var elf = ElfFile.Read(program.Elf);
        var text = Assert.Single(elf.Sections, section => section.Name == ".text");
        var flash = PicoBuild.FlashImage(elf);

        // the two tables, named after C#'s fields of constant data: in flash, among the code and constants (.text)
        var tables = elf.Symbols.Where(symbol => symbol.Key.StartsWith("_PrivateImplementationDetails_", StringComparison.Ordinal))
            .Select(symbol => symbol.Value).ToList();
        Assert.Equal(2, tables.Count);
        foreach (var address in tables)
        {
            Assert.InRange(address, FlashStart, FlashStart + FlashSize - 16);
            Assert.True(address >= text.Address && address < text.Address + text.Size, $"0x{address:x8} is not in .text");
        }

        // holding their elements, little-endian
        byte[] At(uint address, int length) => flash.AsSpan((int)(address - FlashStart), length).ToArray();
        Assert.Contains(tables, address =>
            At(address, 16).SequenceEqual(new byte[] { 0x44, 0x33, 0x22, 0x11, 0xFE, 0xFF, 0xFF, 0xFF, 3, 0, 0, 0, 4, 0, 0, 0 }));
        Assert.Contains(tables, address => At(address, 5).SequenceEqual(new byte[] { 0x3F, 0x06, 0x5B, 0x4F, 0x66 }));
    }

    [SimulatorFact]
    public async Task A_chip_without_a_heap_has_spans_stackalloc_constant_data_and_BinaryPrimitives()
    {
        var target = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"), ("pico-noheap", NoHeapJson)))
            .Load("pico-noheap");
        Assert.False(target.HasHeap);
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(SpanPrograms.Bytes, "Bytes"), "Bytes",
            Path.Combine(_scratch, "dotnet"));

        var program = PicoBuild.Build(SpanPrograms.Bytes, "Bytes", Path.Combine(_scratch, "build"), target);
        var run = await Rp2040Simulator.RunAsync(program.Uf2, 30_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Output, run.Uart0);

        // what does need the heap says so, for this chip
        var problems = WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile("""
            using System;

            class Program
            {
                static void Main()
                {
                    Span<int> values = stackalloc int[] { 1, 2 };
                    Console.WriteLine(values.ToArray().Length);
                }
            }
            """), "Test.dll"), target).Problems.Select(problem => problem.Line);
        Assert.Equal(["Program.Main() at IL_0015 (call): copying a span into a new array (ToArray) needs a heap, and the "
            + "target 'pico-noheap' (264 KB RAM) has no heap. Copy it into an array of your own with CopyTo."], problems);
    }
}
