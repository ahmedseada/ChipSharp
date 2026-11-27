using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Ir;
using ChipSharp.Tests.Llvm;
using ChipSharp.Tests.Tir;
using static ChipSharp.Tests.Llvm.LlvmSamples;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The LLVM the generator writes for an AVR (the Arduino Uno's ATmega328P): its data layout, 2-byte pointers and no
/// alignment, functions in address space 1, and constant data that stays in flash, read with address space 1 loads
/// where TIR shows the address is flash's, through the module's reader where it may be either.
/// </summary>
public sealed class AvrCodeTests
{
    private static readonly LlvmCodeGenerator Generator = new();

    [Fact]
    public void The_module_says_the_avr_data_layout_before_its_triple()
    {
        var ll = Generate(TirSamples.WithMain(new TirModule("Demo")));

        Assert.StartsWith(Text("""
            source_filename = "Demo"
            target datalayout = "e-P1-p:16:8-i8:8-i16:8-i32:8-i64:8-f32:8-f64:8-n8:16-a:8"
            target triple = "avr"

            """), ll);
    }

    [Fact]
    public void Other_targets_say_no_data_layout()
    {
        var ll = Assert.Single(Generator.Generate(TirSamples.WithMain(new TirModule("Demo")), LlvmTargets.Pico).Files).Content;

        Assert.DoesNotContain("datalayout", ll, StringComparison.Ordinal);
    }

    [Fact]
    public void Structs_have_2_byte_pointers_and_no_padding_and_int_stays_32_bits_and_double_64()
    {
        // struct Node { byte Tag; void* Next; int Value; double Weight; nint Size; }: 1 + 2 + 4 + 8 + 2 bytes
        var node = new TirStructType("Program.Node", ("Tag", TirType.I8), ("Next", TirType.Ptr), ("Value", TirType.I32),
            ("Weight", TirType.F64), ("Size", TirType.NativeInt));
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var function = module.AddFunction("Program.SizeOfNode", TirType.I32, ("p", TirType.Ptr));
        var b = new TirBuilder(function.AddBlock("entry"));
        b.Store(b.FieldAddress(function.Parameters[0], node, "Value"), b.Const(TirType.I32, 7));
        b.Return(b.SizeOf(node));

        var ll = Generate(module);

        Assert.Contains("%struct.Program_Node = type { i8, ptr, i32, double, i16 }\n", ll, StringComparison.Ordinal);
        var define = Define(ll, "Program_SizeOfNode");
        Assert.Contains("store i32 7, ptr %t0, align 1", define, StringComparison.Ordinal);
        Assert.Contains("ret i32 17", define, StringComparison.Ordinal);
        Assert.Equal([0L, 1L, 3L, 7L, 15L], new TirLayout(2, 1).OffsetsOf(node));
    }

    [Fact]
    public void A_table_stays_in_flash_and_an_element_is_read_with_an_address_space_1_load()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var digits = module.AddArray("Program.Digits", new TirArrayType(TirType.I8, 3),
            [new TirConst(TirType.I8, 63), new TirConst(TirType.I8, 6), new TirConst(TirType.I8, 91)], isConstant: true);
        var counts = module.AddArray("Program.counts", new TirArrayType(TirType.I16, 2), null, isConstant: false);
        var function = module.AddFunction("Program.Digit", TirType.I8, ("i", TirType.I32));
        var b = new TirBuilder(function.AddBlock("entry"));
        var count = b.Load(TirType.I16, b.ElementAddress(b.AddressOf(counts), TirType.I16, function.Parameters[0]));
        b.Store(b.ElementAddress(b.AddressOf(counts), TirType.I16, b.Const(TirType.I32, 1)), count);
        b.Return(b.Load(TirType.I8, b.ElementAddress(b.AddressOf(digits), TirType.I8, function.Parameters[0])));

        var ll = Generate(module);

        Assert.Contains(Text("""
            @Program_Digits = internal unnamed_addr constant [3 x i8] [i8 63, i8 6, i8 91], section ".progmem.data", align 1
            @Program_counts = internal global [2 x i16] zeroinitializer, align 1
            """), ll, StringComparison.Ordinal);
        Assert.Equal(Text("""
            define internal i8 @Program_Digit(i32 %p.i) nounwind {
            allocas:
              br label %entry

            entry:
              %t0 = getelementptr i16, ptr @Program_counts, i32 %p.i
              %t1 = load i16, ptr %t0, align 1
              %t2 = getelementptr i16, ptr @Program_counts, i32 1
              store i16 %t1, ptr %t2, align 1
              %t3 = getelementptr i8, ptr getelementptr (i8, ptr @Program_Digits, i16 -32768), i32 %p.i
              %t4 = getelementptr i8, ptr addrspace(1) addrspacecast (ptr @Program_Digits to ptr addrspace(1)), i32 %p.i
              %t5 = load i8, ptr addrspace(1) %t4, align 1
              ret i8 %t5
            }

            """), Define(ll, "Program_Digit"));
    }

    [Fact]
    public void An_address_that_may_be_flash_s_is_read_through_the_reader_and_one_only_of_ram_plainly()
    {
        // Pick returns the table's address or a slot's; Ram only ever gets a slot's
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var table = module.AddArray("Program.Table", new TirArrayType(TirType.I32, 2),
            [new TirConst(TirType.I32, 10), new TirConst(TirType.I32, 20)], isConstant: true);
        var read = module.AddFunction("Program.Read", TirType.I32, ("p", TirType.Ptr));
        new TirBuilder(read.AddBlock("entry")).Return(new TirBuilder(read.Blocks[0]).Load(TirType.I32, read.Parameters[0]));
        var ram = module.AddFunction("Program.Ram", TirType.I32, ("p", TirType.Ptr));
        var rb = new TirBuilder(ram.AddBlock("entry"));
        rb.Return(rb.Load(TirType.I32, ram.Parameters[0], isVolatile: true));
        var caller = module.AddFunction("Program.Caller", TirType.I32);
        var local = caller.AddSlot("local", TirType.I32);
        var b = new TirBuilder(caller.AddBlock("entry"));
        var fromTable = b.Call(read, b.AddressOf(table));
        var fromSlot = b.Call(read, b.AddressOf(local));
        b.Return(b.Binary(TirBinaryOp.Add, b.Binary(TirBinaryOp.Add, fromTable, fromSlot), b.Call(ram, b.AddressOf(local))));

        var ll = Generate(module);

        Assert.Contains("  %t0 = call i32 @chipsharp.read.i32(ptr %p.p)\n", Define(ll, "Program_Read"), StringComparison.Ordinal);
        Assert.Contains("  %t0 = load volatile i32, ptr %p.p, align 1\n", Define(ll, "Program_Ram"), StringComparison.Ordinal);
        Assert.Contains("call i32 @Program_Read(ptr getelementptr (i8, ptr @Program_Table, i16 -32768))",
            Define(ll, "Program_Caller"), StringComparison.Ordinal);
        Assert.Equal(Text("""
            define internal i32 @chipsharp.read.i32(ptr %address) nounwind {
            entry:
              %bits = ptrtoint ptr %address to i16
              %in.flash = icmp slt i16 %bits, 0
              br i1 %in.flash, label %flash, label %ram

            flash:
              %tagged = addrspacecast ptr %address to ptr addrspace(1)
              %flash.address = getelementptr i8, ptr addrspace(1) %tagged, i16 -32768
              %flash.value = load i32, ptr addrspace(1) %flash.address, align 1
              ret i32 %flash.value

            ram:
              %ram.value = load i32, ptr %address, align 1
              ret i32 %ram.value
            }

            """), Define(ll, "chipsharp.read.i32"));
    }

    [Fact]
    public void A_flash_address_stored_in_memory_makes_what_is_read_back_from_it_go_through_the_reader()
    {
        // static byte* current = &Table[0]; ... *current
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var table = module.AddConstant("<table>", [1, 2, 3]);
        var current = module.AddGlobal("Program.current", TirType.Ptr);
        var counter = module.AddGlobal("Program.counter", TirType.Ptr);
        var function = module.AddFunction("Program.Next", TirType.I8);
        var b = new TirBuilder(function.AddBlock("entry"));
        b.Store(b.AddressOf(current), b.AddressOf(table));
        var inCurrent = b.Load(TirType.I8, b.Load(TirType.Ptr, b.AddressOf(current)));
        var inCounter = b.Load(TirType.I8, b.Load(TirType.Ptr, b.AddressOf(counter)));
        b.Return(b.Binary(TirBinaryOp.Add, inCurrent, inCounter));

        var define = Define(Generate(module), "Program_Next");

        Assert.Contains("store ptr getelementptr (i8, ptr @_table_, i16 -32768), ptr @Program_current, align 1",
            define, StringComparison.Ordinal);
        Assert.Contains("%t1 = call i8 @chipsharp.read.i8(ptr %t0)", define, StringComparison.Ordinal);
        Assert.Contains("%t3 = load i8, ptr %t2, align 1", define, StringComparison.Ordinal);
    }

    [Fact]
    public void A_string_literal_stays_in_flash_and_its_address_has_bit_15_set()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var stringType = module.AddArrayType("string", TirType.I8);
        stringType.ExternalSymbol = "chipsharp_string_type";
        var text = module.AddConstantObject("<text 0>", stringType, 2, "hi"u8.ToArray());
        var write = module.AddExternalFunction("ChipSharp.Runtime.WriteLineString", TirType.Void, ("value", TirType.Ref));
        var function = module.AddFunction("Program.Hello", TirType.I32);
        var b = new TirBuilder(function.AddBlock("entry"));
        var literal = b.AddressOf(text);
        b.Call(write, literal);
        b.Return(b.ArrayLength(literal));

        var ll = Generate(module);

        Assert.Contains("@chipsharp_string_type = external constant i8, align 2\n", ll, StringComparison.Ordinal);
        Assert.Contains("@_text_0_ = private unnamed_addr constant <{ ptr, i32, [2 x i8], [2 x i8] }> " +
            "<{ ptr @chipsharp_string_type, i32 2, [2 x i8] zeroinitializer, [2 x i8] c\"hi\" }>, " +
            "section \".progmem.data\", align 1\n", ll, StringComparison.Ordinal);
        var define = Define(ll, "Program_Hello");
        Assert.Contains("call void @ChipSharp_Runtime_WriteLineString(ptr getelementptr (i8, ptr @_text_0_, i16 -32768))",
            define, StringComparison.Ordinal);
        Assert.Contains(Text("""
              %t1 = getelementptr i8, ptr addrspace(1) addrspacecast (ptr @_text_0_ to ptr addrspace(1)), i32 2
              %t2 = load i32, ptr addrspace(1) %t1, align 1
            """), define, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_descriptor_stays_in_flash_with_flash_s_addresses_and_function_addresses_of_address_space_1()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var show = module.AddFunction("Program.Show", TirType.Void, ("this", TirType.Ref));
        new TirBuilder(show.AddBlock("entry")).Return();
        var shape = module.AddClass("Program.Shape", TirTypeDescriptor.ObjectStruct("Program.Shape", ("Size", TirType.I32)));
        shape.VirtualMethods.Add(show);
        var empty = module.AddObject("<shape>", shape.ObjectType!, shape, [null]);
        var call = module.AddFunction("Program.CallShow", TirType.Void);
        var b = new TirBuilder(call.AddBlock("entry"));
        var target = b.Call(module.AddFunction("Program.Target", TirType.Ptr, ("o", TirType.Ref)), b.AddressOf(empty));
        b.CallIndirect(TirType.Void, target, b.AddressOf(empty));
        b.Return();
        var targetFunction = module.Functions.Single(f => f.Name == "Program.Target");
        var tb = new TirBuilder(targetFunction.AddBlock("entry"));
        var type = tb.TypeOf(targetFunction.Parameters[0]);
        var virtuals = tb.FieldAddress(type, TirTypeDescriptor.Layout, TirTypeDescriptor.VirtualsField);
        tb.Return(tb.Load(TirType.Ptr, tb.ElementAddress(virtuals, TirType.Ptr, tb.Const(TirType.I32, 0))));

        var ll = Generate(module);

        Assert.Contains("@type.Program_Shape.name = private unnamed_addr constant [14 x i8] c\"Program.Shape\\00\", " +
            "section \".progmem.data\", align 1\n", ll, StringComparison.Ordinal);
        Assert.Contains("ptr getelementptr (i8, ptr @type.Program_Shape.name, i16 -32768), ptr null, i32 0, i32 1, " +
            "[1 x ptr] [ptr addrspacecast (ptr addrspace(1) @Program_Show to ptr)] }, section \".progmem.data\", align 2\n",
            ll, StringComparison.Ordinal);
        Assert.Contains("@_shape_ = internal unnamed_addr constant %struct.Program_Shape " +
            "{ ptr getelementptr (i8, ptr @type.Program_Shape, i16 -32768), i32 0 }, section \".progmem.data\", align 1\n",
            ll, StringComparison.Ordinal);
        Assert.Contains(Text("""
              %t1 = addrspacecast ptr %t0 to ptr addrspace(1)
              call void %t1(ptr getelementptr (i8, ptr @_shape_, i16 -32768))
            """), Define(ll, "Program_CallShow"), StringComparison.Ordinal);
        var target2 = Define(ll, "Program_Target");
        Assert.Contains("%t0 = call ptr @chipsharp.read.ptr(ptr %p.o)", target2, StringComparison.Ordinal);
        Assert.Contains("%t3 = call ptr @chipsharp.read.ptr(ptr %t2)", target2, StringComparison.Ordinal);
    }

    [Fact]
    public void A_function_s_address_is_a_constant_of_address_space_1()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var square = AddSquare(module);
        var function = module.AddFunction("Program.Through", TirType.I32);
        var b = new TirBuilder(function.AddBlock("entry"));
        b.Return(b.CallIndirect(TirType.I32, b.AddressOf(square), b.Const(TirType.I32, 3)));

        Assert.Contains(Text("""
              %t0 = addrspacecast ptr addrspacecast (ptr addrspace(1) @Program_Square to ptr) to ptr addrspace(1)
              %t1 = call i32 %t0(i32 3)
            """), Define(Generate(module), "Program_Through"), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_separate_flash_constant_data_is_ordinary_read_only_data_read_with_plain_loads()
    {
        // "separateFlash": false: the linker script copies it to RAM, as avr-gcc does without PROGMEM
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var table = module.AddConstant("<table>", [1, 2]);
        var function = module.AddFunction("Program.First", TirType.I8, ("p", TirType.Ptr));
        var b = new TirBuilder(function.AddBlock("entry"));
        var first = b.Load(TirType.I8, b.ElementAddress(b.AddressOf(table), TirType.I8, b.Const(TirType.I32, 0)));
        b.Return(b.Binary(TirBinaryOp.Add, first, b.Load(TirType.I8, function.Parameters[0])));

        var ll = Generate(module, AvrCode.Uno(("separateFlash", "false")));

        Assert.Contains("@_table_ = private unnamed_addr constant [2 x i8] c\"\\01\\02\", align 1\n", ll, StringComparison.Ordinal);
        Assert.DoesNotContain("progmem", ll, StringComparison.Ordinal);
        Assert.DoesNotContain("chipsharp.read", ll, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_changes_for_a_program_without_constant_data()
    {
        var module = Everything();

        var ll = Generate(module);

        Assert.DoesNotContain("progmem", ll, StringComparison.Ordinal);
        Assert.DoesNotContain("chipsharp.read", ll, StringComparison.Ordinal);
    }

    [Fact]
    public void A_heap_a_separate_flash_on_another_chip_and_2_byte_pointers_elsewhere_are_errors()
    {
        var withHeap = TirSamples.WithMain(new TirModule("Demo"));
        var type = withHeap.AddClass("Program.Node", TirTypeDescriptor.ObjectStruct("Program.Node", ("Value", TirType.I32)));
        var make = withHeap.AddFunction("Program.Make", TirType.Ref);
        var b = new TirBuilder(make.AddBlock("entry"));
        b.Return(b.NewObject(type));
        var heap = Assert.Throws<ChipSharpException>(() =>
            Generator.Generate(withHeap, AvrCode.UnoWithHeap(1024)));
        Assert.Contains("the heap is not supported yet on target 'uno'", heap.Message, StringComparison.Ordinal);

        var plain = TirSamples.WithMain(new TirModule("Demo"));
        var pico = LlvmTargets.Pico;
        var separate = Assert.Throws<ChipSharpException>(() => Generator.Generate(plain, new TargetDescription
        {
            Name = "arm-harvard", InheritanceChain = ["arm-harvard"], Backend = "llvm", Triple = pico.Triple,
            PointerSize = 4, Endian = Endianness.Little, Settings = new(),
            Capabilities = new Dictionary<string, string> { ["separateFlash"] = "true" },
        }));
        Assert.Contains("knows that only of an AVR", separate.Message, StringComparison.Ordinal);

        var msp430 = Assert.Throws<ChipSharpException>(() =>
            Generator.Generate(plain, LlvmTargets.Make("msp430", "msp430", pointerSize: 2)));
        Assert.Contains("the 2-byte pointers of an AVR", msp430.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Data_of_32_KB_is_too_large_for_16_bit_addresses()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        module.AddArray("Program.buffer", new TirArrayType(TirType.I8, 32768), null, isConstant: false);

        var error = Assert.Throws<ChipSharpException>(() => Generate(module));

        Assert.Equal("The global Program.buffer takes 32768 bytes, more than the 32767 that 16-bit addresses reach on " +
            "the target. Make it smaller.", error.Message);
    }

    private static string Generate(TirModule module, TargetDescription? target = null) =>
        Assert.Single(Generator.Generate(module, target ?? AvrCode.Uno()).Files).Content;
}
