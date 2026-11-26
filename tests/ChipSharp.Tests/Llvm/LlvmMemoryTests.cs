using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Ir;
using ChipSharp.Tests.Tir;
using static ChipSharp.Tests.Llvm.LlvmSamples;

namespace ChipSharp.Tests.Llvm;

/// <summary>The LLVM the generator writes for globals, structs, arrays, addresses, loads and stores.</summary>
public sealed class LlvmMemoryTests
{
    private static readonly LlvmCodeGenerator Generator = new();

    private static readonly TirStructType Node =
        new("Program.Node", ("Tag", TirType.I8), ("Next", TirType.Ptr), ("Value", TirType.I32));

    private static string Generate(TirModule module, TargetDescription? target = null) =>
        Assert.Single(Generator.Generate(module, target ?? LlvmTargets.Host).Files).Content;

    /// <summary>The define of a function added to a module that also has an empty Main.</summary>
    private static string DefineOf(Func<TirModule, TirFunction> add, string symbol, TargetDescription? target = null)
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        add(module);
        return Define(Generate(module, target), symbol);
    }

    // ---- struct types and globals ----

    [Fact]
    public void Writes_the_struct_types_and_the_globals_before_the_functions()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        TirMemorySamples.AddCounter(module);
        module.AddGlobal("Program.total", TirType.I64);
        module.AddGlobal("Program.ready", TirType.Bool, new TirConst(true));
        module.AddGlobal("Program.scale", TirType.F64, new TirConst(TirType.F64, 1.5));
        module.AddGlobal("Program.size", TirType.NativeInt, new TirConst(TirType.NativeInt, -4));
        module.AddGlobal("Program.head", TirType.Ptr);
        module.AddGlobal("Program.lines", new TirArrayType(TirMemorySamples.Line, 2));
        module.AddGlobal("Program.nodes", new TirArrayType(Node, 3));

        Assert.StartsWith(Text("""
            source_filename = "Demo"
            target triple = "x86_64-pc-windows-msvc"

            %struct.Program_Point = type { i32, i32 }
            %struct.Program_Line = type { %struct.Program_Point, %struct.Program_Point, i8 }
            %struct.Program_Node = type { i8, ptr, i32 }

            @Program_counter = internal global i32 5, align 4
            @Program_total = internal global i64 0, align 8
            @Program_ready = internal global i1 true, align 1
            @Program_scale = internal global double 0x3FF8000000000000, align 8
            @Program_size = internal global i64 -4, align 8
            @Program_head = internal global ptr null, align 8
            @Program_lines = internal global [2 x %struct.Program_Line] zeroinitializer, align 4
            @Program_nodes = internal global [3 x %struct.Program_Node] zeroinitializer, align 8

            define internal void @Program_Main() nounwind {
            """), Generate(module));
    }

    [Fact]
    public void Globals_are_aligned_for_4_byte_pointers_on_the_pico()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        module.AddGlobal("Program.size", TirType.NativeInt, new TirConst(TirType.NativeInt, -4));
        module.AddGlobal("Program.head", TirType.Ptr);
        module.AddGlobal("Program.nodes", new TirArrayType(Node, 3));
        module.AddGlobal("Program.ticks", TirType.I64);

        Assert.StartsWith(Text("""
            source_filename = "Demo"
            target triple = "thumbv6m-none-eabi"

            %struct.Program_Node = type { i8, ptr, i32 }

            @Program_size = internal global i32 -4, align 4
            @Program_head = internal global ptr null, align 4
            @Program_nodes = internal global [3 x %struct.Program_Node] zeroinitializer, align 4
            @Program_ticks = internal global i64 0, align 8

            define internal void @Program_Main() nounwind {
            """), Generate(module, LlvmTargets.Pico));
    }

    [Fact]
    public void A_module_without_structs_or_globals_has_neither_section()
    {
        Assert.StartsWith(Text("""
            source_filename = "Test"
            target triple = "x86_64-pc-windows-msvc"

            define internal void @Program_Main() nounwind {
            """), Generate(TirSamples.WithMain(new TirModule("Test"))));
    }

    [Fact]
    public void A_global_whose_symbol_is_taken_gets_2_3_and_so_on()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        var clash = module.AddGlobal("Program.Main", TirType.I32);
        module.AddGlobal("Program.value", TirType.I32);
        var lookalike = module.AddGlobal("Program_value", TirType.I32);
        module.AddExternalFunction("Program.print", TirType.Void, ("value", TirType.I32));
        module.AddGlobal("Program.print", TirType.I32);
        var main = module.EntryPoint!;
        var b = new TirBuilder(main.AddBlock("more"));
        b.Store(b.AddressOf(lookalike), b.Load(TirType.I32, b.AddressOf(clash)));
        b.Return();

        var ll = Generate(module);

        Assert.Contains(Text("""
            @Program_Main_2 = internal global i32 0, align 4
            @Program_value = internal global i32 0, align 4
            @Program_value_2 = internal global i32 0, align 4
            @Program_print_2 = internal global i32 0, align 4

            declare void @Program_print(i32) nounwind

            define internal void @Program_Main() nounwind {
            """), ll);
        Assert.Contains(Text("""
              %t0 = load i32, ptr @Program_Main_2, align 4
              store i32 %t0, ptr @Program_value_2, align 4
            """), ll);
    }

    [Fact]
    public void Struct_types_whose_names_clash_once_made_LLVM_safe_get_2()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        module.AddGlobal("Program.a", new TirStructType("Outer.Inner", ("A", TirType.I8)));
        module.AddGlobal("Program.b", new TirStructType("Outer+Inner", ("B", TirType.I16)));

        Assert.Contains(Text("""
            %struct.Outer_Inner = type { i8 }
            %struct.Outer_Inner_2 = type { i16 }

            @Program_a = internal global %struct.Outer_Inner zeroinitializer, align 1
            @Program_b = internal global %struct.Outer_Inner_2 zeroinitializer, align 2
            """), Generate(module));
    }

    // ---- addresses, loads and stores ----

    [Fact]
    public void A_global_is_read_and_written_through_its_address()
    {
        Assert.Equal(Text("""
            define internal void @Program_Increment() nounwind {
            allocas:
              br label %entry

            entry:
              %t0 = load i32, ptr @Program_counter, align 4
              %t1 = add i32 %t0, 1
              store i32 %t1, ptr @Program_counter, align 4
              ret void
            }

            """), DefineOf(m => TirMemorySamples.AddCounter(m).Increment, "Program_Increment"));
    }

    [Fact]
    public void A_struct_slot_is_a_zeroed_alloca_and_its_fields_are_getelementptr_into_it()
    {
        Assert.Equal(Text("""
            define internal i32 @Program_LengthSquared(i32 %p.x, i32 %p.y) nounwind {
            allocas:
              %s.p = alloca %struct.Program_Point, align 4
              store %struct.Program_Point zeroinitializer, ptr %s.p, align 4
              br label %entry

            entry:
              %t0 = getelementptr %struct.Program_Point, ptr %s.p, i32 0, i32 0
              store i32 %p.x, ptr %t0, align 4
              %t1 = getelementptr %struct.Program_Point, ptr %s.p, i32 0, i32 1
              store i32 %p.y, ptr %t1, align 4
              %t2 = load i32, ptr %t0, align 4
              %t3 = load i32, ptr %t1, align 4
              %t4 = mul i32 %t2, %t2
              %t5 = mul i32 %t3, %t3
              %t6 = add i32 %t4, %t5
              ret i32 %t6
            }

            """), DefineOf(TirMemorySamples.AddLengthSquared, "Program_LengthSquared"));
    }

    [Fact]
    public void A_struct_is_passed_returned_and_copied_whole()
    {
        Assert.Equal(Text("""
            define internal %struct.Program_Point @Program_Swap(%struct.Program_Point %p.p) nounwind {
            allocas:
              %s.p = alloca %struct.Program_Point, align 4
              %s.q = alloca %struct.Program_Point, align 4
              store %struct.Program_Point zeroinitializer, ptr %s.p, align 4
              store %struct.Program_Point zeroinitializer, ptr %s.q, align 4
              br label %entry

            entry:
              store %struct.Program_Point %p.p, ptr %s.p, align 4
              %t0 = getelementptr %struct.Program_Point, ptr %s.q, i32 0, i32 0
              %t1 = getelementptr %struct.Program_Point, ptr %s.p, i32 0, i32 1
              %t2 = load i32, ptr %t1, align 4
              store i32 %t2, ptr %t0, align 4
              %t3 = getelementptr %struct.Program_Point, ptr %s.q, i32 0, i32 1
              %t4 = getelementptr %struct.Program_Point, ptr %s.p, i32 0, i32 0
              %t5 = load i32, ptr %t4, align 4
              store i32 %t5, ptr %t3, align 4
              %t6 = load %struct.Program_Point, ptr %s.q, align 4
              ret %struct.Program_Point %t6
            }

            """), DefineOf(TirMemorySamples.AddSwap, "Program_Swap"));
    }

    [Fact]
    public void An_array_slot_is_a_zeroed_alloca_and_its_elements_are_getelementptr_over_the_element_type()
    {
        var define = DefineOf(TirMemorySamples.AddSumOfSquares, "Program_SumOfSquares");

        Assert.StartsWith(Text("""
            define internal i32 @Program_SumOfSquares() nounwind {
            allocas:
              %s.squares = alloca [10 x i32], align 4
              %s.i = alloca i32, align 4
              %s.j = alloca i32, align 4
              %s.sum = alloca i32, align 4
              store [10 x i32] zeroinitializer, ptr %s.squares, align 4
            """), define);
        Assert.Contains(Text("""
            fillBody:
              %t2 = load i32, ptr %s.i, align 4
              %t3 = getelementptr i32, ptr %s.squares, i32 %t2
              %t4 = mul i32 %t2, %t2
              store i32 %t4, ptr %t3, align 4
            """), define);
        Assert.Contains(Text("""
            addBody:
              %t8 = load i32, ptr %s.j, align 4
              %t9 = getelementptr i32, ptr %s.squares, i32 %t8
              %t10 = load i32, ptr %t9, align 4
            """), define);
    }

    [Fact]
    public void Element_indexes_keep_their_width_and_nested_fields_chain()
    {
        var define = DefineOf(AddAddresses, "Program_Addresses", LlvmTargets.Make("test", null, 4));

        Assert.Contains(Text("""
              %s.lines = alloca [4 x %struct.Program_Line], align 4
            """), define);
        Assert.Contains(Text("""
            entry:
              %t0 = getelementptr %struct.Program_Line, ptr %s.lines, i32 1
              %t1 = getelementptr %struct.Program_Line, ptr %t0, i32 0, i32 1
              %t2 = getelementptr %struct.Program_Point, ptr %t1, i32 0, i32 1
              store i32 7, ptr %t2, align 4
              %t3 = getelementptr %struct.Program_Line, ptr %t0, i32 0, i32 2
              store i8 3, ptr %t3, align 1
              %t4 = getelementptr i16, ptr %p.p, i32 1
              store ptr %t4, ptr %s.keep, align 4
              %t5 = getelementptr %struct.Program_Line, ptr %s.lines, i64 2
            """), define);
        Assert.Contains("  %t9 = getelementptr i16, ptr %p.p, i64 2\n", define);
        Assert.Contains("  %t14 = getelementptr i16, ptr %p.p, i32 3\n", define); // nint is i32 here
    }

    [Fact]
    public void Pointers_compare_as_unsigned_numbers()
    {
        Assert.Contains(Text("""
              %t15 = icmp eq ptr %p.p, %s.lines
              store i1 %t15, ptr %s.same, align 1
              %t16 = icmp ne ptr %p.p, %s.lines
              store i1 %t16, ptr %s.same, align 1
              %t17 = icmp ult ptr %p.p, %s.lines
              store i1 %t17, ptr %s.same, align 1
              %t18 = icmp ule ptr %p.p, %s.lines
              store i1 %t18, ptr %s.same, align 1
              %t19 = icmp ugt ptr %p.p, %s.lines
              store i1 %t19, ptr %s.same, align 1
              %t20 = icmp uge ptr %p.p, %s.lines
            """), DefineOf(AddAddresses, "Program_Addresses"));
    }

    [Fact]
    public void Volatile_accesses_and_address_conversions_with_8_byte_pointers()
    {
        Assert.Equal(Text("""
            define internal i64 @Program_Toggle(i32 %p.address, i32 %p.bits) nounwind {
            allocas:
              br label %entry

            entry:
              %t0 = inttoptr i32 %p.address to ptr
              %t1 = load volatile i32, ptr %t0, align 4
              %t2 = xor i32 %t1, %p.bits
              store volatile i32 %t2, ptr %t0, align 4
              %t3 = ptrtoint ptr %t0 to i64
              ret i64 %t3
            }

            """), DefineOf(TirMemorySamples.AddRegisterToggle, "Program_Toggle"));
    }

    [Fact]
    public void Volatile_accesses_and_address_conversions_with_4_byte_pointers()
    {
        Assert.Equal(Text("""
            define internal i32 @Program_Toggle(i32 %p.address, i32 %p.bits) nounwind {
            allocas:
              br label %entry

            entry:
              %t0 = inttoptr i32 %p.address to ptr
              %t1 = load volatile i32, ptr %t0, align 4
              %t2 = xor i32 %t1, %p.bits
              store volatile i32 %t2, ptr %t0, align 4
              %t3 = ptrtoint ptr %t0 to i32
              ret i32 %t3
            }

            """), DefineOf(TirMemorySamples.AddRegisterToggle, "Program_Toggle", LlvmTargets.Pico));
    }

    [Fact]
    public void A_register_at_a_constant_address_is_an_inttoptr_of_the_constant()
    {
        Assert.Contains(Text("""
            entry:
              %t0 = inttoptr i32 -805306352 to ptr
              store volatile i32 33554432, ptr %t0, align 4
              ret void
            """), DefineOf(m =>
        {
            // Registers.Write(0xd0000010, 1u << 25): SIO GPIO_OUT on the RP2040
            var f = m.AddFunction("Program.LedOn", TirType.Void);
            var b = new TirBuilder(f.AddBlock("entry"));
            b.Store(b.IntToPtr(b.Const(TirType.I32, 0xd0000010)), b.Const(TirType.I32, 1 << 25), isVolatile: true);
            b.Return();
            return f;
        }, "Program_LedOn", LlvmTargets.Pico));
    }

    [Fact]
    public void Zero_is_written_inline_as_zeroinitializer_null_or_0()
    {
        Assert.Equal(Text("""
            define internal ptr @Program_Reset(ptr %p.line) nounwind {
            allocas:
              br label %entry

            entry:
              %t0 = getelementptr %struct.Program_Line, ptr %p.line, i32 0, i32 1
              store %struct.Program_Point zeroinitializer, ptr %t0, align 4
              %t1 = getelementptr %struct.Program_Line, ptr %p.line, i32 0, i32 2
              store i8 0, ptr %t1, align 1
              ret ptr null
            }

            """), DefineOf(m =>
        {
            // static void* Reset(Line* line) { line->To = default; line->Color = 0; return null; }
            var f = m.AddFunction("Program.Reset", TirType.Ptr, ("line", TirType.Ptr));
            var b = new TirBuilder(f.AddBlock("entry"));
            var line = f.Parameters[0];
            b.Store(b.FieldAddress(line, TirMemorySamples.Line, "To"), b.Zero(TirMemorySamples.Point));
            b.Store(b.FieldAddress(line, TirMemorySamples.Line, "Color"), b.Zero(TirType.I8));
            b.Return(b.Zero(TirType.Ptr));
            return f;
        }, "Program_Reset"));
    }

    [Theory]
    [InlineData(4, "12")]
    [InlineData(8, "24")]
    public void Sizeof_is_the_size_of_the_type_on_the_target_written_inline(int pointerSize, string size)
    {
        var define = DefineOf(m =>
        {
            var f = m.AddFunction("Program.Size", TirType.I32);
            var b = new TirBuilder(f.AddBlock("entry"));
            b.Return(b.SizeOf(Node));
            return f;
        }, "Program_Size", LlvmTargets.Make("test", null, pointerSize));

        Assert.EndsWith($"entry:\n  ret i32 {size}\n}}\n", define);
    }

    [Fact]
    public void Sizeof_a_type_of_4_GB_or_more_is_an_error()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        var huge = new TirArrayType(TirType.I64, int.MaxValue);
        var f = module.AddFunction("Program.Size", TirType.I32);
        var b = new TirBuilder(f.AddBlock("entry"));
        b.Return(b.SizeOf(huge));

        var error = Assert.Throws<ChipSharpException>(() => Generate(module));
        Assert.Equal("Function 'Program.Size': [2147483647 x i64] takes 17179869176 bytes, more than sizeof's 32 bits " +
            "can count.", error.Message);
    }

    [Fact]
    public void Every_memory_type_is_loaded_and_stored_with_its_alignment()
    {
        var define = DefineOf(AddAccesses, "Program_Accesses", LlvmTargets.Make("test", null, 4));

        Assert.Contains(Text("""
              %s.local0 = alloca i1, align 1
              %s.local1 = alloca i8, align 1
              %s.local2 = alloca i16, align 2
              %s.local3 = alloca i32, align 4
              %s.local4 = alloca i64, align 8
              %s.local5 = alloca i32, align 4
              %s.local6 = alloca float, align 4
              %s.local7 = alloca double, align 8
              %s.local8 = alloca ptr, align 4
              %s.local9 = alloca %struct.Program_Point, align 4
              %s.local10 = alloca %struct.Program_Line, align 4
              %s.local11 = alloca [10 x i32], align 4
              %s.local12 = alloca [3 x %struct.Program_Point], align 4
              %s.local13 = alloca [2 x [3 x i1]], align 1
              store i1 false, ptr %s.local0, align 1
            """), define);
        Assert.Contains(Text("""
              store ptr null, ptr %s.local8, align 4
              store %struct.Program_Point zeroinitializer, ptr %s.local9, align 4
            """), define);
        Assert.Contains(Text("""
            entry:
              %t0 = load i1, ptr @Program_global0, align 1
              store i1 %t0, ptr %s.local0, align 1
              %t1 = load volatile i1, ptr %s.local0, align 1
              store volatile i1 %t1, ptr @Program_global0, align 1
            """), define);
        Assert.Contains(Text("""
              %t8 = load i64, ptr @Program_global4, align 8
              store i64 %t8, ptr %s.local4, align 8
            """), define);
        Assert.Contains(Text("""
              %t20 = load %struct.Program_Line, ptr @Program_global10, align 4
              store %struct.Program_Line %t20, ptr %s.local10, align 4
              %t21 = load volatile %struct.Program_Line, ptr %s.local10, align 4
              store volatile %struct.Program_Line %t21, ptr @Program_global10, align 4
              store %struct.Program_Line zeroinitializer, ptr %s.local10, align 4
            """), define);
        Assert.Contains(Text("""
              store volatile i1 %t1, ptr @Program_global0, align 1
              store i1 false, ptr %s.local0, align 1
              %t2 = load i8, ptr @Program_global1, align 1
            """), define);
        Assert.Contains(Text("""
              store volatile double %t15, ptr @Program_global7, align 8
              store double 0x0000000000000000, ptr %s.local7, align 8
              %t16 = load ptr, ptr @Program_global8, align 4
              store ptr %t16, ptr %s.local8, align 4
              %t17 = load volatile ptr, ptr %s.local8, align 4
              store volatile ptr %t17, ptr @Program_global8, align 4
              store ptr null, ptr %s.local8, align 4
            """), define);
        Assert.Contains(Text("""
              %t22 = load [10 x i32], ptr @Program_global11, align 4
              store [10 x i32] %t22, ptr %s.local11, align 4
            """), define);
    }

    [Fact]
    public void Ptrtoint_and_inttoptr_take_any_integer_width()
    {
        var define = DefineOf(AddAddresses, "Program_Addresses");

        Assert.Contains("  %t21 = ptrtoint ptr %p.p to i8\n", define);
        Assert.Contains("  %t23 = inttoptr i8 %t22 to ptr\n", define);
        Assert.Contains("  %t30 = ptrtoint ptr %p.p to i64\n", define);
        Assert.Contains("  %t33 = ptrtoint ptr %p.p to i64\n", define); // nint
        Assert.Contains("  %t35 = inttoptr i64 %t34 to ptr\n", define);
    }

    [Fact]
    public void The_layout_of_the_generator_is_the_tir_layout_of_the_target()
    {
        Assert.Equal(4, LlvmTypes.For(LlvmTargets.Pico).Layout.PointerSize);
        Assert.Equal(8, LlvmTypes.For(LlvmTargets.Host).Layout.PointerSize);
        Assert.Equal(8, LlvmTypes.For(LlvmTargets.Host).Alignment(Node));
        Assert.Equal(4, LlvmTypes.For(LlvmTargets.Riscv32).Alignment(Node));
    }
}
