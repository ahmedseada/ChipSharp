using static ChipSharp.Tests.Analysis.WholeProgram;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// Fixed text in the whole-program pass: the string literals it accepts and where (locals, parameters, return values,
/// static fields, consts, compared with null), printed with Console.Write and Console.WriteLine; and what it rejects,
/// building text above all, as one feature with one hint.
/// </summary>
public sealed class TextCheckTests
{
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
    public void The_methods_of_string_compile_and_a_StringBuilder_is_building_text_still()
    {
        // string.Format and Console.WriteLine's formatting overloads of a literal format, the properties and methods of
        // string and == on text compile (those that make text are heap uses, fine without a target); a StringBuilder's
        // methods are building text, which arrives with M3
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

        Assert.Equal([BuildingFeature], analysis.Groups.Select(group => group.Feature));
        Assert.Equal(
        [
            $"Program.Main() at IL_0044 (newobj): {Building}. {BuildingHint}",
            $"Program.Main() at IL_004a (call): {Building}. {BuildingHint}",
        ], analysis.Problems.Select(problem => problem.Line));
    }

    [Fact]
    public void Text_in_the_fields_of_structs_in_arrays_and_a_reference_to_it_compile()
    {
        // a string is an object: in a struct's field and in an array it is a reference the collector follows; a reference
        // to a string is a reference to a variable that holds one (ObjectReferences); an array that is null is fine (see
        // HeapArrayCheckTests)
        var analysis = Check("""
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

        Assert.True(analysis.IsSupported, analysis.Summary);
    }

    [Fact]
    public void A_character_compiles_and_is_printed_and_read_from_text()
    {
        // the indexer is a method of string that makes no text, and Console.Write prints a char
        var analysis = Check("""
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

        Assert.True(analysis.IsSupported, analysis.Details);
    }
}
