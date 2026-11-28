using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Ir;
using ChipSharp.Tests.Support;
using ChipSharp.Tests.Tir;
using static ChipSharp.Tests.Llvm.LlvmSamples;

namespace ChipSharp.Tests.Llvm;

/// <summary>
/// The LLVM the generator writes for objects: the type descriptors, the calls to the runtime's allocator, the null
/// checks, the static roots and the heap's start, and what it refuses.
/// </summary>
public sealed class LlvmObjectTests : IDisposable
{
    private static readonly LlvmCodeGenerator Generator = new();

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    private static string Generate(TirModule module, TargetDescription? target = null) =>
        Assert.Single(Generator.Generate(module, target ?? LlvmTargets.Host).Files).Content;

    [Fact]
    public void Each_descriptor_is_a_private_constant_with_its_references_name_interfaces_and_virtual_methods()
    {
        var ll = Generate(TirObjectSamples.Add(TirSamples.WithMain(new TirModule("Test"))));

        // 64-bit: Square is 48 bytes, its refs at 16 (Next) and 32, 40 (Pair); an array's elements start at 16
        Assert.Contains(Text("""
            @type.System_Object.name = private unnamed_addr constant [14 x i8] c"System.Object\00", align 1
            @type.System_Object = private constant { i32, i32, i32, i32, ptr, ptr, ptr, ptr, i32, i32, [0 x ptr] } { i32 8, i32 0, i32 0, i32 0, ptr null, ptr null, ptr @type.System_Object.name, ptr null, i32 0, i32 0, [0 x ptr] zeroinitializer }, align 8
            @type.Program_Shape.references = private constant [1 x i32] [i32 16], align 4
            @type.Program_Shape.name = private unnamed_addr constant [14 x i8] c"Program.Shape\00", align 1
            @type.Program_Shape = private constant { i32, i32, i32, i32, ptr, ptr, ptr, ptr, i32, i32, [0 x ptr] } { i32 24, i32 0, i32 0, i32 1, ptr @type.Program_Shape.references, ptr @type.System_Object, ptr @type.Program_Shape.name, ptr null, i32 0, i32 0, [0 x ptr] zeroinitializer }, align 8
            @type.Program_Square.references = private constant [3 x i32] [i32 16, i32 32, i32 40], align 4
            @type.Program_Square.name = private unnamed_addr constant [15 x i8] c"Program.Square\00", align 1
            @type.Program_Square.interface0 = private constant [1 x ptr] [ptr @Program_Square_Sides], align 8
            @type.Program_Square.interfaces = private constant [1 x { ptr, ptr }] [{ ptr, ptr } { ptr @type.Program_IArea, ptr @type.Program_Square.interface0 }], align 8
            @type.Program_Square = private constant { i32, i32, i32, i32, ptr, ptr, ptr, ptr, i32, i32, [2 x ptr] } { i32 48, i32 0, i32 0, i32 3, ptr @type.Program_Square.references, ptr @type.Program_Shape, ptr @type.Program_Square.name, ptr @type.Program_Square.interfaces, i32 1, i32 2, [2 x ptr] [ptr @Program_Square_Sides, ptr null] }, align 8
            @type.Program_IArea.name = private unnamed_addr constant [14 x i8] c"Program.IArea\00", align 1
            @type.Program_IArea = private constant { i32, i32, i32, i32, ptr, ptr, ptr, ptr, i32, i32, [0 x ptr] } { i32 0, i32 0, i32 2, i32 0, ptr null, ptr null, ptr @type.Program_IArea.name, ptr null, i32 0, i32 0, [0 x ptr] zeroinitializer }, align 8
            @type.int__.name = private unnamed_addr constant [6 x i8] c"int[]\00", align 1
            @type.int__ = private constant { i32, i32, i32, i32, ptr, ptr, ptr, ptr, i32, i32, [0 x ptr] } { i32 16, i32 4, i32 1, i32 0, ptr null, ptr null, ptr @type.int__.name, ptr null, i32 0, i32 0, [0 x ptr] zeroinitializer }, align 8
            @type.Program_Shape__.references = private constant [1 x i32] [i32 0], align 4
            @type.Program_Shape__.name = private unnamed_addr constant [16 x i8] c"Program.Shape[]\00", align 1
            @type.Program_Shape__ = private constant { i32, i32, i32, i32, ptr, ptr, ptr, ptr, i32, i32, [0 x ptr] } { i32 16, i32 8, i32 1, i32 1, ptr @type.Program_Shape__.references, ptr @type.Program_Shape, ptr @type.Program_Shape__.name, ptr null, i32 0, i32 0, [0 x ptr] zeroinitializer }, align 8
            """), ll);
    }

    [Fact]
    public void Objects_are_made_by_the_runtime_and_reached_through_their_header_length_and_elements()
    {
        var ll = Generate(TirObjectSamples.Add(TirSamples.WithMain(new TirModule("Test"))), LlvmTargets.Pico);

        // 32-bit: the length is at 4, the elements at 8
        Assert.Equal(Text("""
            define internal i32 @Program_Use(ptr %p.shape) nounwind {
            allocas:
              br label %entry

            entry:
              %t0 = call ptr @chipsharp_new_object(ptr @type.Program_Square)
              %t1 = getelementptr %struct.Program_Shape, ptr %t0, i32 0, i32 2
              store ptr %p.shape, ptr %t1, align 4
              %t2 = icmp eq ptr %p.shape, null
              br i1 %t2, label %null_reference, label %not_null

            not_null:
              %t3 = getelementptr %struct.Program_Shape, ptr %p.shape, i32 0, i32 1
              %t4 = load i32, ptr %t3, align 4
              %t5 = call ptr @chipsharp_new_array(ptr @type.int__, i32 %t4)
              %t6 = getelementptr i8, ptr %t5, i32 8
              %t7 = getelementptr i32, ptr %t6, i32 0
              %t8 = getelementptr i8, ptr %t5, i32 4
              %t9 = load i32, ptr %t8, align 4
              store i32 %t9, ptr %t7, align 4
              %t10 = call ptr @chipsharp_new_array(ptr @type.Program_Shape__, i32 2)
              %t11 = getelementptr i8, ptr %t10, i32 8
              %t12 = getelementptr ptr, ptr %t11, i32 1
              store ptr %t0, ptr %t12, align 4
              %t13 = load ptr, ptr %t0, align 4
              %t14 = icmp eq ptr %t13, @type.Program_Square
              %t15 = icmp eq ptr %t0, null
              %t16 = zext i1 %t14 to i32
              %t17 = zext i1 %t15 to i32
              %t18 = add i32 %t16, %t17
              ret i32 %t18

            null_reference:
              call void @ChipSharp_Runtime_NullReference()
              unreachable
            }

            """), Define(ll, "Program_Use"));
        Assert.Contains("declare ptr @chipsharp_new_object(ptr) nounwind\n", ll);
        Assert.Contains("declare ptr @chipsharp_new_array(ptr, i32) nounwind\n", ll);
        Assert.Contains("declare void @ChipSharp_Runtime_NullReference() cold noreturn nounwind\n", ll);
    }

    [Fact]
    public void A_program_that_makes_objects_starts_its_heap_with_the_runs_of_refs_in_its_globals_as_static_roots()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        var thing = module.AddClass("Program.Thing", TirTypeDescriptor.ObjectStruct("Program.Thing"));
        var pair = new TirStructType("Program.Pair", ("Key", TirType.I32), ("Item", TirType.Ref));
        module.AddGlobal("Program.count", TirType.I32);
        module.AddGlobal("Program.one", TirType.Ref);
        module.AddGlobal("Program.pairs", new TirArrayType(pair, 2));
        module.AddGlobal("Program.three", new TirArrayType(TirType.Ref, 3));
        var make = module.AddFunction("Program.Make", TirType.Ref);
        var ir = new TirBuilder(make.AddBlock("entry"));
        ir.Return(ir.NewObject(thing));

        var ll = Generate(module, LlvmTargets.Pico);

        Assert.Contains(Text("""
            @chipsharp.roots = private constant [4 x { ptr, i32 }] [{ ptr, i32 } { ptr @Program_one, i32 1 }, { ptr, i32 } { ptr getelementptr (i8, ptr @Program_pairs, i32 4), i32 1 }, { ptr, i32 } { ptr getelementptr (i8, ptr @Program_pairs, i32 12), i32 1 }, { ptr, i32 } { ptr @Program_three, i32 3 }], align 4
            """), ll);
        Assert.Contains(Text("""
            define i32 @chipsharp_entry() nounwind {
            entry:
              call void @chipsharp_heap_initialize(ptr @chipsharp.roots, i32 4)
              call void @Program_Main()
              ret i32 0
            }
            """), ll);
        Assert.Contains("declare void @chipsharp_heap_initialize(ptr, i32) nounwind\n", ll);
    }

    [Fact]
    public void A_program_that_makes_no_objects_has_no_heap_and_one_without_static_refs_passes_no_roots()
    {
        var plain = TirSamples.WithMain(new TirModule("Plain"));
        plain.AddGlobal("Program.one", TirType.Ref);
        var objects = TirSamples.WithMain(new TirModule("Objects"));
        var thing = objects.AddClass("Program.Thing", TirTypeDescriptor.ObjectStruct("Program.Thing"));
        var ir = new TirBuilder(objects.AddFunction("Program.Make", TirType.Void).AddBlock("entry"));
        ir.NewObject(thing);
        ir.Return();

        var withoutHeap = Generate(plain);
        var withHeap = Generate(objects);

        Assert.DoesNotContain("chipsharp_heap_initialize", withoutHeap);
        Assert.DoesNotContain("chipsharp.roots", withoutHeap);
        Assert.Contains("  call void @chipsharp_heap_initialize(ptr null, i64 0)\n  call void @Program_Main()\n", withHeap);
    }

    [Fact]
    public void A_target_without_a_heap_cannot_make_objects()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        var thing = module.AddClass("Program.Thing", TirTypeDescriptor.ObjectStruct("Program.Thing"));
        var ir = new TirBuilder(module.AddFunction("Program.Make", TirType.Ref).AddBlock("entry"));
        ir.Return(ir.NewObject(thing));

        var error = Assert.Throws<ChipSharpException>(() => Generator.Generate(module, LlvmTargets.Riscv32));

        Assert.Equal("Program.Make creates objects ('%0 = newobject Program.Thing'), which needs a heap, and the target " +
            "'riscv32' has none (its \"heap\" capability is false). Build it for a target with a heap, or use structs and " +
            "static methods.", error.Message);
    }

    [Fact]
    public void The_runtime_functions_for_objects_keep_their_symbols()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        var thing = module.AddClass("Program.Thing", TirTypeDescriptor.ObjectStruct("Program.Thing"));
        var check = module.AddExternalFunction("ChipSharp.Runtime.NullReference", TirType.Void);
        var clash = module.AddFunction("chipsharp.new_object", TirType.Ref, ("o", TirType.Ref));
        var ir = new TirBuilder(clash.AddBlock("entry"));
        ir.NullCheck(clash.Parameters[0]);
        ir.Call(check);
        ir.Return(ir.NewObject(thing));

        var ll = Generate(module);

        // the program's function gets another symbol, and the module's own declaration of NullReference is the one
        Assert.Contains("define internal ptr @chipsharp_new_object_2(ptr %p.o) nounwind {", ll);
        Assert.Contains("call ptr @chipsharp_new_object(ptr @type.Program_Thing)", ll);
        Assert.Single(ll.Split('\n'), line => line.StartsWith("declare void @ChipSharp_Runtime_NullReference(", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("host", "-O0")]
    [InlineData("host", "-O2")]
    [InlineData("pico", "-O0")]
    [InlineData("pico", "-O2")]
    public async Task Clang_compiles_the_objects(string targetName, string optimization)
    {
        var target = targetName == "host" ? LlvmTargets.Host : LlvmTargets.Pico;
        var module = TirObjectSamples.Add(TirSamples.WithMain(new TirModule("Objects")));
        var ll = Assert.Single(Generator.Generate(module, target).Files);
        Directory.CreateDirectory(_scratch);
        await File.WriteAllTextAsync(Path.Combine(_scratch, ll.FileName), ll.Content);

        await LlvmTools.ClangAsync(_scratch,
            [$"--target={target.Triple}", optimization, "-Werror", "-Wno-override-module", "-c", ll.FileName, "-o", "out.o"]);

        Assert.True(new FileInfo(Path.Combine(_scratch, "out.o")).Length > 0);
    }
}
