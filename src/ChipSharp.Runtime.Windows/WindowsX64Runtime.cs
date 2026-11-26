using ChipSharp.Abstractions;

namespace ChipSharp.Runtime.Windows;

/// <summary>
/// The runtime of PC programs (target "host", runtime "windows-x64"): one freestanding C file with the program's
/// entry point (<c>chipsharp_start</c>, which runs <c>chipsharp_entry</c> and exits with its result) and the
/// console output functions (<c>ChipSharp_Runtime_WriteLine</c>, <c>ChipSharp_Runtime_WriteLineInt32</c>, ...).
/// It needs no C runtime, only kernel32. A PC has no interrupts to enter and its atomics are plain
/// instructions, so those parts are empty.
/// </summary>
public sealed class WindowsX64Runtime : IArchRuntime
{
    /// <summary>The name of the C file <see cref="Startup"/> returns.</summary>
    public const string SourceFileName = "chipsharp_runtime.c";

    private static readonly Lazy<string> SourceText = new(ReadSource);

    public string Name => "windows-x64";

    /// <summary>The runtime's C source, as it is compiled into every PC program.</summary>
    public static string Source => SourceText.Value;

    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([new GeneratedFile(SourceFileName, Source)]);
    }

    public RuntimeSources InterruptEntry(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([]);
    }

    public RuntimeSources Atomics(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([]);
    }

    private static string ReadSource()
    {
        var assembly = typeof(WindowsX64Runtime).Assembly;
        using var stream = assembly.GetManifestResourceStream(SourceFileName)
            ?? throw new ChipSharpException(
                $"The runtime source '{SourceFileName}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
