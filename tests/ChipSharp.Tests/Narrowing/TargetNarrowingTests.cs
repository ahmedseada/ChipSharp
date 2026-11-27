using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Targets;
using ChipSharp.Tests.Avr;
using ChipSharp.Tests.Lowering;

namespace ChipSharp.Tests.Narrowing;

/// <summary>
/// Which targets narrow: the 8-bit AVR (2-byte pointers) by default, the Pico and the PC not, so that their code is what
/// it was; the capability "narrowing" decides either way.
/// </summary>
public sealed class TargetNarrowingTests
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private const string Loop = """
        using System;

        class Program
        {
            static void Main()
            {
                for (int i = 0; i < 7; i++)
                {
                    Console.WriteLine(i * 3);
                }
            }
        }
        """;

    [Fact]
    public void The_Uno_narrows_and_knows_an_array_has_no_more_elements_than_its_flash_has_bytes()
    {
        var options = TargetNarrowing.For(Targets.Load("uno"));

        Assert.NotNull(options);
        Assert.Equal(32256, options.MaxLength);
        Assert.Single(options.LengthFields);
    }

    [Theory]
    [InlineData("pico")]
    [InlineData("host")]
    public void The_Pico_and_the_PC_do_not_narrow_unless_their_target_says_so(string name)
    {
        var target = Targets.Load(name);

        Assert.Null(TargetNarrowing.For(target));
        Assert.NotNull(TargetNarrowing.For(NarrowingProgramTests.Narrowed(target)));
        Assert.Null(TargetNarrowing.For(null));
    }

    [Fact]
    public void A_counter_compiled_for_the_Uno_is_a_byte_and_for_the_Pico_an_int_as_before()
    {
        var assembly = Lowered.Compile(Loop, "Loop");

        var uno = IlToTir.Lower(WholeProgramPass.Analyze(assembly, AvrCode.Uno()));
        var unoOff = IlToTir.Lower(WholeProgramPass.Analyze(assembly, AvrCode.Uno(("narrowing", "false"))));
        var pico = IlToTir.Lower(WholeProgramPass.Analyze(assembly, Targets.Load("pico")));
        var plain = IlToTir.Lower(WholeProgramPass.Analyze(assembly));

        Assert.Contains("  $V_0: i8\n", TirPrinter.Print(uno));
        Assert.Contains("= mul i8 ", TirPrinter.Print(uno));
        Assert.Equal(TirPrinter.Print(plain), TirPrinter.Print(pico));
        Assert.Equal(TirPrinter.Print(plain), TirPrinter.Print(unoOff));
        Assert.Contains("  $V_0: i32\n", TirPrinter.Print(pico));
    }
}
