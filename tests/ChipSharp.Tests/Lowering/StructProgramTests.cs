using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Programs with structs, enums and references, compiled by ChipSharp for the PC and run: each must print what the same
/// assembly prints on real .NET. The same programs run on the simulated Pico too (Pico/StructSimulatorTests).
/// </summary>
public sealed class StructProgramTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [HostFact]
    public Task A_copy_of_a_struct_changes_without_changing_the_original() => PrintsWhatDotnetPrints(StructPrograms.Copies);

    [HostFact]
    public Task Constructors_properties_and_methods_of_structs() => PrintsWhatDotnetPrints(StructPrograms.Members);

    [HostFact]
    public Task Nested_structs_are_copied_and_changed_in_place() => PrintsWhatDotnetPrints(StructPrograms.Nested);

    [HostFact]
    public Task Structs_are_passed_and_returned_by_value() => PrintsWhatDotnetPrints(StructPrograms.PassingAndReturning);

    [HostFact]
    public Task Ref_out_and_in_parameters_ref_locals_and_ref_returns() => PrintsWhatDotnetPrints(StructPrograms.References);

    [HostFact]
    public Task Enums_are_their_whole_numbers_flags_too() => PrintsWhatDotnetPrints(StructPrograms.Enums);

    [HostFact]
    public Task Struct_static_fields_keep_their_values_from_call_to_call() =>
        PrintsWhatDotnetPrints(StructPrograms.StaticFields);

    [HostFact]
    public Task Fields_of_every_size_keep_their_values_and_padding_changes_nothing() =>
        PrintsWhatDotnetPrints(StructPrograms.Layout);

    [HostFact]
    public Task A_large_struct_is_copied_whole() => PrintsWhatDotnetPrints(StructPrograms.Large);

    [HostFact]
    public async Task Every_program_compiled_in_Debug_prints_the_same()
    {
        foreach (var (name, source) in StructPrograms.All)
        {
            var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, "Program"), "Program",
                Path.Combine(_scratch, name, "dotnet"));
            var module = Lowered.Lower(WholeProgramPass.Analyze(Lowered.CompileDebug(source, "Program")));

            var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, name, "chipsharp"));

            Assert.True(dotnet.Output.SequenceEqual(chipSharp.Output), $"{name}:\n{dotnet}\n{chipSharp}");
        }
    }

    [Fact]
    public void Every_program_passes_the_check()
    {
        foreach (var (name, source) in StructPrograms.All)
        {
            var analysis = WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile(source, name), name + ".dll"));

            Assert.True(analysis.IsSupported, $"{name}:\n{analysis.Details}");
        }
    }

    /// <summary>Compiles <paramref name="source"/>, runs it on .NET and as a ChipSharp program, and compares.</summary>
    private async Task PrintsWhatDotnetPrints(string source)
    {
        var assembly = CSharpCompiler.Compile(source, "Program");
        var dotnet = await HostRun.RunOnDotnetAsync(assembly, "Program", Path.Combine(_scratch, "dotnet"));
        var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(assembly, "Program.dll")));

        var chipSharp = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "chipsharp"));

        Assert.NotEmpty(dotnet.Output);
        Assert.Equal(dotnet.Text, chipSharp.Text);
        Assert.Equal(dotnet.Output, chipSharp.Output);
        Assert.Equal(dotnet.ExitCode, chipSharp.ExitCode);
    }
}

/// <summary>C# programs with structs, enums and references, for the PC and the Pico tests.</summary>
internal static class StructPrograms
{
    /// <summary>Assigning, passing and returning a struct copies it; a method changes the struct it is called on.</summary>
    public const string Copies = """
        using System;

        struct Counter
        {
            public int Count;
            public void Increment() => Count++;
        }

        class Program
        {
            static void Bump(Counter counter) => counter.Increment();

            static Counter Bumped(Counter counter)
            {
                counter.Increment();
                return counter;
            }

            static void Main()
            {
                var a = new Counter();
                a.Increment();
                var b = a;
                b.Increment();
                b.Increment();
                Console.WriteLine(a.Count);
                Console.WriteLine(b.Count);
                Bump(a);
                Console.WriteLine(a.Count);
                var c = Bumped(a);
                Console.WriteLine(a.Count);
                Console.WriteLine(c.Count);
                a = c;
                c.Count = 100;
                Console.WriteLine(a.Count);
                Console.WriteLine(c.Count);
                Counter d = default;
                Console.WriteLine(d.Count);
                var e = a.Count > 1 ? a : c;
                e.Increment();
                Console.WriteLine(e.Count);
                Console.WriteLine(a.Count);
            }
        }
        """;

    /// <summary>Constructors (a parameterless one too), properties of every kind, methods that call each other, this = ....</summary>
    public const string Members = """
        using System;

        struct Temperature
        {
            private int _tenths;

            public Temperature(int degrees) => _tenths = degrees * 10;

            public int Degrees
            {
                readonly get => _tenths / 10;
                set => _tenths = value * 10;
            }

            public int Tenths { readonly get => _tenths; init => _tenths = value; }

            public void Warm(int degrees) => Degrees = Degrees + degrees;

            public void Reset() => this = new Temperature(20);

            public void Clear() => this = default;
        }

        struct Settings
        {
            public int Speed;
            public bool On;

            public Settings()
            {
                Speed = 5;
                On = true;
            }

            public Settings(int speed) : this() => Speed *= speed;

            public int Level { get; set; }

            public readonly int Score => On ? Speed * 10 + Level : -1;
        }

        readonly struct Range(int start, int end)
        {
            public int Start { get; } = start;
            public int End { get; } = end;
            public int Length => End - Start;

            public bool Contains(int value) => value >= Start && value < End;

            public Range Shifted(int by) => new Range(Start + by, End + by);
        }

        class Program
        {
            static void Main()
            {
                var t = new Temperature(21);
                Console.WriteLine(t.Degrees);
                t.Warm(4);
                Console.WriteLine(t.Degrees);
                t.Degrees = -3;
                Console.WriteLine(t.Tenths);
                t = new Temperature { Tenths = 375 };
                Console.WriteLine(t.Degrees);
                t.Reset();
                Console.WriteLine(t.Tenths);
                t.Clear();
                Console.WriteLine(t.Tenths);

                var s = new Settings();
                Console.WriteLine(s.Speed);
                Console.WriteLine(s.On);
                Console.WriteLine(s.Score);
                Settings d = default;
                Console.WriteLine(d.Speed);
                Console.WriteLine(d.On);
                Console.WriteLine(d.Score);
                var fast = new Settings(3) { Level = 7 };
                Console.WriteLine(fast.Score);
                fast.Level++;
                Console.WriteLine(fast.Level);

                var r = new Range(10, 15);
                Console.WriteLine(r.Length);
                Console.WriteLine(r.Contains(12));
                Console.WriteLine(r.Contains(15));
                var moved = r.Shifted(100).Shifted(-1);
                Console.WriteLine(moved.Start);
                Console.WriteLine(moved.End);
                Console.WriteLine(r.Start);
            }
        }
        """;

    /// <summary>Structs in structs: reached field by field, changed in place through a ref, and copied whole.</summary>
    public const string Nested = """
        using System;

        struct Point
        {
            public int X, Y;
            public Point(int x, int y) => (X, Y) = (x, y);
        }

        struct Line
        {
            public Point From, To;
            public int Width;

            public readonly int LengthSquared
            {
                get
                {
                    int dx = To.X - From.X, dy = To.Y - From.Y;
                    return dx * dx + dy * dy;
                }
            }
        }

        struct Shape
        {
            public Line Edge;
            public byte Sides;
        }

        class Program
        {
            static void Main()
            {
                var line = new Line { From = new Point(1, 1), To = new Point(4, 5), Width = 2 };
                Console.WriteLine(line.LengthSquared);
                line.To.X = 7;
                Console.WriteLine(line.LengthSquared);

                var shape = new Shape { Edge = line, Sides = 3 };
                shape.Edge.From.Y = -3;
                Console.WriteLine(line.From.Y);
                Console.WriteLine(shape.Edge.From.Y);
                Console.WriteLine(shape.Edge.LengthSquared);

                ref Point end = ref shape.Edge.To;
                end.X += 10;
                end = new Point(end.X * 2, end.Y);
                Console.WriteLine(shape.Edge.To.X);

                var copy = shape.Edge;
                copy.To.Y = 0;
                Console.WriteLine(shape.Edge.To.Y);
                Console.WriteLine(copy.To.Y);
                Console.WriteLine(shape.Sides + shape.Edge.Width);
            }
        }
        """;

    /// <summary>Structs as parameters and results, through recursion and as static method arguments.</summary>
    public const string PassingAndReturning = """
        using System;

        struct Fraction
        {
            public long Top, Bottom;

            public Fraction(long top, long bottom)
            {
                long a = top < 0 ? -top : top, b = bottom;
                while (b != 0)
                {
                    (a, b) = (b, a % b);
                }

                Top = top / a;
                Bottom = bottom / a;
            }

            public static Fraction operator +(Fraction x, Fraction y) =>
                new Fraction(x.Top * y.Bottom + y.Top * x.Bottom, x.Bottom * y.Bottom);

            public static Fraction operator *(Fraction x, Fraction y) => new Fraction(x.Top * y.Top, x.Bottom * y.Bottom);

            public static bool operator ==(Fraction x, Fraction y) => x.Top == y.Top && x.Bottom == y.Bottom;

            public static bool operator !=(Fraction x, Fraction y) => !(x == y);

            public override readonly bool Equals(object o) => false;

            public override readonly int GetHashCode() => 0;
        }

        class Program
        {
            static Fraction Harmonic(int n) => n == 1 ? new Fraction(1, 1) : Harmonic(n - 1) + new Fraction(1, n);

            static Fraction Power(Fraction x, int n) => n == 0 ? new Fraction(1, 1) : x * Power(x, n - 1);

            static void Print(Fraction f)
            {
                Console.WriteLine(f.Top);
                Console.WriteLine(f.Bottom);
            }

            static void Main()
            {
                Print(Harmonic(10));
                var half = new Fraction(2, 4);
                Print(Power(half, 5));
                Console.WriteLine(half + half == new Fraction(1, 1));
                Console.WriteLine(half != new Fraction(1, 2));
                Print(half);
            }
        }
        """;

    /// <summary>ref, out and in; ref locals, ref returns and a ref ?:; refs to bools and bytes, which are both one byte.</summary>
    public const string References = """
        using System;

        struct Pair
        {
            public int A, B;
            public bool Flag;
            public byte Small;
        }

        class Program
        {
            static int total;
            static bool ready;

            static void Swap(ref int x, ref int y) => (x, y) = (y, x);

            static void Swap(ref Pair x, ref Pair y) => (x, y) = (y, x);

            static bool TryDivide(int x, int y, out int quotient, out int remainder)
            {
                if (y == 0)
                {
                    quotient = remainder = 0;
                    return false;
                }

                quotient = x / y;
                remainder = x % y;
                return true;
            }

            static void Make(out Pair pair) => pair = new Pair { A = 1, B = 2, Flag = true, Small = 200 };

            static int Sum(in Pair pair) => pair.A + pair.B + pair.Small;

            static ref int Larger(ref int x, ref int y) => ref x >= y ? ref x : ref y;

            static ref int Total() => ref total;

            static ref readonly Pair Smaller(in Pair x, in Pair y) => ref x.A <= y.A ? ref x : ref y;

            static void Toggle(ref bool flag) => flag = !flag;

            static void Add(ref byte value, int amount) => value += (byte)amount;

            static void Twice(ref long value)
            {
                value *= 2;
            }

            static void Main()
            {
                int a = 1, b = 2;
                Swap(ref a, ref b);
                Console.WriteLine(a * 10 + b);

                Console.WriteLine(TryDivide(17, 5, out var q, out var r));
                Console.WriteLine(q);
                Console.WriteLine(r);
                Console.WriteLine(TryDivide(1, 0, out q, out _));
                Console.WriteLine(q);

                Make(out var pair);
                var other = new Pair { A = 10 };
                Swap(ref pair, ref other);
                Console.WriteLine(pair.A);
                Console.WriteLine(other.Small);
                Console.WriteLine(Sum(in other));
                Console.WriteLine(Sum(new Pair { B = 5 }));
                Console.WriteLine(Smaller(in pair, in other).Small);
                Console.WriteLine(Smaller(in other, new Pair { A = -1, B = 9 }).B);

                Larger(ref a, ref b) = 50;
                Console.WriteLine(a);
                Console.WriteLine(b);
                ref int larger = ref Larger(ref a, ref b);
                larger++;
                Console.WriteLine(b);

                Total() += 7;
                Total() *= 3;
                Console.WriteLine(total);

                Toggle(ref ready);
                Console.WriteLine(ready);
                Toggle(ref other.Flag);
                Console.WriteLine(other.Flag);
                bool local = false;
                ref bool chosen = ref a > 0 ? ref local : ref ready;
                chosen = true;
                Console.WriteLine(local);
                Toggle(ref chosen);
                Console.WriteLine(local);
                (b > 1000 ? ref local : ref ready) = false;
                Console.WriteLine(ready);

                Add(ref other.Small, 100);
                Console.WriteLine(other.Small);
                byte small = 250;
                ref byte which = ref b > 0 ? ref small : ref other.Small;
                which += 10;
                Console.WriteLine(small);

                long big = 3_000_000_000;
                Twice(ref big);
                Console.WriteLine(big);
            }
        }
        """;

    /// <summary>Enums of every underlying type, as values, fields and parameters; [Flags] with bit operations; switch.</summary>
    public const string Enums = """
        using System;

        enum Direction : byte { North, East, South, West }

        enum Level : sbyte { Low = -1, Normal = 0, High = 1 }

        enum Code : ushort { Ok = 200, NotFound = 404, Teapot = 418 }

        enum Big : long { Small = 1, Huge = 1L << 40 }

        enum Mask : ulong { Top = 1UL << 63 }

        [Flags]
        enum Permissions : uint
        {
            None = 0,
            Read = 1,
            Write = 2,
            Execute = 4,
            All = Read | Write | Execute,
            Sticky = 0x8000_0000,
        }

        struct Robot
        {
            public Direction Facing;
            public Level Power;
            public Permissions Rights;

            public void TurnRight() => Facing = (Direction)(((int)Facing + 1) % 4);
        }

        class Program
        {
            static Direction Opposite(Direction direction) => direction switch
            {
                Direction.North => Direction.South,
                Direction.East => Direction.West,
                Direction.South => Direction.North,
                _ => Direction.East,
            };

            static int Score(Code code)
            {
                switch (code)
                {
                    case Code.Ok:
                        return 1;
                    case Code.NotFound:
                        return 2;
                    default:
                        return (int)code;
                }
            }

            static void Main()
            {
                var robot = new Robot { Facing = Direction.West, Power = Level.Low, Rights = Permissions.Read };
                robot.TurnRight();
                Console.WriteLine((int)robot.Facing);
                Console.WriteLine((int)Opposite(robot.Facing));
                Console.WriteLine((int)robot.Power);
                Console.WriteLine(robot.Power < Level.Normal);

                robot.Rights |= Permissions.Write | Permissions.Sticky;
                Console.WriteLine((uint)robot.Rights);
                Console.WriteLine((robot.Rights & Permissions.Write) == Permissions.Write);
                Console.WriteLine((robot.Rights & Permissions.Execute) != 0);
                robot.Rights &= ~Permissions.Read;
                Console.WriteLine((uint)robot.Rights);
                Console.WriteLine((uint)(Permissions.All ^ robot.Rights));
                Console.WriteLine(robot.Rights > Permissions.All);

                Console.WriteLine(Score(Code.Ok) + Score(Code.NotFound) + Score(Code.Teapot));
                Console.WriteLine((long)Big.Huge + (long)Big.Small);
                Console.WriteLine((ulong)Mask.Top);
                Console.WriteLine((int)Wrapped(200));
                Console.WriteLine((int)(Direction)Wrapped(300));
            }

            static Level Wrapped(int value) => (Level)value;
        }
        """;

    /// <summary>Static fields of struct type: set up by an initializer, changed in place and through refs, call after call.</summary>
    public const string StaticFields = """
        using System;

        struct Stats
        {
            public int Count;
            public long Sum;
            public int Max;

            public void Add(int value)
            {
                Count++;
                Sum += value;
                if (value > Max)
                {
                    Max = value;
                }
            }
        }

        struct Origin
        {
            public static Origin Zero = new Origin(-5, 5);
            public static int Made;

            public int X, Y;

            static Origin()
            {
                Made = 100;
            }

            public Origin(int x, int y)
            {
                X = x;
                Y = y;
                Made++;
            }
        }

        class Program
        {
            static Stats stats;
            static Stats saved;

            static void Record(int value) => stats.Add(value);

            static void Add(ref Stats into, int value) => into.Add(value);

            static void Main()
            {
                for (int i = 1; i <= 5; i++)
                {
                    Record(i * i);
                }

                saved = stats;
                Add(ref stats, 1000);
                Console.WriteLine(stats.Count);
                Console.WriteLine(stats.Sum);
                Console.WriteLine(stats.Max);
                Console.WriteLine(saved.Count);
                Console.WriteLine(saved.Max);

                Console.WriteLine(Origin.Zero.X);
                Console.WriteLine(Origin.Made);
                var o = new Origin(1, 2);
                Console.WriteLine(Origin.Made);
                Origin.Zero.Y += o.Y;
                Console.WriteLine(Origin.Zero.Y);
            }
        }
        """;

    /// <summary>A struct with fields of every size, in an order that needs padding, read and written one by one.</summary>
    public const string Layout = """
        using System;

        enum Tiny : byte { A = 7 }

        struct Mixed
        {
            public byte B1;
            public long L;
            public bool Flag;
            public short S;
            public Tiny T;
            public int I;
            public sbyte SB;
            public nint N;
            public ushort US;
            public ulong UL;
            public bool Last;
        }

        struct Holder
        {
            public byte Before;
            public Mixed Inner;
            public byte After;
        }

        class Program
        {
            static Mixed Fill(int seed)
            {
                Mixed m;
                m.B1 = (byte)(seed + 250);
                m.L = -(long)seed * 1_000_000_007L;
                m.Flag = seed % 2 == 1;
                m.S = (short)(seed * -1000);
                m.T = Tiny.A;
                m.I = seed * 7;
                m.SB = (sbyte)(-seed);
                m.N = seed * -3;
                m.US = (ushort)(65_000 + seed);
                m.UL = ulong.MaxValue - (ulong)seed;
                m.Last = true;
                return m;
            }

            static void Print(in Mixed m)
            {
                Console.WriteLine(m.B1);
                Console.WriteLine(m.L);
                Console.WriteLine(m.Flag);
                Console.WriteLine(m.S);
                Console.WriteLine((int)m.T);
                Console.WriteLine(m.I);
                Console.WriteLine(m.SB);
                Console.WriteLine((long)m.N);
                Console.WriteLine(m.US);
                Console.WriteLine(m.UL);
                Console.WriteLine(m.Last);
            }

            static void Main()
            {
                var h = new Holder { Before = 1, Inner = Fill(9), After = 2 };
                Print(in h.Inner);
                h.Inner.Flag = false;
                h.Inner.S++;
                h.Inner.US += 600;
                h.Inner.SB--;
                Print(h.Inner);
                Console.WriteLine(h.Before);
                Console.WriteLine(h.After);
            }
        }
        """;

    /// <summary>
    /// A struct of 40 longs and a byte (328 bytes, with padding), cleared and copied through references chosen as it
    /// runs: LLVM makes calls to memset, memcpy and memmove of these, which the runtimes have (the programs have no C
    /// library).
    /// </summary>
    public const string Large = """
        using System;

        struct Block
        {
            public long A0, A1, A2, A3, A4, A5, A6, A7, A8, A9;
            public long B0, B1, B2, B3, B4, B5, B6, B7, B8, B9;
            public long C0, C1, C2, C3, C4, C5, C6, C7, C8, C9;
            public long D0, D1, D2, D3, D4, D5, D6, D7, D8, D9;
            public byte Tag;

            public readonly long Sum => A0 + A9 + B0 + B9 + C0 + C5 + D0 + D9 + Tag;
        }

        class Program
        {
            static Block first, second;

            static void Clear(ref Block block) => block = default;

            static void Copy(ref Block to, in Block from) => to = from;

            static Block Twice(Block block, int times)
            {
                for (int i = 0; i < times; i++)
                {
                    block.A0 += block.A0;
                    block.D9 += block.Tag;
                }

                return block;
            }

            static void Main()
            {
                long total = 0;
                for (int i = 0; i < 20; i++)
                {
                    first.A0 += i;
                    first.C5 = first.A0 * 3;
                    second.D9 += i * 2;
                    second.Tag += 3;
                    Clear(ref i % 3 == 0 ? ref first : ref second);
                    Copy(ref i % 4 == 0 ? ref first : ref second, in (i % 5 == 0 ? ref first : ref second));
                    total += first.Sum * 1000 + second.Sum;
                }

                Console.WriteLine(total);
                var copy = first;
                copy.B9 = 1;
                Console.WriteLine(copy.Sum - first.Sum);
                Console.WriteLine(second.Sum);
                Console.WriteLine(Twice(copy, 3).Sum);
            }
        }
        """;

    /// <summary>Every program above, by name.</summary>
    public static IReadOnlyList<(string Name, string Source)> All { get; } =
    [
        (nameof(Copies), Copies),
        (nameof(Members), Members),
        (nameof(Nested), Nested),
        (nameof(PassingAndReturning), PassingAndReturning),
        (nameof(References), References),
        (nameof(Enums), Enums),
        (nameof(StaticFields), StaticFields),
        (nameof(Layout), Layout),
        (nameof(Large), Large),
    ];
}
