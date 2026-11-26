using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// Fixed text in the whole-program pass: the string literals it accepts and where (locals, parameters, return values,
/// static fields, consts, compared with null), printed with Console.Write and Console.WriteLine; and what it rejects,
/// building text above all, as one feature with one hint.
/// </summary>
public sealed class TextCheckTests
{
    private const string Strings = "For now, keep text in locals, parameters, return values and static fields.";
    private const string NullArrays = "null in place of an array arrives with M3 (the heap). For now, use an empty array "
        + "(new int[0]): an array on the stack is never null.";

    [Fact]
    public void Literals_compile_in_locals_parameters_return_values_static_fields_and_consts_and_compare_with_null()
    {
        var methods = MethodNames("""
            using System;

            class Program
            {
                const string Road = "road : ";
                static readonly string Green = "    green ";
                static string current;

                static string Name(int lane) => lane == 1 ? "north" : null;

                static void Show(string label, int value)
                {
                    Console.Write(label);
                    Console.WriteLine(value);
                }

                static void Main()
                {
                    string none = null;
                    current = Name(1) ?? "nowhere";
                    Console.Write(Road);
                    Console.WriteLine(1);
                    Show(Green, 30);
                    Console.WriteLine(current);
                    Console.WriteLine(none == null);
                    Console.WriteLine(Name(2) is not null);
                    Console.WriteLine(string.Empty);
                    Console.Write(-5);
                    Console.Write(2.5f);
                    Console.Write(0.1);
                    Console.Write(true);
                    Console.Write(7u);
                    Console.Write(-8L);
                    Console.Write(9UL);
                    Console.WriteLine();
                }
            }
            """);

        Assert.Equal(["Program.Main", "Program..cctor", "Program.Name", "Program.Show"], methods);
    }

    [Fact]
    public void Building_text_with_the_methods_of_string_is_one_feature_whatever_builds_it()
    {
        // string.Format, the properties and methods of string, == on text, StringBuilder, and Console.WriteLine's
        // formatting overloads; text built with $"...", + and ToString and printed straight away is printed text
        // (PrintedTextCheckTests)
        var analysis = Check("""
            using System;
            using System.Text;

            class Program
            {
                static void Main()
                {
                    int lane = 2;
                    string name = "lane";
                    Console.WriteLine(string.Format("{0}", lane));
                    Console.WriteLine(name.Length);
                    Console.WriteLine(name.Substring(1));
                    Console.WriteLine(name == "road");
                    Console.WriteLine(new StringBuilder().Append(lane).ToString());
                    Console.WriteLine("{0} and {1}", lane, name);
                }
            }
            """);

        Assert.Equal(["boxing (using a value as an object)", BuildingFeature],
            analysis.Groups.Select(group => group.Feature));
        Assert.Equal([2, 8], analysis.Groups.Select(group => group.Problems.Count));
        Assert.Equal($"{Building}: 8 places in Program.Main() (IL_0013, IL_001e, IL_002a and 5 more). {BuildingHint}",
            analysis.Groups[1].Line);
        Assert.Equal(
        [
            $"Program.Main() at IL_001e (callvirt): {Building}. {BuildingHint}",
            $"Program.Main() at IL_003a (call): {Building}. {BuildingHint}",
            $"Program.Main() at IL_0044 (newobj): {Building}. {BuildingHint}",
            $"Program.Main() at IL_0065 (call): {Building}. {BuildingHint}",
        ], analysis.Problems.Where(problem => problem.Offset is 0x1e or 0x3a or 0x44 or 0x65)
            .Select(problem => problem.Line));
    }

    [Fact]
    public void Text_kept_where_only_the_heap_can_keep_it_says_so()
    {
        // in a struct's field, in an array, behind a reference; an array that is null (stored, passed, or measured where
        // ways join) is fine: arrays may be null on a target with a heap (see HeapArrayCheckTests)
        var problems = Problems("""
            using System;

            struct Person
            {
                public string Name;
            }

            class Program
            {
                static void Clear(out string text) => text = "";

                static int Count(int[] values) => values.Length;

                static bool Flag() => true;

                static void Main()
                {
                    Person person = default;
                    Console.WriteLine(person.Name);
                    string[] names = { "a" };
                    Console.WriteLine(names.Length);
                    Clear(out string text);
                    Console.WriteLine(text);
                    int[] none = null;
                    int[] some = new int[2];
                    Console.WriteLine(Count(null) + Count(none) + (Flag() ? some : null).Length);
                }
            }
            """);

        Assert.Equal(
        [
            $"Program.Main(), local variable V_3 (Person): text strings in the field Person.Name arrive with M3 (the heap). {Strings}",
            $"Program.Main() at IL_0002 (initobj): text strings in the field Person.Name arrive with M3 (the heap). {Strings}",
            $"Program.Main() at IL_0009 (ldfld): text strings in the field Person.Name arrive with M3 (the heap). {Strings}",
            "Program.Main() at IL_0014 (newarr): arrays of strings arrive with M3 (the heap). For now, keep each string "
                + "in a variable or a static field of its own, or pick it with a switch.",
            "Program.Clear(ref string), parameter 'text' (ref string): references to text (ref string, out string) "
                + "arrive with M3 (the heap). For now, return the text instead.",
            "Program.Clear(ref string) at IL_0006 (stind.ref): classes and objects arrive with M3 (the heap). "
                + "For now, use structs and static methods.",
        ], problems);
    }

    [Fact]
    public void A_character_is_not_text_yet()
    {
        // the indexer is a method of string; printing a char is a library method there is not
        var problems = Problems("""
            using System;

            class Program
            {
                static void Main()
                {
                    string name = "lane";
                    Console.Write(name[0]);
                    Console.WriteLine('!');
                }
            }
            """);

        Assert.Equal(
        [
            $"Program.Main() at IL_0006 (callvirt): {Building}. {BuildingHint}",
            "Program.Main() at IL_000b (call): the library method System.Console.Write(char) is not available yet.",
            "Program.Main() at IL_0012 (call): the library method System.Console.WriteLine(char) is not available yet.",
        ], problems);
    }
}
