using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Ir;
using ChipSharp.Tests.Tir;
using static ChipSharp.Tests.Llvm.LlvmSamples;

namespace ChipSharp.Tests.Llvm;

public sealed class LlvmCodeGeneratorTests
{
    private static readonly LlvmCodeGenerator Generator = new();

    private static string Generate(TirModule module, TargetDescription? target = null) =>
        Assert.Single(Generator.Generate(module, target ?? LlvmTargets.Host).Files).Content;

    /// <summary>The define of a function added to a module that also has an empty Main.</summary>
    private static string DefineOf(Func<TirModule, TirFunction> add, string symbol, TargetDescription? target = null)
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        add(module);
        return Define(Generate(module, target), symbol);
    }

    // ---- whole functions ----

    [Fact]
    public void Writes_square_and_a_main_that_calls_it_and_an_external_print()
    {
        var code = Generator.Generate(SquareProgram(), LlvmTargets.Host);

        var file = Assert.Single(code.Files);
        Assert.Equal("Demo.ll", file.FileName);
        Assert.Equal(Text("""
            source_filename = "Demo"
            target triple = "x86_64-pc-windows-msvc"

            declare void @ChipSharp_Runtime_WriteLineInt32(i32) nounwind

            define internal i32 @Program_Square(i32 %p.n) nounwind {
            allocas:
              br label %entry

            entry:
              %t0 = mul i32 %p.n, %p.n
              ret i32 %t0
            }

            define internal void @Program_Main() nounwind {
            allocas:
              br label %entry

            entry:
              %t0 = call i32 @Program_Square(i32 42)
              call void @ChipSharp_Runtime_WriteLineInt32(i32 %t0)
              ret void
            }

            define i32 @chipsharp_entry() nounwind {
            entry:
              call void @Program_Main()
              ret i32 0
            }

            """), file.Content);
    }

    [Fact]
    public void Writes_an_if_else_that_meets_in_a_slot()
    {
        Assert.Equal(Text("""
            define internal i32 @Program_Max(i32 %p.a, i32 %p.b) nounwind {
            allocas:
              %s.result = alloca i32, align 4
              store i32 0, ptr %s.result, align 4
              br label %entry

            entry:
              %t0 = icmp sgt i32 %p.a, %p.b
              br i1 %t0, label %then, label %else

            then:
              store i32 %p.a, ptr %s.result, align 4
              br label %join

            else:
              store i32 %p.b, ptr %s.result, align 4
              br label %join

            join:
              %t1 = load i32, ptr %s.result, align 4
              ret i32 %t1
            }

            """), DefineOf(TirSamples.AddMax, "Program_Max"));
    }

    [Fact]
    public void Writes_a_loop_with_every_slot_set_to_zero_first()
    {
        Assert.Equal(Text("""
            define internal i32 @Program_Sum(i32 %p.n) nounwind {
            allocas:
              %s.i = alloca i32, align 4
              %s.sum = alloca i32, align 4
              store i32 0, ptr %s.i, align 4
              store i32 0, ptr %s.sum, align 4
              br label %entry

            entry:
              store i32 0, ptr %s.i, align 4
              store i32 0, ptr %s.sum, align 4
              br label %test

            test:
              %t0 = load i32, ptr %s.i, align 4
              %t1 = icmp slt i32 %t0, %p.n
              br i1 %t1, label %body, label %done

            body:
              %t2 = load i32, ptr %s.sum, align 4
              %t3 = load i32, ptr %s.i, align 4
              %t4 = add i32 %t2, %t3
              store i32 %t4, ptr %s.sum, align 4
              %t5 = add i32 %t3, 1
              store i32 %t5, ptr %s.i, align 4
              br label %test

            done:
              %t6 = load i32, ptr %s.sum, align 4
              ret i32 %t6
            }

            """), DefineOf(TirSamples.AddSum, "Program_Sum"));
    }

    [Fact]
    public void The_allocas_block_lets_the_TIR_entry_block_be_a_jump_target()
    {
        Assert.Equal(Text("""
            define internal i32 @Program_CountTo(i32 %p.n) nounwind {
            allocas:
              %s.count = alloca i32, align 4
              store i32 0, ptr %s.count, align 4
              br label %entry

            entry:
              %t0 = load i32, ptr %s.count, align 4
              %t1 = add i32 %t0, 1
              store i32 %t1, ptr %s.count, align 4
              %t2 = icmp slt i32 %t1, %p.n
              br i1 %t2, label %entry, label %done

            done:
              %t3 = load i32, ptr %s.count, align 4
              ret i32 %t3
            }

            """), DefineOf(AddCountTo, "Program_CountTo"));
    }

    // ---- instructions ----

    [Fact]
    public void Writes_every_integer_operation_signed_or_unsigned()
    {
        Assert.Equal(Text("""
            define internal void @Program_Ops(i32 %p.a, i32 %p.b) nounwind {
            allocas:
              br label %entry

            entry:
              %t0 = add i32 %p.a, %p.b
              %t1 = sub i32 %p.a, %p.b
              %t2 = mul i32 %p.a, %p.b
              %t3 = sdiv i32 %p.a, %p.b
              %t4 = udiv i32 %p.a, %p.b
              %t5 = srem i32 %p.a, %p.b
              %t6 = urem i32 %p.a, %p.b
              %t7 = and i32 %p.a, %p.b
              %t8 = or i32 %p.a, %p.b
              %t9 = xor i32 %p.a, %p.b
              %t10 = shl i32 %p.a, %p.b
              %t11 = ashr i32 %p.a, %p.b
              %t12 = lshr i32 %p.a, %p.b
              ret void
            }

            """), DefineOf(m => AddBinaryOps(m, "Program.Ops", TirType.I32, Enum.GetValues<TirBinaryOp>()), "Program_Ops"));
    }

    [Theory]
    [InlineData(TirTypeKind.F32, "float")]
    [InlineData(TirTypeKind.F64, "double")]
    public void Writes_float_arithmetic(TirTypeKind kind, string llvmType)
    {
        Assert.Equal(Text($$"""
            define internal void @Program_Ops({{llvmType}} %p.a, {{llvmType}} %p.b) nounwind {
            allocas:
              br label %entry

            entry:
              %t0 = fadd {{llvmType}} %p.a, %p.b
              %t1 = fsub {{llvmType}} %p.a, %p.b
              %t2 = fmul {{llvmType}} %p.a, %p.b
              %t3 = fdiv {{llvmType}} %p.a, %p.b
              %t4 = frem {{llvmType}} %p.a, %p.b
              ret void
            }

            """), DefineOf(m => AddBinaryOps(m, "Program.Ops", Type(kind), FloatBinaryOps), "Program_Ops"));
    }

    [Fact]
    public void Writes_logic_on_bools_as_i1()
    {
        var define = DefineOf(m => AddBinaryOps(m, "Program.Ops", TirType.Bool,
            [TirBinaryOp.And, TirBinaryOp.Or, TirBinaryOp.Xor]), "Program_Ops");

        Assert.Contains(Text("""
              %t0 = and i1 %p.a, %p.b
              %t1 = or i1 %p.a, %p.b
              %t2 = xor i1 %p.a, %p.b
            """), define);
    }

    [Fact]
    public void Writes_every_integer_comparison_signed_or_unsigned()
    {
        Assert.Equal(Text("""
            define internal void @Program_Compare(i64 %p.a, i64 %p.b) nounwind {
            allocas:
              br label %entry

            entry:
              %t0 = icmp eq i64 %p.a, %p.b
              %t1 = icmp ne i64 %p.a, %p.b
              %t2 = icmp slt i64 %p.a, %p.b
              %t3 = icmp sle i64 %p.a, %p.b
              %t4 = icmp sgt i64 %p.a, %p.b
              %t5 = icmp sge i64 %p.a, %p.b
              %t6 = icmp ult i64 %p.a, %p.b
              %t7 = icmp ule i64 %p.a, %p.b
              %t8 = icmp ugt i64 %p.a, %p.b
              %t9 = icmp uge i64 %p.a, %p.b
              ret void
            }

            """), DefineOf(m => AddCompares(m, "Program.Compare", TirType.I64, Enum.GetValues<TirCompareOp>()),
            "Program_Compare"));
    }

    [Fact]
    public void Compares_floats_ordered_except_not_equal()
    {
        Assert.Contains(Text("""
              %t0 = fcmp oeq double %p.a, %p.b
              %t1 = fcmp une double %p.a, %p.b
              %t2 = fcmp olt double %p.a, %p.b
              %t3 = fcmp ole double %p.a, %p.b
              %t4 = fcmp ogt double %p.a, %p.b
              %t5 = fcmp oge double %p.a, %p.b
              ret void
            """), DefineOf(m => AddCompares(m, "Program.Compare", TirType.F64, FloatCompareOps), "Program_Compare"));
    }

    [Fact]
    public void Compares_bools_as_i1()
    {
        Assert.Contains(Text("""
              %t0 = icmp eq i1 %p.a, %p.b
              %t1 = icmp ne i1 %p.a, %p.b
            """), DefineOf(m => AddCompares(m, "Program.Compare", TirType.Bool, [TirCompareOp.Eq, TirCompareOp.Ne]),
            "Program_Compare"));
    }

    [Theory]
    [InlineData(TirCompareOp.LtUnsigned, "fcmp ult")]
    [InlineData(TirCompareOp.LeUnsigned, "fcmp ule")]
    [InlineData(TirCompareOp.GtUnsigned, "fcmp ugt")]
    [InlineData(TirCompareOp.GeUnsigned, "fcmp uge")]
    public void The_unsigned_comparisons_are_unordered_on_floats(TirCompareOp op, string expected)
    {
        // what IL's clt.un, cgt.un, bge.un and the like mean on floats: also true when either side is NaN
        Assert.Equal(expected, LlvmOpcodes.Compare(op, isFloat: true));
        Assert.Contains($"  %t0 = {expected} float %p.a, %p.b\n",
            DefineOf(m => AddCompares(m, "Program.Compare", TirType.F32, [op]), "Program_Compare"));
        Assert.Contains($"  %t0 = {expected} double %p.a, %p.b\n",
            DefineOf(m => AddCompares(m, "Program.Compare", TirType.F64, [op]), "Program_Compare"));
    }

    [Theory]
    [InlineData(TirTypeKind.I32, TirTypeKind.I64, false, 8, "sext i32 %p.x to i64")]
    [InlineData(TirTypeKind.I32, TirTypeKind.I64, true, 8, "zext i32 %p.x to i64")]
    [InlineData(TirTypeKind.I8, TirTypeKind.I16, false, 8, "sext i8 %p.x to i16")]
    [InlineData(TirTypeKind.I8, TirTypeKind.I16, true, 8, "zext i8 %p.x to i16")]
    [InlineData(TirTypeKind.I64, TirTypeKind.I32, false, 8, "trunc i64 %p.x to i32")]
    [InlineData(TirTypeKind.I64, TirTypeKind.I8, true, 8, "trunc i64 %p.x to i8")]
    [InlineData(TirTypeKind.Bool, TirTypeKind.I32, false, 8, "zext i1 %p.x to i32")]
    [InlineData(TirTypeKind.Bool, TirTypeKind.I8, true, 8, "zext i1 %p.x to i8")]
    [InlineData(TirTypeKind.Bool, TirTypeKind.F64, false, 8, "uitofp i1 %p.x to double")]
    [InlineData(TirTypeKind.I32, TirTypeKind.F64, false, 8, "sitofp i32 %p.x to double")]
    [InlineData(TirTypeKind.I32, TirTypeKind.F64, true, 8, "uitofp i32 %p.x to double")]
    [InlineData(TirTypeKind.I64, TirTypeKind.F32, false, 8, "sitofp i64 %p.x to float")]
    [InlineData(TirTypeKind.F64, TirTypeKind.I32, false, 8, "call i32 @llvm.fptosi.sat.i32.f64(double %p.x)")]
    [InlineData(TirTypeKind.F64, TirTypeKind.I32, true, 8, "call i32 @llvm.fptoui.sat.i32.f64(double %p.x)")]
    [InlineData(TirTypeKind.F32, TirTypeKind.I64, false, 8, "call i64 @llvm.fptosi.sat.i64.f32(float %p.x)")]
    [InlineData(TirTypeKind.F32, TirTypeKind.I8, true, 8, "call i8 @llvm.fptoui.sat.i8.f32(float %p.x)")]
    [InlineData(TirTypeKind.F64, TirTypeKind.NativeInt, true, 4, "call i32 @llvm.fptoui.sat.i32.f64(double %p.x)")]
    [InlineData(TirTypeKind.F32, TirTypeKind.F64, false, 8, "fpext float %p.x to double")]
    [InlineData(TirTypeKind.F64, TirTypeKind.F32, false, 8, "fptrunc double %p.x to float")]
    [InlineData(TirTypeKind.I32, TirTypeKind.NativeInt, false, 8, "sext i32 %p.x to i64")]
    [InlineData(TirTypeKind.I32, TirTypeKind.NativeInt, true, 8, "zext i32 %p.x to i64")]
    [InlineData(TirTypeKind.I32, TirTypeKind.NativeInt, false, 4, null)]
    [InlineData(TirTypeKind.NativeInt, TirTypeKind.I64, false, 4, "sext i32 %p.x to i64")]
    [InlineData(TirTypeKind.NativeInt, TirTypeKind.I64, true, 4, "zext i32 %p.x to i64")]
    [InlineData(TirTypeKind.NativeInt, TirTypeKind.I64, false, 8, null)]
    [InlineData(TirTypeKind.NativeInt, TirTypeKind.I32, false, 8, "trunc i64 %p.x to i32")]
    [InlineData(TirTypeKind.NativeInt, TirTypeKind.F64, false, 4, "sitofp i32 %p.x to double")]
    [InlineData(TirTypeKind.I32, TirTypeKind.I32, false, 8, null)]
    [InlineData(TirTypeKind.F64, TirTypeKind.F64, true, 8, null)]
    public void Converts_with_the_matching_cast_or_none_when_the_llvm_types_are_the_same(
        TirTypeKind from, TirTypeKind to, bool unsigned, int pointerSize, string? expected)
    {
        var target = LlvmTargets.Make("test", null, pointerSize);
        var define = DefineOf(m => AddConvert(m, Type(from), Type(to), unsigned), "Program_Convert", target);

        var toType = to == TirTypeKind.NativeInt ? $"i{pointerSize * 8}" : Llvm(to);
        Assert.EndsWith(expected is null
            ? $"entry:\n  ret {toType} %p.x\n}}\n"
            : $"entry:\n  %t0 = {expected}\n  ret {toType} %t0\n}}\n", define);
    }

    [Fact]
    public void A_float_becomes_an_integer_through_the_saturating_intrinsics_declared_once_at_the_end()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        AddConvert(module, TirType.F64, TirType.I32, unsigned: false);
        var second = module.AddFunction("Program.Second", TirType.I64, ("x", TirType.F32), ("y", TirType.F64));
        var b = new TirBuilder(second.AddBlock("entry"));
        var first = b.Convert(second.Parameters[0], TirType.I64, unsigned: true);
        var again = b.Convert(second.Parameters[1], TirType.I32);
        b.Return(b.Binary(TirBinaryOp.Add, first, b.Convert(again, TirType.I64)));

        var ll = Generate(module);

        Assert.Contains("  %t0 = call i64 @llvm.fptoui.sat.i64.f32(float %p.x)\n" +
            "  %t1 = call i32 @llvm.fptosi.sat.i32.f64(double %p.y)\n", Define(ll, "Program_Second"));
        Assert.EndsWith(Text("""
            ret i32 0
            }

            declare i32 @llvm.fptosi.sat.i32.f64(double)
            declare i64 @llvm.fptoui.sat.i64.f32(float)

            """), ll);
    }

    [Fact]
    public void Writes_constants_inline_with_floats_in_hex()
    {
        Assert.Equal(Text("""
            define internal void @Program_Constants() nounwind {
            allocas:
              %s.flag = alloca i1, align 1
              %s.b = alloca i8, align 1
              %s.s = alloca i16, align 2
              %s.i = alloca i32, align 4
              %s.l = alloca i64, align 8
              %s.n = alloca i64, align 8
              %s.f = alloca float, align 4
              %s.f2 = alloca float, align 4
              %s.d = alloca double, align 8
              %s.negativeZero = alloca double, align 8
              %s.infinity = alloca double, align 8
              store i1 false, ptr %s.flag, align 1
              store i8 0, ptr %s.b, align 1
              store i16 0, ptr %s.s, align 2
              store i32 0, ptr %s.i, align 4
              store i64 0, ptr %s.l, align 8
              store i64 0, ptr %s.n, align 8
              store float 0x0000000000000000, ptr %s.f, align 4
              store float 0x0000000000000000, ptr %s.f2, align 4
              store double 0x0000000000000000, ptr %s.d, align 8
              store double 0x0000000000000000, ptr %s.negativeZero, align 8
              store double 0x0000000000000000, ptr %s.infinity, align 8
              br label %entry

            entry:
              store i1 true, ptr %s.flag, align 1
              store i8 -1, ptr %s.b, align 1
              store i16 -32768, ptr %s.s, align 2
              store i32 -1, ptr %s.i, align 4
              store i64 -9223372036854775808, ptr %s.l, align 8
              store i64 -5, ptr %s.n, align 8
              store float 0x3FB99999A0000000, ptr %s.f, align 4
              store float 0x3FF8000000000000, ptr %s.f2, align 4
              store double 0x3FB999999999999A, ptr %s.d, align 8
              store double 0x8000000000000000, ptr %s.negativeZero, align 8
              store double 0x7FF0000000000000, ptr %s.infinity, align 8
              ret void
            }

            """), DefineOf(AddConstants, "Program_Constants"));
    }

    // ---- nint ----

    [Fact]
    public void Nint_is_i64_with_8_byte_pointers()
    {
        Assert.Equal(Text("""
            define internal i64 @Program_Native(i64 %p.a, i32 %p.x) nounwind {
            allocas:
              %s.n = alloca i64, align 8
              store i64 0, ptr %s.n, align 8
              br label %entry

            entry:
              %t0 = sext i32 %p.x to i64
              %t1 = add i64 %p.a, %t0
              %t2 = add i64 %t1, -5
              store i64 %t2, ptr %s.n, align 8
              %t3 = load i64, ptr %s.n, align 8
              ret i64 %t3
            }

            """), DefineOf(AddNative, "Program_Native", LlvmTargets.Make("test", null, 8)));
    }

    [Fact]
    public void Nint_is_i32_with_4_byte_pointers()
    {
        Assert.Equal(Text("""
            define internal i64 @Program_Native(i32 %p.a, i32 %p.x) nounwind {
            allocas:
              %s.n = alloca i32, align 4
              store i32 0, ptr %s.n, align 4
              br label %entry

            entry:
              %t0 = add i32 %p.a, %p.x
              %t1 = add i32 %t0, -5
              store i32 %t1, ptr %s.n, align 4
              %t2 = load i32, ptr %s.n, align 4
              %t3 = sext i32 %t2 to i64
              ret i64 %t3
            }

            """), DefineOf(AddNative, "Program_Native", LlvmTargets.Make("test", null, 4)));
    }

    [Fact]
    public void Other_pointer_sizes_are_an_error()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));

        var error = Assert.Throws<ChipSharpException>(() => Generator.Generate(module, LlvmTargets.Make("avr", null, 2)));
        Assert.Equal("Target 'avr' has 2-byte pointers; the llvm code generator supports 4-byte and 8-byte pointers only.",
            error.Message);
    }

    // ---- names ----

    [Theory]
    [InlineData("Program.Square", "Program_Square")]
    [InlineData("<<Main>$>g__Logit|0_0", "__Main___g__Logit_0_0")]
    [InlineData("1st.Method", "_1st_Method")]
    [InlineData("ChipSharp.Runtime.WriteLineInt32", "ChipSharp_Runtime_WriteLineInt32")]
    [InlineData("Outer+Inner.M", "Outer_Inner_M")]
    [InlineData("Prögram.Main", "Pr_gram_Main")]
    public void A_display_name_becomes_a_symbol_of_letters_digits_and_underscores(string displayName, string symbol)
    {
        Assert.Equal(symbol, LlvmCodeGenerator.SymbolName(displayName));
    }

    [Fact]
    public void A_local_function_of_top_level_statements_gets_a_plain_symbol()
    {
        var module = new TirModule("Test");
        var logit = module.AddFunction("<<Main>$>g__Logit|0_0", TirType.Void);
        new TirBuilder(logit.AddBlock("entry")).Return();
        var main = module.AddFunction("<Program>$.<Main>$", TirType.Void);
        var b = new TirBuilder(main.AddBlock("entry"));
        b.Call(logit);
        b.Return();
        module.EntryPoint = main;

        var ll = Generate(module);

        Assert.Contains("define internal void @__Main___g__Logit_0_0() nounwind {", ll);
        Assert.Contains("  call void @__Main___g__Logit_0_0()\n", ll);
        Assert.Contains("  call void @_Program____Main__()\n", ll);
    }

    [Fact]
    public void Overloads_with_the_same_display_name_get_2_3_and_so_on()
    {
        var module = new TirModule("Test");
        var overloads = new[] { TirType.I32, TirType.I64, TirType.F64 }
            .Select(type =>
            {
                var print = module.AddFunction("Program.Print", TirType.Void, ("value", type));
                new TirBuilder(print.AddBlock("entry")).Return();
                return print;
            })
            .ToList();
        var main = module.AddFunction("Program.Main", TirType.Void);
        var b = new TirBuilder(main.AddBlock("entry"));
        b.Call(overloads[2], b.Const(TirType.F64, 0.5));
        b.Call(overloads[0], b.Const(TirType.I32, 1));
        b.Call(overloads[1], b.Const(TirType.I64, 2));
        b.Return();
        module.EntryPoint = main;

        var ll = Generate(module);

        Assert.Contains("define internal void @Program_Print(i32 %p.value)", ll);
        Assert.Contains("define internal void @Program_Print_2(i64 %p.value)", ll);
        Assert.Contains("define internal void @Program_Print_3(double %p.value)", ll);
        Assert.Contains(Text("""
              call void @Program_Print_3(double 0x3FE0000000000000)
              call void @Program_Print(i32 1)
              call void @Program_Print_2(i64 2)
            """), ll);
    }

    [Fact]
    public void External_functions_keep_their_symbol_and_a_defined_function_that_clashes_moves_aside()
    {
        var module = new TirModule("Test");
        var mine = module.AddFunction("Program.Log", TirType.Void);
        new TirBuilder(mine.AddBlock("entry")).Return();
        var runtime = module.AddExternalFunction("Program.Log", TirType.Void, ("value", TirType.I32));
        var entry = module.AddFunction("chipsharp_entry", TirType.Void);
        var b = new TirBuilder(entry.AddBlock("entry"));
        b.Call(mine);
        b.Call(runtime, b.Const(TirType.I32, 7));
        b.Return();
        module.EntryPoint = entry;

        var ll = Generate(module);

        Assert.Contains("declare void @Program_Log(i32) nounwind\n", ll);
        Assert.Contains("define internal void @Program_Log_2() nounwind {", ll);
        Assert.Contains("define internal void @chipsharp_entry_2() nounwind {", ll);
        Assert.Contains(Text("""
              call void @Program_Log_2()
              call void @Program_Log(i32 7)
            """), ll);
        Assert.EndsWith(Text("""
            define i32 @chipsharp_entry() nounwind {
            entry:
              call void @chipsharp_entry_2()
              ret i32 0
            }

            """), ll);
    }

    [Fact]
    public void Two_external_functions_with_the_same_symbol_are_an_error()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        module.AddExternalFunction("Runtime.Print", TirType.Void, ("value", TirType.I32));
        module.AddExternalFunction("Runtime_Print", TirType.Void, ("value", TirType.I64));

        var error = Assert.Throws<ChipSharpException>(() => Generator.Generate(module, LlvmTargets.Host));
        Assert.Equal("External functions 'Runtime.Print' and 'Runtime_Print' would both have the symbol " +
            "'Runtime_Print'. The runtime implements each external function under its symbol, so they must differ: " +
            "rename one of them.", error.Message);
    }

    [Fact]
    public void An_external_function_cannot_take_the_entry_symbol()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        module.AddExternalFunction("chipsharp.entry", TirType.Void);

        var error = Assert.Throws<ChipSharpException>(() => Generator.Generate(module, LlvmTargets.Host));
        Assert.Equal("External function 'chipsharp.entry' would have the symbol 'chipsharp_entry', which ChipSharp " +
            "keeps for the program's entry.", error.Message);
    }

    [Fact]
    public void Local_names_stay_unique_and_valid()
    {
        Assert.Equal(Text("""
            define internal i32 @Program_Names(i32 %p.a_b, i32 %p.a_b_2, i32 %p._1x) nounwind {
            allocas:
              %s.a_b = alloca i32, align 4
              store i32 0, ptr %s.a_b, align 4
              br label %allocas_2

            allocas_2:
              %t0_2 = add i32 %p.a_b, %p.a_b_2
              store i32 %t0_2, ptr %s.a_b, align 4
              br label %t0

            t0:
              br label %_1st

            _1st:
              %t1 = load i32, ptr %s.a_b, align 4
              %t2 = add i32 %t1, %p._1x
              ret i32 %t2
            }

            """), DefineOf(AddNames, "Program_Names"));
    }

    // ---- the program entry ----

    [Fact]
    public void Chipsharp_entry_returns_0_after_a_void_entry_point()
    {
        Assert.EndsWith(Text("""
            define i32 @chipsharp_entry() nounwind {
            entry:
              call void @Program_Main()
              ret i32 0
            }

            """), Generate(TirSamples.WithMain(new TirModule("Test"))));
    }

    [Fact]
    public void Chipsharp_entry_returns_what_an_int_entry_point_returns()
    {
        var module = new TirModule("Test");
        var main = module.AddFunction("Program.Main", TirType.I32);
        var b = new TirBuilder(main.AddBlock("entry"));
        b.Return(b.Const(TirType.I32, 7));
        module.EntryPoint = main;

        Assert.EndsWith(Text("""
            define internal i32 @Program_Main() nounwind {
            allocas:
              br label %entry

            entry:
              ret i32 7
            }

            define i32 @chipsharp_entry() nounwind {
            entry:
              %result = call i32 @Program_Main()
              ret i32 %result
            }

            """), Generate(module));
    }

    [Fact]
    public void An_entry_point_that_returns_something_other_than_int_is_an_error()
    {
        var module = new TirModule("Test");
        var main = module.AddFunction("Program.Main", TirType.I64);
        var b = new TirBuilder(main.AddBlock("entry"));
        b.Return(b.Const(TirType.I64, 7));
        module.EntryPoint = main;

        var error = Assert.Throws<ChipSharpException>(() => Generator.Generate(module, LlvmTargets.Host));
        Assert.Equal("The entry point 'Program.Main' returns i64; it must return void or i32 (int).", error.Message);
    }

    [Fact]
    public void An_entry_point_with_parameters_is_an_error()
    {
        var module = new TirModule("Test");
        var main = module.AddFunction("Program.Main", TirType.Void, ("argc", TirType.I32));
        new TirBuilder(main.AddBlock("entry")).Return();
        module.EntryPoint = main;

        var error = Assert.Throws<ChipSharpException>(() => Generator.Generate(module, LlvmTargets.Host));
        Assert.Equal("The entry point 'Program.Main' takes parameters; it must take none.", error.Message);
    }

    [Fact]
    public void A_module_without_an_entry_point_fails_verification()
    {
        var module = new TirModule("Test");
        AddSquare(module);

        var error = Assert.Throws<TirException>(() => Generator.Generate(module, LlvmTargets.Host));
        Assert.Equal("Module 'Test': no entry point is set.", error.Message);
    }

    // ---- verification, targets, files ----

    [Fact]
    public void Verifier_errors_surface_before_anything_is_written()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        var bad = module.AddFunction("Program.Bad", TirType.I32);
        var b = new TirBuilder(bad.AddBlock("entry"));
        b.Binary(TirBinaryOp.Add, b.Const(TirType.I32, 1), b.Const(TirType.I64, 2));
        b.Return();

        var error = Assert.Throws<TirException>(() => Generator.Generate(module, LlvmTargets.Host));
        Assert.Equal(Text("""
            Function 'Program.Bad', block 'entry', '%2 = add i32 %0, %1': the operands have different types: i32 and i64.
            Function 'Program.Bad', block 'entry', 'return': returns nothing, but the function returns i32.
            """), error.Message);
    }

    [Fact]
    public void Uses_the_triple_of_the_pico_target()
    {
        var ll = Generate(SquareProgram(), LlvmTargets.Pico);

        Assert.StartsWith(Text("""
            source_filename = "Demo"
            target triple = "thumbv6m-none-eabi"

            """), ll);
    }

    [Fact]
    public void A_target_without_a_triple_gets_no_triple_line()
    {
        var ll = Generate(TirSamples.WithMain(new TirModule("Test")), LlvmTargets.Make("test", null, 4));

        Assert.StartsWith("source_filename = \"Test\"\n\ndefine internal void @Program_Main()", ll);
    }

    [Fact]
    public void Odd_module_names_are_quoted_and_make_a_safe_file_name()
    {
        var code = Generator.Generate(TirSamples.WithMain(new TirModule("My \"App\"\\Ä")), LlvmTargets.Host);

        var file = Assert.Single(code.Files);
        Assert.Equal("My _App__Ä.ll", file.FileName);
        Assert.StartsWith("source_filename = \"My \\22App\\22\\5C\\C3\\84\"\n", file.Content);
    }

    [Fact]
    public void The_same_module_always_gives_the_same_text()
    {
        Assert.Equal(Generate(Everything()), Generate(Everything()));
    }

    [Fact]
    public void Is_registered_as_llvm()
    {
        var registry = new ComponentRegistry().Register(new LlvmCodeGenerator());

        Assert.IsType<LlvmCodeGenerator>(registry.Get<ICodeGenerator>("llvm"));
    }

    private static TirType Type(TirTypeKind kind) => ValueTypes.Single(t => t.Kind == kind);

    private static string Llvm(TirTypeKind kind) => kind switch
    {
        TirTypeKind.Bool => "i1",
        TirTypeKind.F32 => "float",
        TirTypeKind.F64 => "double",
        _ => Type(kind).Name,
    };
}
