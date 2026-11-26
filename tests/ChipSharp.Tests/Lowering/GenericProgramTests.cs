using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Programs with generic methods and structs over values, compiled by ChipSharp for the PC (one copy of the code for
/// each type argument) and run: each must print what the same assembly prints on real .NET, compiled in Release and in
/// Debug (whose IL for constrained calls and generic math differs). The same programs run on the simulated Pico too
/// (Pico/GenericSimulatorTests).
/// </summary>
public sealed class GenericProgramTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>Each program's name, compiled in Release (false) and in Debug (true).</summary>
    public static TheoryData<string, bool> ProgramsInReleaseAndDebug => GenericPrograms.InReleaseAndDebug();

    [HostTheory]
    [MemberData(nameof(ProgramsInReleaseAndDebug))]
    public async Task Each_program_prints_what_dotnet_prints(string name, bool debug)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(GenericPrograms.Source(name), name), name,
            Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(GenericPrograms.Compile(name, debug)));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.Output, chipSharp.Output);
        Assert.Equal(dotnet.ExitCode, chipSharp.ExitCode);
    }

    [Fact]
    public void Every_program_passes_the_check_on_a_chip_without_a_heap()
    {
        var withoutHeap = GenericPrograms.PicoWithoutHeap(_scratch);
        foreach (var (name, source) in GenericPrograms.All)
        {
            var analysis = WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile(source, name), name + ".dll"),
                withoutHeap);

            Assert.True(analysis.IsSupported, $"{name}:\n{analysis.Details}");
        }
    }
}

/// <summary>C# programs with generics over values, for the PC and the Pico tests.</summary>
internal static class GenericPrograms
{
    /// <summary>
    /// Generic methods over every number type, bools and structs: Max through IComparable&lt;T&gt; (whose CompareTo of
    /// the narrow types is a difference), Swap through ref, generic code calling generic code with its own T, recursion,
    /// default(T), typeof(T) and sizeof.
    /// </summary>
    public const string Methods = """
        using System;
        using System.Runtime.CompilerServices;

        struct Point : IComparable<Point>
        {
            public int X, Y;
            public Point(int x, int y) { X = x; Y = y; }
            public int CompareTo(Point other) => (X * X + Y * Y).CompareTo(other.X * other.X + other.Y * other.Y);
        }

        class Program
        {
            static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;
            static T Max3<T>(T a, T b, T c) where T : IComparable<T> => Max(Max(a, b), c);
            static int Compare<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b);
            static bool Same<T>(T a, T b) where T : IEquatable<T> => a.Equals(b);
            static int Hash<T>(T value) => value.GetHashCode();
            static void Swap<T>(ref T a, ref T b) { T old = a; a = b; b = old; }
            static T Default<T>() => default;
            static bool IsInt<T>() => typeof(T) == typeof(int);
            static int Size<T>() => Unsafe.SizeOf<T>();
            static T Last<T>(T[] values) => values.Length == 0 ? default : values[values.Length - 1];

            static int Count<T>(T value, int n) where T : IComparable<T> =>
                n == 0 ? 0 : (value.CompareTo(default) > 0 ? 1 : 0) + Count(value, n - 1);

            static void Main()
            {
                Console.WriteLine(Max(3, -7));
                Console.WriteLine(Max(3u, 4000000000u));
                Console.WriteLine(Max(-3L, -70000000000L));
                Console.WriteLine(Max(3UL, ulong.MaxValue));
                Console.WriteLine(Max((byte)200, (byte)100));
                Console.WriteLine(Max((sbyte)-100, (sbyte)100));
                Console.WriteLine(Max((short)-300, (short)-30000));
                Console.WriteLine(Max((ushort)60000, (ushort)1));
                Console.WriteLine(Max(1.5f, -2.5f));
                Console.WriteLine(Max(double.NaN, 1.0));
                Console.WriteLine(Max(1.0, double.NaN));
                Console.WriteLine(Max(true, false));
                Console.WriteLine(Max(new Point(3, 4), new Point(1, 5)).X);
                Console.WriteLine(Max3(4, 9, 2));
                Console.WriteLine(Max3(0.25, -1.0, 0.125));
                Console.WriteLine(Max(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1500)).TotalMilliseconds);
                Console.WriteLine(Compare(TimeSpan.FromTicks(5), TimeSpan.FromTicks(3)));
                Console.WriteLine(Same(TimeSpan.Zero, TimeSpan.FromTicks(0)));

                Console.WriteLine(Compare((byte)3, (byte)200));
                Console.WriteLine(Compare((sbyte)-100, (sbyte)100));
                Console.WriteLine(Compare((short)-30000, (short)30000));
                Console.WriteLine(Compare((ushort)1, (ushort)60000));
                Console.WriteLine(Compare(false, true));
                Console.WriteLine(Compare(5L, -7L));
                Console.WriteLine(Compare(double.NaN, double.NaN));
                Console.WriteLine(Compare(float.NaN, 1f));
                Console.WriteLine(Compare(-0.0, 0.0));
                Console.WriteLine(5.CompareTo(3));
                Console.WriteLine(Same(2, 2));
                Console.WriteLine(Same(double.NaN, double.NaN));
                Console.WriteLine(Same(0.0, -0.0));
                Console.WriteLine(Same(true, false));
                Console.WriteLine(Hash(-3));
                Console.WriteLine(Hash(0x123456789L));
                Console.WriteLine(Hash(ulong.MaxValue));
                Console.WriteLine(Hash((short)-3));
                Console.WriteLine(Hash(true));
                Console.WriteLine(Hash(3000000000u));

                int a = 1, b = 2;
                Swap(ref a, ref b);
                Console.WriteLine($"{a} {b}");
                var p = new Point(1, 2);
                var q = new Point(3, 4);
                Swap(ref p, ref q);
                Console.WriteLine($"{p.X} {q.Y}");
                double x = 0.5, y = -0.5;
                Swap(ref x, ref y);
                Console.WriteLine(x);

                Console.WriteLine(Default<int>());
                Console.WriteLine(Default<double>());
                Console.WriteLine(Default<bool>());
                Console.WriteLine(Default<Point>().Y);
                Console.WriteLine(IsInt<int>());
                Console.WriteLine(IsInt<long>());
                Console.WriteLine(Size<byte>() + Size<short>() * 10 + Size<double>() * 100);
                Console.WriteLine(Last(new[] { 1.5f, 2.5f }));
                Console.WriteLine(Last(new int[0]));
                Console.WriteLine(Count(7, 5));
                Console.WriteLine(Count(-7L, 5));
            }
        }
        """;

    /// <summary>Generic math (INumber&lt;T&gt;) on every number type: the operators, the comparisons and the constants, as .NET computes them.</summary>
    public const string Math = """
        using System;
        using System.Numerics;

        class Program
        {
            static T Add<T>(T a, T b) where T : INumber<T> => a + b;
            static T Subtract<T>(T a, T b) where T : INumber<T> => a - b;
            static T Multiply<T>(T a, T b) where T : INumber<T> => a * b;
            static T Divide<T>(T a, T b) where T : INumber<T> => a / b;
            static T Remainder<T>(T a, T b) where T : INumber<T> => a % b;
            static T Negate<T>(T a) where T : INumber<T> => -a;
            static T Plus<T>(T a) where T : INumber<T> => +a;
            static T Next<T>(T a) where T : INumber<T> { a++; return a; }
            static T Previous<T>(T a) where T : INumber<T> { --a; return a; }
            static T Checked<T>(T a, T b) where T : INumber<T> => checked(a / b);
            static T CheckedFloat<T>(T a, T b) where T : IFloatingPoint<T> => checked(a + b * b - a);

            static T Bits<T>(T a, T b) where T : IBinaryInteger<T> => (a & b) | (a ^ ~b);
            static T Left<T>(T a, int n) where T : IBinaryInteger<T> => a << n;
            static T Right<T>(T a, int n) where T : IBinaryInteger<T> => a >> n;
            static T RightUnsigned<T>(T a, int n) where T : IBinaryInteger<T> => a >>> n;

            static void Order<T>(T a, T b) where T : INumber<T> => Console.WriteLine(
                $"{a < b} {a <= b} {a > b} {a >= b} {a == b} {a != b}");

            static void Limits<T>() where T : INumber<T>, IMinMaxValue<T> => Console.WriteLine(
                $"{T.Zero} {T.One} {T.MinValue} {T.MaxValue} {T.AdditiveIdentity} {T.MultiplicativeIdentity}");

            static T Sum<T>(T[] values) where T : INumber<T>
            {
                T sum = T.Zero;
                foreach (var value in values)
                {
                    sum += value;
                }

                return sum;
            }

            static T Polynomial<T>(T x) where T : INumber<T>
            {
                T two = T.One + T.One;
                return x * x * two - x + T.One;
            }

            static void Main()
            {
                Console.WriteLine($"{Add(2000000000, 2000000000)} {Add(4000000000u, 500000000u)} {Add(long.MaxValue, 1L)}");
                Console.WriteLine($"{Add((byte)200, (byte)100)} {Add((sbyte)100, (sbyte)100)} {Add((short)30000, (short)30000)} {Add((ushort)60000, (ushort)10000)}");
                Console.WriteLine($"{Add(0.1, 0.2)} {Add(0.1f, 0.2f)} {Add(ulong.MaxValue, 2UL)}");
                Console.WriteLine($"{Subtract(3, 5)} {Subtract(3u, 5u)} {Subtract((byte)3, (byte)5)} {Subtract((sbyte)-100, (sbyte)100)}");
                Console.WriteLine($"{Multiply(100000, 100000)} {Multiply((byte)16, (byte)17)} {Multiply((short)300, (short)300)} {Multiply(1.5f, 3f)}");
                Console.WriteLine($"{Divide(-7, 2)} {Divide(4000000000u, 3u)} {Divide((sbyte)-128, (sbyte)-1)} {Divide(-7L, 2L)} {Divide(ulong.MaxValue, 10UL)} {Divide(1.0, 3.0)} {Divide(1f, 0f)}");
                Console.WriteLine($"{Remainder(-7, 3)} {Remainder(7u, 3u)} {Remainder(-7.5, 2.0)} {Remainder(5.5f, -2f)} {Remainder((short)-7, (short)3)}");
                Console.WriteLine($"{Negate(5)} {Negate(int.MinValue)} {Negate(5u)} {Negate((byte)5)} {Negate((sbyte)-128)} {Negate(0.0)} {Negate(-0.0f)}");
                Console.WriteLine($"{Plus(-3)} {Plus(2.5)}");
                Console.WriteLine($"{Next(int.MaxValue)} {Next((byte)255)} {Next(0.5)} {Next(-1L)} {Previous(0u)} {Previous((sbyte)-128)} {Previous(1.5f)}");
                Console.WriteLine($"{Checked(-7, 2)} {Checked(9L, 4L)} {CheckedFloat(1e30, 1e300)} {CheckedFloat(1f, 2f)}");
                Console.WriteLine($"{Bits(12, 10)} {Bits((byte)12, (byte)10)} {Bits(12UL, 10UL)} {Bits((short)-12, (short)10)}");
                Console.WriteLine($"{Left(1, 33)} {Left((byte)3, 7)} {Left((byte)3, 9)} {Left(1L, 65)} {Left((short)1, 15)} {Left((ushort)1, 17)}");
                Console.WriteLine($"{Right(-16, 2)} {Right(-16, 34)} {Right(0x80000000u, 31)} {Right((sbyte)-128, 3)} {Right((sbyte)-128, 9)} {Right(-1L, 70)}");
                Console.WriteLine($"{RightUnsigned(-16, 28)} {RightUnsigned(-1L, 60)} {RightUnsigned((byte)128, 9)} {RightUnsigned(0xFFFF0000u, 16)}");
                Order(1, 2);
                Order(2u, 1u);
                Order(-1, -1);
                Order(1u, 0xFFFFFFFFu);
                Order(1.5, double.NaN);
                Order(float.NaN, float.NaN);
                Order(-0.0, 0.0);
                Order((sbyte)-1, (sbyte)1);
                Order((byte)255, (byte)1);
                Order(ulong.MaxValue, 1UL);
                Limits<int>();
                Limits<uint>();
                Limits<long>();
                Limits<ulong>();
                Limits<sbyte>();
                Limits<byte>();
                Limits<short>();
                Limits<ushort>();
                Limits<float>();
                Limits<double>();
                Console.WriteLine($"{Sum(new[] { 1, 2, 3 })} {Sum(new[] { 0.5f, 0.25f })} {Sum(new long[] { long.MaxValue, 2 })} {Sum(new byte[] { 250, 10 })}");
                Console.WriteLine($"{Polynomial(3)} {Polynomial(0.5)} {Polynomial((byte)20)} {Polynomial(-2L)}");
            }
        }
        """;

    /// <summary>
    /// Structs that implement interfaces, used through constraints (implicitly and explicitly, generic interfaces, a
    /// generic struct, static abstract members) and directly, without boxing.
    /// </summary>
    public const string Interfaces = """
        using System;

        interface IDevice
        {
            int Count { get; }
            void Tick();
        }

        interface ISource<T>
        {
            T Next();
        }

        interface IShape
        {
            static abstract int Sides { get; }
            static abstract IShape Make();
        }

        interface IScaled<TSelf> where TSelf : IScaled<TSelf>
        {
            static abstract TSelf Scale(TSelf value, int factor);
            int Size { get; }
        }

        struct Counter : IDevice
        {
            public int Ticks;
            public int Count => Ticks;
            public void Tick() => Ticks++;
        }

        struct Blinker : IDevice
        {
            private int _changes;
            int IDevice.Count => _changes * 10;
            void IDevice.Tick() => _changes++;
        }

        struct Countdown : ISource<int>, ISource<bool>
        {
            public int Left;
            public int Next() => Left--;
            bool ISource<bool>.Next() => Left > 0;
        }

        struct Repeat<T> : ISource<T>, IDevice
        {
            public T Value;
            public int Calls;
            public T Next() { Calls++; return Value; }
            public int Count => Calls;
            public void Tick() => Calls += 100;
        }

        struct Triangle : IShape
        {
            public static int Sides => 3;
            public static IShape Make() => null;
        }

        struct Square : IShape
        {
            static int IShape.Sides => 4;
            static IShape IShape.Make() => null;
        }

        struct Length : IScaled<Length>
        {
            public int Millimetres;
            public static Length Scale(Length value, int factor) => new Length { Millimetres = value.Millimetres * factor };
            public int Size => Millimetres;
        }

        class Program
        {
            static int Run<T>(ref T device, int ticks) where T : struct, IDevice
            {
                for (int i = 0; i < ticks; i++)
                {
                    device.Tick();
                }

                return device.Count;
            }

            static int RunCopy<T>(T device) where T : IDevice
            {
                device.Tick();
                return device.Count;
            }

            static T Take<T, TSource>(ref TSource source) where TSource : struct, ISource<T> => source.Next();

            static int SidesOf<T>() where T : IShape => T.Sides;

            static int Grow<T>(T value) where T : IScaled<T> => T.Scale(value, 3).Size;

            static void Main()
            {
                var counter = new Counter();
                Console.WriteLine(Run(ref counter, 3));
                Console.WriteLine(counter.Ticks);
                counter.Tick();
                Console.WriteLine(counter.Count);
                Console.WriteLine(RunCopy(counter));
                Console.WriteLine(counter.Count);

                var blinker = new Blinker();
                Console.WriteLine(Run(ref blinker, 4));
                Console.WriteLine(RunCopy(blinker));

                var countdown = new Countdown { Left = 2 };
                Console.WriteLine(Take<int, Countdown>(ref countdown));
                Console.WriteLine(Take<bool, Countdown>(ref countdown));
                Console.WriteLine(Take<int, Countdown>(ref countdown));
                Console.WriteLine(Take<bool, Countdown>(ref countdown));

                var repeat = new Repeat<double> { Value = 2.5 };
                Console.WriteLine(Take<double, Repeat<double>>(ref repeat));
                Console.WriteLine(Run(ref repeat, 2));
                var flags = new Repeat<bool> { Value = true };
                Console.WriteLine(Take<bool, Repeat<bool>>(ref flags));
                Console.WriteLine(flags.Calls);

                Console.WriteLine(SidesOf<Triangle>() * 10 + SidesOf<Square>());
                Console.WriteLine(Grow(new Length { Millimetres = 7 }));
            }
        }
        """;

    /// <summary>
    /// Generic structs and static classes: nested in each other, holding structs, in arrays, with static fields and a
    /// static constructor for each instance, a generic method of a generic struct; enums, TimeSpan and text as type
    /// arguments; a T printed, tested against null and put in printed text.
    /// </summary>
    public const string Types = """
        using System;

        enum Mode : byte { Off, Slow, Fast }

        struct Point
        {
            public int X, Y;
        }

        struct Pair<T>
        {
            public T First, Second;
            public Pair(T first, T second) { First = first; Second = second; }
            public void Swap() => (First, Second) = (Second, First);
            public Pair<U> With<U>(U first, U second) => new Pair<U>(first, second);
        }

        struct Holder<T>
        {
            public static T Value;
            public static int Made;
            static Holder() => Made = 10;
            public static void Set(T value) { Value = value; Made++; }
        }

        static class Cache<T>
        {
            public static T Last;
            public static int Count = 5;
            public static void Put(T value) { Last = value; Count++; }
        }

        struct Ring<T>
        {
            private T _a, _b, _c;
            private int _next;
            public int Count;

            public void Add(T value)
            {
                if (_next == 0) _a = value;
                else if (_next == 1) _b = value;
                else _c = value;
                _next = (_next + 1) % 3;
                if (Count < 3) Count++;
            }

            public T this[int index] => index == 0 ? _a : index == 1 ? _b : _c;
        }

        class Program
        {
            static void Print<T>(T value) => Console.WriteLine(value);
            static void Write<T>(T value) { Console.Write(value); Console.Write(" "); }
            static string Text<T>(T value) => value == null ? "null" : "value";
            static T Id<T>(T value) => value;
            static Pair<T> Twice<T>(T value) => new Pair<T>(value, value);
            static void Show<T>(Pair<T> pair) => Console.WriteLine($"[{pair.First}|{pair.Second}]");
            static T Mode<T>(T value) where T : struct, Enum => value;

            static void Main()
            {
                var pair = new Pair<int>(1, 2);
                pair.Swap();
                Show(pair);
                pair.Second = 9;
                Show(pair);
                var nested = new Pair<Pair<int>>(pair, Twice(5));
                nested.Swap();
                Console.WriteLine(nested.First.First + nested.Second.Second * 100);
                var deep = Twice(Twice(Twice(1.5f)));
                Console.WriteLine(deep.First.Second.First);
                Show(pair.With(true, false));
                Show(pair.With(0.25, -1.0));
                var points = new Pair<Point>(new Point { X = 1, Y = 2 }, new Point { X = 3, Y = 4 });
                points.Swap();
                Console.WriteLine(points.First.X * 10 + points.Second.Y);

                var pairs = new Pair<long>[3];
                for (int i = 0; i < pairs.Length; i++)
                {
                    pairs[i] = new Pair<long>(i, i * 1000000000000L);
                }

                pairs[1].Swap();
                Console.WriteLine(pairs[1].First + pairs[2].Second);

                Console.WriteLine(Holder<int>.Made);
                Holder<int>.Set(7);
                Holder<double>.Set(0.5);
                Holder<int>.Set(8);
                Console.WriteLine($"{Holder<int>.Value} {Holder<int>.Made} {Holder<double>.Value} {Holder<double>.Made}");
                Cache<byte>.Put(200);
                Cache<Point>.Put(new Point { X = 6 });
                Console.WriteLine($"{Cache<byte>.Last} {Cache<byte>.Count} {Cache<Point>.Last.X} {Cache<Point>.Count} {Cache<bool>.Count}");

                var ring = new Ring<Mode>();
                ring.Add(Program.Mode(global::Mode.Fast));
                ring.Add(global::Mode.Slow);
                ring.Add(global::Mode.Off);
                ring.Add(global::Mode.Slow);
                Console.WriteLine($"{ring.Count} {(int)ring[0]} {(int)ring[1]} {(int)ring[2]}");

                Console.WriteLine(Id(TimeSpan.FromSeconds(1.5)).TotalMilliseconds);
                Console.WriteLine(Id("fixed text"));
                Console.WriteLine(Text(3));
                Console.WriteLine(Text("x"));
                Console.WriteLine(Text<string>(null));

                Print(42);
                Print(-7L);
                Print(3000000000u);
                Print(ulong.MaxValue);
                Print((byte)200);
                Print((sbyte)-5);
                Print((short)-300);
                Print((ushort)65535);
                Print(true);
                Print(1.25f);
                Print(-0.1);
                Print("text");
                Write(1);
                Write(false);
                Write(2.5);
                Console.WriteLine();
            }
        }
        """;

    /// <summary>Every program, by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Methods), Methods),
        (nameof(Math), Math),
        (nameof(Interfaces), Interfaces),
        (nameof(Types), Types),
    ];

    public static string Source(string name) => All.Single(program => program.Name == name).Source;

    /// <summary>Each program's name with false (compiled in Release) and true (in Debug).</summary>
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

    /// <summary>Compiles a program in Release, or in Debug, and reads it back.</summary>
    public static IlAssembly Compile(string name, bool debug, IEnumerable<string>? references = null) =>
        IlAssembly.Load(CSharpCompiler.Compile([Source(name)], references ?? [], name, OutputKind.ConsoleApplication,
            debug ? OptimizationLevel.Debug : OptimizationLevel.Release), name + ".dll");

    /// <summary>
    /// "pico-without-heap": a Pico whose target file says it has no heap, like the chips that have none (it keeps its
    /// console, UART0, so the simulator shows what it prints), written into <paramref name="scratch"/>.
    /// </summary>
    public static TargetDescription PicoWithoutHeap(string scratch) =>
        new TargetLoader(TargetFiles.WithShipped(Path.Combine(scratch, "targets"),
            ("pico-without-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }""")))
            .Load("pico-without-heap");
}
