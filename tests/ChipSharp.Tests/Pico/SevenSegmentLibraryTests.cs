using System.Reflection;
using System.Runtime.CompilerServices;
using ChipSharp.Backend.Llvm;
using ChipSharp.Components;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// ChipSharp.Components' SevenSegment: a struct of 9 bytes with short names; the shapes of every digit and char, drawn
/// and compared with what they should look like; plain C# over ChipSharp.Hardware's Pin, which on .NET reaches the
/// chip-only Pin, and which ChipSharp compiles with a program that uses it, only what it calls, with its shapes as
/// constant data, no heap and no runtime function of its own, also for a chip without a heap; and nothing of it in a
/// program that does not use it. (<see cref="SevenSegmentSimulatorTests"/> runs it.)
/// </summary>
public sealed class SevenSegmentLibraryTests : IDisposable
{
    /// <summary>
    /// The shapes of 0 to 9, A b C d E F, '-' and '_', as a display shows them (A on top, G in the middle), each three
    /// columns wide; the rows without their trailing spaces.
    /// </summary>
    private static readonly string[] Drawn =
    [
        " _     _  _     _  _  _  _  _  _     _     _  _",
        "| |  | _| _||_||_ |_   ||_||_||_||_ |   _||_ |_  _",
        "|_|  ||_  _|  | _||_|  ||_| _|| ||_||_ |_||_ |      _",
    ];

    private const string Chars = "0123456789AbCdEF-_";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void SevenSegment_is_a_readonly_struct_of_9_bytes_that_shows_writes_points_blanks_and_gives_patterns()
    {
        var type = typeof(SevenSegment);
        Assert.Equal("ChipSharp.Components", type.Namespace);
        Assert.True(type.IsValueType);
        Assert.NotNull(type.GetCustomAttribute<IsReadOnlyAttribute>());
        Assert.Equal(9, Unsafe.SizeOf<SevenSegment>());

        static string Signature(MethodBase method) =>
            $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}" +
                (p.HasDefaultValue ? $" = {p.DefaultValue}" : "")))})";
        Assert.Equal(
            [
                ".ctor(Int32 a, Int32 b, Int32 c, Int32 d, Int32 e, Int32 f, Int32 g, Int32 dp = -1, Boolean commonAnode = False)",
                ".ctor(Int32 firstPin, Boolean commonAnode = False)",
            ],
            type.GetConstructors().Select(Signature).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["Blank()", "Point(Boolean on)", "Show(Char character)", "Show(Int32 digit)", "Write(Int32 segments)"],
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(Signature)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["Pattern(Char character)", "Pattern(Int32 digit)"],
            type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(Signature)
                .Order(StringComparer.Ordinal));
        Assert.All(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            m => Assert.Equal(typeof(byte), m.ReturnType));
        Assert.Empty(type.GetProperties());
    }

    [Fact]
    public void Each_digit_and_char_has_the_shape_a_display_shows_with_bit_0_for_A_to_bit_6_for_G()
    {
        Assert.Equal(Drawn, Draw(Chars.Select(SevenSegment.Pattern)));

        // the digits, as the traffic lights' own table has them; a char as its digit
        byte[] digits = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];
        Assert.Equal(digits, Enumerable.Range(0, 10).Select(SevenSegment.Pattern));
        Assert.Equal(digits, "0123456789".Select(SevenSegment.Pattern));
        Assert.Equal([0x77, 0x7C, 0x39, 0x5E, 0x79, 0x71], "ABCDEF".Select(SevenSegment.Pattern));
        Assert.Equal("ABCDEF".Select(SevenSegment.Pattern), "abcdef".Select(SevenSegment.Pattern));
        Assert.Equal([0x40, 0x08, 0x00], "-_ ".Select(SevenSegment.Pattern));
    }

    [Fact]
    public void A_digit_outside_0_to_9_or_a_char_with_no_shape_shows_as_a_dash()
    {
        Assert.All(new[] { -1, 10, 15, 16, 100, -10, int.MinValue, int.MaxValue },
            digit => Assert.Equal(0x40, SevenSegment.Pattern(digit)));

        // every char there is: the 24 it has a shape of its own for, and '-' for '-' and the rest
        const string Shaped = "0123456789ABCDEFabcdef_ ";
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var character = (char)code;
            Assert.True(SevenSegment.Pattern(character) == 0x40 != Shaped.Contains(character, StringComparison.Ordinal),
                $"'{character}' (U+{code:X4}) shows as 0x{SevenSegment.Pattern(character):X2}");
        }
    }

    [Fact]
    public void On_dotnet_Pattern_runs_and_a_display_reaches_Pin_which_runs_on_a_chip()
    {
        Assert.Equal(0x07, SevenSegment.Pattern(7));
        Assert.StartsWith("ChipSharp.Hardware.Pin.Output runs on a chip, not on .NET",
            Assert.Throws<PlatformNotSupportedException>(() => new SevenSegment(firstPin: 2)).Message);
        Assert.StartsWith("ChipSharp.Hardware.Pin.Write runs on a chip, not on .NET",
            Assert.Throws<PlatformNotSupportedException>(() => default(SevenSegment).Show(8)).Message);
    }

    [Fact]
    public void In_a_program_it_is_compiled_with_it_over_Pin_only_what_it_calls_its_shapes_constant_data_with_no_heap()
    {
        var (program, module) = Lower("""
            using ChipSharp.Components;

            class Program
            {
                static void Main()
                {
                    var display = new SevenSegment(firstPin: 2);
                    display.Show('7');
                    display.Write(SevenSegment.Pattern(8) | 0x80);
                }
            }
            """);

        var methods = program.Methods.Where(method => method.DeclaringType.FullName == "ChipSharp.Components.SevenSegment")
            .Select(method => method.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { ".ctor", "Show", "Write", "Pattern", "Segments", "Lit", "Stored", "Blank" },
            methods);
        Assert.DoesNotContain("Point", methods);

        // Pin's two runtime functions and none of its own (the others stop a program that indexes a span wrongly, which
        // it never does: the C compiler drops them); the shapes a constant table of 16 bytes
        var externals = module.Functions.Where(f => f.Kind == TirFunctionKind.External).Select(f => f.Name).ToList();
        Assert.Equal(["ChipSharp.Runtime.PinOutput", "ChipSharp.Runtime.PinWrite"],
            externals.Where(name => name.StartsWith("ChipSharp.Runtime.Pin", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.All(externals.Where(name => !name.StartsWith("ChipSharp.Runtime.Pin", StringComparison.Ordinal)),
            name => Assert.EndsWith("OutOfRange", name));
        var shapes = Assert.Single(module.Globals, global => global.Elements is { Count: 16 });
        Assert.True(shapes.IsConstant);
        Assert.Equal(Chars[..16].Select(SevenSegment.Pattern), shapes.Elements!.Select(element => (byte)element.IntegerValue));
        Assert.False(module.UsesHeap);
    }

    [Fact]
    public void A_program_that_does_not_use_it_has_none_of_it()
    {
        var (program, module) = Lower("""
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Pin.Output(2);
                    Pin.Write(2, true);
                }
            }
            """);

        Assert.DoesNotContain(program.Methods, method => method.DeclaringType.FullName.StartsWith("ChipSharp.Components.",
            StringComparison.Ordinal));
        Assert.Empty(module.Globals);
    }

    [Fact]
    public void On_a_chip_without_a_heap_several_displays_compile_as_structs_on_the_stack_and_in_static_fields()
    {
        var tiny = new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name);
        var source = """
            using ChipSharp.Components;

            class Program
            {
                static SevenSegment tens;

                static void Main()
                {
                    tens = new SevenSegment(firstPin: 2, commonAnode: true);
                    var ones = new SevenSegment(a: 10, b: 11, c: 12, d: 13, e: 14, f: 15, g: 16, dp: 17);
                    for (var count = 0; count < 100; count++)
                    {
                        Show(count, ones);
                    }
                }

                static void Show(int count, SevenSegment ones)
                {
                    tens.Show(count / 10);
                    ones.Show(count % 10);
                    ones.Point(count % 2 == 0);
                    if (count == 99)
                    {
                        tens.Show('-');
                        ones.Blank();
                    }
                }
            }
            """;
        var assembly = Load(source);

        Assert.Empty(WholeProgramPass.Check(assembly, tiny).Problems);
        var module = IlToTir.Lower(WholeProgramPass.Analyze(assembly, tiny));
        Assert.False(module.UsesHeap);
        var llvm = string.Concat(new LlvmCodeGenerator().Generate(module, tiny.WithoutHeap()).Files.Select(file => file.Content));
        Assert.Contains("ChipSharp_Runtime_PinWrite", llvm);
        Assert.DoesNotContain("chipsharp_new_object", llvm);
    }

    /// <summary>
    /// Draws <paramref name="patterns"/> side by side, three columns each: A, then F G B, then E D C; the rows without
    /// their trailing spaces.
    /// </summary>
    private static string[] Draw(IEnumerable<byte> patterns)
    {
        string[] rows = ["", "", ""];
        foreach (var pattern in patterns)
        {
            char Lit(int bit, char shape) => (pattern & (1 << bit)) != 0 ? shape : ' ';
            rows[0] += $" {Lit(0, '_')} ";
            rows[1] += $"{Lit(5, '|')}{Lit(6, '_')}{Lit(1, '|')}";
            rows[2] += $"{Lit(4, '|')}{Lit(3, '_')}{Lit(2, '|')}";
        }

        return [.. rows.Select(row => row.TrimEnd())];
    }

    /// <summary>The program in <paramref name="source"/>, with ChipSharp.Components, loaded for the whole-program pass.</summary>
    private static IlAssembly Load(string source)
    {
        var bytes = CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly, PicoBuild.ComponentsAssembly], "Display");
        return IlAssembly.Load(bytes, "Display.dll", [IlAssembly.Load(PicoBuild.ComponentsAssembly)]);
    }

    private static (IlProgram Program, TirModule Module) Lower(string source)
    {
        var program = WholeProgramPass.Analyze(Load(source));
        return (program, IlToTir.Lower(program));
    }
}
