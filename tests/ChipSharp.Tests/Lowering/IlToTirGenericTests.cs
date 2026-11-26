using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Generics in TIR: one function for each instance of a generic method (and struct for each instance of a generic
/// struct), named as C# writes it, with readable symbols; a constrained call a direct call of the struct's method, and
/// generic math the operation itself.
/// </summary>
public sealed class IlToTirGenericTests
{
    private const string Source = """
        using System;
        using System.Numerics;

        interface IDevice
        {
            void Tick();
        }

        struct Counter : IDevice
        {
            public int Ticks;
            public void Tick() => Ticks++;
        }

        struct Blinker : IDevice
        {
            public bool On;
            void IDevice.Tick() => On = !On;
        }

        struct Pair<T>
        {
            public T First, Second;
            public void Swap() => (First, Second) = (Second, First);
        }

        class Program
        {
            static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;
            static T Add<T>(T a, T b) where T : INumber<T> => a + b;
            static bool AtMost<T>(T a, T b) where T : INumber<T> => a <= b;
            static void Run<T>(ref T device) where T : struct, IDevice => device.Tick();

            static void Main()
            {
                Console.WriteLine(Max(1, 2) + Max(3, 4));
                Console.WriteLine(Max(1.5f, 2.5f));
                Console.WriteLine(Add((byte)200, (byte)100));
                Console.WriteLine(AtMost(1.5, double.NaN));
                var counter = new Counter();
                Run(ref counter);
                var blinker = new Blinker();
                Run(ref blinker);
                var pair = new Pair<long> { First = 1, Second = 2 };
                pair.Swap();
                Console.WriteLine(pair.First);
            }
        }
        """;

    [Fact]
    public void Each_instance_is_one_function_and_the_generic_code_none()
    {
        var module = Module();

        string[] instances =
        [
            "Program.Max<int>", "Program.Max<float>", "Program.Add<byte>", "Program.AtMost<double>",
            "Program.Run<Counter>", "Program.Run<Blinker>", "Pair<long>.Swap",
        ];
        Assert.All(instances, name => Assert.Single(module.Functions, function => function.Name == name));
        Assert.DoesNotContain(module.Functions, function => function.Name is "Program.Max" or "Pair`1.Swap"
            || function.Name.Contains("!!", StringComparison.Ordinal));
    }

    [Fact]
    public void A_constrained_call_on_a_struct_is_a_direct_call_of_its_method()
    {
        var module = Module();

        Assert.Contains("call void Counter.Tick(%device)", Print(module, "Program.Run<Counter>"), StringComparison.Ordinal);
        Assert.Contains("call void Blinker.IDevice.Tick(%device)", Print(module, "Program.Run<Blinker>"),
            StringComparison.Ordinal);
        Assert.Contains("call i32 ChipSharp.Library.Numbers.CompareTo(", Print(module, "Program.Max<int>"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_math_is_the_operation_itself()
    {
        var module = Module();

        var add = Print(module, "Program.Add<byte>");
        Assert.Contains("add i32", add, StringComparison.Ordinal);
        Assert.Contains("convert i32", add, StringComparison.Ordinal);
        Assert.DoesNotContain("call", add, StringComparison.Ordinal);
        Assert.Contains("ugt f64 %a, %b", Print(module, "Program.AtMost<double>"), StringComparison.Ordinal);
    }

    [Fact]
    public void Instances_have_readable_symbols()
    {
        var llvm = Assert.Single(new LlvmCodeGenerator().Generate(Module(), Llvm.LlvmTargets.Host).Files).Content;

        Assert.Contains("define internal i32 @Program_Max_int_(", llvm, StringComparison.Ordinal);
        Assert.Contains("define internal float @Program_Max_float_(", llvm, StringComparison.Ordinal);
        Assert.Contains("define internal void @Pair_long__Swap(", llvm, StringComparison.Ordinal);
        Assert.Contains("define internal void @Program_Run_Counter_(", llvm, StringComparison.Ordinal);
        Assert.Contains("%struct.Pair_long_ = type { i64, i64 }", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_instance_of_a_generic_struct_is_one_TIR_struct()
    {
        var program = WholeProgramPass.Analyze(IlAssembly.Load(CSharpCompiler.Compile("""
            struct Pair<T>
            {
                public T First, Second;
            }

            class Program
            {
                static Pair<T> Make<T>(T value) => new Pair<T> { First = value, Second = value };

                static void Main()
                {
                    System.Console.WriteLine(Make(1).First + Make(2).Second);
                    System.Console.WriteLine(Make(0.5).First);
                }
            }
            """), "Test.dll"));
        var module = Lowered.Lower(program);

        var ints = module.Functions.Single(function => function.Name == "Program.Make<int>").ReturnType;
        var doubles = module.Functions.Single(function => function.Name == "Program.Make<double>").ReturnType;
        var pairOfInts = Assert.IsType<TirStructType>(ints);
        Assert.Equal("Pair<int>", pairOfInts.Name);
        Assert.Equal([TirType.I32, TirType.I32], pairOfInts.Fields.Select(field => field.Type));
        Assert.Equal([TirType.F64, TirType.F64], Assert.IsType<TirStructType>(doubles).Fields.Select(field => field.Type));
        Assert.NotSame(ints, doubles);
    }

    private static TirModule Module() =>
        Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(CSharpCompiler.Compile(Source), "Test.dll")));

    private static string Print(TirModule module, string function) =>
        TirPrinter.Print(module.Functions.Single(f => f.Name == function));
}
