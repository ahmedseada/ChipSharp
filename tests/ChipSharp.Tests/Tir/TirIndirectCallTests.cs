using ChipSharp.Backend.Llvm;
using ChipSharp.Ir;
using ChipSharp.Tests.Llvm;

namespace ChipSharp.Tests.Tir;

/// <summary>
/// A call through a function's address (<see cref="TirCallIndirect"/>), read from a type descriptor's virtual method
/// table through <see cref="TirTypeDescriptor.Layout"/>: how it prints, what the verifier refuses, the LLVM it becomes,
/// where the parts of a descriptor are on 4- and 8-byte pointers, and the .NET name a descriptor holds.
/// </summary>
public sealed class TirIndirectCallTests
{
    private readonly TirModule _module = TirSamples.WithMain(new TirModule("Test"));

    /// <summary>
    /// <c>i32 Program.Use(ref %shape)</c>: loads slot 1 of the virtual method table of %shape's class and calls it with
    /// %shape and 2; and a void call through slot 0.
    /// </summary>
    private TirFunction AddUse()
    {
        var root = _module.AddClass("System.Object", TirTypeDescriptor.ObjectStruct("System.Object"));
        var shape = _module.AddClass("Program.Shape", TirObjectSamples.ShapeType, root);
        var reset = _module.AddFunction("Program.Shape.Reset", TirType.Void, ("this", TirType.Ref));
        new TirBuilder(reset.AddBlock("entry")).Return();
        var scale = _module.AddFunction("Program.Shape.Scale", TirType.I32, ("this", TirType.Ref), ("by", TirType.I32));
        var body = new TirBuilder(scale.AddBlock("entry"));
        body.Return(scale.Parameters[1]);
        shape.VirtualMethods.Add(reset);
        shape.VirtualMethods.Add(scale);
        shape.RuntimeName = "Program+Shape";

        var use = _module.AddFunction("Program.Use", TirType.I32, ("shape", TirType.Ref));
        var ir = new TirBuilder(use.AddBlock("entry"));
        var self = use.Parameters[0];
        ir.NullCheck(self);
        var table = ir.FieldAddress(ir.TypeOf(self), TirTypeDescriptor.Layout, TirTypeDescriptor.VirtualsField);
        ir.CallIndirect(TirType.Void, ir.Load(TirType.Ptr, ir.ElementAddress(table, TirType.Ptr, ir.Const(TirType.I32, 0))),
            self);
        var method = ir.Load(TirType.Ptr, ir.ElementAddress(table, TirType.Ptr, ir.Const(TirType.I32, 1)));
        ir.Return(ir.CallIndirect(TirType.I32, method, self, ir.Const(TirType.I32, 2)));
        return use;
    }

    [Fact]
    public void A_call_through_a_method_table_prints_with_its_type_and_the_address_it_calls()
    {
        var use = AddUse();

        Assert.Empty(TirVerifier.FindErrors(_module));
        Assert.Equal("""
            function i32 Program.Use(ref %shape)
            entry:
              nullcheck %shape
              %0 = typeof %shape
              %1 = fieldaddr <type descriptor> %0, virtuals
              %2 = const i32 0
              %3 = elementaddr ptr %1, %2
              %4 = load ptr [%3]
              call indirect void %4(%shape)
              %5 = const i32 1
              %6 = elementaddr ptr %1, %5
              %7 = load ptr [%6]
              %8 = const i32 2
              %9 = call indirect i32 %7(%shape, %8)
              return %9

            """.ReplaceLineEndings("\n"), TirPrinter.Print(use));
        Assert.Contains("struct <type descriptor> { i32 size, i32 element_size, i32 flags, i32 reference_count, ptr " +
            "references, ptr base, ptr name, ptr interfaces, i32 interface_count, i32 virtual_count, [1 x ptr] virtuals }",
            TirPrinter.Print(_module));
    }

    [Fact]
    public void An_indirect_call_takes_an_address()
    {
        var function = _module.AddFunction("Program.F", TirType.Void, ("n", TirType.I32), ("o", TirType.Ref));
        var ir = new TirBuilder(function.AddBlock("entry"));
        ir.CallIndirect(TirType.Void, function.Parameters[0]);
        ir.CallIndirect(TirType.I32, function.Parameters[1], function.Parameters[0]);
        ir.Return();

        Assert.Equal(
        [
            "Function 'Program.F', block 'entry', 'call indirect void %n()': call indirect takes the address of a function " +
                "(ptr), not i32.",
            "Function 'Program.F', block 'entry', '%0 = call indirect i32 %o(%n)': call indirect takes the address of a " +
                "function (ptr), not ref.",
        ], TirVerifier.FindErrors(_module));
    }

    [Fact]
    public void LLVM_calls_the_loaded_address_and_the_descriptor_holds_the_dotnet_name_and_the_table()
    {
        AddUse();

        var ll = Assert.Single(new LlvmCodeGenerator().Generate(_module, LlvmTargets.Host).Files).Content;

        Assert.Contains("  call void %t4(ptr %p.shape)\n", ll, StringComparison.Ordinal);
        Assert.Contains("  %t7 = call i32 %t6(ptr %p.shape, i32 2)\n", ll, StringComparison.Ordinal);
        Assert.Contains("@type.Program_Shape.name = private unnamed_addr constant [14 x i8] c\"Program+Shape\\00\"", ll,
            StringComparison.Ordinal);
        Assert.Contains("[2 x ptr] [ptr @Program_Shape_Reset, ptr @Program_Shape_Scale] }", ll, StringComparison.Ordinal);
    }

    /// <summary>
    /// The struct puts each part where the code generator writes it and the runtime reads it: the virtual method table
    /// right after virtual_count, at 4 * 4 + 4 * pointer size + 2 * 4.
    /// </summary>
    [Theory]
    [InlineData(4, new long[] { 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40 })]
    [InlineData(8, new long[] { 0, 4, 8, 12, 16, 24, 32, 40, 48, 52, 56 })]
    public void The_layout_of_a_descriptor_has_its_parts_where_the_runtime_reads_them(int pointerSize, long[] offsets)
    {
        var layout = new TirLayout(pointerSize);

        Assert.Equal(offsets, layout.OffsetsOf(TirTypeDescriptor.Layout));
        Assert.Equal(4 * 4 + 4 * pointerSize + 2 * 4, layout.OffsetOf(TirTypeDescriptor.Layout, TirTypeDescriptor.VirtualsField));
        Assert.Equal([0L, pointerSize], layout.OffsetsOf(TirTypeDescriptor.InterfaceEntry));
        Assert.Equal(["flags", "base", "name", "interfaces", "interface_count", "virtuals"],
            new[]
            {
                TirTypeDescriptor.FlagsField, TirTypeDescriptor.BaseField, TirTypeDescriptor.NameField,
                TirTypeDescriptor.InterfacesField, TirTypeDescriptor.InterfaceCountField, TirTypeDescriptor.VirtualsField,
            }.Select(field => TirTypeDescriptor.Layout.Fields[field].Name));
    }
}
