using System.Text;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Image.Uf2;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Library;

/// <summary>
/// A program whose code is split into class libraries of its own, as <c>dotnet build</c> leaves them: the program's
/// .dll with the libraries it references next to it, and the libraries they reference. ChipSharp reads them with the
/// program (<see cref="IlAssembly.Libraries"/>) and compiles their C# with it, as if it were the program's: structs,
/// enums, static fields and their initializers, constant data, classes, generics and virtual methods, on the PC and on
/// the Pico, printing what .NET prints; and only what the program calls, so that a library, or a driver of
/// ChipSharp.Components, adds nothing for what the program does not use.
/// </summary>
public sealed class ProgramLibraryTests : IDisposable
{
    /// <summary>A library that another library uses: the program does not reference it itself.</summary>
    private const string Units = """
        namespace Units;

        public static class Scale
        {
            public static int Twice(int value) => 2 * value;

            public static int Thrice(int value) => 3 * value;
        }
        """;

    /// <summary>The program's library: a struct, an enum, static fields, constant data, classes and generics.</summary>
    private const string Shapes = """
        using System;
        using Units;

        namespace Shapes;

        public enum Kind { Square, Circle }

        public struct Size
        {
            public int Width;
            public int Height;

            public Size(int width, int height)
            {
                Width = width;
                Height = height;
            }

            public int Area => Width * Height;
        }

        public static class Geometry
        {
            static int calls;
            public static readonly int Sides = 4;

            static ReadOnlySpan<byte> Table => new byte[] { 1, 2, 3, 5, 8 };

            public static int Fib(int i) => Table[i];

            public static int Perimeter(Size size)
            {
                calls++;
                return Scale.Twice(size.Width + size.Height);
            }

            public static int Calls => calls;

            public static string Name(Kind kind) => kind == Kind.Square ? "square" : "circle";

            public static long NeverCalled(long x) => x * x * x + Scale.Thrice((int)x);
        }

        public class Counter
        {
            int total;

            public void Add(int value) => total += value;

            public int Total => total;
        }

        public struct Pair<T>
        {
            public T First;
            public T Second;

            public Pair(T first, T second)
            {
                First = first;
                Second = second;
            }
        }

        public static class Pick
        {
            public static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;
        }

        public abstract class Shape
        {
            public abstract int Area();

            public virtual string Name => "shape";
        }

        public sealed class Square(int side) : Shape
        {
            public override int Area() => side * side;

            public override string Name => "square";
        }
        """;

    private const string App = """
        using System;
        using System.Collections.Generic;
        using Shapes;

        var size = new Size(3, 4);
        Console.WriteLine($"{size.Area} {Geometry.Perimeter(size)} {Geometry.Fib(4)} {Geometry.Name(Kind.Circle)} {Geometry.Calls} {Geometry.Sides}");
        var counter = new Counter();
        counter.Add(5);
        counter.Add(7);
        Console.WriteLine(counter.Total);
        var pair = new Pair<int>(3, 9);
        Console.WriteLine($"{pair.First} {pair.Second} {Pick.Max(pair.First, pair.Second)} {Pick.Max(7L, -2L)}");
        Shape shape = new Square(5);
        Console.WriteLine($"{shape.Name} {shape.Area()}");
        var shapes = new List<Shape> { new Square(2), new Square(3) };
        var total = 0;
        foreach (var each in shapes)
        {
            total += each.Area();
        }

        Console.WriteLine(total);
        """;

    /// <summary>What .NET prints for <see cref="App"/>.</summary>
    private const string Printed = "12 14 8 circle 1 4\n12\n3 9 9 7\nsquare 25\n13\n";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void The_libraries_next_to_the_program_and_theirs_are_read_with_it_and_only_what_it_calls_is_compiled()
    {
        var assembly = IlAssembly.Load(BuildApp());

        var program = WholeProgramPass.Analyze(assembly);

        // Units, which only Shapes references, before Shapes; ChipSharp.Hardware and .NET's own are never read
        Assert.Equal(["Units", "Shapes"], assembly.Libraries.Select(library => library.Name));
        Assert.Equal(["Units"], assembly.Libraries[1].Libraries.Select(library => library.Name));

        // the libraries' types are the program's, and their methods its methods: those it reaches
        Assert.NotNull(assembly.FindType("Shapes.Size"));
        Assert.NotNull(assembly.FindType("Units.Scale"));
        var methods = program.Methods.Select(method => method.DisplayName).ToList();
        Assert.Contains("Shapes.Geometry.Perimeter", methods);
        Assert.Contains("Shapes.Geometry..cctor", methods);
        Assert.Contains("Units.Scale.Twice", methods);
        Assert.Contains("Shapes.Square.get_Name", methods);
        Assert.DoesNotContain("Shapes.Geometry.NeverCalled", methods);
        Assert.DoesNotContain("Units.Scale.Thrice", methods);
        Assert.DoesNotContain(methods, method => method.StartsWith("ChipSharp.Hardware", StringComparison.Ordinal));
    }

    [Fact]
    public void A_library_that_is_not_next_to_the_program_is_a_library_ChipSharp_does_not_have()
    {
        var app = BuildApp();
        File.Delete(Path.Combine(Path.GetDirectoryName(app)!, "Units.dll"));

        var assembly = IlAssembly.Load(app);
        var analysis = WholeProgramPass.Check(assembly, null);

        Assert.Equal(["Shapes"], assembly.Libraries.Select(library => library.Name));
        Assert.False(analysis.IsSupported);
        Assert.Contains("Units.Scale.Twice(int)", analysis.Details);
    }

    [HostFact]
    public async Task On_the_PC_the_program_and_its_libraries_print_what_dotnet_prints()
    {
        var module = IlToTir.Lower(WholeProgramPass.Analyze(IlAssembly.Load(BuildApp())));

        var run = await HostRun.BuildAndRunAsync(module, Path.Combine(_scratch, "host"));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Printed.ReplaceLineEndings("\r\n"), Encoding.UTF8.GetString(run.Output));
    }

    [SimulatorFact]
    public async Task On_the_Pico_the_program_and_its_libraries_print_what_dotnet_prints()
    {
        var module = IlToTir.Lower(WholeProgramPass.Analyze(IlAssembly.Load(BuildApp()), PicoBuild.Pico));
        var program = PicoBuild.Build(module, Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 500);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(Printed.ReplaceLineEndings("\r\n"), Encoding.UTF8.GetString(run.Uart0));
    }

    [PicoFact]
    public void A_driver_of_ChipSharp_Components_the_program_does_not_call_adds_nothing()
    {
        var plain = PicoBuild.Build(PicoBuild.BlinkSource, "Blink", Path.Combine(_scratch, "plain"));
        var withComponents = PicoBuild.Build(PicoBuild.BlinkSource, "Blink", Path.Combine(_scratch, "components"),
            PicoBuild.Pico, [PicoBuild.ComponentsAssembly]);

        var elf = ElfFile.Read(withComponents.Elf);
        Assert.DoesNotContain(elf.Symbols.Keys, symbol => symbol.Contains("Components", StringComparison.Ordinal));
        Assert.Equal(PicoBuild.FlashImage(ElfFile.Read(plain.Elf)), PicoBuild.FlashImage(elf));
    }

    /// <summary>
    /// Units.dll, Shapes.dll (which references it) and App.dll (which references Shapes) in one folder, as dotnet build
    /// leaves them; returns App.dll's path.
    /// </summary>
    private string BuildApp()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_scratch, "bin")).FullName;
        var units = Path.Combine(folder, "Units.dll");
        File.WriteAllBytes(units, CSharpCompiler.Compile([Units], "Units"));
        var shapes = Path.Combine(folder, "Shapes.dll");
        File.WriteAllBytes(shapes, CSharpCompiler.Compile([Shapes], [units], "Shapes"));
        var app = Path.Combine(folder, "App.dll");
        File.WriteAllBytes(app, CSharpCompiler.Compile([App], [shapes], "App"));
        return app;
    }
}
