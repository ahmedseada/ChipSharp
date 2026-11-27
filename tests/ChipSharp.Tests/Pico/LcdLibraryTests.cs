using System.Reflection;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Hardware;
using ChipSharp.Ir;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// ChipSharp.Hardware's Lcd: short names, ints, chars, strings and spans of chars, which run on a chip only; in a
/// program it is ChipSharp.Library's C#, over I2c.Write and Timing's waits, with no heap and no runtime function of its
/// own. (<see cref="LcdSimulatorTests"/> runs it.)
/// </summary>
public sealed class LcdLibraryTests
{
    private static IEnumerable<MethodInfo> LcdMethods =>
        typeof(Lcd).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

    [Fact]
    public void Lcd_opens_clears_writes_lines_and_lights_with_plain_names()
    {
        Assert.Equal(
            [
                "Backlight(Boolean on)", "Clear()", "Line(Int32 row, ReadOnlySpan`1 text)", "Line(Int32 row, String text)",
                "Open(Int32 bus, Int32 address, Int32 columns, Int32 rows)", "Write(Int32 column, Int32 row, Char character)",
                "Write(Int32 column, Int32 row, Int32 number)", "Write(Int32 column, Int32 row, ReadOnlySpan`1 text)",
                "Write(Int32 column, Int32 row, String text)",
            ],
            LcdMethods.Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
                .Order(StringComparer.Ordinal));

        // Open says whether the display answered; spans are of chars
        Assert.All(LcdMethods, m => Assert.Equal(m.Name == "Open" ? typeof(bool) : typeof(void), m.ReturnType));
        Assert.All(LcdMethods.SelectMany(m => m.GetParameters()).Where(p => p.ParameterType.IsGenericType),
            p => Assert.Equal(typeof(ReadOnlySpan<char>), p.ParameterType));
    }

    [Fact]
    public void On_dotnet_every_method_says_it_runs_on_a_chip()
    {
        (string Name, Action Call)[] calls =
        [
            ("Lcd.Open", () => Lcd.Open(1, 0x27, 16, 2)),
            ("Lcd.Clear", Lcd.Clear),
            ("Lcd.Write", () => Lcd.Write(0, 0, "text")),
            ("Lcd.Write", () => Lcd.Write(0, 0, "text".AsSpan())),
            ("Lcd.Write", () => Lcd.Write(0, 0, 'c')),
            ("Lcd.Write", () => Lcd.Write(0, 0, 42)),
            ("Lcd.Line", () => Lcd.Line(0, "text")),
            ("Lcd.Line", () => Lcd.Line(0, "text".AsSpan())),
            ("Lcd.Backlight", () => Lcd.Backlight(false)),
        ];

        Assert.Equal(LcdMethods.Count(), calls.Length);
        Assert.All(calls, call => Assert.StartsWith($"ChipSharp.Hardware.{call.Name} runs on a chip, not on .NET",
            Assert.Throws<PlatformNotSupportedException>(call.Call).Message));
    }

    [Fact]
    public void In_a_program_it_is_the_librarys_CSharp_over_I2c_Write_and_the_waits_with_no_heap()
    {
        var source = """
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Span<char> text = stackalloc char[2];
                    I2c.Open(1, 26, 27, 100_000);
                    if (Lcd.Open(1, 0x27, 16, 2))
                    {
                        Lcd.Clear();
                        Lcd.Write(0, 0, "count");
                        Lcd.Write(6, 0, 42);
                        Lcd.Write(0, 1, 'x');
                        Lcd.Write(2, 1, text);
                        Lcd.Line(1, "ready");
                        Lcd.Line(0, text);
                        Lcd.Backlight(false);
                    }
                }
            }
            """;
        var assembly = IlAssembly.Load(CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly], "Display"), "Display.dll");

        var program = WholeProgramPass.Analyze(assembly);
        var module = IlToTir.Lower(program);

        Assert.Contains(program.Methods, method => method.DisplayName == "ChipSharp.Library.Lcd.Open");
        Assert.Contains(program.Methods, method => method.DisplayName == "ChipSharp.Library.I2c.Write");
        var externals = module.Functions.Where(f => f.Kind == TirFunctionKind.External).Select(f => f.Name).ToList();
        Assert.Contains("ChipSharp.Runtime.I2cWrite", externals);
        Assert.Contains("ChipSharp.Runtime.DelayMicroseconds", externals);
        Assert.Contains("ChipSharp.Runtime.DelayMilliseconds", externals);
        Assert.DoesNotContain(externals, name => name.Contains("Lcd", StringComparison.Ordinal));
        Assert.False(module.UsesHeap);
    }
}
