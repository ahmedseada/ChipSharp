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
        ("Uno/Copies", Copies),
        ("Uno/Text", Text),
    ];

    /// <summary>
    /// Copies from flash on the Uno, which a 16-bit int and LLVM 22 made wrong: spans of constant data copied by a
    /// constant and a variable count, a struct read from flash, clearing and filling (memset), struct copies, doubles
    /// passed on through a call of many arguments.
    /// </summary>
    public const string Copies = """
        using System;
        using System.Runtime.InteropServices;

        struct Header
        {
            public long Ticks;
            public int Red, Yellow, Green, Walk;
            public double Factor;
            public byte Flags;
        }

        class Program
        {
            static ReadOnlySpan<byte> Packet => new byte[]
            {
                1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
                31, 32, 33, 34, 35, 36, 37, 38, 39, 40,
            };

            static ReadOnlySpan<int> Words => new int[] { 100000, -200000, 300000, -400000, 500000, -600000 };
            static readonly byte[] Table = { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160 };
            static byte[] buffer = new byte[16];
            static int[] words = new int[6];
            static Header saved;
            static int count = 13;

            static void Show(in Header h) =>
                Console.WriteLine(h.Ticks + " " + h.Red + " " + h.Yellow + " " + h.Green + " " + h.Walk + " " + h.Flags);

            static double Mix(double a, double b, long c, int d, double e) => a * 2 + b - c + d * e;

            static void Main()
            {
                saved = MemoryMarshal.Read<Header>(Packet);
                Show(saved);
                Header copy = saved;
                copy.Red = 7;
                Show(copy);
                Show(saved);

                Span<byte> bytes = stackalloc byte[16];
                Packet.Slice(3, 13).CopyTo(bytes);
                Console.WriteLine(bytes[12]);
                Packet.Slice(5, count).CopyTo(bytes);
                Console.WriteLine(bytes[12]);
                Words.CopyTo(words);
                Console.WriteLine(words[5]);
                Words.Slice(1, count - 9).CopyTo(words);
                Console.WriteLine(words[3]);
                for (int i = 0; i < count; i++)
                {
                    buffer[i] = Table[i];
                }

                Console.WriteLine(buffer[12]);
                Array.Clear(buffer);
                Console.WriteLine(buffer[12] + buffer[0]);
                bytes.Fill(0xA5);
                Console.WriteLine(bytes[7]);
                bytes.Slice(2, 5).Clear();
                Console.WriteLine(bytes[1] + " " + bytes[2] + " " + bytes[6] + " " + bytes[7]);
                Array.Fill(words, -3);
                Console.WriteLine(words[0] + words[5]);

                double half = 0.5, third = 1.0 / 3;
                Console.WriteLine(half);
                Console.WriteLine(third);
                Console.WriteLine(Mix(third, half, 1234567890123, -7, 2.25));
                Console.WriteLine(-1e300 * third);
                Console.WriteLine((float)third);
            }
        }
        """;

    /// <summary>
    /// Fixed text on the Uno, read from flash by the runtime's C: its chars, comparing and searching it, parsing it.
    /// </summary>
    public const string Text = """
        using System;

        class Program
        {
            static string Pick(int i) => i switch { 0 => "zero", 1 => "one", 2 => "two", _ => "many" };

            static void Main()
            {
                string word = Pick(2);
                Console.WriteLine(word);
                Console.WriteLine(word.Length);
                Console.WriteLine(word[1]);
                Console.WriteLine(word == "two");
                Console.WriteLine(word == Pick(3));
                Console.WriteLine(Pick(3).IndexOf('n'));
                Console.WriteLine("traffic light".IndexOf("light"));
                Console.WriteLine("traffic light".LastIndexOf('f'));
                Console.WriteLine("traffic light".Contains("fic"));
                Console.WriteLine("traffic light".StartsWith("traffic"));
                Console.WriteLine("traffic light".EndsWith('t'));
                Console.WriteLine(string.CompareOrdinal("apple", "app"));
                Console.WriteLine("Hello".Equals("hELLO", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine(string.IsNullOrWhiteSpace("  \t"));
                Console.WriteLine(char.IsDigit(word[0]) + " " + char.ToUpper(word[0]));
                Console.WriteLine(int.Parse(" -12345 "));
                Console.WriteLine(long.Parse("9223372036854775807"));
                Console.WriteLine(uint.Parse("4000000000"));
                Console.WriteLine(short.TryParse("70000", out short tooBig) + " " + tooBig);
                Console.WriteLine(byte.TryParse("255", out byte most) + " " + most);
                switch (word)
                {
                    case "one":
                        Console.WriteLine(1);
                        break;
                    case "two":
                        Console.WriteLine(2);
                        break;
                }
            }
        }
        """;

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
