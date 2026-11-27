using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Analysis;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Generics;

/// <summary>
/// Generics over classes in the whole-program pass and the translation (Generics.ReferenceArguments): a reference type
/// argument gets an instance of its own, as a value does, so each instance of a generic class is a class with its own
/// descriptor, named as .NET names it; generic virtual methods have a slot for each instance called; the library's
/// generic code over the program's types is made in the program. What is left is rejected where it is used, by target:
/// without a heap, every use of an instance over a reference type needs one.
/// </summary>
public sealed class GenericClassCheckTests(ITestOutputHelper output) : IDisposable
{
    private const string OverClasses = "generics over classes (a class, an interface, object or an array as a type " +
        "argument) need a heap, and the target 'tiny' (2 KB RAM) has no heap. Use numbers, bools, enums and structs as " +
        "type arguments: ChipSharp makes the code once for each.";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private TargetDescription Tiny => new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name);

    [Fact]
    public void Each_reference_type_argument_has_an_instance_of_its_own_named_as_CSharp_writes_it()
    {
        var program = WholeProgram.Analyze("""
            class Lane
            {
            }

            class Node<T>
            {
                public T Value;
                public Node<T> Next;
            }

            class Holder<T>
            {
                public class Cell
                {
                    public T Item;
                }

                public Cell Make(T item) => new Cell { Item = item };
            }

            class Program
            {
                static Node<T> Push<T>(Node<T> top, T value) => new Node<T> { Value = value, Next = top };

                static void Main()
                {
                    var lanes = Push(Push(null, new Lane()), new Lane());
                    var numbers = Push(null, 1);
                    var cell = new Holder<Lane>().Make(lanes.Value);
                    System.Console.WriteLine(numbers.Value + (cell.Item == lanes.Next.Value ? 1 : 0));
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Main", "Lane..ctor", "Program.Push<Lane>", "Program.Push<int>", "Holder<Lane>..ctor",
            "Holder<Lane>.Make", "Node<Lane>..ctor", "Node<int>..ctor", "Holder<Lane>+Cell..ctor",
        ], program.Methods.Select(method => method.DisplayName));
        var cell = Assert.Single(program.Methods, method => method.DisplayName == "Holder<Lane>+Cell..ctor").DeclaringType;
        Assert.Equal("Holder<Lane>.Cell", CSharpNames.Type(cell.Type));
        Assert.Equal([new IlNamedType("", "Lane", null, null)], cell.TypeArguments);
        Assert.All(program.Methods, method => Assert.Empty(method.DeclaringType.GenericParameters));
    }

    [Fact]
    public void Each_instance_of_a_generic_class_has_a_descriptor_with_the_name_dotnet_gives_it()
    {
        var module = Lowered.Module("""
            class Program
            {
                class Lane
                {
                }

                class Box<T>
                {
                    public T Value;
                }

                static void Main()
                {
                    object[] boxes = { new Box<Lane>(), new Box<int>(), new Box<Box<Lane>>(), new Box<Lane[]>() };
                    System.Console.WriteLine(boxes.Length);
                }
            }
            """);

        // .NET's Type.ToString(): an InvalidCastException's message writes a nested type without "Program+"
        Assert.Equal(
            ["Program+Box`1[Program+Lane]", "Program+Box`1[System.Int32]", "Program+Box`1[Program+Box`1[Program+Lane]]",
                "Program+Box`1[Program+Lane[]]"],
            module.TypeDescriptors.Select(descriptor => descriptor.RuntimeName)
                .Where(name => name?.StartsWith("Program+Box", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void A_generic_virtual_method_has_a_slot_for_each_instance_called_and_runs_only_what_is_called()
    {
        var program = WholeProgram.Analyze("""
            class Item
            {
            }

            abstract class Printer
            {
                public abstract int Print<T>(T item);
            }

            class Counting : Printer
            {
                public override int Print<T>(T item) => 1;
            }

            class Doubling : Counting
            {
                public override int Print<T>(T item) => 2 * base.Print(item);
            }

            class Program
            {
                static int Run(Printer printer) => printer.Print(new Item()) + printer.Print(7);

                static void Main() => System.Console.WriteLine(Run(new Counting()) + Run(new Doubling()));
            }
            """);

        var printed = program.Methods.Where(method => method.Name.StartsWith("Print", StringComparison.Ordinal))
            .Select(method => method.DisplayName).Order(StringComparer.Ordinal);
        Assert.Equal(["Counting.Print<Item>", "Counting.Print<int>", "Doubling.Print<Item>", "Doubling.Print<int>"], printed);
    }

    [Fact]
    public void The_library_generic_code_over_the_program_types_is_made_in_the_program()
    {
        var program = WholeProgram.Analyze("""
            using System;
            using System.Collections.Generic;

            struct Point : IEquatable<Point>
            {
                public int X;
                public bool Equals(Point other) => X == other.X;
            }

            class Program
            {
                static void Main()
                {
                    Span<Point> points = stackalloc Point[2];
                    Console.WriteLine(points.IndexOf(new Point { X = 1 }));
                    Console.WriteLine(EqualityComparer<Point>.Default.Equals(points[0], points[1]));
                }
            }
            """);

        var indexOf = Assert.Single(program.Methods, method => method.DisplayName == "ChipSharp.Library.MemoryExtensions.IndexOf<Point>"
            && method.Parameters[0].Type is IlGenericInstanceType { Arguments: [IlNamedType { Name: "Point" }] } span
            && span.Definition is IlNamedType { Name: "ReadOnlySpan`1" });
        Assert.Same(program.Assembly, indexOf.Assembly);
        Assert.Contains(program.Methods, method => method.DisplayName == "ChipSharp.Library.EqualityComparer<Point>.Equals");
        Assert.Contains(program.Methods, method => method.DisplayName == "ChipSharp.Library.Equality.OfEquatable<Point>");
    }

    [Fact]
    public void Without_a_heap_every_use_of_an_instance_over_a_reference_type_needs_one()
    {
        var analysis = WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile("""
            struct Pair<T>
            {
                public T First;
            }

            class Program
            {
                static int Count<T>(T value) => 1;

                static void Main()
                {
                    var pair = new Pair<object>();
                    System.Console.WriteLine(Count(pair.First) + Count(2));
                }
            }
            """), "Test.dll"), Tiny);

        Assert.Equal(
        [
            $"Program.Main() at IL_0002 (initobj): {OverClasses}",
            $"Program.Main() at IL_0009 (ldfld): {OverClasses}",
            $"Program.Main() at IL_000e (call): {OverClasses}",
            "Program.Main() at IL_001a (call): Console.Write and Console.WriteLine need a console, and the target 'tiny' " +
                "has none. Show what the program does another way, e.g. on a pin with ChipSharp.Hardware.Pin.",
        ], analysis.Problems.Select(problem => problem.Line));
    }

    [Fact]
    public void The_default_comparers_are_objects_which_need_a_heap()
    {
        var analysis = WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile("""
            using System.Collections.Generic;

            class Program
            {
                static int count;

                static void Main()
                {
                    if (EqualityComparer<int>.Default.Equals(1, 2))
                    {
                        count++;
                    }
                }
            }
            """), "Test.dll"), Tiny);

        Assert.Equal(
        [
            "Program.Main() at IL_0000 (call): the default comparers EqualityComparer<T>.Default and " +
                "Comparer<T>.Default (objects) need a heap, and the target 'tiny' (2 KB RAM) has no heap. " +
                "Compare with Equals and CompareTo, through a constraint where the type is a T: static bool Same<T>(T a, " +
                "T b) where T : IEquatable<T> => a.Equals(b);",
        ], analysis.Problems.Select(problem => problem.Line));
    }

    [Fact]
    public void A_foreach_over_an_enumerator_that_is_disposed_arrives_with_the_collections()
    {
        var problems = WholeProgram.Problems("""
            using System.Collections;
            using System.Collections.Generic;

            class Bag : IEnumerable<int>
            {
                public IEnumerator<int> GetEnumerator() => new Walker();

                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

                sealed class Walker : IEnumerator<int>
                {
                    int index = -1;
                    public int Current => index;
                    object IEnumerator.Current => Current;
                    public bool MoveNext() => ++index < 3;
                    public void Reset() => index = -1;
                    public void Dispose() { }
                }
            }

            class Program
            {
                static void Main()
                {
                    var total = 0;
                    foreach (var value in new Bag())
                    {
                        total += value;
                    }

                    System.Console.WriteLine(total);
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Main() at IL_000d: foreach over an enumerator that is disposed (one that implements IDisposable, as " +
                "IEnumerator<T> does, and so a foreach over an IEnumerable<T>), which C# does in a try/finally arrives " +
                "with M3 (collections). For now, give the collection a GetEnumerator() that returns a struct (or a sealed " +
                "class) with MoveNext() and Current and no Dispose, and loop over the collection itself.",
        ], problems);
    }

    [Fact]
    public void An_array_used_as_an_interface_arrives_with_the_collections()
    {
        var problems = WholeProgram.Problems("""
            using System.Collections.Generic;

            class Program
            {
                static IEnumerable<int> all;

                static int First(IReadOnlyList<int> values) => values[0];

                static void Main()
                {
                    var numbers = new int[] { 4, 5 };
                    System.Console.WriteLine(First(numbers));
                    all = new int[3];
                    System.Console.WriteLine(all == null);
                }
            }
            """);

        // a parameter and a static field of an interface type given an array
        const string Message = "arrays used as interfaces (an array as an IEnumerable<T>, ICollection<T>, IList<T> or " +
            "IReadOnlyList<T>) arrive with M3 (collections). For now, take the array itself (T[]) or a span " +
            "(ReadOnlySpan<T>), or wrap it in a class of your own that implements the interface.";
        Assert.Equal([$"Program.Main() at IL_000e (call): {Message}", $"Program.Main() at IL_001e (stsfld): {Message}"],
            problems);
    }

    [Fact]
    public void A_struct_without_IEquatable_in_a_default_comparer_is_boxed_as_dotnet_boxes_it()
    {
        // it compiles where there is a heap (Boxing.BoxingPrograms.Generic prints what it gives), and a chip without one
        // says what boxes it
        var analysis = WholeProgram.Check(Source);
        Assert.True(analysis.IsSupported, analysis.Details);

        var problems = WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile(Source), "Test.dll"), Tiny)
            .Problems.Select(problem => problem.Line);

        Assert.Contains(problems, problem => problem.StartsWith("ChipSharp.Library.Equality.OfObject<Point>(Point, Point)",
            StringComparison.Ordinal) && problem.Contains("(box): boxing (using a value as an object) needs a heap",
            StringComparison.Ordinal));
    }

    private const string Source = """
            using System.Collections.Generic;

            struct Point
            {
                public int X;
            }

            class Program
            {
                static void Main() => System.Console.WriteLine(EqualityComparer<Point>.Default.Equals(new Point(), new Point()));
            }
            """;

    /// <summary>
    /// One copy for each type argument, on the Pico: three instances of a generic class over three classes take a little
    /// more flash than three uses of one instance (each instance's descriptors and .NET names; LLVM inlines the methods
    /// where they are called either way), and less than the same class over object, whose callers cast what they get
    /// back: sharing one copy among the classes would cost more than it saves.
    /// </summary>
    [PicoFact]
    public void Instances_over_classes_take_less_flash_than_one_class_over_object()
    {
        const string Classes = """
            class A { public int V; public A(int v) => V = v; }
            class B { public int V; public B(int v) => V = v; }
            class C { public int V; public C(int v) => V = v; }
            """;
        const string Generic = """
            class Box<T> where T : class
            {
                T item;
                int count;
                public void Put(T value) { item = value; count++; }
                public T Take() { var value = item; item = null; return value; }
                public T Swap(T other) { var old = item; item = other; count++; return old; }
                public T Or(T fallback) => item ?? fallback;
                public int Count => count;
            }
            """;
        const string Objects = """
            class Box
            {
                object item;
                int count;
                public void Put(object value) { item = value; count++; }
                public object Take() { var value = item; item = null; return value; }
                public object Swap(object other) { var old = item; item = other; count++; return old; }
                public object Or(object fallback) => item ?? fallback;
                public int Count => count;
            }
            """;
        static string Use(string box, string type, string cast) =>
            $"{{ var box = new {box}(); box.Put(new {type}(1)); var old = {cast}box.Swap(new {type}(2)); " +
            $"System.Console.WriteLine(({cast}box.Or(new {type}(9))).V + old.V + box.Count + ({cast}box.Take()).V); }}";
        string Program(string box, params (string Box, string Type, string Cast)[] uses) =>
            $"{Classes}\n{box}\nclass Program\n{{\n    static void Main()\n    {{\n" +
            string.Join("\n", uses.Select(use => "        " + Use(use.Box, use.Type, use.Cast))) + "\n    }\n}\n";

        var three = Flash(Program(Generic, ("Box<A>", "A", ""), ("Box<B>", "B", ""), ("Box<C>", "C", "")), "Three");
        var one = Flash(Program(Generic, ("Box<A>", "A", ""), ("Box<A>", "A", ""), ("Box<A>", "A", "")), "One");
        var objects = Flash(Program(Objects, ("Box", "A", "(A)"), ("Box", "B", "(B)"), ("Box", "C", "(C)")), "Objects");
        output.WriteLine($"Box<A>, Box<B>, Box<C>: {three} bytes; Box<A> three times: {one}; Box over object: {objects}");

        Assert.InRange(three - one, 1UL, 2 * 160UL);
        Assert.True(three < objects, $"{three} bytes for three instances, {objects} for one class over object");
    }

    /// <summary>The flash a program for the Pico takes: its code, constants and the starting values of its variables.</summary>
    private ulong Flash(string source, string name) =>
        MemoryReport.Read(new LinkedBinary(PicoBuild.Build(source, name, Path.Combine(_scratch, name)).Elf), PicoBuild.Pico)
            .Code!.Used;
}
