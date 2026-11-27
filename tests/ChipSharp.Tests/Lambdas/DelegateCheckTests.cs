using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Analysis;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Lambdas;

/// <summary>
/// Delegates in the whole-program pass: what it reaches through them, the rules of interrupt handlers (a handler may
/// invoke a delegate, never make one), what a target without a heap says, and what is rejected, each in C# words.
/// </summary>
public sealed class DelegateCheckTests : IDisposable
{
    private const string AllocationRule = "an interrupt handler cannot create objects (new ...). The heap may be in the " +
        "middle of an allocation or a collection when the interrupt comes: create what the handler needs before, e.g. in " +
        "a static field.";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>A Pico without a heap ("pico-no-heap"), and the shipped targets.</summary>
    private TargetLoader Targets() => new(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
        ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }""")));

    private static IlAssembly Load(string source, bool debug = false) => IlAssembly.Load(
        CSharpCompiler.Compile([source], [Pico.PicoBuild.HardwareAssembly], "Test", OutputKind.ConsoleApplication,
            debug ? OptimizationLevel.Debug : OptimizationLevel.Release), "Test.dll");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_methods_delegates_call_are_part_of_the_program_and_the_delegate_types_methods_are_not(bool debug)
    {
        var analysis = WholeProgramPass.Check(Load("""
            using System;

            delegate int Op(int a, int b);

            class Shape
            {
                public virtual int Sides() => 0;
            }

            class Square : Shape
            {
                public override int Sides() => 4;
            }

            class Circle : Shape
            {
                public override int Sides() => 1;
            }

            static class Program
            {
                static int Add(int a, int b) => a + b;

                static int Unused(int a, int b) => a * b;

                static void Main()
                {
                    Op op = Add;
                    Shape shape = new Square();
                    Func<int> sides = shape.Sides;
                    Func<int, int> twice = x => x * 2;
                    Console.WriteLine(op(1, 2) + sides() + twice(3));
                }
            }
            """, debug));

        Assert.True(analysis.IsSupported, analysis.Details);
        var methods = analysis.Methods.Select(method => method.DisplayName).ToList();
        Assert.Contains("Program.Add", methods);
        Assert.Contains("Square.Sides", methods);
        Assert.Contains("Program+<>c.<Main>b__2_0", methods);
        Assert.DoesNotContain("Program.Unused", methods);
        Assert.DoesNotContain("Circle.Sides", methods);
        Assert.DoesNotContain(methods, name => name.StartsWith("Op.", StringComparison.Ordinal));
    }

    [Fact]
    public void An_interrupt_handler_may_invoke_a_delegate_kept_in_a_static_field()
    {
        var analysis = WholeProgramPass.Check(Load("""
            using System;
            using ChipSharp.Hardware;

            partial class Program
            {
                static volatile Action onTick;
                static volatile int ticks;

                static void Main()
                {
                    onTick = () => ticks++;
                    onTick += Count;
                    Timing.EveryMilliseconds(1);
                }

                static void Count() => ticks += 10;

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    onTick?.Invoke();
                    if (onTick == null)
                    {
                        Pin.Toggle(25);
                    }
                }
            }
            """), Targets().Load("pico"));

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    /// <summary>
    /// A handler that makes a delegate (a lambda C# caches in a static field too), adds one to another (+=), or invokes a
    /// delegate whose method makes an object breaks the rule, where it does so.
    /// </summary>
    [Fact]
    public void An_interrupt_handler_that_makes_a_delegate_or_runs_one_that_makes_an_object_breaks_the_rule()
    {
        var problems = WholeProgramPass.Check(Load("""
            using System;
            using ChipSharp.Hardware;

            class Reading
            {
                public int Value;
            }

            partial class Program
            {
                static volatile Action onTick;
                static volatile Func<Reading> read;
                static volatile int ticks;

                static void Main()
                {
                    read = () => new Reading();
                    Timing.EveryMilliseconds(1);
                }

                [Interrupt(Event.Timer)]
                static void Tick()
                {
                    onTick = () => ticks++;
                    onTick += onTick;
                    ticks += read().Value;
                }
            }
            """), Targets().Load("pico")).Problems.Select(problem => problem.Line).ToList();

        Assert.Equal(
        [
            $"Program.Tick() at IL_0014 (newobj): {AllocationRule}",
            $"Program.Tick() at IL_0034 (call): {AllocationRule}",
            $"Program+<>c.<Main>b__3_0() at IL_0000 (newobj): {AllocationRule}",
        ], problems);
    }

    /// <summary>
    /// On a target without a heap a delegate, C#'s object of a closure (or of the lambdas that capture nothing) and +
    /// need the heap, said as such; a local function whose variables are in a struct needs none.
    /// </summary>
    [Fact]
    public void A_target_without_a_heap_says_delegates_and_closures_need_one_and_local_functions_compile()
    {
        var target = Targets().Load("pico-no-heap");
        var problems = WholeProgramPass.Check(Load("""
            using System;

            static class Program
            {
                static void Main()
                {
                    var offset = 3;
                    Func<int, int> shifted = x => x + offset;
                    Console.WriteLine(shifted(1));
                }
            }
            """), target).Problems.Select(problem => problem.Line).ToList();
        var local = WholeProgramPass.Check(Load("""
            using System;

            static class Program
            {
                static void Main()
                {
                    var offset = 3;
                    var total = 0;
                    void Add(int x) => total += x + offset;
                    Add(1);
                    Add(2);
                    Console.WriteLine(total);
                }
            }
            """), target);

        const string Heapless = "need a heap, and the target 'pico-no-heap' (264 KB RAM) has no heap.";
        Assert.Equal(
        [
            "Program.Main() at IL_0000 (newobj): lambdas and the variables they capture (closures) " + Heapless +
                " Pass what the method needs as arguments, or use a local function that is called directly.",
            "Program.Main() at IL_0012 (newobj): delegates and lambdas (each delegate is an object) " + Heapless +
                " Call the method directly, or choose it with a switch.",
        ], problems);
        Assert.True(local.IsSupported, local.Details);
    }

    [Fact]
    public void Function_pointers_delegates_of_arrays_and_BeginInvoke_are_rejected_and_one_of_a_struct_method_compiles()
    {
        var problems = WholeProgram.Problems("""
            using System;

            struct Point
            {
                public int X;

                public int Get() => X;
            }

            static class Program
            {
                static int Twice(int x) => x * 2;

                static unsafe void Main()
                {
                    delegate*<int, int> pointer = &Twice;
                    Console.WriteLine(pointer(2));
                    Action<int[]> print = values => Console.WriteLine(values.Length);
                    print(new int[2]);
                    var point = new Point();
                    Func<int> get = point.Get;
                    Console.WriteLine(get());
                }
            }
            """);

        Assert.Contains(problems, line => line.EndsWith("(ldftn): function pointers (delegate*, &Method) arrive later. " +
            "For now, use a delegate (Func<int, int>, Action), or call the method directly.", StringComparison.Ordinal));
        Assert.Contains(problems, line => line.Contains("delegates that take or return arrays or spans " +
            "(Action<int[]>, Func<Span<int>>) arrive later.", StringComparison.Ordinal));
        // a delegate of a struct's method runs on the box it is made of (Boxing.BoxingPrograms.Interfaces)
        Assert.DoesNotContain(problems, line => line.Contains("delegates of a method of a struct", StringComparison.Ordinal));
    }
}
