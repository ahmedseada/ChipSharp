using ChipSharp.Frontend.Analysis;
using ChipSharp.Targets;
using ChipSharp.Tests.Heap;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.Memory;

/// <summary>
/// Everything that makes objects at once, in a long deterministic program, in a 16 KB heap with the heap verifier on:
/// classes with virtual calls and interfaces, a generic class over an interface and over int (growing its array with
/// Array.Resize), strings built, split, sorted with a comparison and changed, delegates (closures that capture locals of
/// Main and a loop variable, an event with two subscribers). It prints what .NET prints, on the PC and the Pico, while
/// the collector runs hundreds of times and checks the whole heap before and after each.
/// </summary>
public sealed class MixedStressTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
    private TargetLoader? _targets;

    private TargetLoader Targets => _targets ??= HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K");

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public const string Program = """
        using System;

        interface IShape
        {
            double Area();

            string Name { get; }
        }

        abstract class Shape : IShape
        {
            public abstract double Area();

            public virtual string Name => "shape";
        }

        sealed class Rect : Shape
        {
            public int W;
            public int H;

            public Rect(int w, int h)
            {
                W = w;
                H = h;
            }

            public override double Area() => W * H;

            public override string Name => "rect " + W + "x" + H;
        }

        sealed class Circle : Shape
        {
            public int R;

            public Circle(int r) => R = r;

            public override double Area() => 3 * R * R;

            public override string Name => $"circle {R}";
        }

        class Pile<T>
        {
            private T[] items = new T[2];
            private int count;

            public int Count => count;

            public void Push(T item)
            {
                if (count == items.Length)
                {
                    Array.Resize(ref items, count * 2);
                }

                items[count++] = item;
            }

            public T Pop()
            {
                T item = items[--count];
                items[count] = default;
                return item;
            }
        }

        class Bus
        {
            public event Action<string> Message;

            public void Send(string text) => Message?.Invoke(text);
        }

        static class Program
        {
            static uint seed = 7;

            static int Random(int n)
            {
                seed = seed * 1103515245 + 12345;
                return (int)((seed >> 16) % (uint)n);
            }

            static Func<int, int> Adder(int k) => x => x + k;

            static void Main()
            {
                long check = 0;
                var log = new string[8];
                int logged = 0;
                int lengths = 0;
                var bus = new Bus();
                bus.Message += text =>
                {
                    log[logged % log.Length] = text;
                    logged++;
                };
                bus.Message += text => lengths += text.Length;
                for (int round = 0; round < 300; round++)
                {
                    var shapes = new Pile<IShape>();
                    var numbers = new Pile<int>();
                    int count = 5 + Random(20);
                    for (int i = 0; i < count; i++)
                    {
                        IShape shape = Random(2) == 0 ? new Rect(1 + Random(9), 1 + Random(9)) : new Circle(1 + Random(5));
                        shapes.Push(shape);
                        numbers.Push(Random(1000));
                    }

                    var adders = new Func<int, int>[4];
                    for (int i = 0; i < adders.Length; i++)
                    {
                        adders[i] = Adder(i * round);
                    }

                    double area = 0;
                    string names = "";
                    while (shapes.Count > 0)
                    {
                        var shape = shapes.Pop();
                        area += shape.Area();
                        if (names.Length < 60)
                        {
                            names += shape.Name + ";";
                        }

                        check += adders[numbers.Pop() % 4](1);
                    }

                    var words = names.Split(';');
                    Array.Sort(words, (a, b) => string.CompareOrdinal(a, b));
                    bus.Send(words[words.Length - 1].ToUpper() + " " + (int)area);
                    check = check * 31 + (long)area + words.Length;
                    if (round % 30 == 0)
                    {
                        Console.WriteLine($"{round}: {log[(logged - 1) % log.Length]} {check}");
                    }
                }

                Console.WriteLine(logged);
                Console.WriteLine(lengths);
                Console.WriteLine(check);
            }
        }
        """;

    [HostFact]
    public async Task On_the_PC_in_a_16_KB_heap_with_the_verifier_on_it_prints_what_dotnet_prints()
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(Program, "Mixed"), "Mixed",
            Path.Combine(_scratch, "dotnet"));
        var host = Targets.Load("host-16K");
        var module = Lowered.Lower(WholeProgramPass.Analyze(Lowered.Compile(Program, "Mixed"), host));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), host, verifyHeap: true);
        output.WriteLine($"PC, 16 KB heap, verifier on: {result.Time.TotalMilliseconds:0} ms\n{result}");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    [SimulatorFact]
    public async Task On_the_Pico_in_a_16_KB_heap_with_the_verifier_on_it_prints_what_dotnet_prints()
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(Program, "Mixed"), "Mixed",
            Path.Combine(_scratch, "dotnet"));
        var pico = Targets.Load("pico-16K");
        var module = Lowered.Lower(WholeProgramPass.Analyze(Lowered.Compile(Program, "Mixed"), pico));

        var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, "pico"), pico, 120_000,
            verifyHeap: true);
        output.WriteLine($"Pico, 16 KB heap, verifier on: {result.Time.TotalMilliseconds:0} ms simulated\n{result}");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(dotnet.Text.ReplaceLineEndings("\r\n"), result.Raw);
    }
}
