using ChipSharp.Abstractions;
using ChipSharp.Tests.Llvm;
using Xunit.Sdk;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The Arduino Uno as the code generator's tests use it, and Zig's clang (LLVM 22), which compiles for its ATmega328P:
/// the installed LLVM has no AVR backend.
/// </summary>
internal static class AvrCode
{
    /// <summary>The flags of an ATmega328P, as the toolchain "zig" gives them: Zig needs -mcpu and -mmcu both.</summary>
    public static readonly string[] Atmega328P = ["-target", "avr-freestanding-none", "-mcpu=atmega328p", "-mmcu=atmega328p"];

    /// <summary>
    /// The uno target (triple avr, 2-byte pointers, no heap; its flash an address space of its own, the default on an
    /// AVR) with a console, which the runtime "avr" is to give it, so that the samples compile;
    /// <paramref name="capabilities"/> adds to or replaces its capabilities.
    /// </summary>
    public static TargetDescription Uno(params (string Name, string Value)[] capabilities) =>
        Make(null, [("console", "true"), .. capabilities]);

    /// <summary>The <see cref="Uno"/> with a heap of <paramref name="bytes"/>, which no AVR has yet.</summary>
    public static TargetDescription UnoWithHeap(ulong bytes) =>
        Make(bytes, [("heap", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture))]);

    /// <summary>
    /// Compiles <paramref name="ll"/> (written to <paramref name="folder"/> as <paramref name="fileName"/>) with Zig for an
    /// ATmega328P, warnings as errors but the one about the spelling of the triple, and returns the object file's path.
    /// </summary>
    public static async Task<string> CompileAsync(string folder, string fileName, string ll, string optimization)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, fileName), ll);
        var objectFile = Path.ChangeExtension(fileName, optimization.TrimStart('-') + ".o");
        var result = await LlvmTools.RunAsync(AvrTools.Zig.FindZig(),
            ["cc", .. Atmega328P, optimization, "-Werror", "-Wno-override-module", "-c", fileName, "-o", objectFile], folder);
        Assert.True(result.ExitCode == 0, $"zig cc {fileName} failed:\n{result}");
        return Path.Combine(folder, objectFile);
    }

    private static TargetDescription Make(ulong? heap, IEnumerable<(string Name, string Value)> capabilities)
    {
        var uno = AvrTestProgram.Uno;
        var all = new Dictionary<string, string>(uno.Capabilities, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in capabilities)
        {
            all[name] = value;
        }

        return new TargetDescription
        {
            Name = uno.Name,
            InheritanceChain = uno.InheritanceChain,
            Backend = uno.Backend,
            Toolchain = uno.Toolchain,
            Runtime = uno.Runtime,
            Triple = uno.Triple,
            Cpu = uno.Cpu,
            PointerSize = uno.PointerSize,
            Endian = uno.Endian,
            Image = uno.Image,
            Memory = uno.Memory,
            Pins = uno.Pins,
            Capabilities = all,
            HeapSize = heap,
            Settings = uno.Settings,
        };
    }
}

/// <summary>Like <see cref="AvrFactAttribute"/>, for a test that runs once per case: skipped, with the reason, without Zig.</summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionTheoryDiscoverer", "ChipSharp.Tests")]
public sealed class AvrTheoryAttribute : TheoryAttribute
{
    public AvrTheoryAttribute()
    {
        if (AvrTools.MissingZig.Value is { } reason)
        {
            Skip = reason;
        }
    }
}
