using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Tests.Analysis;
using ChipSharp.Tests.Tir;
using static ChipSharp.Tests.Llvm.LlvmSamples;

namespace ChipSharp.Tests.Llvm;

/// <summary>
/// Interrupt handlers in TIR and LLVM: each handler gets an entry function exported under the symbol of its target's
/// interrupt, which takes the place of the runtime's weak default in the vector table.
/// </summary>
public sealed class InterruptCodeTests
{
    private static readonly LlvmCodeGenerator Generator = new();

    [Fact]
    public void A_handler_gets_an_entry_exported_under_its_interrupts_symbol_that_clears_the_event_first()
    {
        var module = Lower(InterruptCheckTests.BlinkSource, LlvmTargets.Pico);

        var entry = Assert.Single(module.Functions, f => f.ExportName is not null);
        Assert.Equal(
            Text("""
                function void <Interrupt timer>() export chipsharp_isr_irq1
                entry:
                  call void chipsharp_clear_timer_event()
                  call void Program.Toggle()
                  return

                """),
            TirPrinter.Print(entry));
        Assert.Equal(TirFunctionKind.External,
            Assert.Single(module.Functions, f => f.Name == "chipsharp_clear_timer_event").Kind);
        Assert.Null(Assert.Single(module.Functions, f => f.Name == "Program.Toggle").ExportName);
        Assert.Contains("function void <Interrupt timer>() export chipsharp_isr_irq1\n", TirPrinter.Print(module));
    }

    [Fact]
    public void Without_a_target_a_handler_is_an_ordinary_function_and_nothing_is_exported()
    {
        var module = IlToTir.Lower(WholeProgramPass.Analyze(InterruptCheckTests.Load(InterruptCheckTests.BlinkSource)));

        Assert.Contains(module.Functions, f => f.Name == "Program.Toggle");
        Assert.DoesNotContain(module.Functions, f => f.ExportName is not null || f.Name.StartsWith("<Interrupt", StringComparison.Ordinal));
    }

    [Fact]
    public void The_type_initializers_a_handler_alone_uses_run_at_start_before_Main()
    {
        var module = Lower("""
            using ChipSharp.Hardware;

            class Program
            {
                static void Main() => Pin.WatchRising(Board.Button);

                [Interrupt(Event.Pin)]
                static void Pressed() => Pin.Toggle(Led.Pin);
            }

            static class Board
            {
                public static readonly int Button = Compute(15);

                static int Compute(int pin) => pin;
            }

            static class Led
            {
                public static readonly int Pin = Compute(25);

                static int Compute(int pin) => pin;
            }
            """, LlvmTargets.Pico);

        Assert.Equal(
            Text("""
                function void <Start>()
                entry:
                  call void Board..cctor()
                  call void Led..cctor()
                  call void Program.Main()
                  return

                """),
            TirPrinter.Print(Assert.Single(module.Functions, f => f.Name == ProgramFunctions.StartName)));
    }

    [Fact]
    public void The_exported_entry_is_defined_under_its_symbol_without_internal_and_the_handler_stays_internal()
    {
        var ll = Generate(Lower(InterruptCheckTests.BlinkSource, LlvmTargets.Pico), LlvmTargets.Pico);

        Assert.Equal(
            Text("""
                define void @chipsharp_isr_irq1() nounwind {
                allocas:
                  br label %entry

                entry:
                  call void @chipsharp_clear_timer_event()
                  call void @Program_Toggle()
                  ret void
                }

                """),
            Define(ll, "chipsharp_isr_irq1"));
        Assert.StartsWith("define internal void @Program_Toggle()", Define(ll, "Program_Toggle"));
        Assert.Contains("declare void @chipsharp_clear_timer_event() nounwind\n", ll);
    }

    [Fact]
    public void An_exported_function_keeps_its_symbol_and_another_function_of_that_name_moves_aside()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        var other = module.AddFunction("chipsharp_isr_irq5", TirType.Void);
        new TirBuilder(other.AddBlock("entry")).Return();
        var handler = module.AddFunction("<Interrupt uart>", TirType.Void);
        handler.ExportName = "chipsharp_isr_irq5";
        new TirBuilder(handler.AddBlock("entry")).Return();

        var ll = Generate(module, LlvmTargets.Pico);

        Assert.Contains("define void @chipsharp_isr_irq5() nounwind {", ll);
        Assert.Contains("define internal void @chipsharp_isr_irq5_2() nounwind {", ll);
    }

    [Fact]
    public void An_export_whose_symbol_an_external_function_or_the_entry_has_is_an_error()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        module.AddExternalFunction("chipsharp_clear_timer_event", TirType.Void);
        var handler = module.AddFunction("<Interrupt timer>", TirType.Void);
        handler.ExportName = "chipsharp_clear_timer_event";
        new TirBuilder(handler.AddBlock("entry")).Return();

        var clash = Assert.Throws<ChipSharpException>(() => Generator.Generate(module, LlvmTargets.Pico));
        handler.ExportName = LlvmCodeGenerator.EntrySymbol;
        var entry = Assert.Throws<ChipSharpException>(() => Generator.Generate(module, LlvmTargets.Pico));

        Assert.Equal("Function '<Interrupt timer>' is exported as 'chipsharp_clear_timer_event', which is also the " +
            "symbol of 'chipsharp_clear_timer_event'.", clash.Message);
        Assert.Equal("Function '<Interrupt timer>' is exported as 'chipsharp_entry', which ChipSharp keeps for the " +
            "program's entry.", entry.Message);
    }

    [Fact]
    public void The_verifier_wants_each_export_defined_and_its_symbol_used_once()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        module.AddExternalFunction("Runtime.Tick", TirType.Void).ExportName = "chipsharp_isr_irq0";
        foreach (var name in new[] { "A", "B" })
        {
            var function = module.AddFunction(name, TirType.Void);
            function.ExportName = "chipsharp_isr_irq3";
            new TirBuilder(function.AddBlock("entry")).Return();
        }

        Assert.Equal(
            [
                "Function 'Runtime.Tick': an external function cannot be exported (as chipsharp_isr_irq0); the runtime " +
                    "implements it.",
                "Module 'Test': 'A' and 'B' are exported under one symbol, chipsharp_isr_irq3; a symbol names one " +
                    "function.",
            ],
            TirVerifier.FindErrors(module));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1st")]
    [InlineData("isr irq")]
    [InlineData("isr.irq")]
    public void An_export_name_is_a_C_name(string symbol)
    {
        var function = new TirModule("Test").AddFunction("Program.Tick", TirType.Void);

        var error = Assert.Throws<TirException>(() => function.ExportName = symbol);

        Assert.Equal($"Function 'Program.Tick': '{symbol}' cannot be a symbol; use letters, digits and _.", error.Message);
    }

    private static TirModule Lower(string source, TargetDescription target)
    {
        var module = IlToTir.Lower(WholeProgramPass.Analyze(InterruptCheckTests.Load(source), target));
        TirVerifier.Verify(module);
        return module;
    }

    private static string Generate(TirModule module, TargetDescription target) =>
        Assert.Single(Generator.Generate(module, target).Files).Content;
}
