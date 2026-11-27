using ChipSharp.Abstractions;

namespace ChipSharp.Runtime.Windows;

/// <summary>
/// The runtime of PC programs (target "host", runtime "windows-x64"): freestanding C with the program's entry point
/// (<c>chipsharp_start</c>, which runs <c>chipsharp_entry</c> and exits with its result) and where the console output
/// goes (<c>chipsharp_console_write</c>: standard output) in <see cref="SourceFileName"/>, and, shared with the other
/// runtimes, Console.Write and Console.WriteLine (<c>ChipSharp_Runtime_Write...</c>, <c>ChipSharp_Runtime_WriteLine...</c>)
/// in <see cref="ConsoleFileName"/>, the text of floats and doubles in <see cref="FloatTextFileName"/> and fmod and the
/// Math methods in <see cref="FloatMathFileName"/> and <see cref="MathFileNames"/> (the float arithmetic itself is the
/// PC's). It needs no C runtime, only kernel32. A PC has no interrupts to enter and its
/// atomics are plain instructions, so those parts are empty. Next to it: what the program does where .NET would throw
/// (<see cref="FailuresFileName"/>), and what a C runtime would give the code LLVM writes (<see cref="MemoryFileName"/>,
/// <see cref="StackProbeFileName"/>). For a target with a heap, objects and their garbage collector, shared with the
/// other runtimes (<see cref="HeapFileName"/>), with what they need from Windows (<see cref="HeapWindowsFileName"/>) and
/// the heap's size (<see cref="HeapSizeFileName"/>, written from the target's "heap" capability).
/// </summary>
public sealed class WindowsX64Runtime : IArchRuntime
{
    /// <summary>The name of the C file with the entry point and the console output: the first <see cref="Startup"/> returns.</summary>
    public const string SourceFileName = "chipsharp_runtime.c";

    /// <summary>
    /// The name of the C file with Console.Write and Console.WriteLine of every type, shared with the other runtimes
    /// (src/ChipSharp.Runtime.Shared), which writes through <see cref="SourceFileName"/>'s <c>chipsharp_console_write</c>.
    /// </summary>
    public const string ConsoleFileName = "chipsharp_console.c";

    /// <summary>The name of the C file that writes floats and doubles as .NET does (src/ChipSharp.Runtime.Shared).</summary>
    public const string FloatTextFileName = "chipsharp_float_text.c";

    /// <summary>
    /// The name of the C file with fmod (C#'s % on floats) and the Math methods programs call, shared with the other
    /// runtimes (src/ChipSharp.Runtime.Shared).
    /// </summary>
    public const string FloatMathFileName = "chipsharp_float_math.c";

    /// <summary>
    /// The C files with the transcendental functions of Math and MathF (Sin, Exp, Pow, ...), shared with the other
    /// runtimes (src/ChipSharp.Runtime.Shared): ChipSharp's entry points, and the functions they call from musl and from
    /// AMD's math library. The linker keeps only those a program calls.
    /// </summary>
    public static IReadOnlyList<string> MathFileNames { get; } =
        ["chipsharp_math.c", "chipsharp_math_musl.c", "chipsharp_math_amd.c"];

    /// <summary>What a program does where .NET would throw: <c>ChipSharp_Runtime_IndexOutOfRange</c> (exit code 1).</summary>
    public const string FailuresFileName = "chipsharp_failures.c";

    /// <summary>memset, memcpy and memmove, which LLVM calls for big blocks of memory.</summary>
    public const string MemoryFileName = "chipsharp_memory.c";

    /// <summary>
    /// The clock and the uptime: DateTime.Now and UtcNow from Windows' clock and time zone, Clock.Start's software clock,
    /// Stopwatch from the performance counter, Environment.TickCount (the target's "clock" capability).
    /// </summary>
    public const string ClockFileName = "chipsharp_clock.c";

    /// <summary>__chkstk, which LLVM calls before a stack frame larger than a page.</summary>
    public const string StackProbeFileName = "chipsharp_chkstk.S";

    /// <summary>Objects, the allocator and the garbage collector (src/ChipSharp.Runtime.Shared), for a target with a heap.</summary>
    public const string HeapFileName = "chipsharp_heap.c";

    /// <summary>What the heap needs from Windows: its memory (VirtualAlloc) and the top of the stack.</summary>
    public const string HeapWindowsFileName = "chipsharp_heap_windows.c";

    /// <summary>Strings (src/ChipSharp.Runtime.Shared): their descriptors, and what reads them and chars, for every target.</summary>
    public const string StringFileName = "chipsharp_string.c";

    /// <summary>What makes strings (src/ChipSharp.Runtime.Shared), for a target with a heap.</summary>
    public const string StringHeapFileName = "chipsharp_string_heap.c";

    /// <summary>The text of C#'s throw (src/ChipSharp.Runtime.Shared), which the failures file prints.</summary>
    public const string ExceptionsFileName = "chipsharp_exceptions.c";

    /// <summary>The heap's size in bytes, <c>chipsharp_heap_size</c>, written for the target from its "heap" capability.</summary>
    public const string HeapSizeFileName = "chipsharp_heap_size.c";

    private static readonly Lazy<string> SourceText = new(() => Read(SourceFileName));
    private static readonly Lazy<string> ConsoleSource = new(() => Read(ConsoleFileName));
    private static readonly Lazy<string> FloatTextSource = new(() => Read(FloatTextFileName));
    private static readonly Lazy<string> FloatMathSource = new(() => Read(FloatMathFileName));

    public string Name => "windows-x64";

    /// <summary>The C source of <see cref="SourceFileName"/>, as it is compiled into every PC program.</summary>
    public static string Source => SourceText.Value;

    /// <summary>The C source of <see cref="ConsoleFileName"/>, as it is compiled into every PC program.</summary>
    public static string Console => ConsoleSource.Value;

    /// <summary>The C source of <see cref="FloatTextFileName"/>, as it is compiled into every PC program.</summary>
    public static string FloatText => FloatTextSource.Value;

    /// <summary>The C source of <see cref="FloatMathFileName"/>, as it is compiled into every PC program.</summary>
    public static string FloatMath => FloatMathSource.Value;

    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources(
        [
            new GeneratedFile(SourceFileName, Source),
            new GeneratedFile(ConsoleFileName, Console),
            new GeneratedFile(FloatTextFileName, FloatText),
            new GeneratedFile(FloatMathFileName, FloatMath),
            .. MathFileNames.Select(name => new GeneratedFile(name, Read(name))),
            new GeneratedFile(FailuresFileName, Read(FailuresFileName)),
            new GeneratedFile(MemoryFileName, Read(MemoryFileName)),
            new GeneratedFile(StackProbeFileName, Read(StackProbeFileName)),
            new GeneratedFile(ClockFileName, Read(ClockFileName)),
            new GeneratedFile(StringFileName, Read(StringFileName)),
            new GeneratedFile(ExceptionsFileName, Read(ExceptionsFileName)),
            .. target.HeapSize is { } heap
                ?
                [
                    new GeneratedFile(HeapFileName, Read(HeapFileName)),
                    new GeneratedFile(HeapWindowsFileName, Read(HeapWindowsFileName)),
                    new GeneratedFile(HeapSizeFileName, HeapSize(target.Name, heap)),
                    new GeneratedFile(StringHeapFileName, Read(StringHeapFileName)),
                ]
                : Array.Empty<GeneratedFile>(),
        ]);
    }

    /// <summary>The C of <see cref="HeapSizeFileName"/>: <c>chipsharp_heap_size</c>, the heap's size in bytes.</summary>
    public static string HeapSize(string target, ulong bytes) =>
        $"// The heap of target '{target}', from its \"heap\" capability: the bytes chipsharp_heap_windows.c takes from Windows.\n" +
        $"const unsigned long long chipsharp_heap_size = {bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}ull;\n";

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
