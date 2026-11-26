using ChipSharp.Abstractions;

namespace ChipSharp.Runtime.Windows;

/// <summary>
/// The runtime of PC programs (target "host", runtime "windows-x64"): one freestanding C file with the program's
/// entry point (<c>chipsharp_start</c>, which runs <c>chipsharp_entry</c> and exits with its result) and the
/// console output functions (<c>ChipSharp_Runtime_WriteLine</c>, <c>ChipSharp_Runtime_WriteLineInt32</c>, ...).
/// It needs no C runtime, only kernel32. A PC has no interrupts to enter and its atomics are plain
/// instructions, so those parts are empty. Next to it: what the program does where .NET would throw
/// (<see cref="FailuresFileName"/>), and what a C runtime would give the code LLVM writes (<see cref="MemoryFileName"/>,
/// <see cref="StackProbeFileName"/>).
/// </summary>
public sealed class WindowsX64Runtime : IArchRuntime
{
    /// <summary>The name of the C file with the entry point and the console output: the first <see cref="Startup"/> returns.</summary>
    public const string SourceFileName = "chipsharp_runtime.c";

    /// <summary>What a program does where .NET would throw: <c>ChipSharp_Runtime_IndexOutOfRange</c> (exit code 1).</summary>
    public const string FailuresFileName = "chipsharp_failures.c";

    /// <summary>memset, memcpy and memmove, which LLVM calls for big blocks of memory.</summary>
    public const string MemoryFileName = "chipsharp_memory.c";

    /// <summary>__chkstk, which LLVM calls before a stack frame larger than a page.</summary>
    public const string StackProbeFileName = "chipsharp_chkstk.S";

    private static readonly Lazy<string> SourceText = new(ReadSource);

    public string Name => "windows-x64";

    /// <summary>The runtime's C source, as it is compiled into every PC program.</summary>
    public static string Source => SourceText.Value;

    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources(
        [
            new GeneratedFile(SourceFileName, Source),
            new GeneratedFile(FailuresFileName, Read(FailuresFileName)),
            new GeneratedFile(MemoryFileName, Read(MemoryFileName)),
            new GeneratedFile(StackProbeFileName, Read(StackProbeFileName)),
        ]);
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

    private static string ReadSource() => Read(SourceFileName);

    /// <summary>One of the runtime's source files, e.g. <see cref="MemoryFileName"/>.</summary>
    public static string Read(string fileName)
    {
        var assembly = typeof(WindowsX64Runtime).Assembly;
        using var stream = assembly.GetManifestResourceStream(fileName)
            ?? throw new ChipSharpException(
                $"The runtime source '{fileName}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
