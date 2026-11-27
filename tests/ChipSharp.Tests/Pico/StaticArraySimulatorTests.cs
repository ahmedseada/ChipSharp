using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Build;
using ChipSharp.Frontend;
using ChipSharp.Image.Uf2;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// Arrays in static fields on a chip: the programs of <see cref="StaticArrayPrograms"/> built for the Pico and run in the
/// simulator print over UART0 what .NET prints; a constant table is in flash (read where the program runs from, never
/// copied to RAM) and a static fixed array in RAM, which the memory report counts; and a chip without a heap has them
/// too.
/// </summary>
public sealed class StaticArraySimulatorTests : IDisposable
{
    private const uint FlashStart = 0x10000000;
    private const uint FlashSize = 2 << 20;
    private const uint RamStart = 0x20000000;
    private const uint RamSize = 264 << 10;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> ProgramNames => [.. StaticArrayPrograms.All.Select(program => program.Name)];

    [SimulatorTheory]
    [MemberData(nameof(ProgramNames))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name)
    {
        var source = StaticArrayPrograms.All.Single(program => program.Name == name).Source;
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name,
            Path.Combine(_scratch, "dotnet"));
        var program = PicoBuild.Build(source, name, Path.Combine(_scratch, name));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 30_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Output, run.Uart0);
    }

    [SimulatorFact]
    public async Task An_index_outside_a_table_says_so_on_UART0_and_stops_at_a_breakpoint()
    {
        var program = PicoBuild.Build(StaticArrayPrograms.OutOfRange, "OutOfRange", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
        Assert.Equal(StaticArrayPrograms.OutOfRangePrinted + ArrayPrograms.IndexOutOfRangeMessage,
            System.Text.Encoding.ASCII.GetString(run.Uart0));
    }

    [PicoFact]
    public void A_table_is_in_flash_with_its_bytes_and_a_fixed_array_in_RAM()
    {
        // a volatile start keeps LLVM from working the elements out while it compiles (and leaving the arrays out)
        var program = PicoBuild.Build("""
            using System;

            class Program
            {
                static readonly int[] Steps = { 0x11223344, -2, 3, 4 };
                static int[] samples = new int[8];
                static long[] history = { 1, 2, 3, 4 };
                static volatile int start = 1;

                static void Main()
                {
                    for (int i = 0; i < samples.Length; i++)
                    {
                        samples[i] = Steps[(start + i) % Steps.Length];
                    }

                    history[start] += samples[(start + 4) % 8];
                    Console.WriteLine(samples[(start + 2) % 8] + history[(start + 3) % 4]);
                }
            }
            """, "Placement", _scratch);
        var elf = ElfFile.Read(program.Elf);
        ElfSection Section(string name) => Assert.Single(elf.Sections, section => section.Name == name);
        bool In(ElfSection section, uint address, uint size) =>
            address >= section.Address && address + size <= section.Address + section.Size;

        // the table: in flash, among the code and constants (.text), holding its elements, little-endian
        var steps = elf.Symbols["Program_Steps"];
        Assert.InRange(steps, FlashStart, FlashStart + FlashSize - 16);
        Assert.True(In(Section(".text"), steps, 16), $"Program_Steps at 0x{steps:x8} is not in .text");
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11, 0xFE, 0xFF, 0xFF, 0xFF, 3, 0, 0, 0, 4, 0, 0, 0 },
            PicoBuild.FlashImage(elf).AsSpan((int)(steps - FlashStart), 16).ToArray());

        // the fixed arrays: in RAM, one starting at zero (.bss), one with its starting values (.data, copied from flash)
        var samples = elf.Symbols["Program_samples"];
        var history = elf.Symbols["Program_history"];
        Assert.InRange(samples, RamStart, RamStart + RamSize - 32);
        Assert.True(In(Section(".bss"), samples, 32), $"Program_samples at 0x{samples:x8} is not in .bss");
        Assert.True(In(Section(".data"), history, 32), $"Program_history at 0x{history:x8} is not in .data");
    }

    [PicoFact]
    public void The_memory_report_counts_a_table_in_flash_and_a_fixed_array_in_RAM()
    {
        // the same programs with 256 ints and with 2: 254 ints, 1016 bytes, more
        string Elements(int count) =>
            string.Join(", ", Enumerable.Range(1, count).Select(i => (i * 7).ToString(CultureInfo.InvariantCulture)));
        const string Fill = "Values[(start + i) % Values.Length] = i;";
        var table = Report("Table", $"static readonly int[] Values = {{ {Elements(256)} }};", "");
        var small = Report("SmallTable", $"static readonly int[] Values = {{ {Elements(2)} }};", "");
        var buffer = Report("Buffer", "static int[] Values = new int[256];", Fill);
        var smallBuffer = Report("SmallBuffer", "static int[] Values = new int[2];", Fill);

        // the table's elements are flash, not RAM; the buffer's are RAM, not flash (.bss: it starts at zero)
        Assert.InRange(table.Code!.Used - small.Code!.Used, 1016UL, 1016UL + 64);
        Assert.Equal(small.Data!.Used, table.Data!.Used);
        Assert.InRange(buffer.Data!.Used - smallBuffer.Data!.Used, 1016UL, 1016UL + 8);
        Assert.InRange(buffer.Code!.Used, smallBuffer.Code!.Used - 64, smallBuffer.Code.Used + 64);
    }

    [SimulatorFact]
    public async Task A_chip_without_a_heap_runs_tables_and_fixed_arrays()
    {
        var targets = TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("heapless", """{ "inherits": "pico", "capabilities": { "heap": false } }"""));
        var heapless = new TargetLoader(targets).Load("heapless");
        Assert.False(heapless.HasHeap);
        var source = StaticArrayPrograms.Buffer;
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Buffer"), "Buffer",
            Path.Combine(_scratch, "dotnet"));

        var program = PicoBuild.Build(source, "Buffer", Path.Combine(_scratch, "heapless"), heapless);
        var run = await Rp2040Simulator.RunAsync(program.Uf2, 30_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Output, run.Uart0);
    }

    [PicoFact]
    public async Task An_armv6m_chip_without_a_heap_or_a_console_gets_its_table_in_flash_and_its_buffer_in_RAM()
    {
        var dll = CSharpCompiler.CompileToFile("""
            class Program
            {
                static readonly byte[] Segments = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };
                static int[] shown = new int[10];
                static volatile int start;
                static volatile int total;

                static void Main()
                {
                    for (int i = 0; i < shown.Length; i++)
                    {
                        shown[i] = Segments[(start + i * 3) % Segments.Length];
                    }

                    for (int i = 0; i < shown.Length; i++)
                    {
                        total += shown[(start + i) % shown.Length];
                    }
                }
            }
            """, Path.Combine(_scratch, "Segments.dll"));
        var output = Path.Combine(_scratch, "tiny");

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("build", dll, "-t", TinyChip.Name,
            "--targets", TinyChip.Targets(_scratch), "-o", output);

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        Assert.Contains("of 2 KB (stack 512 bytes reserved)", printed);
        var elf = ElfFile.Read(Path.Combine(output, "Segments.elf"));
        // tiny: 16 KB of flash at 0x08000000, 2 KB of RAM at 0x20000000
        Assert.InRange(elf.Symbols["Program_Segments"], 0x08000000u, 0x08000000u + (16 << 10) - 10);
        Assert.InRange(elf.Symbols["Program_shown"], 0x20000000u, 0x20000000u + (2 << 10) - 40);
    }

    [Fact]
    public void A_chip_whose_runtime_has_no_console_stops_at_a_breakpoint_where_dotnet_throws()
    {
        // the RP2040's runtime says so on UART0 first: its strong functions replace these weak ones when they link
        // (the failures take nothing and return nothing; Console.Read's and the ADC's functions return what they read)
        var failures = ArmV6MRuntime.Source(ArmV6MRuntime.FailuresFileName).Content;
        var functions = LibrarySubstitutes.Assembly.FindType(LibrarySubstitutes.RuntimeType)!.Methods
            .Where(method => method.IsStatic && method.Name != ".cctor" && method.Parameters.Count == 0
                && method.ReturnType == ChipSharp.Frontend.Il.IlType.Void)
            .Select(method => $"ChipSharp.Runtime.{method.Name}");

        Assert.Contains(ArmV6MRuntime.FailuresFileName, new ArmV6MRuntime().Startup(TinyTarget()).Files.Select(f => f.FileName));
        Assert.All([StackArrays.IndexOutOfRange, .. functions], function => Assert.Contains(
            $"__attribute__((weak, noreturn)) void {LlvmCodeGenerator.SymbolName(function)}(void) {{ fail(); }}", failures));

        // and a throw, with the text of its exception
        Assert.Contains("__attribute__((weak, noreturn)) void ChipSharp_Runtime_Unhandled(const void *text) { (void)text; fail(); }",
            failures);
    }

    private TargetDescription TinyTarget() => new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name);

    /// <summary>
    /// What a program with <paramref name="field"/> as Program.Values takes of the Pico: Main does <paramref name="fill"/>
    /// for each element, then sums them.
    /// </summary>
    private MemoryReport Report(string name, string field, string fill)
    {
        var program = PicoBuild.Build($$"""
            using System;

            class Program
            {
                {{field}}
                static volatile int start;

                static void Main()
                {
                    for (int i = 0; i < Values.Length; i++)
                    {
                        {{fill}}
                    }

                    long total = 0;
                    for (int i = 0; i < Values.Length; i++)
                    {
                        total += Values[(start + i) % Values.Length];
                    }

                    Console.WriteLine(total);
                }
            }
            """, name, Path.Combine(_scratch, name));
        return MemoryReport.Read(new LinkedBinary(program.Elf), PicoBuild.Pico);
    }
}
