using ChipSharp.Ir;

namespace ChipSharp.Tests.Tir;

public sealed class TirVerifierTests
{
    private readonly TirModule _module = TirSamples.WithMain(new TirModule("Test"));

    private TirFunction Main => _module.EntryPoint!;

    // ---- well-formed functions pass ----

    [Fact]
    public void An_if_else_that_meets_in_a_slot_passes()
    {
        TirSamples.AddMax(_module);

        Assert.Empty(TirVerifier.FindErrors(_module));
        TirVerifier.Verify(_module);
    }

    [Fact]
    public void A_loop_passes()
    {
        TirSamples.AddSum(_module);

        Assert.Empty(TirVerifier.FindErrors(_module));
    }

    [Fact]
    public void A_jump_back_to_the_first_block_passes()
    {
        var (f, b) = Function(TirType.Void, ("p", TirType.Bool));
        var done = f.AddBlock("done");
        b.Branch(f.Parameters[0], f.EntryBlock!, done);
        b.Block = done;
        b.Return();

        Assert.Empty(TirVerifier.FindErrors(_module));
    }

    // ---- terminators ----

    [Fact]
    public void A_block_that_does_not_end_with_a_terminator_fails()
    {
        var (_, b) = Function(TirType.Void);
        b.Const(TirType.I32, 1);

        AssertError("Function 'Program.F', block 'entry': the block must end with a terminator (return, jump or branch).");
    }

    [Fact]
    public void An_empty_block_fails()
    {
        Function(TirType.Void);

        AssertError("Function 'Program.F', block 'entry': the block must end with a terminator (return, jump or branch).");
    }

    [Fact]
    public void A_terminator_that_is_not_last_fails()
    {
        var (_, b) = Function(TirType.Void);
        b.Return();
        b.Return();

        AssertError("Function 'Program.F', block 'entry', 'return': " +
            "a terminator (return, jump or branch) must be the last instruction of its block.");
    }

    // ---- types ----

    [Fact]
    public void Binary_operands_of_different_types_fail()
    {
        var (f, b) = Function(TirType.I32, ("a", TirType.I32), ("b", TirType.I64));
        b.Return(b.Binary(TirBinaryOp.Add, f.Parameters[0], f.Parameters[1]));

        AssertError("Function 'Program.F', block 'entry', '%0 = add i32 %a, %b': the operands have different types: i32 and i64.");
    }

    [Fact]
    public void Arithmetic_on_bool_fails()
    {
        var (f, b) = Function(TirType.Bool, ("p", TirType.Bool));
        b.Return(b.Binary(TirBinaryOp.Add, f.Parameters[0], f.Parameters[0]));

        AssertError("Function 'Program.F', block 'entry', '%0 = add bool %p, %p': add works on integers and floats, not on bool.");
    }

    [Fact]
    public void A_shift_of_a_float_fails()
    {
        var (f, b) = Function(TirType.F64, ("x", TirType.F64));
        b.Return(b.Binary(TirBinaryOp.Shl, f.Parameters[0], f.Parameters[0]));

        AssertError("Function 'Program.F', block 'entry', '%0 = shl f64 %x, %x': shl works on integers, not on f64.");
    }

    [Fact]
    public void Compare_operands_of_different_types_fail()
    {
        var (f, b) = Function(TirType.Bool, ("a", TirType.I8), ("b", TirType.I16));
        b.Return(b.Compare(TirCompareOp.Eq, f.Parameters[0], f.Parameters[1]));

        AssertError("Function 'Program.F', block 'entry', '%0 = eq i8 %a, %b': the operands have different types: i8 and i16.");
    }

    [Fact]
    public void Ordering_bools_fails()
    {
        var (f, b) = Function(TirType.Bool, ("p", TirType.Bool));
        b.Return(b.Compare(TirCompareOp.Lt, f.Parameters[0], f.Parameters[0]));

        AssertError("Function 'Program.F', block 'entry', '%0 = lt bool %p, %p': lt works on integers and floats, not on bool.");
    }

    [Fact]
    public void Converting_to_bool_fails()
    {
        var (f, b) = Function(TirType.Bool, ("x", TirType.I32));
        b.Return(b.Convert(f.Parameters[0], TirType.Bool));

        AssertError("Function 'Program.F', block 'entry', '%0 = convert i32 %x to bool': " +
            "convert cannot produce bool; compare with zero (ne) instead.");
    }

    [Fact]
    public void Storing_a_value_of_another_type_than_the_slot_fails()
    {
        var (f, b) = Function(TirType.Void, ("big", TirType.I64));
        var x = f.AddSlot("x", TirType.I32);
        b.Store(x, f.Parameters[0]);
        b.Return();

        AssertError("Function 'Program.F', block 'entry', 'store $x, %big': the value is i64, but slot $x holds i32.");
    }

    [Fact]
    public void Returning_a_value_of_the_wrong_type_fails()
    {
        var (f, b) = Function(TirType.I32, ("big", TirType.I64));
        b.Return(f.Parameters[0]);

        AssertError("Function 'Program.F', block 'entry', 'return %big': returns i64, but the function returns i32.");
    }

    [Fact]
    public void Returning_nothing_from_a_function_that_returns_a_value_fails()
    {
        var (_, b) = Function(TirType.I32);
        b.Return();

        AssertError("Function 'Program.F', block 'entry', 'return': returns nothing, but the function returns i32.");
    }

    [Fact]
    public void Returning_a_value_from_a_void_function_fails()
    {
        var (f, b) = Function(TirType.Void, ("x", TirType.I32));
        b.Return(f.Parameters[0]);

        AssertError("Function 'Program.F', block 'entry', 'return %x': returns a value, but the function returns void.");
    }

    [Fact]
    public void A_branch_on_something_other_than_bool_fails()
    {
        var (f, b) = Function(TirType.Void, ("x", TirType.I32));
        var yes = f.AddBlock("yes");
        var no = f.AddBlock("no");
        b.Branch(f.Parameters[0], yes, no);
        new TirBuilder(yes).Return();
        new TirBuilder(no).Return();

        AssertError("Function 'Program.F', block 'entry', 'branch %x, yes, no': the condition is i32; it must be bool.");
    }

    // ---- calls ----

    [Fact]
    public void A_call_with_too_few_arguments_fails()
    {
        var print = _module.AddExternalFunction("Runtime.Print", TirType.Void, ("value", TirType.I32));
        var (_, b) = Function(TirType.Void);
        b.Call(print);
        b.Return();

        AssertError("Function 'Program.F', block 'entry', 'call void Runtime.Print()': " +
            "Runtime.Print takes 1 argument, but 0 are given.");
    }

    [Fact]
    public void A_call_with_an_argument_of_the_wrong_type_fails()
    {
        var print = _module.AddExternalFunction("Runtime.Print", TirType.Void, ("value", TirType.I32));
        var (f, b) = Function(TirType.Void, ("big", TirType.I64));
        b.Call(print, f.Parameters[0]);
        b.Return();

        AssertError("Function 'Program.F', block 'entry', 'call void Runtime.Print(%big)': " +
            "argument 1 is i64, but parameter %value of Runtime.Print is i32.");
    }

    [Fact]
    public void Using_the_result_of_a_void_call_fails()
    {
        var print = _module.AddExternalFunction("Runtime.Print", TirType.Void, ("value", TirType.I32));
        var (f, b) = Function(TirType.I32, ("x", TirType.I32));
        var nothing = b.Call(print, f.Parameters[0]);
        b.Return(nothing);

        AssertError("Function 'Program.F', block 'entry', 'return %?': " +
            "uses 'call void Runtime.Print(%x)', which produces no value.");
    }

    [Fact]
    public void Calling_a_function_of_another_module_fails()
    {
        var other = new TirModule("Other").AddFunction("Other.Helper", TirType.Void);
        var (_, b) = Function(TirType.Void);
        b.Call(other);
        b.Return();

        AssertError("Function 'Program.F', block 'entry', 'call void Other.Helper()': " +
            "calls 'Other.Helper', which is not a function of module 'Test'.");
    }

    // ---- where values may be used ----

    [Fact]
    public void A_temporary_used_in_another_block_fails()
    {
        var (f, b) = Function(TirType.I32, ("x", TirType.I32));
        var next = f.AddBlock("next");
        var doubled = b.Binary(TirBinaryOp.Add, f.Parameters[0], f.Parameters[0]);
        b.Jump(next);
        b.Block = next;
        b.Return(doubled);

        AssertError("Function 'Program.F', block 'next', 'return %0': uses %0 from block 'entry'; a temporary can only " +
            "be used in the block that defines it (pass values between blocks through a slot).");
    }

    [Fact]
    public void A_temporary_used_before_it_is_defined_fails()
    {
        var (f, _) = Function(TirType.I32);
        var one = new TirConst(TirType.I32, 1);
        var entry = f.EntryBlock!;
        var sum = entry.Append(new TirBinary(TirBinaryOp.Add, one, one));
        entry.Append(one);
        entry.Append(new TirReturn(sum));

        AssertError("Function 'Program.F', block 'entry', '%0 = add i32 %1, %1': uses %1 before it is defined.");
    }

    [Fact]
    public void A_temporary_of_another_function_fails()
    {
        var one = new TirBuilder(Main.AddBlock("unused")).Const(TirType.I32, 1);
        new TirBuilder(Main.Blocks[^1]).Return();
        var (_, b) = Function(TirType.I32);
        b.Return(one);

        AssertError("Function 'Program.F', block 'entry', 'return %?': " +
            "uses '%0 = const i32 1' from function 'Program.Main'.");
    }

    [Fact]
    public void A_parameter_of_another_function_fails()
    {
        var square = _module.AddFunction("Program.Square", TirType.I32, ("n", TirType.I32));
        new TirBuilder(square.AddBlock("entry")).Return(square.Parameters[0]);
        var (_, b) = Function(TirType.I32);
        b.Return(square.Parameters[0]);

        AssertError("Function 'Program.F', block 'entry', 'return %n': uses parameter %n of function 'Program.Square'.");
    }

    [Fact]
    public void A_slot_of_another_function_fails()
    {
        var x = Main.AddSlot("x", TirType.I32);
        var (_, b) = Function(TirType.I32);
        b.Return(b.Load(x));

        AssertError("Function 'Program.F', block 'entry', '%0 = load i32 $x': uses slot $x of function 'Program.Main'.");
    }

    [Fact]
    public void A_jump_to_a_block_of_another_function_fails()
    {
        var (_, b) = Function(TirType.Void);
        b.Jump(Main.EntryBlock!);

        AssertError("Function 'Program.F', block 'entry', 'jump entry': " +
            "goes to block 'entry' of function 'Program.Main'; a jump or branch must stay in its own function.");
    }

    [Fact]
    public void A_branch_to_a_block_of_another_function_fails()
    {
        var (f, b) = Function(TirType.Void, ("p", TirType.Bool));
        var done = f.AddBlock("done");
        b.Branch(f.Parameters[0], done, Main.EntryBlock!);
        new TirBuilder(done).Return();

        AssertError("Function 'Program.F', block 'entry', 'branch %p, done, entry': " +
            "goes to block 'entry' of function 'Program.Main'; a jump or branch must stay in its own function.");
    }

    // ---- functions and the module ----

    [Fact]
    public void An_external_function_with_a_block_fails()
    {
        var print = _module.AddExternalFunction("Runtime.Print", TirType.Void, ("value", TirType.I32));
        new TirBuilder(print.AddBlock("entry")).Return();

        AssertError("Function 'Runtime.Print': an external function has no body (the runtime implements it), " +
            "but it has 1 block.");
    }

    [Fact]
    public void A_defined_function_without_blocks_fails()
    {
        _module.AddFunction("Program.F", TirType.Void);

        AssertError("Function 'Program.F': a defined function needs at least one block, but it has none.");
    }

    [Fact]
    public void A_module_without_an_entry_point_fails()
    {
        _module.EntryPoint = null;

        AssertError("Module 'Test': no entry point is set.");
    }

    [Fact]
    public void An_entry_point_from_another_module_fails()
    {
        var other = TirSamples.WithMain(new TirModule("Other"));
        _module.EntryPoint = other.EntryPoint;

        AssertError("Module 'Test': the entry point 'Program.Main' is not a function of this module.");
    }

    [Fact]
    public void An_external_entry_point_fails()
    {
        _module.EntryPoint = _module.AddExternalFunction("Runtime.Start", TirType.Void);

        AssertError("Module 'Test': the entry point 'Runtime.Start' is external; it must be a defined function.");
    }

    [Fact]
    public void Verify_throws_with_every_error_one_per_line()
    {
        _module.EntryPoint = null;
        _module.AddFunction("Program.F", TirType.Void);

        var error = Assert.Throws<TirException>(() => TirVerifier.Verify(_module));

        Assert.Equal("Module 'Test': no entry point is set.\n" +
            "Function 'Program.F': a defined function needs at least one block, but it has none.", error.Message);
    }

    /// <summary>Adds <c>Program.F</c> with an empty entry block and a builder positioned there.</summary>
    private (TirFunction Function, TirBuilder Builder) Function(TirType returnType,
        params (string Name, TirType Type)[] parameters)
    {
        var function = _module.AddFunction("Program.F", returnType, parameters);
        return (function, new TirBuilder(function.AddBlock("entry")));
    }

    private void AssertError(string expected) => Assert.Equal([expected], TirVerifier.FindErrors(_module));
}
