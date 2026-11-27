using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Strings;

/// <summary>
/// Programs that build, keep, compare and take apart strings and chars, each printing what it does, so that ChipSharp's
/// output can be compared with .NET's line by line (see <see cref="StringProgramTests"/> and
/// <see cref="StringSimulatorTests"/>).
/// </summary>
internal static class StringPrograms
{
    /// <summary>
    /// Kept text: $"..." and + in locals, fields of classes and structs, static fields, return values, arrays; +=;
    /// ToString of numbers, bools and chars with formats; string.Concat of many parts, string.Join, string.Format.
    /// </summary>
    public const string Building = """
        using System;

        struct Label
        {
            public string Text;
            public int Count;
        }

        class Sign
        {
            public string Title = "none";
            public Label Label;
        }

        class Program
        {
            static string last;

            static string Describe(int lane, bool busy, double load) =>
                $"lane {lane,3}|{busy,-6}|{load} {lane:X4} {lane:D5} {(byte)lane:x}";

            static string Join(string a, string b, string c, string d, string e) => a + b + c + d + e + a;

            static void Main()
            {
                string a = "road";
                string b = a + " " + 7 + '!' + true + 2.5f + (-3L) + 4u;
                Console.WriteLine(b);
                Console.WriteLine(b.Length);
                last = Describe(12, true, 0.75);
                Console.WriteLine(last);
                var sign = new Sign();
                sign.Title = $"{a}-{sign.Title}";
                sign.Label.Text = sign.Title + "?";
                sign.Label.Count = sign.Label.Text.Length;
                Console.WriteLine(sign.Title + " " + sign.Label.Text + " " + sign.Label.Count);
                string all = "";
                for (int i = 0; i < 5; i++)
                {
                    all += i.ToString() + ",";
                }

                Console.WriteLine(all);
                Console.WriteLine(Join("1", "22", "333", "4444", "55555"));
                string[] parts = { "x", null, "z", "" };
                Console.WriteLine(string.Join("+", parts));
                Console.WriteLine(string.Concat(parts) + "|" + string.Join(", ", new[] { "one", "two", "three" }));
                string format = string.Format("{0} of {1,4} is {2:X2}", 3, "ten", 255);
                Console.WriteLine(format);
                Console.WriteLine("{0} then {1}", 'q', -1.5);
                string empty = $"";
                Console.WriteLine(empty.Length + "" + (empty == "") + (12.ToString() + 3.ToString("D3")));
                Console.WriteLine(ReferenceEquals(a, "road"));
                char c = 'k';
                string one = c.ToString() + char.ToString('j') + true.ToString() + (-128).ToString();
                Console.WriteLine(one);
                Console.WriteLine(Pick(true) + Pick(false));
            }

            static string Pick(bool first) => first ? $"[{1}]" : "none";
        }
        """;

    /// <summary>The methods of string, with .NET's results, and stable hashes.</summary>
    public const string Methods = """
        using System;

        class Program
        {
            static void Show(string label, string value) => Console.WriteLine(label + ": [" + value + "]");

            static void Main()
            {
                string s = "  Hello, World! Hello!  ";
                string t = s.Trim();
                Show("Trim", t);
                Show("TrimStart", s.TrimStart());
                Show("TrimEnd", s.TrimEnd());
                Show("Trim char", "xxabcxx".Trim('x') + "|" + "xxabcxx".TrimStart('x') + "|" + "xxabcxx".TrimEnd('x'));
                Show("Substring", t.Substring(7) + "|" + t.Substring(0, 5) + "|" + t.Substring(13, 0));
                Console.WriteLine(t.IndexOf('o') + " " + t.IndexOf('o', 5) + " " + t.IndexOf('o', 5, 3) + " " + t.IndexOf('z'));
                Console.WriteLine(t.IndexOf("Hello") + " " + t.IndexOf("Hello", 1) + " " + t.IndexOf("") + " " + t.IndexOf("hello"));
                Console.WriteLine(t.LastIndexOf('l') + " " + t.LastIndexOf('l', 5) + " " + t.LastIndexOf("Hello") + " " + t.LastIndexOf(""));
                Console.WriteLine(t.Contains("World") + " " + t.Contains('!') + " " + t.Contains("world"));
                Console.WriteLine(t.StartsWith("Hello") + " " + t.StartsWith('H') + " " + t.EndsWith("!") + " " + t.EndsWith('x'));
                Show("Replace", t.Replace('l', 'L') + "|" + t.Replace("Hello", "Bye") + "|" + t.Replace("!", null));
                Show("Pad", "7".PadLeft(3) + "|" + "7".PadLeft(3, '0') + "|" + "7".PadRight(3) + "|" + "7".PadRight(3, '.') + "|" + "long".PadLeft(2));
                Show("Case", t.ToUpper() + "|" + t.ToLower() + "|" + t.ToUpperInvariant() + "|" + t.ToLowerInvariant());
                Show("Insert", t.Insert(5, "!!!") + "|" + t.Insert(0, ">") + "|" + t.Insert(t.Length, "<"));
                Show("Remove", t.Remove(5) + "|" + t.Remove(5, 7) + "|" + t.Remove(0, 0));
                Console.WriteLine(t == "Hello, World! Hello!" ? "equal" : "different");
                Console.WriteLine((t != "x") + " " + t.Equals("x") + " " + string.Equals(t, t.Substring(0)) + " " + string.Equals(null, null));
                Console.WriteLine(string.CompareOrdinal("abc", "abd") + " " + string.CompareOrdinal("b", "a") + " " + string.CompareOrdinal("ab", "abc") + " " + string.CompareOrdinal(null, "a"));
                Console.WriteLine("apple".CompareTo("banana") + " " + "pear".CompareTo("pear") + " " + "b".CompareTo(null) + " " + string.Compare("x", "w"));
                Console.WriteLine(string.IsNullOrEmpty("") + " " + string.IsNullOrEmpty(null) + " " + string.IsNullOrEmpty(" ") + " " + string.IsNullOrWhiteSpace(" \t\r\n") + " " + string.IsNullOrWhiteSpace("a"));
                Console.WriteLine(t.GetHashCode() == ("Hello, World!" + " Hello!").GetHashCode());
                Console.WriteLine(t.GetHashCode() == "something else".GetHashCode());
                object boxed = t;
                Console.WriteLine(t.Equals(boxed) + " " + t.Equals((object)"no") + " " + t.ToString().Length);
                string[] words = "a,b,,c,".Split(',');
                Console.WriteLine(words.Length + ": " + string.Join("|", words));
                string[] kept = " a , b ,, c ".Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Console.WriteLine(kept.Length + ": " + string.Join("|", kept));
                char[] letters = "stop".ToCharArray();
                (letters[0], letters[3]) = (letters[3], letters[0]);
                Console.WriteLine(new string(letters) + " " + new string('-', 4) + " " + new string(letters, 1, 2).Length);
                int vowels = 0;
                foreach (char c in t)
                {
                    if ("aeiouAEIOU".IndexOf(c) >= 0)
                    {
                        vowels++;
                    }
                }

                Console.WriteLine(vowels);
                for (int i = 0; i < t.Length; i += 4)
                {
                    Console.Write(t[i]);
                }

                Console.WriteLine();
            }
        }
        """;

    /// <summary>Chars: arithmetic, conversions, the char methods over Latin-1, chars in text, spans of chars.</summary>
    public const string Characters = """
        using System;

        class Program
        {
            static char Next(char c) => (char)(c + 1);

            static void Main()
            {
                char c = 'a';
                c++;
                Console.WriteLine(c);
                Console.WriteLine((int)c + " " + (c > 'a') + " " + Next('y') + " " + (char)65 + " " + ('z' - 'a'));
                Console.WriteLine($"[{c}] [{c,3}] [{c,-3}] {c:X}");
                int digits = 0, letters = 0, spaces = 0, upper = 0, lower = 0, both = 0;
                long changed = 0;
                for (int i = 0; i < 0x100; i++)
                {
                    char x = (char)i;
                    digits += char.IsDigit(x) ? 1 : 0;
                    letters += char.IsLetter(x) ? 1 : 0;
                    spaces += char.IsWhiteSpace(x) ? 1 : 0;
                    upper += char.IsUpper(x) ? 1 : 0;
                    lower += char.IsLower(x) ? 1 : 0;
                    both += char.IsLetterOrDigit(x) ? 1 : 0;
                    changed += char.ToUpper(x) * (long)i + char.ToLowerInvariant(x) + char.ToUpperInvariant(x) * 3 + char.ToLower(x);
                }

                Console.WriteLine(digits + " " + letters + " " + spaces + " " + upper + " " + lower + " " + both + " " + changed);
                Console.WriteLine(char.IsWhiteSpace('\u3000') + " " + char.IsWhiteSpace('\u2028') + " " + char.IsWhiteSpace('\u200B'));
                Span<char> buffer = stackalloc char[5];
                for (int i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = (char)('v' + i);
                }

                Console.WriteLine(buffer.ToString() + " " + new string(buffer.Slice(1, 2)));
                ReadOnlySpan<char> view = "spans of chars".AsSpan(6);
                Console.WriteLine(view.Length + " " + view[0] + " " + view.ToString() + " " + view.IndexOf('c'));
                ReadOnlySpan<char> wide = "\u20ac10".AsSpan();
                Console.WriteLine(wide.Length + " " + (int)wide[0] + " " + wide.Slice(1).ToString());
                char[] chars = { 'o', 'k' };
                Console.WriteLine(chars.Length + new string(chars) + chars[1]);
            }
        }
        """;

    /// <summary>Enum names: printed, kept, [Flags] combinations, values without a name, formats.</summary>
    public const string Enums = """
        using System;

        enum Light : byte { Red = 1, Yellow, Green = 4 }

        [Flags]
        enum Access { None = 0, Read = 1, Write = 2, Run = 4, ReadWrite = Read | Write, Big = 64 }

        [Flags]
        enum Bits : long { One = 1, High = 1L << 40 }

        enum Signed : sbyte { Minus = -1, Zero, Plus }

        class Program
        {
            static Light light = Light.Yellow;

            static void Main()
            {
                Console.WriteLine(light);
                Console.WriteLine(Light.Green.ToString() + " " + (Light)3 + " " + (Light)0);
                string kept = $"{Light.Red}/{light}/{(Light)9}";
                Console.WriteLine(kept + " " + kept.Length);
                Access a = Access.Read | Access.Run;
                Console.WriteLine(a);
                Console.WriteLine($"{Access.None} {Access.ReadWrite} {Access.Read | Access.Write | Access.Run} {(Access)8} {(Access)65}");
                Console.WriteLine((a | Access.Big).ToString());
                Console.WriteLine($"{Bits.One | Bits.High} {(Bits)2} {Signed.Minus} {(Signed)(-5)} {Signed.Plus}");
                Console.WriteLine($"{light:D} {light:X} {Access.Run:d} {light:x} {Signed.Minus:X}");
                Console.WriteLine(string.Format("{0} and {1}", light, Access.Write));
                for (var l = Light.Red; l <= Light.Green; l++)
                {
                    Console.Write(l + ";");
                }

                Console.WriteLine();
            }
        }
        """;

    /// <summary>A small command parser: switch on strings (a long one, which Release compiles with a hash), Trim, Parse.</summary>
    public const string Commands = """
        using System;

        class Program
        {
            static int position;

            static string Run(string line)
            {
                string command = line.Trim();
                int space = command.IndexOf(' ');
                string verb = space < 0 ? command : command.Substring(0, space);
                string argument = space < 0 ? "" : command.Substring(space + 1).Trim();
                switch (verb.ToLowerInvariant())
                {
                    case "left":
                        position -= argument.Length == 0 ? 1 : int.Parse(argument);
                        return "moved left";
                    case "right":
                        position += argument.Length == 0 ? 1 : int.Parse(argument);
                        return "moved right";
                    case "home":
                        position = 0;
                        return "home";
                    case "where":
                        return $"at {position}";
                    default:
                        return $"unknown '{verb}'";
                }
            }

            static int Code(string name)
            {
                switch (name)
                {
                    case "alpha": return 1;
                    case "bravo": return 2;
                    case "charl": return 3;
                    case "delta": return 4;
                    case "echoo": return 5;
                    case "foxtr": return 6;
                    case "golfx": return 7;
                    case "hotel": return 8;
                    case "india": return 9;
                    case "julie": return 10;
                    default: return 0;
                }
            }

            static void Main()
            {
                string[] lines = { "  right 5 ", "LEFT 2", "where", "jump", "Right", "home", "where", "left  12", "where" };
                foreach (var line in lines)
                {
                    Console.WriteLine(Run(line));
                }

                Console.WriteLine(Code("hotel") + Code("alpha") * 100 + Code("zulu") + Code("julie") * 1000);
                Console.WriteLine(int.Parse("-2147483648") + " " + uint.Parse("+4294967295") + " " + long.Parse(" 9223372036854775807\t"));
                Console.WriteLine(ulong.Parse("18446744073709551615") + " " + short.Parse("-32768") + " " + ushort.Parse("65535"));
                Console.WriteLine(sbyte.Parse("-128") + " " + byte.Parse("255") + " " + int.Parse("007") + " " + uint.Parse("-0"));
                Console.WriteLine(int.TryParse("12x", out var bad) + " " + bad + " " + int.TryParse(" 42 ", out var good) + " " + good);
                Console.WriteLine(byte.TryParse("256", out var big) + " " + big + " " + long.TryParse((string)null, out var none) + " " + none);
                Console.WriteLine(sbyte.TryParse("-129", out var low) + " " + ushort.TryParse("1\0", out var zero) + " " + zero);
            }
        }
        """;

    /// <summary>Text that is not ASCII: Latin-1, the euro sign, an emoji's surrogate pair, a lone surrogate.</summary>
    public const string NotAscii = """
        using System;

        class Program
        {
            static void Main()
            {
                string cafe = "caf\u00e9";
                string euro = "\u20ac" + 5;
                string smile = "\U0001F600!";
                Console.WriteLine(cafe + " " + cafe.Length + " " + (int)cafe[3]);
                Console.WriteLine(euro + " " + euro.Length + " " + (int)euro[0]);
                Console.WriteLine(smile + " " + smile.Length + " " + (int)smile[0] + " " + (int)smile[1]);
                Console.WriteLine(cafe.ToUpper() + " " + "\u00c0\u00de\u00ff\u00b5\u00df".ToLower() + " " + "\u00ff\u00b5".ToUpper().Length);
                string mixed = cafe + euro + smile;
                Console.WriteLine(mixed + " " + mixed.Length + " " + mixed.IndexOf('\u20ac') + " " + mixed.Substring(4, 2));
                Console.WriteLine(mixed.Substring(0, 4) == cafe);
                Console.Write(smile[0]);
                Console.Write(smile[1]);
                Console.WriteLine();
                Console.WriteLine("\ud800|" + "x\udc00");
                foreach (char c in "\u00e9\u20ac")
                {
                    Console.Write((int)c + " ");
                }

                Console.WriteLine(string.Join(",", "a\u00e9b\u20acc".Split('\u00e9')));
            }
        }
        """;

    /// <summary>
    /// Many short-lived strings, made and dropped, with a few kept in fields, an array and a list of objects: in a small
    /// heap only collections make room, and every kept string must live through them unchanged.
    /// </summary>
    public const string Garbage = """
        using System;

        class Entry
        {
            public string Name;
            public Entry Next;
        }

        class Program
        {
            static string kept = "start";
            static Entry entries;

            static void Main()
            {
                string[] recent = new string[4];
                long total = 0;
                for (int i = 0; i < 4000; i++)
                {
                    string text = $"item {i}: {i * 7 % 13,2} {(i % 3 == 0 ? "fizz" : "")}";
                    string upper = text.ToUpper().Trim();
                    total += upper.Length + text.IndexOf(':');
                    recent[i % 4] = upper.Substring(0, 6);
                    if (i % 500 == 0)
                    {
                        kept = kept + "|" + i;
                        entries = new Entry { Name = "entry " + i, Next = entries };
                    }
                }

                Console.WriteLine(total);
                Console.WriteLine(kept);
                Console.WriteLine(string.Join(",", recent));
                for (var e = entries; e != null; e = e.Next)
                {
                    Console.Write(e.Name + ";");
                }

                Console.WriteLine();
            }
        }
        """;

    /// <summary>
    /// Strings among the other objects: ToString of the program's classes (overridden, and object's own, the type's
    /// name), printed and in text, object's methods on a string held as an object, is, as and casts to string.
    /// </summary>
    public const string Objects = """
        using System;

        class Shape
        {
            public virtual string Name => "shape";

            public override string ToString() => $"{Name} of {Sides()} sides";

            public virtual int Sides() => 0;
        }

        sealed class Square : Shape
        {
            public override string Name => "square";

            public override int Sides() => 4;
        }

        class Plain
        {
        }

        class Program
        {
            class Nested
            {
            }

            static string Describe(object value) => value switch
            {
                string text => "text " + text.Length,
                Shape shape => "a " + shape.Name,
                null => "nothing",
                _ => "other",
            };

            static void Main()
            {
                Shape[] shapes = { new Shape(), new Square() };
                foreach (var shape in shapes)
                {
                    Console.WriteLine(shape);
                    Console.WriteLine(shape.ToString().ToUpper());
                }

                Console.WriteLine(new Plain());
                Console.WriteLine(new Nested().ToString());
                Console.WriteLine("made " + new Square() + "!");
                object text = "hello";
                object other = new Plain();
                Console.WriteLine(text);
                Console.WriteLine(text.ToString() + " " + text.Equals("hello") + " " + text.Equals(other) + " " + (text.GetHashCode() == "hello".GetHashCode()));
                Console.WriteLine((text is string) + " " + (other is string) + " " + (text as string) + "|" + (other as string) + "|");
                Console.WriteLine(Describe(text) + ", " + Describe(shapes[1]) + ", " + Describe(null) + ", " + Describe(other));
                string back = (string)text;
                Console.WriteLine(back.Length);
                object none = null;
                Console.WriteLine(none);
                Console.WriteLine((string)none == null);
            }
        }
        """;

    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Objects), Objects),
        (nameof(Building), Building),
        (nameof(Methods), Methods),
        (nameof(Characters), Characters),
        (nameof(Enums), Enums),
        (nameof(Commands), Commands),
        (nameof(NotAscii), NotAscii),
    ];

    /// <summary>
    /// What .NET is compiled with too: a UTF-8 console, without the byte order mark, so that it writes text the way
    /// ChipSharp's runtimes do (a redirected .NET console otherwise writes in the console's code page).
    /// </summary>
    public const string Utf8Console = """
        static class Utf8Console
        {
            [System.Runtime.CompilerServices.ModuleInitializer]
            internal static void Start() => System.Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        """;

    public static string Source(string name) => All.Single(program => program.Name == name).Source;

    /// <summary>Each program's name, compiled in Release (false) and in Debug (true).</summary>
    public static TheoryData<string, bool> InReleaseAndDebug()
    {
        var data = new TheoryData<string, bool>();
        foreach (var (name, _) in All)
        {
            data.Add(name, false);
            data.Add(name, true);
        }

        return data;
    }

    /// <summary>The program for .NET: its source and the UTF-8 console.</summary>
    public static byte[] ForDotnet(string source, string name) => CSharpCompiler.Compile([source, Utf8Console], name);

    public static IlAssembly Compile(string source, string name, bool debug, IEnumerable<string>? references = null) =>
        IlAssembly.Load(CSharpCompiler.Compile([source], references ?? [], name, OutputKind.ConsoleApplication,
            debug ? OptimizationLevel.Debug : OptimizationLevel.Release), name + ".dll");
}
