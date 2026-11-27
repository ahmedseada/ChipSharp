using ChipSharp.Tests.Boxing;
using ChipSharp.Tests.Classes;
using ChipSharp.Tests.Collections;
using ChipSharp.Tests.Generics;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Lambdas;
using ChipSharp.Tests.Library;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Records;
using ChipSharp.Tests.Strings;
using ChipSharp.Tests.Support;
using ChipSharp.Tests.Tuples;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The programs the other tests compare with .NET on the PC and the Pico, gathered for the Arduino Uno: the samples and
/// every program of the test corpora, each by "&lt;corpus&gt;/&lt;name&gt;" (e.g. "samples/Square",
/// "Structs/Copies"). On the Uno, which has no heap, those that make objects are refused, and the rest run.
/// </summary>
internal static class UnoPrograms
{
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        .. Samples.Names.Select(name => ("samples/" + name, File.ReadAllText(Path.Combine(Samples.Folder, name + ".cs")))),
        .. From("Arrays",
        [
            ("ElementTypes", ArrayPrograms.ElementTypes), ("Callees", ArrayPrograms.Callees),
            ("Loops", ArrayPrograms.Loops), ("Indexes", ArrayPrograms.Indexes), ("BigArrays", ArrayPrograms.BigArrays),
            ("StructsAndReferences", ArrayPrograms.StructsAndReferences),
        ]),
        .. From("Structs", StructPrograms.All),
        .. From("StaticFields", StaticFieldPrograms.All),
        .. From("StaticArrays", StaticArrayPrograms.All),
        .. From("Spans", SpanPrograms.All),
        .. From("PrintedText", PrintedTextPrograms.All),
        .. From("Text", TextPrograms.All),
        .. From("Generics", GenericPrograms.All),
        .. From("Tuples", TuplePrograms.All),
        .. From("Records", RecordPrograms.All),
        .. From("DateTime",
        [
            ("Members", DateTimePrograms.Members(random: 7)),
            ("Text", DateTimePrograms.Text(DateTimeProgramTests.TextTicks(random: 3).Take(3), kept: false)),
        ]),
        .. From("TimeSpan", [("Members", TimeSpanPrograms.Members(random: 7))]),
        .. From("ArrayMethods",
        [
            ("Copies", ArrayMethodPrograms.Copies), ("Searches", ArrayMethodPrograms.Searches),
            ("Sorting", ArrayMethodPrograms.Sorting), ("Extremes", ArrayMethodPrograms.Extremes),
            ("EmptyArrays", ArrayMethodPrograms.EmptyArrays), ("WithoutHeap", ArrayMethodPrograms.WithoutHeap),
        ]),
        .. From("HeapArrays", [("Basics", HeapArrayPrograms.Basics)]),
        .. From("Strings", StringPrograms.All),
        .. From("Boxing", BoxingPrograms.All),
        .. From("Classes", ClassPrograms.All),
        .. From("Virtual", VirtualPrograms.All),
        .. From("Collections", CollectionPrograms.All),
        .. From("GenericClasses", GenericClassPrograms.All),
        .. From("Delegates", DelegatePrograms.All),
    ];

    /// <summary>
    /// The programs that make no object but need more flash than the Uno's 31.5 KB (or a local array larger than its
    /// 16-bit addresses reach): the build stops and says so. Each runs every member of its library type, or every
    /// function, with doubles in software (compiler-rt's add, multiply and divide are 3 to 4 KB each on an AVR).
    /// </summary>
    public static IReadOnlySet<string> TooLarge { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "samples/MathFunctions", "DateTime/Members", "DateTime/Text", "TimeSpan/Members", "ArrayMethods/Sorting",
        "Arrays/BigArrays",
    };

    /// <summary>
    /// The programs whose output differs from .NET's (on a 64-bit PC) by what the AVR is, with why: they run to the
    /// end, and print as many lines as .NET.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Differences { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["StaticFields/EveryType"] = "nint and nuint have the AVR's 16 bits: -123456 does not fit in them",
        ["StaticFields/Initializers"] = "nuint has the AVR's 16 bits: 3,000,000,000 does not fit in it",
        ["Spans/Bytes"] = "structs have no padding on an AVR: MemoryMarshal.Write of a struct { byte; ushort; int; } " +
            "writes 7 bytes, not .NET's 8",
    };

    public static string Source(string name) => All.Single(program => program.Name == name).Source;

    private static IEnumerable<(string Name, string Source)> From(string corpus, IEnumerable<(string Name, string Source)> programs) =>
        programs.Select(program => (corpus + "/" + program.Name, program.Source));
}
