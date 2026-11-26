using ChipSharp.Ir;

namespace ChipSharp.Tests.Tir;

/// <summary>What building TIR refuses right away, and what the builder gives back.</summary>
public sealed class TirModelTests
{
    [Fact]
    public void The_builder_appends_to_its_current_block_and_returns_each_instruction()
    {
        var f = new TirModule("Demo").AddFunction("Program.F", TirType.I32, ("x", TirType.I32));
        var entry = f.AddBlock("entry");
        var exit = f.AddBlock("exit");
        var slot = f.AddSlot("result", TirType.I32);
        var b = new TirBuilder(entry);

        var store = b.Store(slot, f.Parameters[0]);
        var jump = b.Jump(exit);
        b.Block = exit;
        var load = b.Load(slot);
        var ret = b.Return(load);

        Assert.Same(f, b.Function);
        Assert.Equal([store, jump], entry.Instructions);
        Assert.Equal([load, ret], exit.Instructions);
        Assert.Same(jump, entry.Terminator);
        Assert.Equal([exit], jump.Targets);
        Assert.Same(entry, f.EntryBlock);
        Assert.Same(exit, load.Block);
        Assert.Equal(TirType.I32, load.Type);
        Assert.False(store.HasResult);
        Assert.True(load.HasResult);
    }

    [Fact]
    public void A_call_has_the_return_type_of_its_callee()
    {
        var module = new TirModule("Demo");
        var square = module.AddFunction("Program.Square", TirType.I64, ("n", TirType.I64));
        var b = new TirBuilder(module.AddFunction("Program.Main", TirType.Void).AddBlock("entry"));

        var call = b.Call(square, b.Const(TirType.I64, 3));

        Assert.Equal(TirType.I64, call.Type);
        Assert.Same(square, call.Callee);
        Assert.Single(call.Arguments);
    }

    [Fact]
    public void An_instruction_belongs_to_one_block_only()
    {
        var f = new TirModule("Demo").AddFunction("Program.F", TirType.Void);
        var one = new TirBuilder(f.AddBlock("entry")).Const(TirType.I32, 1);

        var error = Assert.Throws<TirException>(() => f.AddBlock("other").Append(one));

        Assert.Equal("'%0 = const i32 1' is already in block 'entry' of function 'Program.F'; " +
            "an instruction belongs to one block only.", error.Message);
    }

    [Fact]
    public void Block_names_are_unique_in_a_function()
    {
        var f = new TirModule("Demo").AddFunction("Program.F", TirType.Void);
        f.AddBlock("entry");

        var error = Assert.Throws<TirException>(() => f.AddBlock("entry"));

        Assert.Equal("Function 'Program.F' already has a block named 'entry'.", error.Message);
    }

    [Fact]
    public void Slot_names_are_unique_in_a_function()
    {
        var f = new TirModule("Demo").AddFunction("Program.F", TirType.Void);
        f.AddSlot("x", TirType.I32);

        var error = Assert.Throws<TirException>(() => f.AddSlot("x", TirType.I64));

        Assert.Equal("Function 'Program.F' already has a slot named $x.", error.Message);
    }

    [Fact]
    public void Parameter_names_are_unique_in_a_function()
    {
        var error = Assert.Throws<TirException>(() =>
            new TirModule("Demo").AddFunction("Program.F", TirType.Void, ("a", TirType.I32), ("a", TirType.I64)));

        Assert.Equal("Function 'Program.F' has two parameters named %a.", error.Message);
    }

    [Fact]
    public void Slots_and_parameters_cannot_be_void()
    {
        var module = new TirModule("Demo");
        var f = module.AddFunction("Program.F", TirType.Void);

        Assert.Equal("Function 'Program.F': slot $x cannot have type void.",
            Assert.Throws<TirException>(() => f.AddSlot("x", TirType.Void)).Message);
        Assert.Equal("Function 'Program.G': parameter %p cannot have type void.",
            Assert.Throws<TirException>(() => module.AddFunction("Program.G", TirType.Void, ("p", TirType.Void))).Message);
    }

    [Theory]
    [InlineData(TirTypeKind.I8, 256L, "The constant 256 does not fit in i8, which takes -128 to 255.")]
    [InlineData(TirTypeKind.I8, -129L, "The constant -129 does not fit in i8, which takes -128 to 255.")]
    [InlineData(TirTypeKind.I16, 65536L, "The constant 65536 does not fit in i16, which takes -32768 to 65535.")]
    [InlineData(TirTypeKind.I32, 4294967296L,
        "The constant 4294967296 does not fit in i32, which takes -2147483648 to 4294967295.")]
    [InlineData(TirTypeKind.NativeInt, 2147483648L,
        "The constant 2147483648 does not fit in nint, which takes -2147483648 to 2147483647.")]
    [InlineData(TirTypeKind.Bool, 2L, "A bool constant must be 0 or 1, not 2.")]
    [InlineData(TirTypeKind.Void, 0L, "A constant cannot have type void.")]
    public void A_constant_must_fit_its_type(TirTypeKind kind, long value, string expected)
    {
        var error = Assert.Throws<TirException>(() => new TirConst(TypeOf(kind), value));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void Integer_constants_are_signless_and_read_back_sign_extended()
    {
        Assert.Equal(-1, new TirConst(TirType.I8, 255).IntegerValue);
        Assert.Equal(-1, new TirConst(TirType.I16, ushort.MaxValue).IntegerValue);
        Assert.Equal(-1, new TirConst(TirType.I32, uint.MaxValue).IntegerValue);
        Assert.Equal(-1, new TirConst(TirType.I64, unchecked((long)ulong.MaxValue)).IntegerValue);
        Assert.Equal(1, new TirConst(true).IntegerValue);
    }

    [Fact]
    public void A_floating_point_constant_needs_a_float_type()
    {
        var error = Assert.Throws<TirException>(() => new TirConst(TirType.I32, 1.5));

        Assert.Equal("A floating-point constant needs type f32 or f64, not i32.", error.Message);
    }

    [Fact]
    public void Types_are_shared_instances_with_their_kind_and_name()
    {
        Assert.Same(TirType.NativeInt, TypeOf(TirTypeKind.NativeInt));
        Assert.Equal("nint", TirType.NativeInt.Name);
        Assert.True(TirType.NativeInt.IsInteger);
        Assert.False(TirType.Bool.IsInteger);
        Assert.True(TirType.F32.IsFloat);
        Assert.False(TirType.I64.IsFloat);
    }

    private static TirType TypeOf(TirTypeKind kind) => kind switch
    {
        TirTypeKind.Void => TirType.Void,
        TirTypeKind.Bool => TirType.Bool,
        TirTypeKind.I8 => TirType.I8,
        TirTypeKind.I16 => TirType.I16,
        TirTypeKind.I32 => TirType.I32,
        TirTypeKind.I64 => TirType.I64,
        TirTypeKind.F32 => TirType.F32,
        TirTypeKind.F64 => TirType.F64,
        TirTypeKind.NativeInt => TirType.NativeInt,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
