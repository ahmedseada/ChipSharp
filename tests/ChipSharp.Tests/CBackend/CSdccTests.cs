using ChipSharp.Abstractions;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Tests.Support;
using Xunit.Abstractions;

namespace ChipSharp.Tests.CBackend;

/// <summary>
/// The C the c code generator writes for SDCC's chips, through SDCC: every sample that needs no heap compiles with
/// sdcc for the PFS154 (pdk14) and the STM8S103 (stm8) without an error or a warning (a console added, which their
/// runtimes are to get), and small programs link with the toolchain "sdcc" into a program image.
/// </summary>
public sealed class CSdccTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> Ports => ["pdk14", "stm8"];

    [SdccFact]
    [RunsLong]
    public async Task Every_sample_without_a_heap_compiles_with_sdcc_for_the_PFS154_and_the_STM8S103_without_a_warning()
    {
        var targets = new[] { CSdcc.Pfs154, CSdcc.Stm8s103 }.Select(target => CSdcc.With(target, "console", "true")).ToList();
        var cases = Samples.Names.SelectMany(name => targets.Select(target => (Name: name, Target: target))).ToList();
        var results = await Concurrently.SelectAsync(cases, async item =>
        {
            var source = await File.ReadAllTextAsync(Path.Combine(Samples.Folder, item.Name + ".cs"));
            (Ir.TirModule Module, string C)? generated;
            try
            {
                generated = CSdcc.Generate(source, item.Name, item.Target);
            }
            catch (ChipSharpException e) when (e.Message.Contains("uses double", StringComparison.Ordinal))
            {
                return (item.Name, item.Target.Name, Result: "refused: double");
            }

            if (generated is not { } code)
            {
                return (item.Name, item.Target.Name, Result: "not supported");
            }

            var messages = await CSdcc.CompileAsync(code.C, item.Name + ".c", item.Target,
                Path.Combine(_scratch, item.Target.Name, item.Name));

            // a note of the pdk14 port's register allocator about itself, not of the C (it compiles the code right)
            var warnings = string.Join('\n', messages.Split('\n').Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("Warning: Non-connected liverange found", StringComparison.Ordinal)));
            return (item.Name, item.Target.Name, Result: warnings.Length == 0 ? "compiled" : "warned: " + warnings);
        });

        foreach (var (name, target, result) in results)
        {
            output.WriteLine($"{target,-16} {name,-16} {result}");
        }

        Assert.DoesNotContain(results, result => result.Result.StartsWith("warned", StringComparison.Ordinal));
        Assert.True(results.Count(result => result.Result == "compiled") >= 40,
            $"{results.Count(result => result.Result == "compiled")} compiled");
    }

    /// <summary>Recursion, a struct and a table in code memory, small enough for the PFS154's 128 bytes of RAM.</summary>
    private const string Program = """
        static class Program
        {
            struct Pair { public byte Low; public short High; }

            static readonly byte[] Table = { 3, 1, 4, 1, 5, 9, 2, 6 };

            static byte Fib(byte n) => n < 2 ? n : (byte)(Fib((byte)(n - 1)) + Fib((byte)(n - 2)));

            static Pair Make(byte low, int high) => new Pair { Low = low, High = (short)high };

            static int Main()
            {
                var pair = Make(Table[5], Fib(7));
                return (byte)(pair.High - (sbyte)pair.Low);
            }
        }
        """;

    [SdccFact]
    public void A_program_with_recursion_structs_and_a_table_links_for_the_PFS154_the_recursive_function_reentrant()
    {
        var target = CSdcc.Pfs154;
        var c = CSdcc.Required(Program, "Linked", target).C;

        // the PFS154 keeps locals at fixed addresses: Fib calls itself, Make does not
        Assert.Contains("#define CS_REENTRANT __reentrant", c);
        Assert.Contains("static uint8_t Program_Fib(uint8_t p_n) CS_REENTRANT;", c);
        Assert.Contains("static struct Program_Pair Program_Make(uint8_t p_low, uint32_t p_high);", c);

        Assert.True(File.Exists(CSdcc.Link(Program, "Linked", target, Path.Combine(_scratch, "pfs154"))));
    }

    [SdccFact]
    public void The_same_program_links_for_the_STM8S103_whose_functions_keep_their_locals_on_the_stack_anyway()
    {
        var target = CSdcc.Stm8s103;
        var c = CSdcc.Required(Program, "Linked", target).C;

        Assert.DoesNotContain("CS_REENTRANT", c);
        Assert.True(File.Exists(CSdcc.Link(Program, "Linked", target, Path.Combine(_scratch, "stm8"))));
    }

    [SdccFact]
    public void Calls_through_an_address_and_a_function_an_interrupt_shares_with_Main_link_for_both_chips()
    {
        foreach (var target in new[] { CSdcc.Pfs154, CSdcc.Stm8s103 })
        {
            var module = CPrograms.IndirectAndInterrupt();
            var c = Assert.Single(new ChipSharp.Backend.C.CCodeGenerator().Generate(module, target).Files).Content;
            if (target.Setting("sdcc.port") == "pdk14")
            {
                Assert.Contains("static uint32_t Program_Twice(uint32_t p_x) CS_REENTRANT;", c);
                Assert.Contains("static uint32_t Program_Shared(uint32_t p_x) CS_REENTRANT;", c);
                Assert.Contains("typedef uint32_t (*cs_function_0)(uint32_t) CS_REENTRANT;", c);
                Assert.Contains("void chipsharp_isr_irq1(void);", c);
            }

            Assert.True(File.Exists(CSdcc.Link(module, target, Path.Combine(_scratch, "indirect-" + target.Name))));
        }
    }
}
