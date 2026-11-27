using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Avr;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Avr;

/// <summary>The avr runtime's files and its vector table, as a build gets them.</summary>
public sealed class AvrRuntimeTests
{
    [Fact]
    public void A_build_finds_the_avr_runtime_by_its_name()
    {
        var build = new BuildContext(new BuildOptions(), AvrCoreProgram.Target, "Program",
            Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N")));

        Assert.IsType<AvrRuntime>(DefaultComponents.CreateRegistry(build).Get<IArchRuntime>("avr"));
    }

    [Fact]
    public void The_runtime_is_the_start_up_the_helpers_soft_float_the_float_math_and_the_failures()
    {
        var runtime = new AvrRuntime();

        var files = runtime.Startup(AvrCoreProgram.Target).Files;

        var softFloat = CompilerRt.Sources().Select(file => file.FileName).ToList();
        Assert.Equal(
            ["avr_startup.S", "avr_runtime.c", "avr_multiply.S", "avr_divide.S", "avr_memory.S", .. softFloat,
                AvrRuntime.FloatMathFileName, "avr_failures.c"],
            files.Select(file => file.FileName));
        Assert.Equal([AvrRuntime.VectorsFileName], runtime.InterruptEntry(AvrCoreProgram.Target).Files.Select(file => file.FileName));
        Assert.Empty(runtime.Atomics(AvrCoreProgram.Target).Files);

        // the very compiler-rt files and float math the armv6m runtime has; ChipSharp's own files include nothing
        Assert.Equal(CompilerRt.Sources().Select(file => file.Content), files.Where(file => softFloat.Contains(file.FileName))
            .Select(file => file.Content));
        Assert.Equal(ArmV6MRuntime.Source(ArmV6MRuntime.FloatMathFileName).Content,
            files.Single(file => file.FileName == AvrRuntime.FloatMathFileName).Content);
        Assert.All(files.Where(file => !softFloat.Contains(file.FileName)),
            file => Assert.DoesNotContain("#include", file.Content));
    }

    /// <summary>C# double is 64 bits; AVR's clang makes it 32 unless told -mdouble=64, so the runtime refuses to build without.</summary>
    [Fact]
    public void The_runtimes_C_stops_unless_double_is_64_bits()
    {
        Assert.Contains("#if __SIZEOF_DOUBLE__ != 8\n#error", AvrRuntime.Source(AvrRuntime.RuntimeFileName).Content.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_Uno_builds_with_a_64_bit_double_and_says_how_many_vectors_its_chip_has()
    {
        var uno = AvrTestProgram.Uno;

        Assert.Contains("-mdouble=64", ChipSettings.Read(uno).ClangFlags);
        Assert.Equal(26, AvrRuntime.VectorCount(uno));
    }

    [Fact]
    public void The_vector_table_has_an_entry_for_reset_and_a_weak_handler_for_every_interrupt_of_the_chip()
    {
        var vectors = Assert.Single(new AvrRuntime().InterruptEntry(AvrCoreProgram.Target).Files).Content;

        Assert.Contains("    .section .vectors, \"ax\", @progbits\n", vectors);
        Assert.Contains("chipsharp_vectors:\n    VECTOR chipsharp_reset\n    VECTOR __vector_1\n", vectors);
        Assert.Contains("    VECTOR __vector_25\n    .size chipsharp_vectors", vectors);
        Assert.DoesNotContain("__vector_26", vectors);
        Assert.Equal(25, vectors.Split("    .weak __vector_").Length - 1);
        Assert.Contains("    .set __vector_7, chipsharp_unhandled_interrupt\n", vectors);
        Assert.Contains("chipsharp_unhandled_interrupt:\n    VECTOR chipsharp_halt_failure\n", vectors);
    }

    [Theory]
    [InlineData(null, "'vectors' is missing")]
    [InlineData("0", "from 1 to 128")]
    [InlineData("many", "\"many\"")]
    public void A_target_without_its_number_of_vectors_says_so(string? vectors, string message)
    {
        var settings = new JsonObject();
        if (vectors is not null)
        {
            settings[AvrRuntime.VectorsSetting] = vectors;
        }

        var target = new TargetDescription
        {
            Name = "chip",
            InheritanceChain = ["chip"],
            Backend = "llvm",
            PointerSize = 2,
            Endian = Endianness.Little,
            Settings = settings,
        };

        var error = Assert.Throws<ChipSharpException>(() => new AvrRuntime().InterruptEntry(target));
        Assert.Contains(message, error.Message);
        Assert.Contains("Target 'chip'", error.Message);
    }
}
