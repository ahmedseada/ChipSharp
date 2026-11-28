using System.Globalization;
using System.Text;

namespace ChipSharp.Tests.Backends;

/// <summary>
/// Random C# programs of arithmetic, made from a seed, for <see cref="DifferentialFuzzTests"/>: each function starts
/// from a variable of every integer width and of float and double, read from tables through a volatile index (so no
/// compiler works the values out ahead), and mixes them with every operator C# has for numbers (its promotions too:
/// <c>uint + int</c> is a long), casts between every pair of types (a float that is NaN, infinite or out of the range
/// made an integer; <c>(uint)</c> of a negative; <c>(byte)300.5f</c>), shifts by any amount (negative, past the width),
/// compound assignments on the small types, increments, ternaries, ifs and loops, and divisions by values that may be
/// 0 or -1 (a helper skips the two that .NET throws for, which <see cref="DifferentialFuzzTests"/> checks on its own).
/// It prints every value it makes, and its variables at the end.
/// </summary>
internal static class FuzzPrograms
{
    /// <summary>How large a program is, and which types it uses.</summary>
    public sealed record Shape(int Functions, int Statements, bool Longs, bool Floats, bool Doubles);

    /// <summary>For the PC: every type.</summary>
    public static Shape Pc { get; } = new(Functions: 8, Statements: 12, Longs: true, Floats: true, Doubles: true);

    /// <summary>
    /// For the STM8 (8 KB of flash): a smaller program of the integers up to 32 bits. No float: SDCC's float arithmetic
    /// is not IEEE 754's (its lib/src/_fsadd.c and the others drop the bits they shift out, and have no NaN, infinity
    /// or subnormal), so its results are not .NET's, as the README says; no long, whose code does not fit.
    /// </summary>
    public static Shape Stm8 { get; } = new(Functions: 2, Statements: 6, Longs: false, Floats: false, Doubles: false);

    /// <summary>The program of <paramref name="seed"/>.</summary>
    public static string Make(int seed, Shape shape) => new Generator(new Random(seed), shape).Program();

    private enum T
    {
        SByte, Byte, Short, UShort, Char, Int, UInt, Long, ULong, Float, Double,
    }

    private sealed class Generator(Random random, Shape shape)
    {
        private static readonly string[] Names = ["sbyte", "byte", "short", "ushort", "char", "int", "uint", "long", "ulong", "float", "double"];
        private static readonly string[] Variables = ["sb", "b", "s", "us", "c", "i", "u", "l", "ul", "f", "d"];
        private int _temporaries;

        private IEnumerable<T> Types => Enum.GetValues<T>().Where(Enabled);

        private bool Enabled(T type) => type switch
        {
            T.Long or T.ULong => shape.Longs,
            T.Float => shape.Floats,
            T.Double => shape.Doubles,
            _ => true,
        };

        public string Program()
        {
            var text = new StringBuilder();
            text.Append("""
                using System;

                class Program
                {
                    static volatile int input = 1;
                    static readonly int[] Ints = { 0, 1, -1, 7, 127, 128, 255, -128, 32767, -32768, 65535, int.MaxValue, int.MinValue, 1000000007, -123456789, 0x55AA55AA };

                """);
            if (shape.Floats)
            {
                text.Append("""
                        static readonly float[] Floats = { 0f, -0f, 1.5f, -2.5f, 255.5f, 300.75f, -129.5f, 65535.5f, 2147483648f, -3e9f, 1e20f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1.17549435E-38f, 16777215f };

                    """);
            }

            if (shape.Longs)
            {
                text.Append("""
                        static readonly long[] Longs = { 0, -1, long.MinValue, long.MaxValue, 0x100000000, 0xFFFFFFFF, -0x80000001, 0x123456789ABCDEF0 };

                    """);
            }

            if (shape.Doubles)
            {
                text.Append("""
                        static readonly double[] Doubles = { 0.0, -0.0, 0.1, -2.5, 4294967295.5, 4294967296.0, -2147483648.5, 9.2233720368547758E+18, 1.8446744073709552E+19, -1e19, 1e300, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 5e-324, 123456.789 };

                    """);
            }

            // the divisions by a value: of 0, and of the smallest value by -1, the dividend (.NET throws for both)
            foreach (var type in new[] { T.Int, T.UInt, T.Long, T.ULong }.Where(Enabled))
            {
                var name = Names[(int)type];
                var overflow = IsSigned(type) ? $" || (b == -1 && a == {name}.MinValue)" : "";
                text.Append(CultureInfo.InvariantCulture,
                    $"    static {name} Div{Variables[(int)type]}({name} a, {name} b) => b == 0{overflow} ? a : a / b;\n");
                text.Append(CultureInfo.InvariantCulture,
                    $"    static {name} Rem{Variables[(int)type]}({name} a, {name} b) => b == 0{overflow} ? a : a % b;\n");
            }

            for (var f = 0; f < shape.Functions; f++)
            {
                text.Append('\n').Append(Function($"F{f}"));
            }

            text.Append("""

                    static void Main()
                    {
                        for (int k = input; k < input + 3; k++)
                        {

                """);
            for (var f = 0; f < shape.Functions; f++)
            {
                text.Append(CultureInfo.InvariantCulture, $"            F{f}(k);\n");
            }

            text.Append("        }\n    }\n}\n");
            return text.ToString();
        }

        private string Function(string name)
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"    static void {name}(int k)\n    {{\n        unchecked\n        {{\n");
            foreach (var type in Types)
            {
                var (table, mask) = type switch
                {
                    T.Long or T.ULong => ("Longs", 7),
                    T.Float => ("Floats", 15),
                    T.Double => ("Doubles", 15),
                    _ => ("Ints", 15),
                };
                text.Append(CultureInfo.InvariantCulture,
                    $"            {Names[(int)type]} {Variables[(int)type]} = ({Names[(int)type]}){table}[(k + {random.Next(16)}) & {mask}];\n");
            }

            for (var i = 0; i < shape.Statements; i++)
            {
                text.Append(Indent(Statement(2, []), 12));
            }

            foreach (var type in Types)
            {
                text.Append(CultureInfo.InvariantCulture, $"            Console.WriteLine({Printable(Variables[(int)type], type)});\n");
            }

            text.Append("        }\n    }\n");
            return text.ToString();
        }

        private string Statement(int depth, IReadOnlyList<string> counters)
        {
            var (variable, type) = Variable();
            switch (random.Next(depth > 0 ? 10 : 7))
            {
                case 0:
                case 1:
                {
                    var (text, of) = Expression(3, counters);
                    return $"Console.WriteLine({Printable(text, of)});\n";
                }

                case 2:
                    return $"Console.WriteLine({Condition(2, counters)});\n";

                case 3:
                    return $"{variable} = ({Names[(int)type]})({Expression(3, counters).Text});\n";

                case 4:
                {
                    if (IsFloat(type))
                    {
                        return $"{variable} {Pick("+=", "-=", "*=", "/=", "%=")} ({Names[(int)type]})({Expression(2, counters).Text});\n";
                    }

                    var op = Pick("+=", "-=", "*=", "&=", "|=", "^=", "<<=", ">>=", ">>>=");
                    return op.Contains('<') || op.Contains('>')
                        ? $"{variable} {op} (int)({Expression(2, counters).Text});\n"
                        : $"{variable} {op} ({Names[(int)type]})({Expression(2, counters).Text});\n";
                }

                case 5:
                    return Pick($"{variable}++;\n", $"{variable}--;\n", $"++{variable};\n", $"--{variable};\n");

                case 6:
                {
                    if (IsFloat(type))
                    {
                        goto case 4;
                    }

                    // a division by a value that may be 0 or -1: skipped where .NET would throw
                    var t = $"t{_temporaries++}";
                    var guard = type is T.Int or T.Long ? $" && !({t} == -1 && {variable} == {Names[(int)type]}.MinValue)" : "";
                    return $"{{\n    {Names[(int)type]} {t} = ({Names[(int)type]})({Expression(2, counters).Text});\n" +
                        $"    if ({t} != 0{guard})\n    {{\n        {variable} {Pick("/=", "%=")} {t};\n    }}\n}}\n";
                }

                case 7:
                    return $"if ({Condition(2, counters)})\n{{\n{Indent(Statement(depth - 1, counters), 4)}}}\n" +
                        $"else\n{{\n{Indent(Statement(depth - 1, counters), 4)}}}\n";

                case 8:
                {
                    var j = $"j{_temporaries++}";
                    return $"for (int {j} = {random.Next(-2, 2)}; {j} < (k & 3) + {random.Next(1, 5)}; {j}++)\n" +
                        $"{{\n{Indent(Statement(depth - 1, [.. counters, j]), 4)}{Indent(Statement(depth - 1, [.. counters, j]), 4)}}}\n";
                }

                default:
                {
                    var (text, of) = Expression(2, counters);
                    return $"{variable} = {Condition(1, counters)} ? ({Names[(int)type]})({text}) : {variable};\n" +
                        $"Console.WriteLine({Printable(text, of)});\n";
                }
            }
        }

        /// <summary>A comparison of two values, as C# promotes them, or two of them joined.</summary>
        private string Condition(int depth, IReadOnlyList<string> counters)
        {
            if (depth > 1 && random.Next(4) == 0)
            {
                return $"({Condition(depth - 1, counters)} {Pick("&&", "||")} {Pick("", "!")}{Condition(depth - 1, counters)})";
            }

            var (left, right, _) = Promote(Expression(depth, counters), Expression(depth, counters));
            var comparison = $"({left} {Pick("<", "<=", ">", ">=", "==", "!=")} {right})";
            return random.Next(4) == 0 ? $"!{comparison}" : comparison;
        }

        private (string Text, T Type) Expression(int depth, IReadOnlyList<string> counters)
        {
            if (depth <= 0 || random.Next(4) == 0)
            {
                return Leaf(counters);
            }

            var (a, b) = (Expression(depth - 1, counters), Expression(depth - 1, counters));
            switch (random.Next(12))
            {
                case 0:
                case 1:
                case 2:
                {
                    var (left, right, type) = Promote(a, b);
                    return ($"({left} {Pick("+", "-", "*")} {right})", type);
                }

                case 3:
                {
                    var (left, right, type) = Promote(Integer(a), Integer(b));
                    return ($"({left} {Pick("&", "|", "^")} {right})", type);
                }

                case 4:
                {
                    // the amount is any int: C# masks it to the width of the promoted left operand
                    var left = Integer(a);
                    return ($"({left.Text} {Pick("<<", ">>", ">>>")} (int)({b.Text}))", Promoted(left.Type));
                }

                case 5:
                {
                    var amount = random.Next(-3, 70).ToString(CultureInfo.InvariantCulture);
                    var left = Integer(a);
                    return ($"({left.Text} {Pick("<<", ">>", ">>>")} {amount})", Promoted(left.Type));
                }

                case 6:
                {
                    var to = Pick([.. Types]);
                    return ($"(({Names[(int)to]})({a.Text}))", to);
                }

                case 7:
                    return Negate(a);

                case 8:
                {
                    var (_, right, type) = Promote(a, b);
                    return ($"({Condition(1, counters)} ? ({Names[(int)type]})({a.Text}) : {right})", type);
                }

                case 9:
                {
                    // a division by a value, through the helpers, or by a constant, as it is
                    var (left, right, type) = Promote(a, b);
                    if (IsFloat(type))
                    {
                        return ($"({left} {Pick("/", "%")} {right})", type);
                    }

                    var wide = type is T.Int or T.UInt or T.Long or T.ULong ? type : T.Int;
                    return ($"{Pick("Div", "Rem")}{Variables[(int)wide]}(({Names[(int)wide]})({left}), ({Names[(int)wide]})({right}))", wide);
                }

                case 10:
                {
                    var (left, type) = (a.Text, Promoted(a.Type));
                    var divisor = IsSigned(type) || IsFloat(type)
                        ? Pick("3", "10", "-7", "16", "1000", "-2")
                        : Pick("3", "10", "7", "16", "1000", "2");
                    return ($"({left} {Pick("/", "%")} {Literal(divisor, type)})", type);
                }

                default:
                {
                    var operand = Integer(a);
                    return ($"(~{operand.Text})", Promoted(operand.Type));
                }
            }
        }

        private (string Text, T Type) Leaf(IReadOnlyList<string> counters)
        {
            var choice = random.Next(10);
            if (choice < 6)
            {
                var (variable, type) = Variable();
                return (variable, type);
            }

            if (choice < 8 && counters.Count > 0)
            {
                return (counters[random.Next(counters.Count)], T.Int);
            }

            return Constant();
        }

        private (string Variable, T Type) Variable()
        {
            var type = Pick([.. Types]);
            return (Variables[(int)type], type);
        }

        private (string Text, T Type) Constant()
        {
            var type = Pick([.. Types]);
            var text = type switch
            {
                T.SByte => Pick("(sbyte)-128", "(sbyte)-1", "(sbyte)0", "(sbyte)1", "(sbyte)127"),
                T.Byte => Pick("(byte)0", "(byte)1", "(byte)128", "(byte)200", "(byte)255"),
                T.Short => Pick("(short)-32768", "(short)-1", "(short)255", "(short)32767"),
                T.UShort => Pick("(ushort)0", "(ushort)255", "(ushort)32768", "(ushort)65535"),
                T.Char => Pick("'A'", "'\\0'", "'\\uFFFF'", "'\\u0080'"),
                T.Int => Pick("int.MinValue", "(-1)", "0", "1", "7", "31", "32", "33", "255", "65536", "int.MaxValue", "(-1000000)"),
                T.UInt => Pick("0u", "1u", "0x80000000u", "uint.MaxValue", "4000000000u"),
                T.Long => Pick("long.MinValue", "long.MaxValue", "(-1L)", "0x100000000L", "0x80000000L", "(-0x80000001L)"),
                T.ULong => Pick("ulong.MaxValue", "0x8000000000000000UL", "0x100000000UL", "1UL"),
                T.Float => Pick("float.NaN", "float.PositiveInfinity", "(-0f)", "0.5f", "(-1.5f)", "255.5f", "2147483648f",
                    "(-2147483904f)", "1e20f", "float.MaxValue", "float.Epsilon", "3.4f"),
                _ => Pick("double.NaN", "double.NegativeInfinity", "(-0.0)", "0.1", "2.5", "(-2.5)", "4294967295.5",
                    "9.2233720368547758E+18", "1.8446744073709552E+19", "(-9.2233720368547758E+18)", "1e300", "double.Epsilon"),
            };
            return (text, type);
        }

        /// <summary>The two operands as C# promotes them for a binary operator, and the type it gives.</summary>
        private (string Left, string Right, T Type) Promote((string Text, T Type) a, (string Text, T Type) b)
        {
            if (a.Type == T.Double || b.Type == T.Double)
            {
                return (a.Text, b.Text, T.Double);
            }

            if (a.Type == T.Float || b.Type == T.Float)
            {
                return (a.Text, b.Text, T.Float);
            }

            if (a.Type == T.ULong || b.Type == T.ULong)
            {
                // a ulong with a signed value: C# has no operator for the two, the signed one is made a ulong
                return (IsSigned(a.Type) ? $"(ulong)({a.Text})" : a.Text, IsSigned(b.Type) ? $"(ulong)({b.Text})" : b.Text, T.ULong);
            }

            if (a.Type == T.Long || b.Type == T.Long)
            {
                return (a.Text, b.Text, T.Long);
            }

            if (a.Type == T.UInt || b.Type == T.UInt)
            {
                if (!IsSigned(a.Type) && !IsSigned(b.Type))
                {
                    return (a.Text, b.Text, T.UInt);
                }

                // a uint with a signed value is a long; without longs, the uint is made an int
                return shape.Longs ? (a.Text, b.Text, T.Long)
                    : (a.Type == T.UInt ? $"(int)({a.Text})" : a.Text, b.Type == T.UInt ? $"(int)({b.Text})" : b.Text, T.Int);
            }

            return (a.Text, b.Text, T.Int);
        }

        /// <summary>The type of a unary operator's or a shift's operand of <paramref name="type"/>: the small ones are ints.</summary>
        private static T Promoted(T type) => type < T.Int ? T.Int : type;

        /// <summary>The value, made an integer when it is a float or double (a long for a double).</summary>
        private static (string Text, T Type) Integer((string Text, T Type) value) => value.Type switch
        {
            T.Float => ($"((int)({value.Text}))", T.Int),
            T.Double => ($"((long)({value.Text}))", T.Long),
            _ => value,
        };

        private (string Text, T Type) Negate((string Text, T Type) value) => value.Type switch
        {
            T.ULong => ($"(-(long)({value.Text}))", T.Long),
            T.UInt => shape.Longs ? ($"(-{value.Text})", T.Long) : ($"(-(int)({value.Text}))", T.Int),
            _ => ($"(-{value.Text})", Promoted(value.Type)),
        };

        /// <summary>A constant number of <paramref name="type"/> (int, uint, long, ulong, float or double).</summary>
        private static string Literal(string number, T type)
        {
            var text = type switch
            {
                T.UInt => number + "u",
                T.Long => number + "L",
                T.ULong => number + "UL",
                T.Float => number + "f",
                T.Double => number + ".0",
                _ => number,
            };
            return text.StartsWith('-') ? $"({text})" : text;
        }

        /// <summary>A value as Console.WriteLine prints it: the small types as ints, a char as its number.</summary>
        private static string Printable(string text, T type) => type < T.Int ? $"(int)({text})" : text;

        private static bool IsSigned(T type) => type is T.SByte or T.Short or T.Int or T.Long;

        private static bool IsFloat(T type) => type is T.Float or T.Double;

        private TValue Pick<TValue>(params TValue[] options) => options[random.Next(options.Length)];

        private static string Indent(string text, int spaces) => string.Concat(text.Split('\n')
            .Select(line => line.Length == 0 ? "" : new string(' ', spaces) + line + "\n"));
    }
}
