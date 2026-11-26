using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Ir;
using ChipSharp.Tests.Tir;
using static ChipSharp.Tests.Llvm.LlvmSamples;

namespace ChipSharp.Tests.Llvm;

/// <summary>
/// The LLVM the generator writes for global arrays (C#'s arrays in static fields): a variable starts with its elements or
/// at zero; a constant table is an internal (so it has a symbol), unnamed_addr constant, read-only data that the linker
/// puts with the code, in flash on a chip.
/// </summary>
public sealed class LlvmArrayGlobalTests
{
    private static readonly LlvmCodeGenerator Generator = new();

    [Fact]
    public void A_table_is_an_internal_constant_with_its_elements_and_a_fixed_array_a_global()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        Add(module);

        Assert.StartsWith(Text("""
            source_filename = "Demo"

            %struct.Program_Point = type { i32, i64 }

            @Program_Digits = internal unnamed_addr constant [3 x i8] [i8 63, i8 6, i8 -1], align 1
            @Program_Gains = internal unnamed_addr constant [2 x float] [float 0x3FF8000000000000, float 0x8000000000000000], align 4
            @Program_Origins = internal unnamed_addr constant [2 x %struct.Program_Point] zeroinitializer, align 8
            @Program_counts = internal global [2 x i64] [i64 -1, i64 2], align 8
            @Program_buffer = internal global [16 x i32] zeroinitializer, align 4
            @Program_flags = internal global [2 x i8] [i8 1, i8 0], align 1

            define internal void @Program_Main() nounwind {
            """), Generate(module, LlvmTargets.Make("plain", null, pointerSize: 8)));
    }

    [Fact]
    public void A_table_has_the_same_form_on_the_pico_aligned_for_its_elements()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        Add(module);

        var ll = Generate(module, LlvmTargets.Pico);

        Assert.Contains(Text("""
            @Program_Digits = internal unnamed_addr constant [3 x i8] [i8 63, i8 6, i8 -1], align 1
            @Program_Gains = internal unnamed_addr constant [2 x float] [float 0x3FF8000000000000, float 0x8000000000000000], align 4
            @Program_Origins = internal unnamed_addr constant [2 x %struct.Program_Point] zeroinitializer, align 8
            @Program_counts = internal global [2 x i64] [i64 -1, i64 2], align 8
            @Program_buffer = internal global [16 x i32] zeroinitializer, align 4

            """), ll, StringComparison.Ordinal);
    }

    private static void Add(TirModule module)
    {
        module.AddArray("Program.Digits", new TirArrayType(TirType.I8, 3),
            [new TirConst(TirType.I8, 63), new TirConst(TirType.I8, 6), new TirConst(TirType.I8, 255)], isConstant: true);
        module.AddArray("Program.Gains", new TirArrayType(TirType.F32, 2),
            [new TirConst(TirType.F32, 1.5), new TirConst(TirType.F32, -0.0)], isConstant: true);
        module.AddArray("Program.Origins",
            new TirArrayType(new TirStructType("Program.Point", ("X", TirType.I32), ("Y", TirType.I64)), 2), null,
            isConstant: true);
        module.AddArray("Program.counts", new TirArrayType(TirType.I64, 2),
            [new TirConst(TirType.I64, -1), new TirConst(TirType.I64, 2)], isConstant: false);
        module.AddArray("Program.buffer", new TirArrayType(TirType.I32, 16), null, isConstant: false);
        module.AddArray("Program.flags", new TirArrayType(TirType.I8, 2),
            [new TirConst(TirType.I8, 1), new TirConst(TirType.I8, 0)], isConstant: false);
    }

    private static string Generate(TirModule module, TargetDescription target) =>
        Assert.Single(Generator.Generate(module, target).Files).Content;
}
