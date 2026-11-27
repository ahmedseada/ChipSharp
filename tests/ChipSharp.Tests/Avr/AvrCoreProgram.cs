using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Runtime.Avr;
using ChipSharp.Tests.Llvm;
using Xunit.Sdk;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// C, assembly and LLVM IR built for the ATmega328P to run on <see cref="AvrCpu"/> (Zig's clang, -mdouble=64, function
/// sections, ld.lld with --gc-sections and <see cref="LinkerScript"/>), loaded from the .elf: its flash and its symbols.
/// The same files build once: the program lives in a folder named after their hash, which later test runs reuse.
/// </summary>
internal sealed class AvrCoreProgram
{
    /// <summary>
    /// The flags every file is compiled with: the ATmega328P (zig cc needs both -mcpu and -mmcu for it: -mmcu alone
    /// builds for the generic AVR2, -mcpu alone warns), C#'s 64-bit double, and no C library.
    /// </summary>
    public static readonly string[] CompileFlags =
    [
        "-target", "avr-freestanding-none", "-mcpu=atmega328p", "-mmcu=atmega328p", "-mdouble=64", "-Os",
        "-ffreestanding", "-fno-builtin", "-ffunction-sections", "-fdata-sections", "-Werror", "-Wno-override-module",
    ];

    /// <summary>
    /// A linker script like the one the toolchain writes for the ATmega328P: the vector table first in flash, then the
    /// code and the flash data; .data (with .rodata) and .bss in RAM, at 0x800100 as AVR's ELF files put RAM, and the
    /// symbols the runtime uses.
    /// </summary>
    public const string LinkerScript = """
        MEMORY
        {
          flash (rx) : ORIGIN = 0, LENGTH = 32K
          ram (rw!x) : ORIGIN = 0x800100, LENGTH = 2K
        }
        ENTRY(chipsharp_reset)
        SECTIONS
        {
          .text : { KEEP(*(.vectors)) *(.text .text.*) *(.progmem.data .progmem.data.*) } > flash
          .data : { __chipsharp_data_start = .; *(.data .data.* .rodata .rodata.*) __chipsharp_data_end = .; } > ram AT > flash
          __chipsharp_data_load = LOADADDR(.data);
          .bss (NOLOAD) : { __chipsharp_bss_start = .; *(.bss .bss.* COMMON) __chipsharp_bss_end = .; } > ram
          __chipsharp_stack_top = ORIGIN(ram) + LENGTH(ram);
          __chipsharp_stack_limit = __chipsharp_bss_end + 64;
        }
        """;

    private const uint RamInElf = 0x800000;

    private AvrCoreProgram(byte[] flash, IReadOnlyDictionary<string, (uint Value, uint Size)> symbols)
    {
        Flash = flash;
        Symbols = symbols;
    }

    /// <summary>The Arduino Uno, as its target files say (the ATmega328P's 26 vectors).</summary>
    public static TargetDescription Target { get; } = AvrTestProgram.Uno;

    /// <summary>The avr runtime's files for <see cref="Target"/>: start-up, helpers, float math, failures, the vector table.</summary>
    public static IReadOnlyList<GeneratedFile> RuntimeFiles { get; } =
        [.. new AvrRuntime().Startup(Target).Files, .. new AvrRuntime().InterruptEntry(Target).Files];

    /// <summary>The bytes of flash, from address 0 to the end of the last thing in it.</summary>
    public byte[] Flash { get; }

    /// <summary>Every symbol's value (a byte address in flash; for RAM the data address, without the 0x800000) and size.</summary>
    public IReadOnlyDictionary<string, (uint Value, uint Size)> Symbols { get; }

    /// <summary>The address of <paramref name="symbol"/>; the test fails when the program has none.</summary>
    public int Address(string symbol) => Symbols.TryGetValue(symbol, out var found)
        ? (int)found.Value
        : throw new XunitException($"The program has no '{symbol}'.");

    /// <summary>The size of <paramref name="symbol"/> in bytes, 0 when the program has none.</summary>
    public int SizeOf(string symbol) => Symbols.TryGetValue(symbol, out var found) ? (int)found.Size : 0;

    /// <summary>The bytes of flash of the core the program runs on: the ATmega328P's 32 KB, or 64 KB for a larger one.</summary>
    public int FlashSize => Flash.Length > 32 * 1024 ? 64 * 1024 : 32 * 1024;

    /// <summary>A core with this program in its flash, at reset.</summary>
    public AvrCpu Load() => new(Flash, flashSize: FlashSize);

    /// <summary>
    /// Builds <paramref name="files"/> (.c, .S, .ll; the others, headers, are written next to them) into a program.
    /// <paramref name="keep"/> are symbols the linker keeps although nothing calls them (ld.lld -u), e.g. the helpers a
    /// test calls itself.
    /// </summary>
    public static Task<AvrCoreProgram> BuildAsync(IEnumerable<GeneratedFile> files, IEnumerable<string>? keep = null) =>
        BuildAsync(files, keep, LinkerScript);

    /// <summary>
    /// Like <see cref="BuildAsync(IEnumerable{GeneratedFile}, IEnumerable{string}?)"/>, for a core with 64 KB of flash
    /// (an ATmega644's): for functions too large for the ATmega328P's 32 KB together.
    /// </summary>
    public static Task<AvrCoreProgram> BuildLargeAsync(IEnumerable<GeneratedFile> files, IEnumerable<string> keep) =>
        BuildAsync(files, keep, LinkerScript.Replace("LENGTH = 32K", "LENGTH = 64K", StringComparison.Ordinal));

    private static async Task<AvrCoreProgram> BuildAsync(IEnumerable<GeneratedFile> files, IEnumerable<string>? keep,
        string linkerScript)
    {
        var zig = AvrTools.Zig.FindZig();
        var sources = files.ToList();
        var kept = (keep ?? []).ToList();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(
            sources.Select(file => file.FileName + "\n" + file.Content + "\n").Append(string.Join(',', kept))
                .Append(string.Join(' ', CompileFlags)).Append(linkerScript)))))[..16];
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "avr-" + hash);
        var elf = Path.Combine(folder, "program.elf");
        if (!File.Exists(elf))
        {
            Directory.CreateDirectory(folder);
            foreach (var file in sources)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, file.FileName), file.Content);
            }

            await File.WriteAllTextAsync(Path.Combine(folder, "program.ld"), linkerScript);
            var compiled = sources.Where(file => Path.GetExtension(file.FileName) is ".c" or ".S" or ".ll")
                .Select(file => file.FileName).ToList();
            var results = await Task.WhenAll(compiled.Select(name =>
                LlvmTools.RunAsync(zig, ["cc", .. CompileFlags, "-c", name, "-o", name + ".o"], folder)));
            for (var i = 0; i < compiled.Count; i++)
            {
                Assert.True(results[i].ExitCode == 0, $"zig cc {compiled[i]} failed:\n{results[i]}");
            }

            var built = $"program-{Guid.NewGuid():N}.elf"; // then renamed: a half-written program is never loaded
            var link = await LlvmTools.RunAsync(zig,
            [
                "ld.lld", "-T", "program.ld", "--gc-sections", .. kept.Select(symbol => "--undefined=" + symbol),
                .. compiled.Select(name => name + ".o"), "-o", built,
            ], folder);
            Assert.True(link.ExitCode == 0, $"ld.lld failed:\n{link}");
            try
            {
                File.Move(Path.Combine(folder, built), elf);
            }
            catch (IOException) when (File.Exists(elf))
            {
                // another test built it first
            }
        }

        return Read(await File.ReadAllBytesAsync(elf));
    }

    /// <summary>The program in an AVR .elf, e.g. one a build made.</summary>
    public static AvrCoreProgram FromElf(string path) => Read(File.ReadAllBytes(path));

    /// <summary>The flash image (the loaded segments, at their load addresses) and the symbols of an AVR .elf.</summary>
    private static AvrCoreProgram Read(byte[] elf)
    {
        if (elf.Length < 52 || BinaryPrimitives.ReadUInt32BigEndian(elf) != 0x7F454C46 || elf[4] != 1 || elf[5] != 1)
        {
            throw new XunitException("Not a 32-bit little-endian ELF file.");
        }

        var programHeaders = (int)U32(elf, 28);
        var programHeaderSize = U16(elf, 42);
        var programHeaderCount = U16(elf, 44);
        var flash = new List<byte>();
        for (var i = 0; i < programHeaderCount; i++)
        {
            var header = programHeaders + i * programHeaderSize;
            var (type, offset, loadAddress, fileSize) = (U32(elf, header), U32(elf, header + 4), U32(elf, header + 12),
                U32(elf, header + 16));
            if (type != 1 || fileSize == 0 || loadAddress >= RamInElf)
            {
                continue; // not loaded, or nothing in flash
            }

            while (flash.Count < loadAddress + fileSize)
            {
                flash.Add(0xFF);
            }

            for (var b = 0u; b < fileSize; b++)
            {
                flash[(int)(loadAddress + b)] = elf[offset + b];
            }
        }

        var sections = (int)U32(elf, 32);
        var sectionSize = U16(elf, 46);
        var sectionCount = U16(elf, 48);
        var symbols = new Dictionary<string, (uint, uint)>(StringComparer.Ordinal);
        for (var i = 0; i < sectionCount; i++)
        {
            var section = sections + i * sectionSize;
            if (U32(elf, section + 4) != 2)
            {
                continue; // not the symbol table
            }

            var (offset, size, entrySize) = ((int)U32(elf, section + 16), (int)U32(elf, section + 20), (int)U32(elf, section + 36));
            var strings = (int)U32(elf, sections + (int)U32(elf, section + 24) * sectionSize + 16);
            for (var entry = offset; entry < offset + size; entry += entrySize)
            {
                var nameStart = strings + (int)U32(elf, entry);
                var nameEnd = Array.IndexOf(elf, (byte)0, nameStart);
                var name = Encoding.ASCII.GetString(elf, nameStart, nameEnd - nameStart);
                var value = U32(elf, entry + 4);
                if (name.Length > 0)
                {
                    symbols[name] = (value >= RamInElf ? value - RamInElf : value, U32(elf, entry + 8));
                }
            }
        }

        return new AvrCoreProgram([.. flash], symbols);
    }

    private static uint U32(byte[] bytes, long at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)at));

    private static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
}
