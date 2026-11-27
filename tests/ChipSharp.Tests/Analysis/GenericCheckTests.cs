using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;
using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// Generics in the whole-program pass (Generics.Instantiations): the program has one instance of a generic method or
/// type for each list of type arguments it reaches, found through the code that calls it, each a method of its own; type
/// arguments that are not values, and a struct turned into an interface, are reported where the code uses them, by
/// target: they need the heap.
/// </summary>
public sealed class GenericCheckTests : IDisposable
{
    private const string OverClasses = "generics over classes (a class, an interface, object or an array as a type " +
        "argument)";

    private const string OverClassesHint = "use numbers, bools, enums and structs as type arguments: ChipSharp makes " +
        "the code once for each.";

    private const string Boxing = "converting a struct to an interface or to object (boxing it)";

    private const string BoxingHint = "pass the struct to a generic method instead: static void Run<T>(ref T device) " +
        "where T : struct, IDevice.";

    /// <summary>A class as a type argument, next to an int.</summary>
    private const string ClassArgument = """
        class Program
        {
            static int count;

            static void Keep<T>(T value) => count++;

            static void Main()
            {
                Keep(1);
                Keep<object>(null);
            }
        }
        """;

    /// <summary>A struct used through a constraint and called directly, which needs no heap.</summary>
    private const string StructThroughConstraint = """
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
            static void Run<T>(ref T device) where T : struct, IDevice => device.Tick();

            static void Main()
            {
                var counter = new Counter();
                Run(ref counter);
                counter.Tick();
            }
        }
        """;

    /// <summary>A struct turned into an interface (boxed) to be passed on, next to the same call through a constraint.</summary>
    private const string StructAsInterface = """
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
            static void Run<T>(ref T device) where T : struct, IDevice => device.Tick();

            static void Use(IDevice device) => device.Tick();

            static void Main()
            {
                var counter = new Counter();
                Run(ref counter);
                Use(counter);
            }
        }
        """;

    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));
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
    public void Instances_are_found_through_the_generic_code_that_calls_them_once_each_in_the_order_first_called()
    {
        // Main's calls first, then those of Max3<int> (none new), ..., Max3<long> (Max<long>), Wrap<int> (the
        // constructor of Pair<int>) and Wrap<Pair<int>>; Pair<T>.Unused is never called, so it has no instance here
        Assert.Equal(
        [
            "Program.Main", "Program.Max3<int>", "Program.Max<int>", "Program.Max<double>", "Program.Max3<long>",
            "Program.Wrap<int>", "Program.Wrap<Pair<int>>", "Program.Max<long>", "Pair<int>..ctor",
            "Pair<Pair<int>>..ctor",
        ], MethodNames("""
            using System;

            struct Pair<T>
            {
                public T First, Second;
                public Pair(T first, T second) { First = first; Second = second; }
                public T Unused() => First;
            }

            class Program
            {
                static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;
                static T Max3<T>(T a, T b, T c) where T : IComparable<T> => Max(Max(a, b), c);
                static Pair<T> Wrap<T>(T value) => new Pair<T>(value, value);

                static void Main()
                {
                    Console.WriteLine(Max3(1, 2, 3) + Max(4, 5));
                    Console.WriteLine(Max(0.5, 1.5));
                    Console.WriteLine(Max3(1L, 2L, 3L));
                    Console.WriteLine(Wrap(Wrap(7)).Second.First);
                }
            }
            """).Where(name => !name.StartsWith("ChipSharp.Library", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_instance_is_a_method_of_its_own_with_the_types_in_place_and_the_generic_code_is_never_one()
    {
        var program = Analyze("""
            struct Pair<T>
            {
                public T First, Second;
                public void Set(T value) => First = Second = value;
            }

            class Program
            {
                static Pair<T> Make<T>(T value)
                {
                    var pair = new Pair<T>();
                    pair.Set(value);
                    return pair;
                }

                static void Main() => System.Console.WriteLine(Make(2.5f).Second);
            }
            """);

        Assert.All(program.Methods, method => Assert.Empty(method.GenericParameters));
        Assert.All(program.Methods, method => Assert.Empty(method.DeclaringType.GenericParameters));
        var make = Assert.Single(program.Methods, method => method.DisplayName == "Program.Make<float>");
        Assert.Equal("Program.Make", make.GenericDefinition!.DisplayName);
        Assert.Equal([IlType.Float32], make.MethodArguments);
        Assert.Equal("Pair<float>", CSharpNames.Type(make.ReturnType));
        Assert.Equal([IlType.Float32], make.Signature.ParameterTypes);
        Assert.Equal("Pair<float>", CSharpNames.Type(Assert.Single(make.Body!.Locals).Type));

        var set = Assert.Single(program.Methods, method => method.DisplayName == "Pair<float>.Set");
        var pair = set.DeclaringType;
        Assert.Equal("Pair`1", pair.GenericDefinition!.Name);
        Assert.Equal([IlType.Float32], pair.TypeArguments);
        Assert.Equal(["float32 Pair<float>::First", "float32 Pair<float>::Second"], pair.Fields.Select(field => field.ToString()));
        Assert.Same(pair, program.Assembly.FindType("Pair<float>"));

        // the field the instance's IL names is the instance's, with its type
        var store = Assert.IsType<IlFieldOperand>(set.Body!.Instructions.Last(i => i.OpCode == IlOpCode.Stfld).Operand).Field;
        Assert.Same(pair.Fields[0], store.Definition);
        Assert.Equal(IlType.Float32, store.FieldType);
    }

    [Fact]
    public void Type_arguments_that_grow_without_end_stop_at_a_limit()
    {
        var problems = WholeProgram.Problems("""
            struct Box<T>
            {
                public T Value;
            }

            class Program
            {
                static int Depth<T>(T value, int n) => n == 0 ? 0 : 1 + Depth(new Box<T> { Value = value }, n - 1);

                static void Main() => System.Console.WriteLine(Depth(1, 3));
            }
            """);

        var deepest = "Program.Depth<Box<Box<Box<Box<Box<Box<Box<Box<int>>>>>>>>>(Box<Box<Box<Box<Box<Box<Box<Box<int>>>>>>>>, int)";
        Assert.Equal(
        [
            $"{deepest} at IL_0018 (call): type arguments nested more than 8 generic types deep (a generic method or " +
                "type that uses itself with a bigger type argument each time). ChipSharp makes a copy of a generic " +
                "method or type for each list of type arguments, so it needs to know them all: give the call type " +
                "arguments that do not grow each time.",
        ], problems.Where(problem => problem.StartsWith(deepest, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pico")]
    [InlineData("host")]
    public void Classes_as_type_arguments_compile_on_a_target_with_a_heap(string? target)
    {
        var analysis = Check(ClassArgument, target is null ? null : Shipped.Load(target));

        Assert.True(analysis.IsSupported, analysis.Details);
        Assert.Equal(["Program.Main", "Program.Keep<int>", "Program.Keep<object>"],
            analysis.Methods.Select(method => method.DisplayName));
    }

    [Fact]
    public void Classes_as_type_arguments_need_a_heap_a_chip_without_one_does_not_have()
    {
        var hint = char.ToUpperInvariant(OverClassesHint[0]) + OverClassesHint[1..];
        Assert.Equal(
            [$"Program.Main() at IL_0007 (call): {OverClasses} need a heap, and the target 'tiny' (2 KB RAM) has no heap. {hint}"],
            Problems(ClassArgument, Tiny));
    }

    [Fact]
    public void Generic_types_over_types_that_cannot_be_type_arguments_are_their_problems()
    {
        var problems = WholeProgram.Problems("""
            struct Pair<T>
            {
                public T First;
            }

            class Box<T>
            {
                public static int Made;
            }

            class Program
            {
                static void Main()
                {
                    var pair = new Pair<int[,]>();
                    System.Console.WriteLine(pair.First == null);
                    Box<int>.Made++;
                    System.Console.WriteLine(Box<int>.Made);
                }
            }
            """);

        // an array as a type argument is what arrays of its kind need: a multi-dimensional one arrives later (Box<int>
        // compiles)
        const string Arrays = "multi-dimensional arrays (int[,]) arrive later. For now, use a one-dimensional array and " +
            "compute the index: row * width + column.";
        Assert.Equal(
        [
            $"Program.Main(), local variable V_0 (Pair<int[,]>): {Arrays}",
            $"Program.Main() at IL_0002 (initobj): {Arrays}",
            $"Program.Main() at IL_0009 (ldfld): {Arrays}",
        ], problems);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pico")]
    public void Converting_a_struct_to_an_interface_boxes_it_where_there_is_a_heap(string? target)
    {
        var analysis = Check(StructAsInterface, target is null ? null : Shipped.Load(target));

        // a parameter of an interface type compiles (it holds an object: a class's, or a box of a struct that
        // implements the interface)
        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void Converting_a_struct_to_an_interface_needs_a_heap_a_chip_without_one_does_not_have()
    {
        var hint = char.ToUpperInvariant(BoxingHint[0]) + BoxingHint[1..];
        Assert.Contains($"Program.Main() at IL_0010 (box): {Boxing} needs a heap, and the target 'tiny' (2 KB RAM) has " +
            $"no heap. {hint}", Problems(StructAsInterface, Tiny));
    }

    [Fact]
    public void A_struct_used_through_a_constraint_and_called_directly_needs_no_heap()
    {
        var analysis = Check(StructThroughConstraint, Tiny);

        Assert.True(analysis.IsSupported, analysis.Details);
        Assert.Equal(["Program.Main", "Program.Run<Counter>", "Counter.Tick"],
            analysis.Methods.Select(method => method.DisplayName));
    }

    [Fact]
    public void Generic_math_ChipSharp_does_not_have_is_a_library_method_and_checked_arithmetic_arrives_later()
    {
        var problems = WholeProgram.Problems("""
            using System;
            using System.Numerics;

            class Program
            {
                static T Larger<T>(T a, T b) where T : INumber<T> => T.Max(a, b);
                static T Total<T>(T a, T b) where T : INumber<T> => checked(a + b);

                static void Main()
                {
                    Console.WriteLine(Larger(1, 2));
                    Console.WriteLine(Total(1, 2));
                    Console.WriteLine(Total(0.5, 0.25));
                }
            }
            """);

        Assert.Equal(
        [
            "Program.Larger<int>(int, int) at IL_0008 (call): the library method System.Numerics.INumber<int>.Max(int, int) " +
                "is not available yet.",
            "Program.Total<int>(int, int) at IL_0008 (call): checked arithmetic arrives later. For now, leave out " +
                "checked: ChipSharp's arithmetic wraps around on overflow, like unchecked C#.",
        ], problems);
    }

    [Fact]
    public void An_instance_of_a_generic_local_function_is_named_with_its_type_arguments()
    {
        var analysis = WholeProgram.Check("""
            using System;
            using System.Numerics;

            Console.WriteLine(Twice(2.5));

            static T Twice<T>(T value) where T : INumber<T> => T.CreateChecked(2) * value;
            """);

        Assert.False(analysis.IsSupported);
        Assert.Contains("1 place in local function Twice<double> (IL_", Assert.Single(analysis.Groups).Line,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_a_type_argument_where_it_is_fixed_text()
    {
        var analysis = WholeProgram.Check("""
            class Program
            {
                static T Id<T>(T value) => value;

                static void Main() => System.Console.WriteLine(Id("fixed"));
            }
            """);

        Assert.True(analysis.IsSupported, analysis.Details);
        Assert.Equal(["Program.Main", "Program.Id<string>"], analysis.Methods.Select(method => method.DisplayName));
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
