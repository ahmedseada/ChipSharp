using System.Globalization;
using System.Text;

namespace ChipSharp.Tests.Narrowing;

/// <summary>
/// C# programs whose arithmetic narrowing rewrites, to run with it and compare with .NET: <see cref="Edges"/>, written
/// by hand at the edges of each type, and <see cref="Random"/>, made from a seed: functions of bytes, sbytes, shorts,
/// ushorts and chars in loops, with every operator, casts, comparisons signed and unsigned, divisions and shifts by
/// amounts past the width. Their inputs come from tables through a volatile index, so that LLVM computes nothing
/// ahead and the narrowed code itself runs. Each program prints its results (<c>Main</c>) and also has <c>Result</c>,
/// a hash of them, which <c>Main</c> returns when the program is made for a chip without a console.
/// </summary>
internal static class NarrowingPrograms
{
    /// <summary>The edges: 255 + 1, -128 / -1, remainders' signs, shifts by 32 and more, unsigned comparisons of negative values.</summary>
    public static string Edges(bool returnsResult) => Program(returnsResult, """
            static readonly int[] Bytes = { 0, 1, 127, 128, 200, 255 };
            static readonly int[] SBytes = { -128, -127, -1, 0, 1, 127 };
            static readonly int[] Shorts = { -32768, -1, 0, 255, 256, 32767 };

            static int Run(int k)
            {
                int hash = 0;
                for (int i = 0; i < Bytes.Length; i++)
                {
                    byte b = (byte)Bytes[(i + k) % Bytes.Length];
                    int next = b + 1;
                    byte wrapped = (byte)(b + 1);
                    byte added = b;
                    added += 200;
                    int masked = (b * 3) & 0xFF;
                    Report(ref hash, next, wrapped, added, b * b, b / 3, b % 7, b >> 3, b << 5, (byte)(b << 5), ~b, -b, masked);
                }

                for (int i = 0; i < SBytes.Length; i++)
                {
                    for (int j = 0; j < SBytes.Length; j++)
                    {
                        sbyte x = (sbyte)SBytes[(i + k) % SBytes.Length], y = (sbyte)SBytes[j];
                        int quotient = y != 0 ? x / y : 99, remainder = y != 0 ? x % y : 99;
                        Report(ref hash, quotient, remainder, (sbyte)quotient, x < y ? 1 : 0, (uint)x < (uint)y ? 1 : 0,
                            (byte)x == (byte)y ? 1 : 0, x == (byte)y ? 1 : 0, x >> (y & 15), x >>> 1, x << (y & 31),
                            (ushort)x > (ushort)y ? 1 : 0, x * y, (x & 0x7F) / 5);
                    }
                }

                for (int s = 0; s < 70; s += 7)
                {
                    int one = k, many = 255 * k;
                    Report(ref hash, one << s, many >> s, (int)((uint)(-k) >> s), (int)((long)k << s), (byte)k << s, (short)(many << s),
                        (int)(0xFFu >> (s & 7)));
                }

                for (int i = 0; i < Shorts.Length; i++)
                {
                    short h = (short)Shorts[(i + k) % Shorts.Length];
                    ushort u = (ushort)h;
                    char c = (char)(h & 0x7F);
                    u++;
                    c++;
                    Report(ref hash, h + 1, (short)(h + 1), u, u >> 4, h >> 4, h / 10, h % 10, u % 10, c, c / 2, h * h, h < u ? 1 : 0);
                }

                int total = 0;
                for (int i = 0; i < 300; i++)
                {
                    total += i & 0xFF;
                }

                int steps = 0;
                for (byte b = 250; b != 4; b += 3)
                {
                    steps++;
                }

                int count = 0;
                for (int i = 10; i > 0; i = i - 3)
                {
                    count += i % 4;
                }

                long big = 0;
                for (int i = 0; i < 20; i++)
                {
                    big += (long)i * 100000 + (byte)(i * 37);
                }

                Report(ref hash, total, steps, count, (int)big, (int)(big >> 32));
                return hash;
            }
            """, "Run(input)");

    /// <summary>A program of <paramref name="functions"/> random functions made from <paramref name="seed"/>.</summary>
    public static string Random(int seed, int functions, bool returnsResult)
    {
        var generator = new Generator(new System.Random(seed));
        var body = new StringBuilder();
        var calls = new StringBuilder();
        body.AppendLine("""
                static readonly int[] Small = { 0, 1, 7, 100, 127, 128, 200, 255 };
                static readonly int[] Wide = { -1, 2147483647, -2147483648, 65535, -32768, 40000, -129, 300 };
            """);
        for (var f = 0; f < functions; f++)
        {
            body.AppendLine(generator.Function($"F{f}"));
            var table = generator.Coin() ? "Small" : "Wide";
            calls.Append(CultureInfo.InvariantCulture, $"Report(ref hash, F{f}({table}[(k + {f}) & 7]));\n");
        }

        body.AppendLine($$"""
                static int Run(int start)
                {
                    int hash = 0;
                    for (int k = start; k < start + 8; k++)
                    {
            {{Indent(calls.ToString(), 12)}}
                    }

                    return hash;
                }
            """);
        return Program(returnsResult, body.ToString(), "Run(input)");
    }

    /// <summary>
    /// The program around <paramref name="members"/>: a volatile input (1, so tables are read from their second element
    /// on), <c>Report</c>, which prints each value (or, for a chip without a console, only hashes it), <c>Result</c> and
    /// <c>Main</c>.
    /// </summary>
    private static string Program(bool returnsResult, string members, string run) => $$"""
        using System;

        class Program
        {
            static volatile int input = 1;

            static void Report(ref int hash, params int[] values)
            {
                foreach (int value in values)
                {
                    {{(returnsResult ? "" : "Console.WriteLine(value);")}}
                    hash = unchecked(hash * 31 + value);
                }
            }

        {{Indent(members, 4)}}

            public static int Result() => {{run}};

            static {{(returnsResult ? "int" : "void")}} Main()
            {
                {{(returnsResult ? "return Result();" : "Console.WriteLine(Result());")}}
            }
        }
        """;

    private static string Indent(string text, int spaces)
    {
        var lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        var common = lines.Where(line => line.Trim().Length > 0).Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(line => line.Trim().Length == 0 ? "" : new string(' ', spaces) + line[common..]))
            .TrimStart();
    }

    /// <summary>Random C#: each function starts from its seed in five small variables and an int, and loops.</summary>
    private sealed class Generator(System.Random random)
    {
        private static readonly string[] Small = ["a", "b", "c", "d", "e"];
        private int _names;

        public bool Coin() => random.Next(2) == 0;

        public string Function(string name)
        {
            var statements = new StringBuilder();
            var count = random.Next(4, 10);
            for (var i = 0; i < count; i++)
            {
                statements.Append(Statement(2, ["i"])).Append('\n');
            }

            return $$"""
                static int {{name}}(int seed)
                {
                    unchecked
                    {
                        byte a = (byte)seed;
                        sbyte b = (sbyte)(seed >> 3);
                        short c = (short)(seed * 7);
                        ushort d = (ushort)(seed >> 1);
                        char e = (char)(seed & 0x7F);
                        int acc = seed;
                        for (int i = 0; i < {{random.Next(1, 13)}}; i++)
                        {
                {{Indent(statements.ToString(), 16)}}
                        }

                        return acc ^ a ^ (b << 8) ^ (c << 4) ^ d ^ e;
                    }
                }
                """;
        }

        private string Statement(int depth, IReadOnlyList<string> counters)
        {
            var choice = random.Next(depth > 0 ? 13 : 9);
            var x = Small[random.Next(Small.Length)];
            switch (choice)
            {
                case 0:
                    return $"a = (byte)({Expression(3, counters)});";
                case 1:
                    return $"b = (sbyte)({Expression(3, counters)});";
                case 2:
                    return $"c = (short)({Expression(3, counters)});";
                case 3:
                    return $"d = (ushort)({Expression(3, counters)});";
                case 4:
                    return $"e = (char)({Expression(2, counters)} & 0x7F);";
                case 5:
                    return $"acc += {Expression(3, counters)};";
                case 6:
                    return $"acc = acc * 31 ^ {Expression(3, counters)};";
                case 7:
                    return random.Next(3) switch
                    {
                        0 => $"{x}++;",
                        1 => $"{x}--;",
                        _ => $"{x} = ({TypeOf(x)})({x} + {random.Next(1, 300)});",
                    };
                case 8:
                    return random.Next(3) == 0
                        ? $"{x} {Pick(">>=", "<<=")} {Leaf(counters)};"
                        : $"{x} = ({TypeOf(x)})({x} {Pick("+", "-", "^", "&", "|", "*")} {Leaf(counters)});";
                case 9:
                case 10:
                    return $$"""
                        if ({{Condition(2, counters)}})
                        {
                            {{Statement(depth - 1, counters)}}
                        }
                        else
                        {
                            {{Statement(depth - 1, counters)}}
                        }
                        """;
                case 11:
                {
                    var j = $"j{_names++}";
                    return $$"""
                        for (int {{j}} = {{random.Next(0, 3)}}; {{j}} < {{random.Next(3, 7)}}; {{j}}++)
                        {
                            {{Statement(depth - 1, [.. counters, j])}}
                        }
                        """;
                }

                default:
                {
                    var w = $"w{_names++}";
                    return $$"""
                        {
                            byte {{w}} = {{(x == "a" ? "a" : $"(byte){x}")}};
                            while ({{w}} > 0)
                            {
                                {{w}} >>= 1;
                                acc += {{w}} % 3;
                            }
                        }
                        """;
                }
            }
        }

        private string Condition(int depth, IReadOnlyList<string> counters) => random.Next(5) switch
        {
            0 => $"(uint)({Expression(depth, counters)}) {Pick("<", "<=", ">", ">=")} (uint)({Expression(depth, counters)})",
            1 => $"(byte)({Expression(depth, counters)}) == (sbyte)({Expression(depth, counters)})",
            _ => $"{Expression(depth, counters)} {Pick("<", "<=", ">", ">=", "==", "!=")} {Expression(depth, counters)}",
        };

        private string Expression(int depth, IReadOnlyList<string> counters)
        {
            if (depth == 0 || random.Next(4) == 0)
            {
                return Leaf(counters);
            }

            var (left, right) = (Expression(depth - 1, counters), Expression(depth - 1, counters));
            return random.Next(12) switch
            {
                0 or 1 or 2 => $"({left} {Pick("+", "-", "*", "&", "|", "^")} {right})",
                3 => $"({left} << ({right} & {Pick("7", "15", "31")}))",
                4 => $"({left} >> ({right} & {Pick("7", "15", "31")}))",
                5 => $"({left} >>> ({right} & 31))",
                6 => $"({left} {Pick("<<", ">>")} {random.Next(30, 70)})",
                7 => $"({Dividend(counters)} {Pick("/", "%")} ({right} | 1))",
                8 => $"(int)((uint)({left}) {Pick("/", "%")} ((uint)({right}) | 1u))",
                9 => $"(int)({Pick("byte", "sbyte", "short", "ushort", "char")})({left})",
                10 => $"({Condition(depth - 1, counters)} ? (int)({left}) : (int)({right}))",
                _ => $"({Pick("-", "~")}(int)({left}))",
            };
        }

        /// <summary>A dividend that is never int.MinValue, which divided by -1 would overflow.</summary>
        private string Dividend(IReadOnlyList<string> counters) =>
            Coin() ? Leaf(counters) : $"({Expression(2, counters)} & 0xFFFF)";

        private string Leaf(IReadOnlyList<string> counters) => random.Next(10) switch
        {
            < 5 => Small[random.Next(Small.Length)],
            5 or 6 => counters[random.Next(counters.Count)],
            7 => "(acc & 0xFF)",
            _ => random.Next(-3, 300).ToString(CultureInfo.InvariantCulture),
        };

        private string Pick(params string[] options) => options[random.Next(options.Length)];

        /// <summary>The type of the small variable <paramref name="name"/>.</summary>
        private static string TypeOf(string name) => name switch
        {
            "a" => "byte",
            "b" => "sbyte",
            "c" => "short",
            "d" => "ushort",
            _ => "char",
        };
    }
}
