using System.Buffers.Binary;
using ChipSharp.Abstractions;
using ChipSharp.Simulators;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Toolchain.Sdcc;
using Xunit.Sdk;

namespace ChipSharp.Tests.Simulators;

/// <summary>Like <see cref="SdccFactAttribute"/>, for a test that runs once per case.</summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionTheoryDiscoverer", "ChipSharp.Tests")]
public sealed class SdccTheoryAttribute : TheoryAttribute
{
    public SdccTheoryAttribute()
    {
        if (SdccInstall.Missing.Value is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// A C program for a simulator test, built for a board (pfs154-bare, stm8s103-board) with the toolchain "sdcc", as a
/// program of ChipSharp's is: its .ihx, the symbols of its map, and runs of it on the chip's simulator, in process.
/// What SDCC compiles is kept in a build cache of the tests' own, under the temp folder.
/// </summary>
internal sealed class ChipProgram
{
    private static readonly string Cache = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "sdcc-cache");

    private ChipProgram(string board, string hex, IReadOnlyDictionary<string, ulong> symbols)
    {
        Board = board;
        Hex = hex;
        Symbols = symbols;
    }

    /// <summary>The board: "pfs154-bare" or "stm8s103-board".</summary>
    public string Board { get; }

    /// <summary>The chip, as chipsim names it.</summary>
    public string Chip => Board.StartsWith("pfs154", StringComparison.Ordinal) ? "pfs154" : "stm8s103";

    /// <summary>The Intel HEX file.</summary>
    public string Hex { get; }

    /// <summary>The map's symbols (_main, _out, s_CODE, ...), by name: code in bytes, as in the .ihx; RAM data addresses.</summary>
    public IReadOnlyDictionary<string, ulong> Symbols { get; }

    /// <summary>Whether the chip keeps the high byte of a multi-byte value first (the STM8, and SDCC on it).</summary>
    public bool BigEndian => Chip == "stm8s103";

    /// <summary>
    /// report(), which a program calls for each result (and <see cref="RunReports"/> reads the result then): in a file
    /// of its own, since SDCC leaves out the call of an empty function it can see.
    /// </summary>
    public static readonly GeneratedFile ReportFile = new("report.c", "void report(void) {}" + Environment.NewLine);

    /// <summary>
    /// Builds <paramref name="source"/> (main.c), <see cref="ReportFile"/> and <paramref name="more"/> for
    /// <paramref name="board"/> in <paramref name="folder"/>.
    /// </summary>
    public static ChipProgram Build(string board, string source, string folder, params GeneratedFile[] more)
    {
        var target = SdccPrograms.Shipped.Load(board);
        var binary = new SdccToolchain(new SdccToolchainOptions { OutputFolder = folder, ProgramName = "program", CacheFolder = Cache })
            .Build(new GeneratedCode([new GeneratedFile("main.c", source), ReportFile, .. more]), target);
        return new ChipProgram(board, binary.Path, SdccPrograms.MapSymbols(Path.ChangeExtension(binary.Path, ".map")));
    }

    /// <summary>The address of <paramref name="symbol"/> (e.g. "_out"); the test fails when the program has none.</summary>
    public int Address(string symbol) => Symbols.TryGetValue(symbol, out var value)
        ? (int)value
        : throw new XunitException($"The program has no '{symbol}'.");

    /// <summary>Runs the program for at most <paramref name="milliseconds"/> of simulated time.</summary>
    public SimulationResult Run(double milliseconds, IReadOnlyList<PinInput>? inputs = null, IReadOnlyList<SerialSend>? sends = null,
        Action<SimulatedChip>? setUp = null) =>
        Simulation.Run(new SimulationOptions
        {
            Chip = Chip,
            Image = Hex,
            Milliseconds = milliseconds,
            Inputs = inputs ?? [],
            Sends = sends ?? [],
        }, setUp: setUp);

    /// <summary>
    /// Runs the program and, each time it calls report(), reads its variable out (<paramref name="size"/> bytes):
    /// the values reported, in order, and the run.
    /// </summary>
    public (SimulationResult Run, List<ulong> Values) RunReports(double milliseconds, int size, IReadOnlyList<PinInput>? inputs = null)
    {
        var values = new List<ulong>();
        var report = Address("_report");
        var output = Address("_out");
        var run = Run(milliseconds, inputs, setUp: chip => chip.Hooks = new Dictionary<int, Action<SimulatedChip>>
        {
            [report] = c => values.Add(Read(c.Ram, output, size)),
        });
        return (run, values);
    }

    /// <summary>A variable of <paramref name="size"/> bytes at <paramref name="address"/> in <paramref name="ram"/>.</summary>
    public ulong Read(byte[] ram, int address, int size)
    {
        Span<byte> bytes = stackalloc byte[8];
        ram.AsSpan(address, size).CopyTo(BigEndian ? bytes[(8 - size)..] : bytes);
        return BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
