using ChipSharp.Ir;

namespace ChipSharp.Tests.Tir;

/// <summary>How the memory model prints: structs and globals at the top of a module, addresses, loads and stores.</summary>
public sealed class TirMemoryPrinterTests
{
    [Fact]
    public void Prints_the_structs_and_globals_of_a_module_before_its_functions()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        TirMemorySamples.AddCounter(module);
        module.AddGlobal("Program.lines", new TirArrayType(TirMemorySamples.Line, 2));
        module.AddGlobal("Program.ready", TirType.Bool, new TirConst(true));
        module.AddGlobal("Program.scale", TirType.F64, new TirConst(TirType.F64, 0.5));
        module.AddGlobal("Program.next", TirType.Ptr);

        TirVerifier.Verify(module);
        Assert.Equal(Text("""
            module Demo
            entrypoint Program.Main

            struct Program.Point { i32 X, i32 Y }
            struct Program.Line { Program.Point From, Program.Point To, i8 Color }
            global i32 @Program.counter = 5
            global [2 x Program.Line] @Program.lines
            global bool @Program.ready = true
            global f64 @Program.scale = 0.5
            global ptr @Program.next

            function void Program.Main()
            entry:
              return

            function void Program.Increment()
            entry:
              %0 = addr @Program.counter
              %1 = load i32 [%0]
              %2 = const i32 1
              %3 = add i32 %1, %2
              store i32 [%0], %3
              return

            """), TirPrinter.Print(module));
    }

    [Fact]
    public void A_constant_prints_its_bytes_printable_ASCII_as_it_is_and_the_others_in_hex()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        module.AddGlobal("Program.counter", TirType.I32);
        module.AddConstant("<text 0>", [.. "say \"hi\"\\\n"u8, 0xC3, 0xA9]);

        TirVerifier.Verify(module);
        Assert.Equal(Text("""
            module Demo
            entrypoint Program.Main

            global i32 @Program.counter
            constant [12 x i8] @<text 0> = c"say \22hi\22\5C\0A\C3\A9"

            function void Program.Main()
            entry:
              return

            """), TirPrinter.Print(module));
    }

    [Fact]
    public void A_module_without_structs_or_globals_prints_as_before()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));

        Assert.Equal(Text("""
            module Demo
            entrypoint Program.Main

            function void Program.Main()
            entry:
              return

            """), TirPrinter.Print(module));
    }

    [Fact]
    public void Prints_a_struct_in_a_slot_through_field_addresses()
    {
        var module = new TirModule("Demo");

        Assert.Equal(Text("""
            function i32 Program.LengthSquared(i32 %x, i32 %y)
              $p: Program.Point
            entry:
              %0 = addr $p
              %1 = fieldaddr Program.Point %0, X
              store i32 [%1], %x
              %2 = fieldaddr Program.Point %0, Y
              store i32 [%2], %y
              %3 = load i32 [%1]
              %4 = load i32 [%2]
              %5 = mul i32 %3, %3
              %6 = mul i32 %4, %4
              %7 = add i32 %5, %6
              return %7

            """), TirPrinter.Print(TirMemorySamples.AddLengthSquared(module)));
    }

    [Fact]
    public void Prints_a_struct_passed_returned_and_copied_whole()
    {
        var module = new TirModule("Demo");

        Assert.Equal(Text("""
            function Program.Point Program.Swap(Program.Point %p)
              $p: Program.Point
              $q: Program.Point
            entry:
              store $p, %p
              %0 = addr $p
              %1 = addr $q
              %2 = fieldaddr Program.Point %1, X
              %3 = fieldaddr Program.Point %0, Y
              %4 = load i32 [%3]
              store i32 [%2], %4
              %5 = fieldaddr Program.Point %1, Y
              %6 = fieldaddr Program.Point %0, X
              %7 = load i32 [%6]
              store i32 [%5], %7
              %8 = load Program.Point $q
              return %8

            """), TirPrinter.Print(TirMemorySamples.AddSwap(module)));
    }

    [Fact]
    public void Prints_an_array_in_a_slot_through_element_addresses()
    {
        var module = new TirModule("Demo");

        Assert.Equal(Text("""
            function i32 Program.SumOfSquares()
              $squares: [10 x i32]
              $i: i32
              $j: i32
              $sum: i32
            fill:
              %0 = load i32 $i
              %1 = const i32 10
              %2 = lt i32 %0, %1
              branch %2, fillBody, add
            fillBody:
              %3 = load i32 $i
              %4 = addr $squares
              %5 = elementaddr i32 %4, %3
              %6 = mul i32 %3, %3
              store i32 [%5], %6
              %7 = const i32 1
              %8 = add i32 %3, %7
              store $i, %8
              jump fill
            add:
              %9 = load i32 $j
              %10 = const i32 10
              %11 = lt i32 %9, %10
              branch %11, addBody, done
            addBody:
              %12 = load i32 $j
              %13 = addr $squares
              %14 = elementaddr i32 %13, %12
              %15 = load i32 [%14]
              %16 = load i32 $sum
              %17 = add i32 %16, %15
              store $sum, %17
              %18 = const i32 1
              %19 = add i32 %12, %18
              store $j, %19
              jump add
            done:
              %20 = load i32 $sum
              return %20

            """), TirPrinter.Print(TirMemorySamples.AddSumOfSquares(module)));
    }

    [Fact]
    public void Prints_volatile_accesses_and_conversions_between_numbers_and_addresses()
    {
        var module = new TirModule("Demo");

        Assert.Equal(Text("""
            function nint Program.Toggle(i32 %address, i32 %bits)
            entry:
              %0 = inttoptr i32 %address to ptr
              %1 = load volatile i32 [%0]
              %2 = xor i32 %1, %bits
              store volatile i32 [%0], %2
              %3 = ptrtoint ptr %0 to nint
              return %3

            """), TirPrinter.Print(TirMemorySamples.AddRegisterToggle(module)));
    }

    [Fact]
    public void Prints_pointer_comparisons_and_unordered_float_comparisons()
    {
        var f = new TirModule("Demo").AddFunction("Program.F", TirType.Bool,
            ("a", TirType.Ptr), ("b", TirType.Ptr), ("x", TirType.F64));
        var b = new TirBuilder(f.AddBlock("entry"));
        var (pa, pb, x) = (f.Parameters[0], f.Parameters[1], f.Parameters[2]);

        Assert.Equal("%0 = eq ptr %a, %b", b.Compare(TirCompareOp.Eq, pa, pb).ToString());
        Assert.Equal("%1 = ult ptr %a, %b", b.Compare(TirCompareOp.LtUnsigned, pa, pb).ToString());
        Assert.Equal("%2 = uge f64 %x, %x", b.Compare(TirCompareOp.GeUnsigned, x, x).ToString());
    }

    [Fact]
    public void Prints_zero_of_any_type()
    {
        var f = new TirModule("Demo").AddFunction("Program.F", TirType.Void);
        var b = new TirBuilder(f.AddBlock("entry"));

        Assert.Equal("%0 = zero Program.Point", b.Zero(TirMemorySamples.Point).ToString());
        Assert.Equal("%1 = zero [10 x i32]", b.Zero(TirMemorySamples.TenInts).ToString());
        Assert.Equal("%2 = zero ptr", b.Zero(TirType.Ptr).ToString());
        Assert.Equal("%3 = zero f64", b.Zero(TirType.F64).ToString());
    }

    [Fact]
    public void Prints_sizeof_and_lists_a_struct_only_measured()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var f = module.AddFunction("Program.Size", TirType.I32);
        var b = new TirBuilder(f.AddBlock("entry"));
        b.Return(b.SizeOf(TirMemorySamples.Point));

        Assert.Equal(Text("""
            module Demo
            entrypoint Program.Main

            struct Program.Point { i32 X, i32 Y }

            function void Program.Main()
            entry:
              return

            function i32 Program.Size()
            entry:
              %0 = sizeof Program.Point
              return %0

            """), TirPrinter.Print(module));
    }

    [Fact]
    public void A_field_number_out_of_range_prints_as_a_number()
    {
        var f = new TirModule("Demo").AddFunction("Program.F", TirType.Void, ("p", TirType.Ptr));
        var b = new TirBuilder(f.AddBlock("entry"));

        var field = b.FieldAddress(f.Parameters[0], TirMemorySamples.Point, 7);

        Assert.Equal("%0 = fieldaddr Program.Point %p, #7", field.ToString());
    }

    /// <summary>The expected text with "\n" line endings, whatever the test file was checked out with.</summary>
    private static string Text(string text) => text.ReplaceLineEndings("\n");
}
