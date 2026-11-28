using ChipSharp.Tests.Avr;
using ChipSharp.Tests.Boxing;
using ChipSharp.Tests.Classes;
using ChipSharp.Tests.Collections;
using ChipSharp.Tests.Generics;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Lambdas;
using ChipSharp.Tests.Library;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Narrowing;
using ChipSharp.Tests.Records;
using ChipSharp.Tests.Strings;
using ChipSharp.Tests.Support;
using ChipSharp.Tests.Tuples;

namespace ChipSharp.Tests.Backends;

/// <summary>
/// Every program the differential tests compare with .NET, gathered in one list, each by "&lt;corpus&gt;/&lt;name&gt;"
/// (e.g. "samples/Square", "Structs/Copies"): the samples, the programs of the language corpora (structs, spans, printed
/// text, generics, tuples, records, classes, collections, delegates, ...) and of the library tests (dates, TimeSpan,
/// System.Array, strings), the Uno's own, the narrowing's edges and the small chips' own (<see cref="ChipPrograms"/>).
/// The conformance suite runs each through every backend (<see cref="BackendConformanceTests"/>), the Uno's tests on the
/// Uno (<see cref="UnoPrograms"/>).
/// </summary>
internal static class ConformancePrograms
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
        ("Uno/Copies", UnoPrograms.Copies),
        ("Uno/Text", UnoPrograms.Text),
        ("Narrowing/Edges", NarrowingPrograms.Edges(returnsResult: false)),
        ("Narrowing/Random1", NarrowingPrograms.Random(1, 12, returnsResult: false)),
        ("Narrowing/Random2", NarrowingPrograms.Random(2, 12, returnsResult: false)),
        .. From("Chips", ChipPrograms.All),
    ];

    public static string Source(string name) => All.Single(program => program.Name == name).Source;

    private static IEnumerable<(string Name, string Source)> From(string corpus, IEnumerable<(string Name, string Source)> programs) =>
        programs.Select(program => (corpus + "/" + program.Name, program.Source));
}
