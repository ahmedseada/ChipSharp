using ChipSharp.Ir;

namespace ChipSharp.Tests.Tir;

/// <summary>The verifier's rules for globals, addresses, loads and stores, structs and arrays.</summary>
public sealed class TirMemoryVerifierTests
{
    private readonly TirModule _module = TirSamples.WithMain(new TirModule("Test"));

    private TirFunction Main => _module.EntryPoint!;

    private static TirStructType Point => TirMemorySamples.Point;

    // ---- well-formed memory code passes ----

    [Fact]
    public void Every_memory_sample_passes()
    {
        TirMemorySamples.AddCounter(_module);
        TirMemorySamples.AddLengthSquared(_module);
        TirMemorySamples.AddSwap(_module);
        TirMemorySamples.AddSumOfSquares(_module);
        TirMemorySamples.AddRegisterToggle(_module);

        Assert.Empty(TirVerifier.FindErrors(_module));
    }

    [Fact]
    public void Loads_and_stores_through_addresses_it_cannot_see_into_pass()
    {
        // a ptr parameter or a loaded ptr may point to anything
        var (f, b) = Function(TirType.I64, ("p", TirType.Ptr));
        var holder = f.AddSlot("holder", TirType.Ptr);
        b.Store(holder, f.Parameters[0]);
        var loaded = b.Load(holder);
        b.Store(loaded, b.Const(TirType.I8, 1));
        b.Store(b.FieldAddress(loaded, Point, 1), b.Const(TirType.I32, 2));
        b.Return(b.Load(TirType.I64, b.ElementAddress(f.Parameters[0], TirType.I64, b.Const(TirType.NativeInt, -1))));

        Assert.Empty(TirVerifier.FindErrors(_module));
    }

    [Fact]
    public void An_element_address_takes_an_array_its_element_or_a_row_of_arrays()
    {
        var grid = new TirArrayType(new TirArrayType(TirType.I16, 3), 2);
        var (f, b) = Function(TirType.I16, ("row", TirType.I64), ("column", TirType.NativeInt));
        var cells = f.AddSlot("cells", grid);
        var cellsAddress = b.AddressOf(cells);
        var row = b.ElementAddress(cellsAddress, grid.Element, f.Parameters[0]);
        var cell = b.ElementAddress(row, TirType.I16, f.Parameters[1]);
        var next = b.ElementAddress(cell, TirType.I16, b.Const(TirType.I32, 1));
        b.ElementAddress(cellsAddress, grid, b.Const(TirType.I32, 0));
        b.Return(b.Load(TirType.I16, next));

        Assert.Empty(TirVerifier.FindErrors(_module));
    }

    [Fact]
    public void Structs_and_arrays_live_in_slots_globals_and_defined_functions()
    {
        var line = _module.AddGlobal("Program.line", TirMemorySamples.Line);
        var (f, b) = Function(TirMemorySamples.Line, ("from", Point));
        var copy = f.AddSlot("copy", TirMemorySamples.Line);
        var address = b.AddressOf(line);
        b.Store(b.FieldAddress(address, TirMemorySamples.Line, "From"), f.Parameters[0]);
        var whole = b.Load(TirMemorySamples.Line, address);
        b.Store(copy, whole);
        b.Store(b.AddressOf(copy), whole);
        b.Return(b.Load(copy));

        Assert.Empty(TirVerifier.FindErrors(_module));
    }

    // ---- addresses ----

    [Fact]
    public void A_load_from_something_other_than_ptr_fails()
    {
        var (f, b) = Function(TirType.I32, ("address", TirType.NativeInt));
        b.Return(b.Load(TirType.I32, f.Parameters[0]));

        AssertError("Function 'Program.F', block 'entry', '%0 = load i32 [%address]': " +
            "the address %address is nint; it must be ptr.");
    }

    [Fact]
    public void A_store_to_something_other_than_ptr_fails()
    {
        var (f, b) = Function(TirType.Void, ("address", TirType.I32));
        b.Store(f.Parameters[0], f.Parameters[0], isVolatile: true);
        b.Return();

        AssertError("Function 'Program.F', block 'entry', 'store volatile i32 [%address], %address': " +
            "the address %address is i32; it must be ptr.");
    }

    [Fact]
    public void A_field_or_element_address_of_something_other_than_ptr_fails()
    {
        var (f, b) = Function(TirType.Void, ("address", TirType.I64));
        b.FieldAddress(f.Parameters[0], Point, 0);
        b.ElementAddress(f.Parameters[0], TirType.I8, b.Const(TirType.I32, 0));
        b.Return();

        Assert.Equal(
        [
            "Function 'Program.F', block 'entry', '%0 = fieldaddr Program.Point %address, X': " +
                "the address %address is i64; it must be ptr.",
            "Function 'Program.F', block 'entry', '%2 = elementaddr i8 %address, %1': " +
                "the address %address is i64; it must be ptr.",
        ], TirVerifier.FindErrors(_module));
    }

    [Fact]
    public void A_load_of_another_type_than_the_slot_holds_fails()
    {
        var (f, b) = Function(TirType.I64);
        var x = f.AddSlot("x", TirType.I32);
        b.Return(b.Load(TirType.I64, b.AddressOf(x)));

        AssertError("Function 'Program.F', block 'entry', '%1 = load i64 [%0]': loads i64, but %0 points to i32.");
    }

    [Fact]
    public void A_store_of_another_type_than_the_global_holds_fails()
    {
        var counter = _module.AddGlobal("Program.counter", TirType.I32);
        var (_, b) = Function(TirType.Void);
        b.Store(b.AddressOf(counter), b.Const(TirType.I16, 1));
        b.Return();

        AssertError("Function 'Program.F', block 'entry', 'store i16 [%0], %1': stores i16, but %0 points to i32.");
    }

    [Fact]
    public void A_load_of_another_type_than_the_field_holds_fails()
    {
        var (f, b) = Function(TirType.Void);
        var p = f.AddSlot("p", TirMemorySamples.Line);
        b.Load(TirType.I32, b.FieldAddress(b.AddressOf(p), TirMemorySamples.Line, "To"));
        b.Return();

        AssertError("Function 'Program.F', block 'entry', '%2 = load i32 [%1]': " +
            "loads i32, but %1 points to Program.Point.");
    }

    [Fact]
    public void A_store_of_another_type_than_the_element_fails()
    {
        var (f, b) = Function(TirType.Void, ("p", TirType.Ptr));
        b.Store(b.ElementAddress(f.Parameters[0], TirType.I64, b.Const(TirType.I32, 2)), b.Const(TirType.I32, 7));
        b.Return();

        AssertError("Function 'Program.F', block 'entry', 'store i32 [%1], %2': stores i32, but %1 points to i64.");
    }

    [Fact]
    public void Loading_void_fails()
    {
        var (f, b) = Function(TirType.Void, ("p", TirType.Ptr));
        b.Load(TirType.Void, f.Parameters[0]);
        b.Return();

        AssertError("Function 'Program.F', block 'entry', 'load void [%p]': load cannot produce void.");
    }

    [Fact]
    public void Zero_works_on_any_type_but_void()
    {
        var (f, b) = Function(TirType.Ptr, ("line", TirType.Ptr));
        var squares = f.AddSlot("squares", TirMemorySamples.TenInts);
        b.Store(b.FieldAddress(f.Parameters[0], TirMemorySamples.Line, "To"), b.Zero(Point));
        b.Store(squares, b.Zero(TirMemorySamples.TenInts));
        b.Zero(TirType.Void);
        b.Return(b.Zero(TirType.Ptr));

        AssertError("Function 'Program.F', block 'entry', 'zero void': zero cannot produce void.");
    }

    [Fact]
    public void Sizeof_works_on_any_type_but_void()
    {
        var (_, b) = Function(TirType.I32);
        b.SizeOf(TirMemorySamples.Line);
        b.SizeOf(TirType.Ptr);
        b.SizeOf(TirType.Void);
        b.Return(b.SizeOf(TirMemorySamples.TenInts));

        AssertError("Function 'Program.F', block 'entry', '%2 = sizeof void': void takes no memory, so it has no size.");
    }

    // ---- fields ----

    [Fact]
    public void A_field_number_out_of_range_fails()
    {
        var (f, b) = Function(TirType.Void, ("p", TirType.Ptr));
        b.FieldAddress(f.Parameters[0], Point, 2);
        b.FieldAddress(f.Parameters[0], Point, -1);
        b.Return();

        Assert.Equal(
        [
            "Function 'Program.F', block 'entry', '%0 = fieldaddr Program.Point %p, #2': " +
                "Program.Point has 2 fields; there is no field number 2.",
            "Function 'Program.F', block 'entry', '%1 = fieldaddr Program.Point %p, #-1': " +
                "Program.Point has 2 fields; there is no field number -1.",
        ], TirVerifier.FindErrors(_module));
    }

    [Fact]
    public void A_field_of_another_struct_than_the_slot_holds_fails()
    {
        var (f, b) = Function(TirType.Void);
        var x = f.AddSlot("x", TirType.I64);
        var line = f.AddSlot("line", TirMemorySamples.Line);
        b.FieldAddress(b.AddressOf(x), Point, 0);
        b.FieldAddress(b.AddressOf(line), Point, 0);
        b.Return();

        Assert.Equal(
        [
            "Function 'Program.F', block 'entry', '%1 = fieldaddr Program.Point %0, X': " +
                "%0 points to i64, not to the struct Program.Point.",
            "Function 'Program.F', block 'entry', '%3 = fieldaddr Program.Point %2, X': " +
                "%2 points to Program.Line, not to the struct Program.Point.",
        ], TirVerifier.FindErrors(_module));
    }

    // ---- elements ----

    [Fact]
    public void An_element_address_of_another_element_type_than_the_array_holds_fails()
    {
        var (f, b) = Function(TirType.Void);
        var squares = f.AddSlot("squares", TirMemorySamples.TenInts);
        b.ElementAddress(b.AddressOf(squares), TirType.I64, b.Const(TirType.I32, 3));
        b.Return();

        AssertError("Function 'Program.F', block 'entry', '%2 = elementaddr i64 %0, %1': %0 points to [10 x i32]; " +
            "an element address of i64 needs the address of i64 or of an array of i64.");
    }

    [Theory]
    [InlineData(TirTypeKind.I8)]
    [InlineData(TirTypeKind.I16)]
    [InlineData(TirTypeKind.Bool)]
    public void An_index_that_is_not_i32_i64_or_nint_fails(TirTypeKind kind)
    {
        var type = new[] { TirType.I8, TirType.I16, TirType.Bool }.Single(t => t.Kind == kind);
        var (f, b) = Function(TirType.Void, ("p", TirType.Ptr), ("i", type));
        b.ElementAddress(f.Parameters[0], TirType.I32, f.Parameters[1]);
        b.Return();

        AssertError($"Function 'Program.F', block 'entry', '%0 = elementaddr i32 %p, %i': the index is {type}; " +
            "it must be i32, i64 or nint.");
    }

    [Fact]
    public void An_element_address_of_void_fails()
    {
        var (f, b) = Function(TirType.Void, ("p", TirType.Ptr));
        b.ElementAddress(f.Parameters[0], TirType.Void, b.Const(TirType.I32, 1));
        b.Return();

        AssertError("Function 'Program.F', block 'entry', '%1 = elementaddr void %p, %0': " +
            "elementaddr needs an element type, not void.");
    }

    // ---- numbers and addresses ----

    [Fact]
    public void Ptrtoint_takes_a_ptr_and_produces_an_integer()
    {
        var (f, b) = Function(TirType.Void, ("p", TirType.Ptr), ("n", TirType.NativeInt));
        b.PtrToInt(f.Parameters[1], TirType.NativeInt);
        b.PtrToInt(f.Parameters[0], TirType.F64);
        b.PtrToInt(f.Parameters[0], TirType.Bool);
        b.PtrToInt(f.Parameters[0], TirType.I8);
        b.Return();

        Assert.Equal(
        [
            "Function 'Program.F', block 'entry', '%0 = ptrtoint nint %n to nint': ptrtoint takes a ptr, not nint.",
            "Function 'Program.F', block 'entry', '%1 = ptrtoint ptr %p to f64': " +
                "ptrtoint produces an integer (i8, i16, i32, i64 or nint), not f64.",
            "Function 'Program.F', block 'entry', '%2 = ptrtoint ptr %p to bool': " +
                "ptrtoint produces an integer (i8, i16, i32, i64 or nint), not bool.",
        ], TirVerifier.FindErrors(_module));
    }

    [Fact]
    public void Inttoptr_takes_an_integer()
    {
        var (f, b) = Function(TirType.Void, ("p", TirType.Ptr), ("x", TirType.F32));
        b.IntToPtr(f.Parameters[0]);
        b.IntToPtr(f.Parameters[1]);
        b.IntToPtr(b.Const(TirType.I64, 0x2000_0000));
        b.Return();

        Assert.Equal(
        [
            "Function 'Program.F', block 'entry', '%0 = inttoptr ptr %p to ptr': " +
                "inttoptr takes an integer (i8, i16, i32, i64 or nint), not ptr.",
            "Function 'Program.F', block 'entry', '%1 = inttoptr f32 %x to ptr': " +
                "inttoptr takes an integer (i8, i16, i32, i64 or nint), not f32.",
        ], TirVerifier.FindErrors(_module));
    }

    [Fact]
    public void Convert_does_not_take_ptr_structs_or_arrays()
    {
        var (f, b) = Function(TirType.Void, ("p", TirType.Ptr), ("point", Point), ("n", TirType.NativeInt));
        b.Convert(f.Parameters[0], TirType.NativeInt);
        b.Convert(f.Parameters[2], TirType.Ptr);
        b.Convert(f.Parameters[1], TirType.I64);
        b.Convert(f.Parameters[2], TirMemorySamples.TenInts);
        b.Return();

        Assert.Equal(
        [
            "Function 'Program.F', block 'entry', '%0 = convert ptr %p to nint': " +
                "convert does not take or produce ptr; use ptrtoint or inttoptr.",
            "Function 'Program.F', block 'entry', '%1 = convert nint %n to ptr': " +
                "convert does not take or produce ptr; use ptrtoint or inttoptr.",
            "Function 'Program.F', block 'entry', '%2 = convert Program.Point %point to i64': " +
                "convert works on integers, floats and bool, not on Program.Point.",
            "Function 'Program.F', block 'entry', '%3 = convert nint %n to [10 x i32]': " +
                "convert works on integers, floats and bool, not on [10 x i32].",
        ], TirVerifier.FindErrors(_module));
    }

    // ---- no arithmetic on addresses and aggregates ----

    [Fact]
    public void Arithmetic_on_ptr_fails()
    {
        var (f, b) = Function(TirType.Ptr, ("p", TirType.Ptr));
        b.Return(b.Binary(TirBinaryOp.Add, f.Parameters[0], f.Parameters[0]));

        AssertError("Function 'Program.F', block 'entry', '%0 = add ptr %p, %p': add works on integers and floats, not on ptr.");
    }

    [Fact]
    public void Arithmetic_and_logic_on_a_struct_or_an_array_fail()
    {
        var (f, b) = Function(TirType.Void, ("a", Point), ("b", TirMemorySamples.TenInts));
        b.Binary(TirBinaryOp.Mul, f.Parameters[0], f.Parameters[0]);
        b.Binary(TirBinaryOp.Xor, f.Parameters[1], f.Parameters[1]);
        b.Return();

        Assert.Equal(
        [
            "Function 'Program.F', block 'entry', '%0 = mul Program.Point %a, %a': " +
                "mul works on integers and floats, not on Program.Point.",
            "Function 'Program.F', block 'entry', '%1 = xor [10 x i32] %b, %b': " +
                "xor works on integers and bool, not on [10 x i32].",
        ], TirVerifier.FindErrors(_module));
    }

    [Fact]
    public void Pointers_compare_for_equality_and_as_unsigned_numbers_only()
    {
        var (f, b) = Function(TirType.Void, ("p", TirType.Ptr), ("q", TirType.Ptr));
        var (p, q) = (f.Parameters[0], f.Parameters[1]);
        b.Compare(TirCompareOp.Eq, p, q);
        b.Compare(TirCompareOp.Ne, p, q);
        b.Compare(TirCompareOp.LtUnsigned, p, q);
        b.Compare(TirCompareOp.GeUnsigned, p, q);
        b.Compare(TirCompareOp.Lt, p, q);
        b.Return();

        AssertError("Function 'Program.F', block 'entry', '%4 = lt ptr %p, %q': lt works on integers and floats, not on ptr.");
    }

    [Fact]
    public void Structs_and_arrays_cannot_be_compared()
    {
        var (f, b) = Function(TirType.Void, ("a", Point), ("b", TirMemorySamples.TenInts));
        b.Compare(TirCompareOp.Eq, f.Parameters[0], f.Parameters[0]);
        b.Compare(TirCompareOp.GtUnsigned, f.Parameters[1], f.Parameters[1]);
        b.Return();

        Assert.Equal(
        [
            "Function 'Program.F', block 'entry', '%0 = eq Program.Point %a, %a': " +
                "eq works on integers, floats, bool and ptr, not on Program.Point.",
            "Function 'Program.F', block 'entry', '%1 = ugt [10 x i32] %b, %b': " +
                "ugt works on integers, floats and ptr, not on [10 x i32].",
        ], TirVerifier.FindErrors(_module));
    }

    [Theory]
    [InlineData(TirCompareOp.LtUnsigned)]
    [InlineData(TirCompareOp.LeUnsigned)]
    [InlineData(TirCompareOp.GtUnsigned)]
    [InlineData(TirCompareOp.GeUnsigned)]
    public void The_unsigned_comparisons_work_on_floats_as_unordered_ones(TirCompareOp op)
    {
        var (f, b) = Function(TirType.Void, ("x", TirType.F64), ("y", TirType.F32));
        b.Compare(op, f.Parameters[0], f.Parameters[0]);
        b.Compare(op, f.Parameters[1], f.Parameters[1]);
        b.Return();

        Assert.Empty(TirVerifier.FindErrors(_module));
    }

    [Fact]
    public void The_unsigned_comparisons_still_do_not_work_on_bool()
    {
        var (f, b) = Function(TirType.Void, ("p", TirType.Bool));
        b.Compare(TirCompareOp.LtUnsigned, f.Parameters[0], f.Parameters[0]);
        b.Return();

        AssertError("Function 'Program.F', block 'entry', '%0 = ult bool %p, %p': ult works on integers, floats and ptr, not on bool.");
    }

    // ---- references ----

    [Fact]
    public void The_address_of_a_slot_of_another_function_fails()
    {
        var x = Main.AddSlot("x", TirType.I32);
        var (_, b) = Function(TirType.Void);
        b.AddressOf(x);
        b.Return();

        AssertError("Function 'Program.F', block 'entry', '%0 = addr $x': uses slot $x of function 'Program.Main'.");
    }

    [Fact]
    public void The_address_of_a_global_of_another_module_fails()
    {
        var other = new TirModule("Other").AddGlobal("Other.counter", TirType.I32);
        var (_, b) = Function(TirType.Void);
        b.AddressOf(other);
        b.Return();

        AssertError("Function 'Program.F', block 'entry', '%0 = addr @Other.counter': " +
            "uses global @Other.counter of module 'Other'.");
    }

    [Fact]
    public void An_address_used_in_another_block_fails_like_any_temporary()
    {
        var (f, b) = Function(TirType.I32);
        var x = f.AddSlot("x", TirType.I32);
        var next = f.AddBlock("next");
        var address = b.AddressOf(x);
        b.Jump(next);
        b.Block = next;
        b.Return(b.Load(TirType.I32, address));

        AssertError("Function 'Program.F', block 'next', '%1 = load i32 [%0]': uses %0 from block 'entry'; a temporary " +
            "can only be used in the block that defines it (pass values between blocks through a slot).");
    }

    // ---- functions and the module ----

    [Fact]
    public void An_external_function_that_takes_or_returns_a_struct_or_an_array_fails()
    {
        _module.AddExternalFunction("Runtime.Draw", TirMemorySamples.TenInts,
            ("line", TirMemorySamples.Line), ("x", TirType.I32));

        AssertError("Function 'Runtime.Draw': parameter %line is Program.Line; an external function cannot take a " +
            "struct or an array, so pass its address (ptr) instead.",
            "Function 'Runtime.Draw': it returns [10 x i32]; an external function cannot return a struct or an " +
            "array, so take the address (ptr) of where to put it instead.");
    }

    [Fact]
    public void Two_different_structs_of_one_name_fail()
    {
        var lookalike = new TirStructType("Program.Point", ("X", TirType.I32), ("Y", TirType.I32));
        _module.AddGlobal("Program.a", Point);
        _module.AddGlobal("Program.b", new TirArrayType(lookalike, 2));

        AssertError("Module 'Test': 2 different struct types are named 'Program.Point'; " +
            "make one struct type per struct and use it everywhere.");
    }

    [Fact]
    public void A_slot_store_of_a_lookalike_struct_fails()
    {
        var lookalike = new TirStructType("Program.Other", ("X", TirType.I32), ("Y", TirType.I32));
        var (f, b) = Function(TirType.Void, ("value", lookalike));
        b.Store(f.AddSlot("p", Point), f.Parameters[0]);
        b.Return();

        AssertError("Function 'Program.F', block 'entry', 'store $p, %value': " +
            "the value is Program.Other, but slot $p holds Program.Point.");
    }

    [Fact]
    public void A_constant_can_be_read_but_a_store_into_it_fails()
    {
        // through its own address or that of one of its bytes; a store through an address it cannot see passes
        var text = _module.AddConstant("<text 0>", [1, 2, 3, 4]);
        var (f, b) = Function(TirType.I8, ("p", TirType.Ptr));
        var first = b.AddressOf(text);
        var third = b.ElementAddress(first, TirType.I8, b.Const(TirType.I32, 2));
        var value = b.Load(TirType.I8, third);
        b.Store(third, value);
        b.Store(first, b.Zero(text.Type));
        b.Store(f.Parameters[0], value);
        b.Return(value);

        AssertError(
            "Function 'Program.F', block 'entry', 'store i8 [%2], %3': stores into @<text 0>, which is constant: " +
                "nothing may write it.",
            "Function 'Program.F', block 'entry', 'store [4 x i8] [%0], %4': stores into @<text 0>, which is " +
                "constant: nothing may write it.");
    }

    /// <summary>Adds <c>Program.F</c> with an empty entry block and a builder positioned there.</summary>
    private (TirFunction Function, TirBuilder Builder) Function(TirType returnType,
        params (string Name, TirType Type)[] parameters)
    {
        var function = _module.AddFunction("Program.F", returnType, parameters);
        return (function, new TirBuilder(function.AddBlock("entry")));
    }

    private void AssertError(params string[] expected) => Assert.Equal(expected, TirVerifier.FindErrors(_module));
}
