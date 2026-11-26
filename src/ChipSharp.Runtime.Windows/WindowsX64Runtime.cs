using ChipSharp.Abstractions;

namespace ChipSharp.Runtime.Windows;

/// <summary>
/// The runtime of PC programs (target "host", runtime "windows-x64"): freestanding C with the program's entry point
/// (<c>chipsharp_start</c>, which runs <c>chipsharp_entry</c> and exits with its result) and the console output
/// functions (<c>ChipSharp_Runtime_WriteLine</c>, <c>ChipSharp_Runtime_WriteLineInt32</c>, ...) in
/// <see cref="SourceFileName"/>, and the text of floats and doubles, shared with the other runtimes, in
/// <see cref="FloatTextFileName"/>. It needs no C runtime, only kernel32. A PC has no interrupts to enter and its
/// atomics are plain instructions, so those parts are empty.
/// </summary>
public sealed class WindowsX64Runtime : IArchRuntime
{
    /// <summary>The name of the C file with the entry point and the console output functions.</summary>
    public const string SourceFileName = "chipsharp_runtime.c";

    /// <summary>The name of the C file that writes floats and doubles as .NET does (src/ChipSharp.Runtime.Shared).</summary>
    public const string FloatTextFileName = "chipsharp_float_text.c";

    private static readonly Lazy<string> SourceText = new(() => ReadSource(SourceFileName));
    private static readonly Lazy<string> FloatTextSource = new(() => ReadSource(FloatTextFileName));

    public string Name => "windows-x64";

    /// <summary>The C source of <see cref="SourceFileName"/>, as it is compiled into every PC program.</summary>
    public static string Source => SourceText.Value;

    /// <summary>The C source of <see cref="FloatTextFileName"/>, as it is compiled into every PC program.</summary>
    public static string FloatText => FloatTextSource.Value;

    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([new GeneratedFile(SourceFileName, Source), new GeneratedFile(FloatTextFileName, FloatText)]);
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

    private static string ReadSource(string fileName)
    {
        var assembly = typeof(WindowsX64Runtime).Assembly;
        using var stream = assembly.GetManifestResourceStream(fileName)
            ?? throw new ChipSharpException(
                $"The runtime source '{fileName}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
