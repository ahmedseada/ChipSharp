using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Tests.Llvm;
using Xunit.Sdk;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// An AVR for the tests, like the Arduino Uno's ATmega328P, and Zig, whose clang (LLVM 22) compiles for it: the
/// installed LLVM has no AVR backend.
/// </summary>
internal static class AvrTools
{
    /// <summary>Where Zig is when <c>CHIPSHARP_ZIG</c> does not say and it is not on the PATH.</summary>
    public const string DefaultZig = @"D:\Tools\zig\zig.exe";

    /// <summary>
    /// The flags of an ATmega328P: Zig's -mcpu, and -mmcu too (alone, -mmcu leaves Zig's generic avr2, without MUL; alone,
    /// -mcpu warns that no microcontroller was given).
    /// </summary>
    public static readonly string[] Atmega328P = ["-target", "avr-freestanding-none", "-mcpu=atmega328p", "-mmcu=atmega328p"];

    /// <summary>Zig: <c>CHIPSHARP_ZIG</c>, else zig on the PATH, else <see cref="DefaultZig"/>; null when none is there.</summary>
    public static readonly Lazy<string?> Zig = new(() =>
    {
        var configured = Environment.GetEnvironmentVariable("CHIPSHARP_ZIG");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var name = OperatingSystem.IsWindows() ? "zig.exe" : "zig";
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), name))
            .FirstOrDefault(File.Exists);
        return onPath ?? (File.Exists(DefaultZig) ? DefaultZig : null);
    });

    /// <summary>
    /// An ATmega328P with a console and no heap, as the uno target describes it: triple avr, 2-byte pointers, a flash of
    /// its own (by default on an AVR), any alignment. <paramref name="capabilities"/> adds to or replaces its capabilities.
    /// </summary>
    public static TargetDescription Uno(params (string Name, string Value)[] capabilities) => Make(null, capabilities);

    /// <summary>The <see cref="Uno"/> with a heap of <paramref name="bytes"/>, which no AVR has yet.</summary>
    public static TargetDescription UnoWithHeap(ulong bytes) =>
        Make(bytes, [("heap", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture))]);

    private static TargetDescription Make(ulong? heap, (string Name, string Value)[] capabilities)
    {
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["console"] = "true",
            ["unalignedAccess"] = "true",
            ["heap"] = "false",
        };
        foreach (var (name, value) in capabilities)
        {
            all[name] = value;
        }

        return new TargetDescription
        {
            Name = "uno-test",
            InheritanceChain = ["uno-test"],
            Backend = "llvm",
            Triple = "avr",
            Cpu = "atmega328p",
            PointerSize = 2,
            Endian = Endianness.Little,
            Capabilities = all,
            HeapSize = heap,
            Settings = new JsonObject(),
        };
    }

    /// <summary>
    /// Compiles <paramref name="ll"/> (written to <paramref name="folder"/> as <paramref name="fileName"/>) with Zig for an
    /// ATmega328P, warnings as errors but the one about the spelling of the triple, and returns the object file's path.
    /// </summary>
    public static async Task<string> CompileAsync(string folder, string fileName, string ll, string optimization)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, fileName), ll);
        var objectFile = Path.ChangeExtension(fileName, optimization.TrimStart('-') + ".o");
        var result = await LlvmTools.RunAsync(Zig.Value!,
            ["cc", .. Atmega328P, optimization, "-Werror", "-Wno-override-module", "-c", fileName, "-o", objectFile], folder);
        Assert.True(result.ExitCode == 0, $"zig cc {fileName} failed:\n{result}");
        return Path.Combine(folder, objectFile);
    }
}

/// <summary>A test that compiles for an AVR with Zig: skipped, with the reason, on a machine without Zig.</summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionFactDiscoverer", "ChipSharp.Tests")]
public sealed class ZigFactAttribute : FactAttribute
{
    public ZigFactAttribute()
    {
        if (AvrTools.Zig.Value is null)
        {
            Skip = NoZig;
        }
    }

    internal const string NoZig = "Zig was not found (CHIPSHARP_ZIG, the PATH, " + AvrTools.DefaultZig + "): its clang " +
        "compiles for an AVR, which the installed LLVM cannot.";
}

/// <summary>Like <see cref="ZigFactAttribute"/>, for a test that runs once per case.</summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionTheoryDiscoverer", "ChipSharp.Tests")]
public sealed class ZigTheoryAttribute : TheoryAttribute
{
    public ZigTheoryAttribute()
    {
        if (AvrTools.Zig.Value is null)
        {
            Skip = ZigFactAttribute.NoZig;
        }
    }
}
