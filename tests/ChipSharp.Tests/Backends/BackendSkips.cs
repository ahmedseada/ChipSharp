using System.Text.RegularExpressions;
using ChipSharp.Frontend.Analysis;

namespace ChipSharp.Tests.Backends;

/// <summary>
/// The programs of the corpora a target of <see cref="BackendTargets"/> does not take, each with why: what the target
/// lacks (a heap, a 64-bit double, memory) or what the tests' startup on a chip lacks (the runtime's own functions,
/// which the chips' runtimes are to have). The suite checks that each program is skipped exactly for the reason here,
/// so the list can neither grow nor keep a program that runs now without the test saying so.
/// </summary>
internal static partial class BackendSkips
{
    /// <summary>The program makes objects (or keeps text, boxes, ...), and the target has no heap.</summary>
    public const string Heap = "needs a heap, which the target has not";

    /// <summary>The program uses C#'s double, and the target's C has a 32-bit double only.</summary>
    public const string Double = "uses double, and the target's C double has 32 bits";

    /// <summary>The program's variables and stack need more RAM than the chip has.</summary>
    public const string Ram = "does not fit in the chip's RAM";

    /// <summary>The program's code and constants need more program memory than the chip has.</summary>
    public const string ProgramMemory = "does not fit in the chip's program memory";

    /// <summary>A local or a global larger than the target's 16-bit addresses reach.</summary>
    public const string Addresses = "has an array larger than 16-bit addresses reach";

    /// <summary>The program calls the runtime's float math (MathF.Sqrt, Max, ...), which the chips' runtimes are to have.</summary>
    public const string FloatMath = "calls the runtime's float math, which the chip's runtime is to have";

    /// <summary>The program compares, searches or parses fixed text, which the chips' runtimes are to do.</summary>
    public const string FixedText = "calls the runtime's functions of fixed text, which the chip's runtime is to have";

    /// <summary>
    /// The program's stack outgrows the chip's RAM: a function that checks the stack stopped it with "Stack overflow."
    /// (or the simulator, a stack past the end of RAM).
    /// </summary>
    public const string Stack = "its stack outgrows the chip's RAM";

    /// <summary>What <see cref="Classify(string)"/> says of any other message: not a reason to skip.</summary>
    public const string Other = "other";

    /// <summary>Why the whole-program pass does not take a program: a heap, or the first thing it says.</summary>
    public static string Classify(AnalysisResult analysis)
    {
        // the check refuses the program's own double too (DoubleRules), where the C code generator refuses the rest
        var others = analysis.Groups.Where(group => !group.Line.StartsWith("double (C#'s 64-bit floating point)", StringComparison.Ordinal)).ToList();
        return others.Count == 0 ? Double
            : others.All(group => NeedsHeap().IsMatch(group.Line)) ? Heap
            : "not supported: " + others[0].Line;
    }

    /// <summary>Why a build failed, by its message: <see cref="Other"/> for anything but a reason the target has.</summary>
    public static string Classify(string message) => message switch
    {
        _ when message.Contains("uses double (C#'s 64-bit floating point)", StringComparison.Ordinal) => Double,
        _ when message.Contains("16-bit addresses reach on the target", StringComparison.Ordinal) => Addresses,
        _ when message.Contains(" does not fit in target ", StringComparison.Ordinal) =>
            message.Contains(" of program memory", StringComparison.Ordinal) || message.Contains(" of flash", StringComparison.Ordinal)
                ? ProgramMemory
                : Ram,
        _ => Other,
    };

    /// <summary>Why a program the tests' startup cannot run is not run: the runtime functions it calls, by kind.</summary>
    public static string Runtime(IReadOnlyList<string> functions)
    {
        var names = functions.Select(name => name.Replace("ChipSharp.Runtime.", "", StringComparison.Ordinal)).ToList();
        return names.Any(name => FloatFunction().IsMatch(name)) ? FloatMath
            : names.All(name => TextFunction().IsMatch(name)) ? FixedText
            : "calls the runtime's " + string.Join(", ", names);
    }

    /// <summary>Why <paramref name="target"/> does not take <paramref name="program"/>; null when it does.</summary>
    public static string? For(string target, string program) =>
        ByTarget.TryGetValue(target, out var skips) ? skips.GetValueOrDefault(program) : null;

    /// <summary>The programs that make objects (or keep text, box values, ...): every chip without a heap skips them.</summary>
    private static readonly string[] WithObjects =
    [
        "ArrayMethods/Copies", "ArrayMethods/EmptyArrays", "ArrayMethods/Searches", "Boxing/Equality", "Boxing/Generic",
        "Boxing/Interfaces", "Boxing/Nullables", "Boxing/Text", "Boxing/Values", "Classes/Basics",
        "Classes/Inheritance", "Classes/Structures", "Classes/Survivors", "Collections/Arrays",
        "Collections/Dictionaries", "Collections/Exceptions", "Collections/Finally", "Collections/Garbage",
        "Collections/Lists", "Collections/Objects", "Collections/QueuesAndStacks", "Collections/Sets",
        "Delegates/ArrayDelegates", "Delegates/Churn", "Delegates/Creation", "Delegates/Events", "Delegates/Multicast",
        "GenericClasses/Comparers", "GenericClasses/Constraints", "GenericClasses/Containers", "GenericClasses/Garbage",
        "GenericClasses/Interfaces", "HeapArrays/Basics", "Records/Builders", "Records/Inheritance",
        "Records/Positional", "Records/Types", "Strings/Building", "Strings/Characters", "Strings/Commands",
        "Strings/Enums", "Strings/Methods", "Strings/NotAscii", "Strings/Objects", "Tuples/CollectionExpressions",
        "Tuples/Collections", "Tuples/Iterators", "Tuples/Text", "Tuples/TextInTryBlocks", "Virtual/Dispatch",
        "Virtual/Interfaces", "Virtual/ObjectMethods", "Virtual/TypeTests", "Virtual/Zoo", "samples/ArrayMethods",
        "samples/Boxing", "samples/Classes", "samples/Collections", "samples/Delegates", "samples/GenericClasses",
        "samples/HeapArrays", "samples/Records", "samples/Shapes", "samples/Text", "samples/Tuples",
    ];

    /// <summary>The programs that use double (most corpora print doubles somewhere): SDCC's chips skip them.</summary>
    private static readonly string[] WithDoubles =
    [
        "ArrayMethods/Sorting", "Generics/Interfaces", "Generics/Math", "Generics/Methods", "Generics/Types",
        "PrintedText/AlignmentsAndFormats", "PrintedText/Concatenations", "PrintedText/Interpolations",
        "PrintedText/LoopsAndPlaces", "Spans/Bytes", "Spans/Copies", "Spans/Views", "StaticArrays/ElementTypes",
        "Structs/Floats", "Text/WriteAndWriteLine", "TimeSpan/Members", "Tuples/Basics", "Uno/Copies",
        "samples/Generics", "samples/PrintedText", "samples/TimeSpans",
        // they call the runtime's float math too (FloatMath's reason), but the check finds their double first
        "DateTime/Members", "DateTime/Text", "samples/FloatMath", "samples/MathFunctions",
    ];

    /// <summary>
    /// The programs whose runtime functions the tests' startup on a chip has not (float math, fixed text's searches and
    /// comparisons), and the one whose array is larger than 16-bit addresses reach.
    /// </summary>
    private static readonly (string Reason, string[] Programs)[] OnEveryChip =
    [
        (Heap, WithObjects),
        (Double, WithDoubles),
        (FloatMath, ["Chips/MathFunctions", "samples/Projectile"]),
        (FixedText, ["Uno/Text"]),
        (Addresses, ["Arrays/BigArrays"]),
    ];

    /// <summary>The skips of each target, by program: none on the PC.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ByTarget { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            // 128 bytes of RAM, 2K words of program memory
            ["pfs154-bare"] = Skips(
            [
                .. OnEveryChip,
                (Ram,
                [
                    "Arrays/Indexes", "Chips/Longs", "Chips/Tables", "PrintedText/SideEffects",
                    "StaticArrays/Initializers", "StaticFields/EveryType", "StaticFields/StaticReadonly", "Structs/Enums",
                    "Structs/Layout", "Structs/Nested", "Structs/StaticFields", "samples/Arithmetic",
                    "samples/FactorialLong", "samples/Primes", "samples/StaticFields", "samples/TrafficLights",
                    "samples/VendingMachine", "samples/WhileSum",
                ]),
                (ProgramMemory,
                [
                    "ArrayMethods/Extremes", "ArrayMethods/WithoutHeap", "Arrays/Callees", "Arrays/ElementTypes",
                    "Arrays/Loops", "Arrays/StructsAndReferences", "Chips/ByteOrder", "Chips/Dates", "Chips/Floats",
                    "Chips/Generics", "Chips/Spans", "Chips/Text", "Chips/Values", "GenericClasses/Library",
                    "Narrowing/Edges", "Narrowing/Random1", "Narrowing/Random2", "StaticArrays/ComputedElements",
                    "StaticArrays/Buffer", "StaticArrays/Tables", "StaticArrays/WrittenByCallees", "Structs/Large",
                    "Structs/Members", "Structs/PassingAndReturning", "Structs/References", "samples/Arrays",
                    "samples/Gcd", "samples/Logit", "samples/LookupTables", "samples/Spans", "samples/Structs",
                ]),
                // recursion deeper than what the variables leave of the 128 bytes holds
                (Stack, ["StaticFields/Counters", "samples/Fibonacci"]),
            ]),

            // 1 KB of RAM, 8 KB of flash
            ["stm8s103-board"] = Skips(
            [
                .. OnEveryChip,
                (ProgramMemory,
                [
                    "ArrayMethods/Extremes", "ArrayMethods/WithoutHeap", "GenericClasses/Library", "Narrowing/Edges",
                    "Narrowing/Random1", "Narrowing/Random2",
                ]),
                // two static structs of 321 bytes, and one more passed by value
                (Stack, ["Structs/Large"]),
            ]),

            // 2 KB of SRAM (no heap), 16 KB of flash
            ["ch32v003-evt"] = Skips(
            [
                (Heap, WithObjects),
                (ProgramMemory,
                [
                    "ArrayMethods/Sorting", "DateTime/Members", "DateTime/Text", "TimeSpan/Members", "samples/MathFunctions",
                ]),
                // a frame of 40 KB (an array of 10,000 ints), twenty times the SRAM: the stack check stops it
                (Stack, ["Arrays/BigArrays"]),
            ]),
        };

    /// <summary>
    /// The programs that run on a target to their end but print otherwise than .NET on a 64-bit PC, by what the chip is,
    /// with why: as many lines as .NET's.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Differences { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["pfs154-bare"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["StaticFields/Initializers"] = SixteenBitNuint,
            },
            ["stm8s103-board"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["StaticFields/EveryType"] = "nint and nuint have the chip's 16 bits: -123456 does not fit in them",
                ["StaticFields/Initializers"] = SixteenBitNuint,
                ["samples/Spans"] = "MemoryMarshal reads a struct in the chip's byte order, as .NET does on a big-endian " +
                    "processor: the STM8's (its BinaryPrimitives give .NET's numbers)",
            },
        };

    /// <summary>Why <paramref name="program"/> prints otherwise on <paramref name="target"/>; null when it prints what .NET does.</summary>
    public static string? DifferenceOf(string target, string program) =>
        Differences.TryGetValue(target, out var differences) ? differences.GetValueOrDefault(program) : null;

    private const string SixteenBitNuint = "nuint has the chip's 16 bits: 3,000,000,000 does not fit in it";

    private static IReadOnlyDictionary<string, string> Skips(IEnumerable<(string Reason, string[] Programs)> lists) =>
        lists.SelectMany(list => list.Programs.Select(program => (program, list.Reason)))
            .ToDictionary(pair => pair.program, pair => pair.Reason, StringComparer.Ordinal);

    [GeneratedRegex(@"needs? a heap, and the target '[^']+' \([^)]*\) has no heap")]
    private static partial Regex NeedsHeap();

    [GeneratedRegex("Float(32|64)$")]
    private static partial Regex FloatFunction();

    [GeneratedRegex("^(String|Char|Parse|TryParse)")]
    private static partial Regex TextFunction();
}
