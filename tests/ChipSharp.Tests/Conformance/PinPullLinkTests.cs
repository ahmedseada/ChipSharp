// a ChipTheory's cases, one per chip, are its discoverer's (ChipTheoryDiscoverer), not a data attribute's
#pragma warning disable xUnit1003

using ChipSharp.Build;
using ChipSharp.Image.Uf2;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// The pull of an input is code only a program that asks for one has: <c>Pin.Input(pin)</c> alone links no
/// <c>ChipSharp_Runtime_PinInputPull</c>, on every chip, and the program is smaller than the one that does.
/// </summary>
public sealed class PinPullLinkTests : ConformanceTests
{
    private const string Symbol = "ChipSharp_Runtime_PinInputPull";

    [ChipTheory("chip")]
    public void A_program_that_never_asks_for_a_pull_has_no_code_for_one(string target)
    {
        var chip = ChipSimulators.Get(target);

        var plain = Link(chip, "Plain", "Pin.Input(Input);\nPin.Output(Output);\nPin.Write(Output, Pin.Read(Input));");
        var pulled = Link(chip, "Pulled", "Pin.Input(Input, Pull.Up);\nPin.Output(Output);\nPin.Write(Output, Pin.Read(Input));");

        Assert.DoesNotContain(plain.Symbols, name => name.Contains(Symbol, StringComparison.Ordinal));
        Assert.Contains(pulled.Symbols, name => name.Contains(Symbol, StringComparison.Ordinal));
        Assert.True(plain.Used < pulled.Used, $"{chip}: {plain.Used} of the plain program, {pulled.Used} with a pull");
    }

    /// <summary>The symbols the linker kept (the ELF's, or the map's) and the program's size.</summary>
    private (IReadOnlyCollection<string> Symbols, ulong Used) Link(ChipSimulator chip, string name, string statements)
    {
        var folder = Path.Combine(_scratch, name + chip.Target);
        Directory.CreateDirectory(folder);
        var source = $$"""
            using ChipSharp.Hardware;

            {{statements}}

            partial class Program
            {
                const int Input = {{chip.Pins.Input}};
                const int Output = {{chip.Pins.Output}};
            }
            """;
        var dll = Path.Combine(folder, name + ".dll");
        File.WriteAllBytes(dll, CSharpCompiler.Compile([source], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], name));
        var result = ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = chip.Target,
            TargetsFolder = Path.Combine(AppContext.BaseDirectory, "targets"),
            OutputFolder = Path.Combine(folder, chip.Target),
            KeepIntermediates = true,
            UseCache = false,
        });
        var symbols = new List<string>();
        foreach (var path in result.IntermediatePaths)
        {
            if (path.EndsWith(".elf", StringComparison.Ordinal))
            {
                symbols.AddRange(ElfFile.Read(path).Symbols.Keys);
            }
            else if (path.EndsWith(".map", StringComparison.Ordinal))
            {
                symbols.AddRange(File.ReadAllLines(path));
            }
        }

        Assert.NotEmpty(symbols);
        return (symbols, result.Memory.Code!.Used);
    }
}
