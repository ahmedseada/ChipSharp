using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Records;

/// <summary>
/// Records, StringBuilder and type objects in the whole-program pass and the translation: what needs a heap (text, a
/// record class's objects) and what does not (a record struct's equality, <c>with</c> and deconstruction; a type
/// object, a constant), what of reflection is rejected and how; a type object and an appended interpolation in TIR.
/// </summary>
public sealed class RecordCheckTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>A Pico whose target file says it has no heap (but its console).</summary>
    private TargetDescription NoHeap => new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
        ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");

    [Fact]
    public void A_record_struct_compares_copies_and_deconstructs_with_no_heap_and_its_text_needs_one()
    {
        var source = """
            using System;

            readonly record struct Reading(int Pin, int Value, int? Code);

            class Program
            {
                static void Main()
                {
                    var a = new Reading(1, 20, null);
                    var b = a with { Value = 21 };
                    var (pin, value, _) = b;
                    Console.WriteLine(a == b);
                    Console.WriteLine(a.Equals(b with { Value = 20 }));
                    Console.WriteLine(a.GetHashCode() == new Reading(1, 20, null).GetHashCode());
                    Console.WriteLine(pin + value);
                }
            }
            """;
        var withText = source.Replace("Console.WriteLine(pin + value);", "Console.WriteLine(pin + value); Console.WriteLine(b);",
            StringComparison.Ordinal);

        Assert.True(Check(source, NoHeap).IsSupported, string.Join("\n", Check(source, NoHeap).Problems.Select(p => p.Line)));
        Assert.Contains(Problems(withText, NoHeap), line => line.StartsWith("Reading.ToString() at IL_0000 (newobj): " +
            "creating objects (new ...) needs a heap, and the target 'pico-no-heap'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_record_class_needs_a_heap_and_a_chip_without_one_says_so_where_one_is_made()
    {
        var problems = Problems("""
            using System;

            record Point(int X, int Y);

            class Program
            {
                static void Main()
                {
                    var point = new Point(1, 2);
                    Console.WriteLine(point.X);
                }
            }
            """, NoHeap);

        Assert.Contains(problems, line => line.StartsWith("Program.Main() at IL_0002 (newobj): creating objects (new ...) " +
            "needs a heap", StringComparison.Ordinal));
    }

    [Fact]
    public void Typeof_and_its_names_need_no_heap_as_type_objects_are_constants()
    {
        var analysis = Check("""
            using System;

            struct Point
            {
                public int X;
            }

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(typeof(Point).Name);
                    Console.WriteLine(typeof(int[]).FullName);
                    Console.WriteLine(typeof(Point) == typeof(int));
                    Console.WriteLine(typeof(Point).IsValueType);
                }
            }
            """, NoHeap);

        Assert.True(analysis.IsSupported, string.Join("\n", analysis.Problems.Select(problem => problem.Line)));
    }

    [Fact]
    public void Reflection_beyond_the_names_and_typeof_of_an_open_generic_type_are_rejected_saying_what_there_is()
    {
        var problems = Problems("""
            using System;
            using System.Collections.Generic;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(typeof(Program).GetMethods().Length);
                    Console.WriteLine(Activator.CreateInstance(typeof(Program)) == null);
                    Console.WriteLine(typeof(List<>).Name);
                    Console.WriteLine(typeof(Program).BaseType == null);
                }
            }
            """, null);

        Assert.Equal(
        [
            "Program.Main() at IL_000a (call): reflection (System.Type.GetMethods()) arrives later. ChipSharp's System.Type " +
                "has Name, FullName, Namespace, ToString, ==, Equals, GetHashCode, IsValueType, IsArray, IsEnum, IsClass, " +
                "IsInterface, IsPrimitive and IsGenericType; for more, give the types a virtual method that says it, or " +
                "switch on the object's type.",
            "Program.Main() at IL_0020 (call): reflection (System.Activator.CreateInstance(System.Type)) arrives later. " +
                "ChipSharp's System.Type has Name, FullName, Namespace, ToString, ==, Equals, GetHashCode, IsValueType, " +
                "IsArray, IsEnum, IsClass, IsInterface, IsPrimitive and IsGenericType; for more, give the types a virtual " +
                "method that says it, or switch on the object's type.",
            "Program.Main() at IL_002d (ldtoken): typeof of an open generic type (typeof(List<>)) arrives later. For now, " +
                "name an instance of it, such as typeof(List<int>).",
            "Program.Main() at IL_0032 (call): typeof of an open generic type (typeof(List<>)) arrives later. For now, name " +
                "an instance of it, such as typeof(List<int>).",
            "Program.Main() at IL_004b (callvirt): reflection (System.Type.BaseType) arrives later. ChipSharp's System.Type " +
                "has Name, FullName, Namespace, ToString, ==, Equals, GetHashCode, IsValueType, IsArray, IsEnum, IsClass, " +
                "IsInterface, IsPrimitive and IsGenericType; for more, give the types a virtual method that says it, or " +
                "switch on the object's type.",
        ], problems);
    }

    [Fact]
    public void A_type_object_is_a_constant_with_dotnets_names_and_GetType_finds_it_by_the_header()
    {
        var module = Lowered.Module("""
            using System;

            class Program
            {
                class Node
                {
                }

                static void Main()
                {
                    object node = new Node();
                    Console.WriteLine(node.GetType() == typeof(Node));
                }
            }
            """);

        var text = TirPrinter.Print(module);
        Assert.Contains("constant ChipSharp.Library.Type @<type Program+Node> = object ChipSharp.Library.Type {", text);
        var typeObject = Assert.Single(module.Globals, global => global.Name == "<type Program+Node>");
        Assert.True(typeObject.IsConstant);
        Assert.Equal(["Node", "Program+Node", null, "Program+Node"], typeObject.FieldValues!.Take(4)
            .Select(value => value is TirGlobalAddress { Global: var name } ? Text(name) : null));
        var of = Assert.Single(module.Functions, function => function.Name == "ChipSharp.Type.Of");
        Assert.Contains(of.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction is TirTypeAddress { TypeDescriptor.Name: "Program.Node" });
    }

    [Fact]
    public void An_interpolation_appended_to_a_builder_appends_each_part_and_makes_no_string_of_the_whole()
    {
        var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(CSharpCompiler.Compile("""
            using System;
            using System.Text;

            class Program
            {
                static void Main()
                {
                    var text = new StringBuilder();
                    int count = 3;
                    text.Append($"count {count,4} of {count:X2}");
                    Console.WriteLine(text);
                }
            }
            """), "Test.dll")));

        var main = Assert.Single(module.Functions, function => function.Name == "Program.Main");
        var calls = main.Blocks.SelectMany(block => block.Instructions).OfType<TirCall>().Select(call => call.Callee.Name).ToList();
        Assert.Equal(4, calls.Count(name => name == "ChipSharp.Library.StringBuilder.Append"));
        Assert.DoesNotContain("ChipSharp.Runtime.StringConcat", calls);
    }

    /// <summary>The text of a constant string object.</summary>
    private static string Text(TirGlobal global) =>
        global.ObjectType!.Name == "string" ? System.Text.Encoding.Latin1.GetString([.. global.Data!]) : "(wide)";

    private static AnalysisResult Check(string source, TargetDescription? target) =>
        WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile(source), "Test.dll"), target);

    private static string[] Problems(string source, TargetDescription? target)
    {
        var analysis = Check(source, target);
        Assert.False(analysis.IsSupported);
        return [.. analysis.Problems.Select(problem => problem.Line)];
    }
}
