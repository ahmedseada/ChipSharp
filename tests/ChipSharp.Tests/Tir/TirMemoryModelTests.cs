using ChipSharp.Ir;

namespace ChipSharp.Tests.Tir;

/// <summary>The memory model's types, globals and instructions: what building them refuses and what they give back.</summary>
public sealed class TirMemoryModelTests
{
    // ---- types ----

    [Fact]
    public void Ptr_is_a_shared_instance_that_is_neither_an_integer_nor_an_aggregate()
    {
        Assert.Equal(TirTypeKind.Ptr, TirType.Ptr.Kind);
        Assert.Equal("ptr", TirType.Ptr.ToString());
        Assert.False(TirType.Ptr.IsInteger);
        Assert.False(TirType.Ptr.IsAggregate);
        Assert.True(TirType.Ptr == TirType.Ptr);
        Assert.True(TirType.Ptr != TirType.NativeInt);
    }

    [Fact]
    public void Array_types_with_the_same_element_type_and_count_are_equal()
    {
        var a = new TirArrayType(TirType.I32, 4);
        var b = new TirArrayType(TirType.I32, 4);

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(new TirArrayType(a, 2) == new TirArrayType(b, 2));
        Assert.False(a == new TirArrayType(TirType.I32, 5));
        Assert.False(a == new TirArrayType(TirType.I64, 4));
        Assert.False(a == TirType.I32);
        Assert.Single(new HashSet<TirType> { a, b });
        Assert.Equal("[4 x i32]", a.Name);
        Assert.Equal("[2 x [4 x i32]]", new TirArrayType(a, 2).ToString());
        Assert.Equal(TirTypeKind.Array, a.Kind);
        Assert.True(a.IsAggregate);
        Assert.Same(TirType.I32, a.Element);
        Assert.Equal(4, a.Count);
    }

    [Fact]
    public void Struct_types_are_equal_only_to_themselves()
    {
        var point = TirMemorySamples.Point;
        var lookalike = new TirStructType("Program.Point", ("X", TirType.I32), ("Y", TirType.I32));

        Assert.True(point == TirMemorySamples.Point);
        Assert.False(point == lookalike);
        Assert.NotEqual<TirType>(point, lookalike);
        Assert.False(new TirArrayType(point, 2) == new TirArrayType(lookalike, 2));
        Assert.Equal("Program.Point", point.ToString());
        Assert.Equal(TirTypeKind.Struct, point.Kind);
        Assert.True(point.IsAggregate);
        Assert.False(point.IsInteger);
    }

    [Fact]
    public void A_struct_has_its_fields_in_order_and_finds_them_by_name()
    {
        var line = TirMemorySamples.Line;

        Assert.Equal(["From", "To", "Color"], line.Fields.Select(f => f.Name));
        Assert.Equal([0, 1, 2], line.Fields.Select(f => f.Index));
        Assert.Same(TirMemorySamples.Point, line.Fields[1].Type);
        Assert.Same(line.Fields[2], line.FindField("Color"));
        Assert.Null(line.FindField("color"));
        Assert.Equal("Program.Point From", line.Fields[0].ToString());
    }

    [Fact]
    public void A_struct_needs_fields_with_unique_names_and_no_void()
    {
        Assert.Equal("Struct 'Program.Empty' has no fields; a struct needs at least one " +
            "(C# gives an empty struct one byte: give it one i8 field).",
            Assert.Throws<TirException>(() => new TirStructType("Program.Empty")).Message);
        Assert.Equal("Struct 'Program.S' has two fields named A.",
            Assert.Throws<TirException>(() =>
                new TirStructType("Program.S", ("A", TirType.I32), ("A", TirType.I64))).Message);
        Assert.Equal("Struct 'Program.S': field A cannot have type void.",
            Assert.Throws<TirException>(() => new TirStructType("Program.S", ("A", TirType.Void))).Message);
        Assert.Throws<ArgumentException>(() => new TirStructType(" ", ("A", TirType.I32)));
    }

    [Fact]
    public void An_array_needs_at_least_one_element_that_is_not_void()
    {
        Assert.Equal("An array of i32 needs at least one element, not 0.",
            Assert.Throws<TirException>(() => new TirArrayType(TirType.I32, 0)).Message);
        Assert.Equal("An array cannot have elements of type void.",
            Assert.Throws<TirException>(() => new TirArrayType(TirType.Void, 3)).Message);
    }

    [Fact]
    public void Ptr_structs_and_arrays_have_no_constants()
    {
        Assert.Equal("A constant cannot have type ptr; null is 'zero ptr', and inttoptr turns a number into an address.",
            Assert.Throws<TirException>(() => new TirConst(TirType.Ptr, 0)).Message);
        Assert.Equal("A constant cannot have type Program.Point; only bool, integers and floats have constants " +
            "('zero' makes a value of any type with every byte zero).",
            Assert.Throws<TirException>(() => new TirConst(TirMemorySamples.Point, 0)).Message);
        Assert.Equal("A constant cannot have type [10 x i32]; only bool, integers and floats have constants " +
            "('zero' makes a value of any type with every byte zero).",
            Assert.Throws<TirException>(() => new TirConst(TirMemorySamples.TenInts, 0)).Message);
        Assert.Equal("A floating-point constant needs type f32 or f64, not ptr.",
            Assert.Throws<TirException>(() => new TirConst(TirType.Ptr, 1.5)).Message);
    }

    // ---- globals ----

    [Fact]
    public void A_global_has_its_name_type_and_initial_value()
    {
        var module = new TirModule("Demo");
        var counter = module.AddGlobal("Program.counter", TirType.I32, new TirConst(TirType.I32, 5));
        var origin = module.AddGlobal("Program.origin", TirMemorySamples.Point);

        Assert.Equal([counter, origin], module.Globals);
        Assert.Same(module, counter.Module);
        Assert.Equal(1, origin.Index);
        Assert.Equal("@Program.counter", counter.ToString());
        Assert.Equal(5, counter.InitialValue!.IntegerValue);
        Assert.Null(origin.InitialValue);
        Assert.Same(TirMemorySamples.Point, origin.Type);
    }

    [Fact]
    public void A_global_must_have_a_unique_name_a_type_and_a_matching_constant()
    {
        var module = new TirModule("Demo");
        module.AddGlobal("Program.counter", TirType.I32);
        var main = module.AddFunction("Program.Main", TirType.Void);
        var inBlock = new TirBuilder(main.AddBlock("entry")).Const(TirType.I64, 1);

        Assert.Equal("Module 'Demo' already has a global named @Program.counter.",
            Assert.Throws<TirException>(() => module.AddGlobal("Program.counter", TirType.I64)).Message);
        Assert.Equal("Module 'Demo': global @Program.nothing cannot have type void.",
            Assert.Throws<TirException>(() => module.AddGlobal("Program.nothing", TirType.Void)).Message);
        Assert.Equal("Module 'Demo': global @Program.big holds i64, but its initial value is i32.",
            Assert.Throws<TirException>(() =>
                module.AddGlobal("Program.big", TirType.I64, new TirConst(TirType.I32, 1))).Message);
        Assert.Equal("Module 'Demo': the initial value of global @Program.big, '%0 = const i64 1', is an instruction " +
            "of block 'entry' of function 'Program.Main'; give a constant that is in no block.",
            Assert.Throws<TirException>(() => module.AddGlobal("Program.big", TirType.I64, inBlock)).Message);
        Assert.Throws<ArgumentException>(() => module.AddGlobal("", TirType.I32));
        Assert.Single(module.Globals);
    }

    // ---- instructions ----

    [Fact]
    public void The_builder_makes_addresses_and_returns_each_instruction()
    {
        var module = new TirModule("Demo");
        var counter = module.AddGlobal("Program.counter", TirType.I32);
        var f = module.AddFunction("Program.F", TirType.Void, ("n", TirType.I32));
        var p = f.AddSlot("p", TirMemorySamples.Point);
        var b = new TirBuilder(f.AddBlock("entry"));

        var global = b.AddressOf(counter);
        var slot = b.AddressOf(p);
        var field = b.FieldAddress(slot, TirMemorySamples.Point, "Y");
        var load = b.Load(TirType.I32, field, isVolatile: true);
        var store = b.Store(global, load);
        var element = b.ElementAddress(global, TirType.I32, f.Parameters[0]);
        var number = b.PtrToInt(element, TirType.NativeInt);
        var back = b.IntToPtr(number);

        Assert.Same(counter, global.Global);
        Assert.Same(p, slot.Slot);
        Assert.Equal(1, field.FieldIndex);
        Assert.Same(TirMemorySamples.Point.Fields[1], field.Field);
        Assert.True(load.IsVolatile);
        Assert.Equal(TirType.I32, load.Type);
        Assert.False(store.IsVolatile);
        Assert.False(store.HasResult);
        Assert.Equal([global, load], store.Operands);
        Assert.Equal([global, f.Parameters[0]], element.Operands);
        Assert.Equal(TirType.NativeInt, number.Type);
        Assert.All(new TirInstruction[] { global, slot, field, element, back }, i => Assert.Equal(TirType.Ptr, i.Type));
        Assert.Equal([global, slot, field, load, store, element, number, back], f.EntryBlock!.Instructions);
    }

    [Fact]
    public void A_field_address_by_name_needs_a_field_of_that_name()
    {
        var f = new TirModule("Demo").AddFunction("Program.F", TirType.Void, ("p", TirType.Ptr));
        var b = new TirBuilder(f.AddBlock("entry"));

        var error = Assert.Throws<TirException>(() => b.FieldAddress(f.Parameters[0], TirMemorySamples.Point, "Z"));

        Assert.Equal("Struct 'Program.Point' has no field named Z.", error.Message);
        Assert.Null(new TirFieldAddress(f.Parameters[0], TirMemorySamples.Point, 2).Field);
    }

    [Fact]
    public void A_module_finds_every_struct_it_uses_once_with_nested_ones_first()
    {
        var module = new TirModule("Demo");
        var inner = new TirStructType("Program.Inner", ("A", TirType.I8));
        var outer = new TirStructType("Program.Outer", ("Items", new TirArrayType(inner, 3)));
        var unused = new TirStructType("Program.Unused", ("A", TirType.I8));
        var viaLoad = new TirStructType("Program.Loaded", ("A", TirType.I16));
        var viaField = new TirStructType("Program.Field", ("A", TirType.I32));
        var viaElement = new TirStructType("Program.Element", ("A", TirType.I64));
        module.AddGlobal("Program.items", new TirArrayType(outer, 2));
        var f = module.AddFunction("Program.F", TirMemorySamples.Line, ("p", TirType.Ptr));
        f.AddSlot("point", TirMemorySamples.Point);
        var b = new TirBuilder(f.AddBlock("entry"));
        var p = f.Parameters[0];
        b.Load(viaLoad, p);
        b.FieldAddress(p, viaField, 0);
        b.ElementAddress(p, viaElement, b.Const(TirType.I32, 1));
        b.Return(b.Load(TirMemorySamples.Line, p));

        Assert.DoesNotContain(unused, module.FindStructTypes());
        Assert.Equal(["Program.Inner", "Program.Outer", "Program.Point", "Program.Line", "Program.Loaded",
            "Program.Field", "Program.Element"], module.FindStructTypes().Select(s => s.Name));
    }
}
