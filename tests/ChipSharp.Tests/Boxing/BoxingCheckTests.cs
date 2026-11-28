using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Boxing;

/// <summary>
/// Boxing in the whole-program pass and the translation: a box is a heap use, which a chip without a heap names, with a
/// hint; a nullable, a struct's own methods, a value printed straight away and the boxes generic code folds need none.
/// What a box is in TIR: an object of its own class, named as .NET names its type, made only where a box is kept.
/// </summary>
public sealed class BoxingCheckTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    /// <summary>A Pico whose target file says it has no heap (but its console).</summary>
    private TargetDescription NoHeap => new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
        ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");

    [Fact]
    public void Boxing_needs_a_heap_and_a_chip_without_one_says_so_with_what_to_write_instead()
    {
        var problems = Problems("""
            using System;

            interface IDevice
            {
                void Tick();
            }

            struct Counter : IDevice
            {
                public int Ticks;

                public void Tick() => Ticks++;
            }

            class Program
            {
                static object Keep(int value) => value;

                static void Main()
                {
                    Console.WriteLine(Keep(1) == null);
                    IDevice device = new Counter();
                    device.Tick();
                }
            }
            """, NoHeap);

        Assert.Equal(
        [
            "Program.Main() at IL_0017 (box): converting a struct to an interface or to object (boxing it) needs a heap, " +
                "and the target 'pico-no-heap' (264 KB RAM) has no heap. Pass the struct to a generic method instead: " +
                "static void Run<T>(ref T device) where T : struct, IDevice.",
            "Program.Keep(int) at IL_0001 (box): boxing (using a value as an object) needs a heap, and the target " +
                "'pico-no-heap' (264 KB RAM) has no heap. Keep values in their own types, such as int and bool, and give " +
                "a method that takes any of them a type parameter instead of object: static void Show<T>(T value) where " +
                "T : struct.",
        ], problems);
    }

    [Fact]
    public void Nullables_the_methods_of_values_and_values_printed_straight_away_need_no_heap()
    {
        var analysis = Check("""
            using System;

            enum Light { Red, Green }

            struct Point
            {
                public int X;
            }

            class Program
            {
                static int? Half(int value) => value % 2 == 0 ? value / 2 : null;

                static void Main()
                {
                    int? half = Half(4) + Half(3) ?? 7;
                    Light? light = Light.Green;
                    var point = new Point { X = 1 };
                    Console.WriteLine(half);
                    Console.WriteLine($"{half} {light} {point} {Half(5)}");
                    Console.WriteLine("{0} {1} {2}", half, light, point);
                    Console.WriteLine(point);
                    Console.WriteLine(point.GetHashCode() == new Point().GetHashCode());
                    Console.WriteLine(light.Equals(null) + " " + light.HasValue + " " + half.GetValueOrDefault());
                }
            }
            """, NoHeap);

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void Generic_code_over_values_folds_the_tests_of_its_boxes_and_needs_no_heap()
    {
        var analysis = Check("""
            using System;

            interface IDevice
            {
                int Tick();
            }

            struct Counter : IDevice
            {
                public int Ticks;

                public int Tick() => ++Ticks;
            }

            class Program
            {
                static int Kind<T>(T value) => value is IComparable ? 1 : value is IDevice ? 2 : value is int ? 3 : 0;

                static int Value<T>(T value) => value is int number ? number : -1;

                static bool IsNull<T>(T value) => value == null;

                static void Main()
                {
                    Console.WriteLine(Kind(5) + Kind(new Counter()) + Kind(2.5));
                    Console.WriteLine(Value(4));
                    Console.WriteLine(IsNull(3) || IsNull(new Counter()));
                }
            }
            """, NoHeap);

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void A_box_is_an_object_of_its_own_class_named_as_dotnet_names_its_type()
    {
        var module = Lowered.Module("""
            using System;

            struct Point
            {
                public int X;
            }

            class Program
            {
                static object Keep(object value) => value;

                static void Main()
                {
                    Console.WriteLine(Keep(1));
                    Console.WriteLine(Keep(new Point()));
                    Console.WriteLine(Keep(DayKind.Rest));
                }

                enum DayKind { Work, Rest }
            }
            """);

        var boxes = module.TypeDescriptors.Where(type => type.Name.EndsWith(" (boxed)", StringComparison.Ordinal))
            .Select(type => (type.Name, type.RuntimeName, type.Base?.Name, type.ObjectType!.Fields[1].Type.Name)).ToList();
        Assert.Equal(
        [
            ("int (boxed)", "System.Int32", "System.Object", "i32"),
            ("Point (boxed)", "Point", "System.Object", "Point"),
            ("Program.DayKind (boxed)", "Program+DayKind", "System.Object", "i32"),
        ], boxes);
    }

    [Fact]
    public void A_value_printed_straight_away_and_a_constrained_call_make_no_box()
    {
        var module = Lowered.Module("""
            using System;

            struct Point
            {
                public int X;
            }

            enum Light { Red, Green }

            class Program
            {
                static void Main()
                {
                    var point = new Point { X = 2 };
                    int? half = 3;
                    Console.WriteLine(5);
                    Console.WriteLine((object)Light.Green);
                    Console.WriteLine(point);
                    Console.WriteLine(half);
                    Console.WriteLine("{0} {1}", point, half);
                    Console.WriteLine(point.GetHashCode() + point.ToString().Length);
                }
            }
            """);

        var main = Assert.Single(module.Functions, function => function.Name == "Program.Main");
        Assert.DoesNotContain(main.Blocks.SelectMany(block => block.Instructions), instruction => instruction is TirNewObject);
        Assert.DoesNotContain(module.TypeDescriptors, type => type.Name.EndsWith(" (boxed)", StringComparison.Ordinal));
    }

    private static AnalysisResult Check(string source, TargetDescription? target) =>
        WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile(source), "Test.dll"), target);

    private static string[] Problems(string source, TargetDescription? target)
    {
        var analysis = Check(source, target);
        Assert.False(analysis.IsSupported);
        return [.. analysis.Problems.Select(problem => problem.Line)];
    }
}
