using System.Reflection;
using ChipSharp.Components;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// ChipSharp.Components' Lcd: short names, ints, chars, strings and spans of chars; plain C# over ChipSharp.Hardware's
/// I2c and Timing, which on .NET does nothing before Open and reaches the chip-only I2c in it, and which ChipSharp
/// compiles with a program that calls it, only what it calls, with no heap and no runtime function of its own.
/// (<see cref="LcdSimulatorTests"/> runs it.)
/// </summary>
public sealed class LcdLibraryTests
{
    private static IEnumerable<MethodInfo> LcdMethods =>
        typeof(Lcd).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

    [Fact]
    public void Lcd_opens_clears_writes_lines_and_lights_with_plain_names()
    {
        Assert.Equal("ChipSharp.Components", typeof(Lcd).Namespace);
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
    public void On_dotnet_it_does_nothing_before_Open_and_Open_reaches_I2c_which_runs_on_a_chip()
    {
        // not open: nothing to write to, nothing thrown
        Lcd.Clear();
        Lcd.Write(0, 0, "text");
        Lcd.Write(0, 0, "text".AsSpan());
        Lcd.Write(0, 0, 'c');
        Lcd.Write(0, 0, 42);
        Lcd.Line(0, "text");
        Lcd.Line(0, "text".AsSpan());
        Lcd.Backlight(false);

        // a size no HD44780 has is false before any transfer; then the first transfer is the chip's
        Assert.False(Lcd.Open(1, 0x27, 41, 2));
        Assert.False(Lcd.Open(1, 0x27, 21, 4));
        Assert.StartsWith("ChipSharp.Hardware.I2c.Write runs on a chip, not on .NET",
            Assert.Throws<PlatformNotSupportedException>(() => Lcd.Open(1, 0x27, 16, 2)).Message);
        Lcd.Line(0, "still not open");
    }

    [Fact]
    public void In_a_program_it_is_compiled_with_it_over_I2c_Write_and_the_waits_only_what_it_calls_with_no_heap()
    {
        var source = """
            using System;
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Span<char> text = stackalloc char[2];
                    I2c.Open(1, 26, 27, 100_000);
                    if (Lcd.Open(1, 0x27, 16, 2))
                    {
                        Lcd.Write(0, 0, "count");
                        Lcd.Write(6, 0, 42);
                        Lcd.Line(1, text);
                    }
                }
            }
            """;
        var bytes = CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly, PicoBuild.ComponentsAssembly], "Display");
        var components = IlAssembly.Load(PicoBuild.ComponentsAssembly);
        var assembly = IlAssembly.Load(bytes, "Display.dll", [components]);

        var program = WholeProgramPass.Analyze(assembly);
        var module = IlToTir.Lower(program);

        // ChipSharp.Components is the program's to compile; ChipSharp.Hardware is the chip's runtime and the library's
        Assert.Equal(["ChipSharp.Components"], assembly.Libraries.Select(library => library.Name));
        Assert.Empty(components.Libraries);
        var lcd = program.Methods.Where(method => method.DeclaringType.FullName == "ChipSharp.Components.Lcd")
            .Select(method => method.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "Open", "Write", "Line", "Send", "Half", "MoveTo", "Code", "End", "Fits" }, lcd);
        Assert.DoesNotContain("Clear", lcd);
        Assert.DoesNotContain("Backlight", lcd);
        Assert.Contains(program.Methods, method => method.DisplayName == "ChipSharp.Library.I2c.Write");

        var externals = module.Functions.Where(f => f.Kind == TirFunctionKind.External).Select(f => f.Name).ToList();
        Assert.Contains("ChipSharp.Runtime.I2cWrite", externals);
        Assert.Contains("ChipSharp.Runtime.DelayMicroseconds", externals);
        Assert.Contains("ChipSharp.Runtime.DelayMilliseconds", externals);
        Assert.DoesNotContain(externals, name => name.Contains("Lcd", StringComparison.Ordinal));
        Assert.False(module.UsesHeap);
    }
}
