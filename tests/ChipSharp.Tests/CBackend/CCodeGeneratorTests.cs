using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Backend.C;
using ChipSharp.Build;
using ChipSharp.Ir;
using ChipSharp.Tests.Llvm;
using ChipSharp.Tests.Tir;

namespace ChipSharp.Tests.CBackend;

/// <summary>The C the c code generator writes, one family of TIR instructions at a time.</summary>
public sealed class CCodeGeneratorTests
{
    private static readonly CCodeGenerator Generator = new();

    /// <summary>A PC with 8-byte pointers and no heap.</summary>
    private static readonly TargetDescription Pc = CTargets.Make("pc", pointerSize: 8, triple: "x86_64-pc-windows-msvc");

    private static string Generate(TirModule module, TargetDescription? target = null) =>
        Assert.Single(Generator.Generate(module, target ?? Pc).Files).Content;

    /// <summary>The definition of the function <paramref name="symbol"/>: from its head to its closing brace.</summary>
    private static string Definition(string code, string symbol)
    {
        // the line with the head ("static uint32_t Program_F(...)" or "static uint8_t *Program_F(...)") and then "{"
        var lines = code.Split('\n');
        var head = Array.FindIndex(lines, line => !line.StartsWith(' ') && !line.EndsWith(';')
            && (line.Contains($" {symbol}(", StringComparison.Ordinal) || line.Contains($"*{symbol}(", StringComparison.Ordinal)));
        Assert.True(head >= 0, $"no definition of {symbol} in:\n{code}");
        var close = Array.IndexOf(lines, "}", head);
        return string.Join('\n', lines[head..(close + 1)]) + "\n";
    }

    /// <summary>The definition of the function <paramref name="add"/> makes, in a module whose Main calls it.</summary>
    private static string DefinitionOf(Func<TirModule, TirFunction> add, TargetDescription? target = null)
    {
        var module = new TirModule("Test");
        var function = add(module);
        return Definition(Generate(Calling(module, function), target), CText.Identifier(function.Name));
    }

    /// <summary><c>function T Program.F(T %a, T %b) { return op %a, %b }</c>.</summary>
    private static Func<TirModule, TirFunction> Binary(TirBinaryOp op, TirType type, long? constant = null) => module =>
    {
        var function = module.AddFunction("Program.F", type, ("a", type), ("b", type));
        var ir = new TirBuilder(function.AddBlock("entry"));
        TirValue right = constant is { } value ? ir.Const(type, value) : function.Parameters[1];
        ir.Return(ir.Binary(op, function.Parameters[0], right));
        return function;
    };

    private static string BinaryLine(TirBinaryOp op, TirType type, long? constant = null) =>
        Definition(Generate(WithFunction(Binary(op, type, constant))), "Program_F").Split('\n')[3].Trim();

    /// <summary>A module with the function <paramref name="add"/> makes, and a Main that calls it (so that it is written).</summary>
    private static TirModule WithFunction(Func<TirModule, TirFunction> add)
    {
        var module = new TirModule("Test");
        return Calling(module, add(module));
    }

    /// <summary>
    /// Adds a Main that calls <paramref name="function"/> with zeros and passes the address of each of
    /// <paramref name="globals"/> (what the program reaches is all that is written), and makes it the entry point.
    /// </summary>
    private static TirModule Calling(TirModule module, TirFunction? function, params TirGlobal[] globals)
    {
        var main = module.AddFunction("Program.Main", TirType.Void);
        module.EntryPoint = main;
        var ir = new TirBuilder(main.AddBlock("entry"));
        if (function is not null)
        {
            ir.Call(function, [.. function.Parameters.Select(parameter => (TirValue)ir.Zero(parameter.Type))]);
        }

        var keep = new Dictionary<TirType, TirFunction>();
        foreach (var global in globals)
        {
            // passed to the runtime, so that the address is used and the global written
            var address = ir.AddressOf(global);
            if (!keep.TryGetValue(address.Type, out var keeper))
            {
                keeper = module.AddExternalFunction($"Program.Keep_{address.Type}", TirType.Void, ("address", address.Type));
                keep[address.Type] = keeper;
            }

            ir.Call(keeper, address);
        }

        ir.Return();
        return module;
    }

    /// <summary>The objects of <see cref="TirObjectSamples"/>, with a Main that calls their Program.Use.</summary>
    private static TirModule Objects()
    {
        var module = TirObjectSamples.Add(new TirModule("Objects"));
        return Calling(module, module.Functions.Single(function => function.Name == "Program.Use"));
    }

    /// <summary>A module whose Main only passes the addresses of the globals <paramref name="add"/> makes to the runtime.</summary>
    private static TirModule WithGlobals(Func<TirModule, IEnumerable<TirGlobal>> add)
    {
        var module = new TirModule("Test");
        return Calling(module, null, [.. add(module)]);
    }
    // ---- the file ----

    [Fact]
    public void Writes_one_C_file_of_the_module_with_its_checks_the_runtimes_function_and_an_entry_that_calls_Main()
    {
        var file = Assert.Single(Generator.Generate(LlvmSamples.SquareProgram(), Pc).Files);

        Assert.Equal("Demo.c", file.FileName);
        var code = file.Content;
        Assert.StartsWith("/* Demo: C written by ChipSharp's c code generator for target 'pc'. */\n\n" +
            "#include <stdint.h>\n#include <stddef.h>\n", code);
        Assert.Contains("typedef char cs_pointer_size[sizeof(void *) == 8 ? 1 : -1];", code);
        Assert.Contains("typedef char cs_arithmetic_shift[(-2 >> 1) == -1 ? 1 : -1];", code);
        Assert.Contains("/* the runtime's */\nvoid ChipSharp_Runtime_WriteLineInt32(uint32_t p_value);\n", code);
        Assert.Contains("/* the program's */\nstatic uint32_t Program_Square(uint32_t p_n);\nstatic void Program_Main(void);\n", code);
        Assert.Equal("""
            static uint32_t Program_Square(uint32_t p_n)
            {
                {
                    uint32_t t0 = p_n * p_n;
                    return t0;
                }
            }

            """.ReplaceLineEndings("\n"), Definition(code, "Program_Square"));
        Assert.Equal("""
            static void Program_Main(void)
            {
                {
                    uint32_t t1 = Program_Square(42);
                    ChipSharp_Runtime_WriteLineInt32(t1);
                    return;
                }
            }

            """.ReplaceLineEndings("\n"), Definition(code, "Program_Main"));
        Assert.EndsWith("int32_t chipsharp_entry(void)\n{\n    Program_Main();\n    return 0;\n}\n", code);
    }

    [Fact]
    public void Blocks_follow_one_another_and_only_a_jump_elsewhere_is_a_goto_to_a_label()
    {
        var max = DefinitionOf(TirSamples.AddMax);

        Assert.Equal("""
            static uint32_t Program_Max(uint32_t p_a, uint32_t p_b)
            {
                uint32_t s_result = 0;

                {
                    uint8_t t0 = (int32_t)p_a > (int32_t)p_b;
                    if (!t0) goto b_else;
                }

                /* then */
                s_result = p_a;
                goto join;

            b_else:
                s_result = p_b;

            join:
                {
                    uint32_t t1 = s_result;
                    return t1;
                }
            }

            """.ReplaceLineEndings("\n"), max);
    }

    // ---- arithmetic ----

    [Fact]
    public void Arithmetic_of_32_and_64_bits_wraps_around_in_the_unsigned_type()
    {
        Assert.Equal("uint32_t t0 = p_a + p_b;", BinaryLine(TirBinaryOp.Add, TirType.I32));
        Assert.Equal("uint64_t t0 = p_a * p_b;", BinaryLine(TirBinaryOp.Mul, TirType.I64));
        Assert.Equal("uint32_t t0 = p_a / p_b;", BinaryLine(TirBinaryOp.DivUnsigned, TirType.I32));
        Assert.Equal("uint32_t t1 = p_a - 5;", BinaryLine(TirBinaryOp.Sub, TirType.I32, 5));
    }

    [Fact]
    public void Bytes_and_shorts_multiply_and_shift_as_unsigned_ints_and_are_cut_back_to_their_width()
    {
        Assert.Equal("uint8_t t0 = (uint8_t)((unsigned)p_a * (unsigned)p_b);", BinaryLine(TirBinaryOp.Mul, TirType.I8));
        Assert.Equal("uint16_t t0 = (uint16_t)((unsigned)p_a << (p_b & 15));", BinaryLine(TirBinaryOp.Shl, TirType.I16));
        Assert.Equal("uint8_t t0 = (uint8_t)(p_a + p_b);", BinaryLine(TirBinaryOp.Add, TirType.I8));
    }

    [Fact]
    public void Signed_operations_read_their_operands_as_the_signed_type()
    {
        Assert.Equal("uint32_t t0 = (uint32_t)((int32_t)p_a / (int32_t)p_b);", BinaryLine(TirBinaryOp.Div, TirType.I32));
        Assert.Equal("uint64_t t0 = (uint64_t)((int64_t)p_a % (int64_t)p_b);", BinaryLine(TirBinaryOp.Rem, TirType.I64));
        Assert.Equal("uint32_t t0 = (uint32_t)((int32_t)p_a >> (p_b & 31));", BinaryLine(TirBinaryOp.Shr, TirType.I32));
        Assert.Equal("uint32_t t1 = (uint32_t)((int32_t)p_a / -3);", BinaryLine(TirBinaryOp.Div, TirType.I32, -3));
    }

    [Fact]
    public void Shift_amounts_are_masked_below_the_width_as_CSharp_masks_them()
    {
        Assert.Equal("uint64_t t0 = p_a << (p_b & 63);", BinaryLine(TirBinaryOp.Shl, TirType.I64));
        Assert.Equal("uint32_t t0 = p_a >> (p_b & 31);", BinaryLine(TirBinaryOp.ShrUnsigned, TirType.I32));
        Assert.Equal("uint32_t t1 = p_a << 7;", BinaryLine(TirBinaryOp.Shl, TirType.I32, 7));
    }

    [Fact]
    public void Constants_are_small_numbers_or_cast_to_their_type_and_floats_are_exact()
    {
        var module = new TirModule("Test");
        var function = module.AddFunction("Program.F", TirType.Void, ("p", TirType.Ptr));
        var at = function.Parameters[0];
        var ir = new TirBuilder(function.AddBlock("entry"));
        ir.Store(at, ir.Const(TirType.I32, -5));
        ir.Store(at, ir.Const(TirType.I32, 40000));
        ir.Store(at, ir.Const(TirType.I64, long.MinValue));
        ir.Store(at, ir.Const(TirType.F32, 0.1));
        ir.Store(at, ir.Const(TirType.F32, float.NaN));
        ir.Store(at, ir.Const(TirType.F64, -2.0));
        ir.Store(at, ir.Const(TirType.F64, double.NegativeInfinity));
        ir.Store(at, ir.Const(TirType.F64, double.NaN));
        ir.Store(at, ir.Const(TirType.F64, 1e-310));
        ir.Return();

        var code = Definition(Generate(Calling(module, function)), "Program_F");

        Assert.Contains("*(uint32_t *)p_p = (uint32_t)-5;", code);
        Assert.Contains("*(uint32_t *)p_p = (uint32_t)40000;", code);
        Assert.Contains("*(uint64_t *)p_p = (uint64_t)(-9223372036854775807 - 1);", code);
        Assert.Contains("*(float *)p_p = 0x1.99999ap-4f /* 0.1 */;", code);
        Assert.Contains("*(float *)p_p = (-(0.0f / 0.0f));", code); // .NET's NaN has its sign bit set
        Assert.Contains("*(double *)p_p = -2.0;", code);
        Assert.Contains("*(double *)p_p = (-1.0 / 0.0);", code);
        Assert.Contains("*(double *)p_p = (-(0.0 / 0.0));", code);
        Assert.Contains("*(double *)p_p = 0x0.012688b70e62bp-1022 /* 1E-310 */;", code);
    }

    // ---- comparisons and conversions ----

    [Fact]
    public void Comparisons_are_signed_through_casts_unordered_on_floats_through_the_opposite_and_numbers_on_addresses()
    {
        string Line(TirCompareOp op, TirType type) => Definition(Generate(WithFunction(module =>
        {
            var function = module.AddFunction("Program.F", TirType.Bool, ("a", type), ("b", type));
            var ir = new TirBuilder(function.AddBlock("entry"));
            ir.Return(ir.Compare(op, function.Parameters[0], function.Parameters[1]));
            return function;
        })), "Program_F").Split('\n')[3].Trim();

        Assert.Equal("uint8_t t0 = (int8_t)p_a < (int8_t)p_b;", Line(TirCompareOp.Lt, TirType.I8));
        Assert.Equal("uint8_t t0 = p_a < p_b;", Line(TirCompareOp.LtUnsigned, TirType.I64));
        Assert.Equal("uint8_t t0 = p_a != p_b;", Line(TirCompareOp.Ne, TirType.F64));
        Assert.Equal("uint8_t t0 = !(p_a < p_b);", Line(TirCompareOp.GeUnsigned, TirType.F32));
        Assert.Equal("uint8_t t0 = (uint64_t)p_a >= (uint64_t)p_b;", Line(TirCompareOp.GeUnsigned, TirType.Ptr));
        Assert.Equal("uint8_t t0 = p_a == p_b;", Line(TirCompareOp.Eq, TirType.Ref));
    }

    [Fact]
    public void Conversions_extend_by_sign_or_zero_and_a_float_becomes_an_integer_through_a_helper_that_saturates()
    {
        string Convert(TirType from, TirType to, bool unsigned, out string code)
        {
            code = Generate(WithFunction(module => LlvmSamples.AddConvert(module, from, to, unsigned)));
            return code.Split('\n').Single(line => line.Contains(" t0 = ", StringComparison.Ordinal)).Trim();
        }

        Assert.Equal("uint64_t t0 = (uint64_t)(int32_t)p_x;", Convert(TirType.I32, TirType.I64, false, out _));
        Assert.Equal("uint64_t t0 = (uint64_t)p_x;", Convert(TirType.I32, TirType.I64, true, out _));
        Assert.Equal("uint8_t t0 = (uint8_t)p_x;", Convert(TirType.I64, TirType.I8, false, out _));
        Assert.Equal("float t0 = (float)(int64_t)p_x;", Convert(TirType.I64, TirType.F32, false, out _));
        Assert.Equal("uint32_t t0 = cs_f64_to_i32(p_x);", Convert(TirType.F64, TirType.I32, false, out var code));
        Assert.Contains("""
            static uint32_t cs_f64_to_i32(double value)
            {
                if (value != value)
                {
                    return 0;
                }

                if (value <= -0x1p+31)
                {
                    return 0x80000000u;
                }

                if (value >= 0x1p+31)
                {
                    return 0x7FFFFFFFu;
                }

                return (uint32_t)(int32_t)value;
            }
            """.ReplaceLineEndings("\n"), code);
        Convert(TirType.F32, TirType.I16, true, out var unsignedCode);
        Assert.Contains("if (!(value >= 0.0f))", unsignedCode);
        Assert.Contains("if (value >= 0x1p+16f)", unsignedCode);
    }

    [Fact]
    public void Floats_add_as_C_does_and_their_remainder_is_fmod()
    {
        Assert.Equal("double t0 = p_a + p_b;", BinaryLine(TirBinaryOp.Add, TirType.F64));
        Assert.Equal("float t0 = fmodf(p_a, p_b);", BinaryLine(TirBinaryOp.Rem, TirType.F32));
        var code = Generate(WithFunction(Binary(TirBinaryOp.Rem, TirType.F64)));
        Assert.Contains("/* the C library's */\ndouble fmod(double x, double y);\n", code);
        Assert.Contains("#ifdef __clang__\n#pragma STDC FP_CONTRACT OFF\n#endif\n", code);
    }

    // ---- memory ----

    [Fact]
    public void Fields_and_elements_of_a_place_are_that_place_and_of_any_other_address_through_a_cast_pointer()
    {
        var point = new TirStructType("Program.Point", ("X", TirType.I8), ("Y", TirType.I32));
        var row = new TirArrayType(TirType.I32, 4);
        var code = DefinitionOf(module =>
        {
            var function = module.AddFunction("Program.F", TirType.I32, ("p", TirType.Ptr), ("i", TirType.I32));
            var slot = function.AddSlot("s", point);
            var rowSlot = function.AddSlot("row", row);
            var ir = new TirBuilder(function.AddBlock("entry"));
            ir.Store(ir.FieldAddress(ir.AddressOf(slot), point, 1), ir.Const(TirType.I32, 7));
            ir.Store(ir.ElementAddress(ir.AddressOf(rowSlot), TirType.I32, function.Parameters[1]), ir.Const(TirType.I32, 1));
            var y = ir.Load(TirType.I32, ir.FieldAddress(function.Parameters[0], point, 1));
            var element = ir.Load(TirType.I32, ir.ElementAddress(function.Parameters[0], TirType.I32, ir.Const(TirType.I64, -2)));
            ir.Store(function.Parameters[0], ir.Binary(TirBinaryOp.Add, y, element), isVolatile: true);
            ir.Return(ir.Load(TirType.I32, function.Parameters[0], isVolatile: true));
            return function;
        });

        Assert.Contains("struct Program_Point s_s = { 0 };", code);
        Assert.Contains("struct cs_a4_i32 s_row = { { 0 } };", code);
        Assert.Contains("s_s.Y = 7;", code);
        Assert.Contains("s_row.e[(int32_t)p_i] = 1;", code);
        Assert.Contains("uint32_t t7 = ((struct Program_Point *)p_p)->Y;", code);
        Assert.Contains("uint32_t t10 = ((uint32_t *)p_p)[-2];", code);
        Assert.Contains("*(volatile uint32_t *)p_p = t11;", code);
        Assert.Contains("uint32_t t12 = *(volatile uint32_t *)p_p;", code);
    }

    [Fact]
    public void Structs_are_C_structs_arrays_structs_around_C_arrays_and_their_layout_is_checked()
    {
        var point = new TirStructType("Program.Point", ("X", TirType.I8), ("Y", TirType.I32), ("<type>", TirType.Ptr),
            ("Row", new TirArrayType(TirType.I16, 3)));
        var module = WithGlobals(m => [m.AddGlobal("Program.p", point)]);

        var code = Generate(module);

        Assert.Contains("struct cs_a3_i16\n{\n    uint16_t e[3];\n};\n", code);
        Assert.Contains("struct Program_Point\n{\n    uint8_t X;\n    uint32_t Y;\n    uint8_t *f_type_;\n    struct cs_a3_i16 Row;\n};\n", code);
        Assert.Contains("typedef char cs_layout_Program_Point[sizeof(struct Program_Point) == 24\n" +
            "    && offsetof(struct Program_Point, Y) == 4\n    && offsetof(struct Program_Point, f_type_) == 8\n" +
            "    && offsetof(struct Program_Point, Row) == 16 ? 1 : -1];", code);
        Assert.Contains("typedef char cs_layout_cs_a3_i16[sizeof(struct cs_a3_i16) == 6 ? 1 : -1];", code);
        Assert.Contains("static struct Program_Point Program_p;", code);
    }

    [Fact]
    public void An_8_bit_chip_lays_structs_out_without_padding()
    {
        var point = new TirStructType("Program.Point", ("X", TirType.I8), ("Y", TirType.I32));
        var module = WithGlobals(m => [m.AddGlobal("Program.p", point)]);

        var code = Generate(module, CTargets.Make("chip", pointerSize: 2));

        Assert.Contains("typedef char cs_layout_Program_Point[sizeof(struct Program_Point) == 5\n" +
            "    && offsetof(struct Program_Point, Y) == 1 ? 1 : -1];", code);
    }

    [Fact]
    public void A_value_at_an_address_that_may_not_be_aligned_is_copied_byte_by_byte()
    {
        var code = Generate(WithFunction(module =>
        {
            var function = module.AddFunction("Program.F", TirType.I32, ("p", TirType.Ptr));
            var ir = new TirBuilder(function.AddBlock("entry"));
            ir.StoreUnaligned(function.Parameters[0], ir.Const(TirType.I64, 1));
            ir.Return(ir.LoadUnaligned(TirType.I32, function.Parameters[0]));
            return function;
        }));

        Assert.Contains("uint64_t v = 1;\n        cs_copy(p_p, (uint8_t *)&v, 8);\n", code);
        Assert.Contains("uint32_t t1;\n        cs_copy((uint8_t *)&t1, p_p, 4);\n", code);
        Assert.Contains("static void cs_copy(uint8_t *to, const uint8_t *from, size_t count)", code);
    }

    [Fact]
    public void Addresses_become_numbers_and_back_through_the_integer_of_the_pointers_width()
    {
        var code = DefinitionOf(module =>
        {
            var function = module.AddFunction("Program.F", TirType.Ptr, ("p", TirType.Ptr));
            var ir = new TirBuilder(function.AddBlock("entry"));
            ir.Return(ir.IntToPtr(ir.PtrToInt(function.Parameters[0], TirType.I32)));
            return function;
        });

        Assert.Contains("uint32_t t0 = (uint32_t)(uint64_t)p_p;", code);
        Assert.Contains("uint8_t *t1 = (uint8_t *)(uint64_t)t0;", code);
    }

    [Fact]
    public void A_struct_value_only_the_next_store_uses_is_written_there()
    {
        var code = DefinitionOf(PointCopies);

        // a and b are stored to first: their zero would never be read
        Assert.Contains("    struct Program_Point s_a;\n    struct Program_Point s_b;\n", code);
        Assert.Contains("struct Program_Point t0 = { 0 };\n        s_a = t0;\n        s_b = s_a;\n        return s_b;", code);
    }

    /// <summary>With "c.structReturn": "pointer" (SDCC's STM8) a function gives a struct it returns through a pointer of its own.</summary>
    [Fact]
    public void A_struct_result_goes_through_a_pointer_where_the_target_asks()
    {
        var target = CTargets.Make("chip", pointerSize: 4, c: new JsonObject { ["structReturn"] = "pointer" });
        var code = DefinitionOf(PointCopies, target);

        Assert.StartsWith("static void Program_F(struct Program_Point *cs_result)", code);
        Assert.Contains("s_b = s_a;\n        *cs_result = s_b;\n        return;", code);
    }

    /// <summary><c>function Point Program.F() { a = zero; b = a; return b }</c>.</summary>
    private static TirFunction PointCopies(TirModule module)
    {
        var point = new TirStructType("Program.Point", ("X", TirType.I32));
        var function = module.AddFunction("Program.F", point);
        var (a, b) = (function.AddSlot("a", point), function.AddSlot("b", point));
        var ir = new TirBuilder(function.AddBlock("entry"));
        ir.Store(a, ir.Zero(point));
        ir.Store(b, ir.Load(a));
        ir.Return(ir.Load(b));
        return function;
    }

    // ---- calls ----

    [Fact]
    public void A_call_through_an_address_casts_it_to_a_typedef_of_the_function_pointer()
    {
        var code = Generate(CPrograms.IndirectAndInterrupt(), Pc);

        Assert.Contains("typedef uint32_t (*cs_function_0)(uint32_t);", code);
        Assert.Contains("uint32_t t2 = ((cs_function_0)(uint8_t *)&Program_Twice)(21);", code);
        Assert.Contains("/* the program's entry, which the runtime's startup calls: Main's result, 0 for a void Main */\n" +
            "int32_t chipsharp_entry(void)\n{\n    return (int32_t)Program_Main();\n}\n", code);
        Assert.Contains("\nvoid chipsharp_isr_irq1(void)\n{", code); // exported: not static, by its symbol
        Assert.DoesNotContain("CS_REENTRANT", code);
    }

    [Fact]
    public void On_a_target_whose_functions_keep_their_locals_in_static_memory_those_that_may_run_twice_are_reentrant()
    {
        var target = CTargets.Make("pdk", pointerSize: 2, toolchain: "sdcc",
            capabilities: new Dictionary<string, string> { ["recursion"] = "reentrant" });

        var code = Generate(CPrograms.IndirectAndInterrupt(), target);

        Assert.Contains("#define CS_REENTRANT __reentrant", code);
        Assert.Contains("static uint32_t Program_Twice(uint32_t p_x) CS_REENTRANT;", code); // called through an address
        Assert.Contains("static uint32_t Program_Shared(uint32_t p_x) CS_REENTRANT;", code); // in the interrupt and Main
        Assert.Contains("static uint32_t Program_Main(void);", code);
        Assert.Contains("typedef uint32_t (*cs_function_0)(uint32_t) CS_REENTRANT;", code);

        var recursive = Generate(WithFunction(TirSamples.AddMax), target);
        Assert.DoesNotContain("CS_REENTRANT", recursive);
    }

    [Fact]
    public void The_C_librarys_memset_is_called_with_its_own_types_an_int_and_a_size_t()
    {
        var code = DefinitionOf(module =>
        {
            var memset = module.AddExternalFunction("memset", TirType.Ptr, ("destination", TirType.Ptr),
                ("value", TirType.I32), ("count", TirType.NativeInt));
            var function = module.AddFunction("Program.F", TirType.Void, ("p", TirType.Ptr), ("n", TirType.NativeInt));
            var ir = new TirBuilder(function.AddBlock("entry"));
            ir.Call(memset, function.Parameters[0], ir.Const(TirType.I32, 0x1FF), function.Parameters[1]);
            ir.Return();
            return function;
        });

        Assert.Contains("memset(p_p, (int)(uint8_t)511, (size_t)p_n);", code);
    }

    // ---- globals and objects ----

    [Fact]
    public void Constant_data_is_static_const_with_its_text_in_a_comment_and_a_table_its_elements()
    {
        var module = WithGlobals(m =>
        [
            m.AddConstant("<text 0>", "Hi!\0"u8.ToArray()),
            m.AddArray("Program.Table", new TirArrayType(TirType.I16, 3),
            [new TirConst(TirType.I16, -2), new TirConst(TirType.I16, 300), new TirConst(TirType.I16, 0x7FFF)], isConstant: true),
            m.AddGlobal("Program.start", TirType.F32, new TirConst(TirType.F32, 2.5)),
        ]);

        var code = Generate(module);

        Assert.Contains("/* \"Hi!\" */\nstatic const struct cs_a4_i8 cs_text_0_ = { { 72, 105, 33, 0 } };", code);
        Assert.Contains("static const struct cs_a3_i16 Program_Table = { { (uint16_t)-2, 300, 32767 } };", code);
        Assert.Contains("static float Program_start = 0x1.4p+1f /* 2.5 */;", code);
    }

    [Fact]
    public void A_target_may_name_a_qualifier_for_constant_data()
    {
        var module = WithGlobals(m => [m.AddConstant("<text 0>", "A"u8.ToArray())]);

        var code = Generate(module, CTargets.Make("chip", pointerSize: 2, c: new JsonObject { ["constQualifier"] = "__code" }));

        Assert.Contains("static const struct cs_a1_i8 __code cs_text_0_ = { { 65 } };", code);
    }

    [Fact]
    public void Objects_are_made_by_the_runtime_from_their_type_descriptor_and_a_null_reference_stops_the_program()
    {
        var module = Objects();

        var code = Generate(module, CTargets.Make("pc", pointerSize: 8, heap: 1 << 20));

        Assert.Contains("void *chipsharp_new_object(const void *type);", code);
        Assert.Contains("chipsharp_new_object((uint8_t *)&cs_type_Program_Square)", code);
        Assert.Contains("static const struct cs_type_", code);
        Assert.Contains("ChipSharp_Runtime_NullReference();", code);
        Assert.Contains("int32_t chipsharp_entry(void)\n{\n    chipsharp_heap_initialize(", code);
    }

    [Fact]
    public void A_big_endian_target_gets_a_constant_strings_two_byte_chars_high_byte_first()
    {
        var module = WithGlobals(m => [m.AddConstantObject("<text 0>", m.AddArrayType("string", TirType.I16), 2,
            [0x41, 0x00, 0xAC, 0x20])]);

        var little = Generate(module, CTargets.Make("pc", pointerSize: 2));
        var big = Generate(module, CTargets.Make("stm8", pointerSize: 2, Endianness.Big));

        Assert.Contains("(uint8_t *)&cs_type_string, 2, { 0 }, { 65, 0, 172, 32 } };", little);
        Assert.Contains("(uint8_t *)&cs_type_string, 2, { 0 }, { 0, 65, 32, 172 } };", big);
    }

    // ---- what a target can run ----

    [Fact]
    public void A_double_is_refused_on_a_target_whose_C_double_has_32_bits()
    {
        var module = WithFunction(Binary(TirBinaryOp.Add, TirType.F64));
        var sdcc = CTargets.Make("pdk", pointerSize: 2, toolchain: "sdcc");
        var flagged = CTargets.Make("chip", pointerSize: 2, capabilities: new Dictionary<string, string> { ["double64"] = "false" });

        var error = Assert.Throws<ChipSharpException>(() => Generate(module, sdcc));
        Assert.Contains("The function Program.F uses double (C#'s 64-bit floating point), and target 'pdk' compiles C " +
            "whose double has 32 bits", error.Message);
        Assert.Contains("Use float instead.", error.Message);
        Assert.Throws<ChipSharpException>(() => Generate(module, flagged));
        Assert.Contains("typedef char cs_double_has_64_bits[sizeof(double) == 8 ? 1 : -1];", Generate(module));
        Assert.DoesNotContain("double", Generate(WithFunction(Binary(TirBinaryOp.Add, TirType.F32)), sdcc));
    }

    [Fact]
    public void The_target_files_C_types_are_checked_against_the_compiler()
    {
        var target = CTargets.Make("chip", pointerSize: 2,
            c: new JsonObject { ["int"] = 2, ["long"] = 4, ["double"] = 4, ["charSigned"] = false });

        var code = Generate(TirSamples.WithMain(new TirModule("Test")), target);

        Assert.Contains("typedef char cs_target_types[sizeof(int) == 2\n    && sizeof(long) == 4\n    && (char)-1 > 0 ? 1 : -1];", code);
    }

    [Fact]
    public void Objects_are_refused_without_a_heap_and_stack_checks_need_the_stacks_room()
    {
        var objects = Objects();
        Assert.Contains("which needs a heap", Assert.Throws<ChipSharpException>(() => Generate(objects)).Message);

        var checks = CTargets.Make("chip", pointerSize: 4, capabilities: new Dictionary<string, string> { ["stackCheck"] = "true" });
        Assert.Contains("asks for stack checks (\"stackCheck\": true), which the c code generator writes from the region " +
            "of the stack and its size: the target has no memory region 'ram'", Assert.Throws<ChipSharpException>(() =>
            Generate(TirSamples.WithMain(new TirModule("Test")), checks)).Message);
    }

    /// <summary>
    /// On the STM8 (1 KB of RAM, the stack growing down from its end toward the variables) a function that calls itself
    /// checks the address of a byte of its frame against the end of the variables (the program has none here, and 64
    /// bytes are the runtime's), a margin of 64 bytes, and room for one more call of it (its 4 bytes of argument, the
    /// return address, the byte, 8 bytes of values): 0x80 + 15; on the PDK14 (128 bytes, the stack growing up to their
    /// end) against the end of RAM less 4 bytes, and room for one more call (each byte of argument 2 on its stack). A
    /// function that does not recurse checks nothing.
    /// </summary>
    [Fact]
    public void A_function_that_calls_itself_checks_the_stack_where_it_starts_on_both_SDCC_chips()
    {
        const string source = """
            static class Program
            {
                static int Depth(int n) => n == 0 ? 0 : 1 + Depth(n - 1);

                static int Twice(int n) => n * 2;

                static int Main() => Twice(Depth(5));
            }
            """;
        var stm8 = CSdcc.Required(source, "Checks", CSdcc.Stm8s103).C;
        Assert.Contains("#define CS_STACK_LIMIT 0x0080u\nvoid chipsharp_stack_overflow(void);", stm8);
        Assert.Contains("static uint32_t Program_Depth(uint32_t p_n)\n{\n    uint8_t cs_frame;\n" +
            "    if ((uintptr_t)&cs_frame < CS_STACK_LIMIT + 15u)\n        chipsharp_stack_overflow();\n", stm8);
        Assert.Contains("static uint32_t Program_Twice(uint32_t p_n)\n{\n    {\n", stm8);

        var pdk14 = CSdcc.Required(source, "Checks", CSdcc.Pfs154).C;
        Assert.Contains("#define CS_STACK_LIMIT 0x007Cu", pdk14);
        Assert.Contains("if ((uintptr_t)&cs_frame > CS_STACK_LIMIT - 19u)", pdk14);
    }

    [Fact]
    public void External_functions_keep_their_symbols_and_two_that_clash_are_an_error()
    {
        var module = TirSamples.WithMain(new TirModule("Test"));
        module.AddExternalFunction("ChipSharp.Runtime.Write", TirType.Void);
        module.AddExternalFunction("ChipSharp_Runtime.Write", TirType.Void);

        var error = Assert.Throws<ChipSharpException>(() => Generate(module));

        Assert.Contains("would both have the symbol 'ChipSharp_Runtime_Write'", error.Message);
    }

    [Fact]
    public void Names_the_compiler_keeps_for_itself_are_not_taken()
    {
        var module = new TirModule("Test");
        var global = module.AddGlobal("<Module>.int", TirType.I32);
        var function = module.AddFunction("<lambda>", TirType.Void, ("int", TirType.I32));
        new TirBuilder(function.AddBlock("entry")).Return();

        var code = Generate(Calling(module, function, global));

        Assert.Contains("static uint32_t cs_Module__int;", code);
        Assert.Contains("static void cs_lambda_(uint32_t p_int);", code);
    }

    [Fact]
    public void The_backend_c_is_registered_and_host_c_picks_it_while_every_other_target_keeps_llvm()
    {
        var registry = DefaultComponents.CreateRegistry(new BuildContext(new BuildOptions(), CTargets.HostC, "Test",
            Path.Combine(Path.GetTempPath(), "unused")));

        Assert.IsType<CCodeGenerator>(registry.Get<ICodeGenerator>("c"));
        Assert.Equal("c", CTargets.HostC.Backend);
        Assert.Equal(["host-c", "host", "x86_64"], CTargets.HostC.InheritanceChain);
        var targets = new Targets.TargetLoader(Path.Combine(AppContext.BaseDirectory, "targets"));
        foreach (var name in targets.Names.Where(name => name is not ("host-c" or "pdk14" or "pfs154" or "pfs154-bare"
            or "stm8" or "stm8s103f3" or "stm8s103-board")))
        {
            Assert.Equal("llvm", targets.Load(name).Backend);
        }
    }
}
