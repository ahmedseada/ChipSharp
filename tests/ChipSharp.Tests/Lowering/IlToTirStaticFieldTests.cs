using System.Reflection.Emit;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// The TIR that static fields become: a global per field, reached through its address; initial values for the
/// fields an initializer only sets to constants; and a start function that runs the other initializers before Main.
/// </summary>
public sealed class IlToTirStaticFieldTests
{
    [Fact]
    public void A_static_field_is_a_global_whose_address_each_load_and_store_takes()
    {
        var module = Lowered.Module("""
            class Program
            {
                static int counter;

                static int Next()
                {
                    counter++;
                    return counter;
                }

                static void Main() => System.Console.WriteLine(Next() + Next());
            }
            """);

        Assert.Equal("""
            module Test
            entrypoint Program.Main

            global i32 @Program.counter

            function void Program.Main()
            IL_0000:
              %0 = call i32 Program.Next()
              %1 = call i32 Program.Next()
              %2 = add i32 %0, %1
              call void ChipSharp.Runtime.WriteLineInt32(%2)
              return

            function i32 Program.Next()
            IL_0000:
              %0 = addr @Program.counter
              %1 = load i32 [%0]
              %2 = const i32 1
              %3 = add i32 %1, %2
              %4 = addr @Program.counter
              store i32 [%4], %3
              %5 = addr @Program.counter
              %6 = load i32 [%5]
              return %6

            external function void ChipSharp.Runtime.WriteLineInt32(i32 %value)

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module));
    }

    [Fact]
    public void Small_integers_and_bools_are_widened_when_loaded_and_narrowed_when_stored()
    {
        // like locals: conv.u1 and conv.i1 widen again, the store keeps the low 8 bits; a byte is zero-extended and an
        // sbyte sign-extended onto the stack; a bool is stored as it is (Roslyn writes x != 0 as cgt.un) and loaded as 0 or 1
        Assert.Equal("""
            function i32 C.Mix(i32 %x)
            IL_0000:
              %0 = convert i32 %x to i8
              %1 = uconvert i8 %0 to i32
              %2 = convert i32 %1 to i8
              %3 = addr @C.b
              store i8 [%3], %2
              %4 = convert i32 %x to i8
              %5 = convert i8 %4 to i32
              %6 = convert i32 %5 to i8
              %7 = addr @C.s
              store i8 [%7], %6
              %8 = const i32 0
              %9 = ugt i32 %x, %8
              %10 = addr @C.f
              store bool [%10], %9
              %11 = addr @C.b
              %12 = load i8 [%11]
              %13 = uconvert i8 %12 to i32
              %14 = addr @C.s
              %15 = load i8 [%14]
              %16 = convert i8 %15 to i32
              %17 = add i32 %13, %16
              %18 = addr @C.f
              %19 = load bool [%18]
              %20 = convert bool %19 to i32
              %21 = const i32 0
              %22 = ugt i32 %20, %21
              %23 = convert bool %22 to i32
              %24 = add i32 %17, %23
              return %24

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static byte b;
                static sbyte s;
                static bool f;

                static int Mix(int x)
                {
                    b = (byte)x;
                    s = (sbyte)x;
                    f = x != 0;
                    return b + s + (f ? 1 : 0);
                }
            }
            """, "C.Mix"));
    }

    [Fact]
    public void A_volatile_field_is_loaded_and_stored_volatile()
    {
        Assert.Equal("""
            function i32 C.Tick()
            IL_0000:
              %0 = addr @C.ticks
              %1 = load volatile i32 [%0]
              %2 = const i32 1
              %3 = add i32 %1, %2
              %4 = addr @C.ticks
              store volatile i32 [%4], %3
              %5 = addr @C.ticks
              %6 = load volatile i32 [%5]
              return %6

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static volatile int ticks;

                static int Tick()
                {
                    ticks = ticks + 1;
                    return ticks;
                }
            }
            """, "C.Tick"));
    }

    [Fact]
    public void Fields_an_initializer_only_sets_to_constants_start_with_those_values_and_it_is_not_compiled()
    {
        var module = Lowered.Module("""
            using System;

            class Program
            {
                static bool on = true;
                static sbyte minus = -5;
                static byte max = 255;
                static short low = -300;
                static ushort high = 60000;
                static int number = 123456;
                static uint big = 4_000_000_000;
                static long wide = -1L << 40;
                static ulong all = ulong.MaxValue;
                static nint native = -7;
                static nuint unsigned = 7;
                static int zero = 0;
                static int unused = 99;
                static int none;

                static void Main()
                {
                    Console.WriteLine(on);
                    Console.WriteLine(minus + max + low + high + number + none + zero);
                    Console.WriteLine(big);
                    Console.WriteLine(wide);
                    Console.WriteLine(all);
                    Console.WriteLine((long)native + (long)unsigned);
                }
            }
            """);

        Assert.Equal(
        [
            "global bool @Program.on = true",
            "global i8 @Program.minus = -5",
            "global i8 @Program.max = -1",
            "global i16 @Program.low = -300",
            "global i16 @Program.high = -5536",
            "global i32 @Program.number = 123456",
            "global i32 @Program.none",
            "global i32 @Program.zero",
            "global i32 @Program.big = -294967296",
            "global i64 @Program.wide = -1099511627776",
            "global i64 @Program.all = -1",
            "global nint @Program.native = -7",
            "global nint @Program.unsigned = 7",
        ], Globals(module));
        Assert.Equal("Program.Main", module.EntryPoint!.Name);
        Assert.DoesNotContain(module.Functions, function => function.Name == "Program..cctor");
    }

    [Fact]
    public void In_Debug_a_static_constructor_of_constants_still_becomes_initial_values()
    {
        // Debug adds nops around the static constructor's body
        var module = Lowered.Lower(Lowered.Program(Lowered.CompileDebug("""
            class Program
            {
                static readonly int Limit;
                static readonly bool Ready = true;

                static Program()
                {
                    Limit = 10;
                }

                static void Main() => System.Console.WriteLine(Ready ? Limit : 0);
            }
            """, "Test")));

        Assert.Equal(["global bool @Program.Ready = true", "global i32 @Program.Limit = 10"], Globals(module));
        Assert.Equal("Program.Main", module.EntryPoint!.Name);
    }

    [Fact]
    public void The_start_runs_the_initializers_that_are_code_in_order_and_then_Main()
    {
        // B's initializer reads A's field, so A's runs first; C has a static constructor, so calling its method counts
        var module = Lowered.Module("""
            using System;

            static class A
            {
                public static readonly int Value = Compute();

                static int Compute() => 6 * 7;
            }

            static class B
            {
                public static readonly int Value = A.Value + 1;
            }

            static class C
            {
                static int factor;

                static C()
                {
                    factor = A.Value / 21;
                }

                public static int Twice(int x) => x * factor;
            }

            static class D
            {
                public static readonly int Constant = 5;
            }

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(B.Value);
                    Console.WriteLine(C.Twice(D.Constant));
                }
            }
            """);

        Assert.Equal("<Start>", module.EntryPoint!.Name);
        Assert.Equal("""
            function void <Start>()
            entry:
              call void A..cctor()
              call void B..cctor()
              call void C..cctor()
              call void Program.Main()
              return

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module.EntryPoint));
        Assert.Equal("""
            function void B..cctor()
            IL_0000:
              %0 = addr @A.Value
              %1 = load i32 [%0]
              %2 = const i32 1
              %3 = add i32 %1, %2
              %4 = addr @B.Value
              store i32 [%4], %3
              return

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module.Functions.Single(f => f.Name == "B..cctor")));
        Assert.Contains("global i32 @D.Constant = 5", Globals(module));
    }

    [Fact]
    public void The_start_returns_the_exit_code_Main_returns()
    {
        var module = Lowered.Module("""
            class Program
            {
                static int code = Code();

                static int Code() => 40 + 2;

                static int Main() => code;
            }
            """);

        Assert.Equal("""
            function i32 <Start>()
            entry:
              call void Program..cctor()
              %0 = call i32 Program.Main()
              return %0

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module.EntryPoint!));
    }

    [Fact]
    public void A_native_constant_that_differs_by_target_is_computed_by_the_initializer()
    {
        // 3 000 000 000 does not fit a TIR nint constant (its meaning would depend on the width of nint)
        var module = Lowered.Module("""
            class Program
            {
                static nuint large = 3_000_000_000;

                static void Main() => System.Console.WriteLine((ulong)large);
            }
            """);

        Assert.Equal(["global nint @Program.large"], Globals(module));
        Assert.Equal("""
            function void Program..cctor()
            IL_0000:
              %0 = const i32 -1294967296
              %1 = uconvert i32 %0 to nint
              %2 = addr @Program.large
              store nint [%2], %1
              return

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module.Functions.Single(f => f.Name == "Program..cctor")));
    }

    [Fact]
    public void Volatile_before_anything_but_a_field_is_an_error()
    {
        var emitted = new EmittedAssembly();
        emitted.Method("F", typeof(int), [], il =>
        {
            il.Emit(OpCodes.Volatile);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
        });

        var error = Assert.Throws<ChipSharpException>(() => Lowered.Lower(Lowered.Program(emitted.Read(), "Program.F")));

        Assert.Equal("Emitted.dll: Program.F() at IL_0000: volatile. must come right before ldsfld, stsfld, ldfld or " +
            "stfld: ChipSharp has no other memory access it applies to.", error.Message);
    }

    [Fact]
    public void A_field_of_an_unsupported_type_is_an_error()
    {
        var emitted = new EmittedAssembly();
        var name = emitted.Field("name", typeof(object));
        emitted.Method("F", typeof(void), [], il =>
        {
            il.Emit(OpCodes.Ldsfld, name);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        });

        var error = Assert.Throws<ChipSharpException>(() => Lowered.Lower(Lowered.Program(emitted.Read(), "Program.F")));

        Assert.Equal("Emitted.dll: Program.F() at IL_0000: the field Program.name has type object, which is not " +
            "supported yet (ChipSharp compiles bool, whole numbers, float, double, fixed text (string), and the structs " +
            "and enums of the program).", error.Message);
    }

    [Fact]
    public void A_value_of_the_wrong_stack_type_for_a_field_is_an_error()
    {
        var emitted = new EmittedAssembly();
        var total = emitted.Field("total", typeof(long));
        emitted.Method("F", typeof(void), [], il =>
        {
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Stsfld, total);
            il.Emit(OpCodes.Ret);
        });

        var error = Assert.Throws<ChipSharpException>(() => Lowered.Lower(Lowered.Program(emitted.Read(), "Program.F")));

        Assert.Equal("Emitted.dll: Program.F() at IL_0001: field Program.total has type int64, but the value on the " +
            "evaluation stack is int32.", error.Message);
    }

    [Fact]
    public void A_field_of_the_library_is_an_error()
    {
        var error = Assert.Throws<ChipSharpException>(() => Lowered.Module("""
            class Program
            {
                static void Main() => System.Console.WriteLine((long)nint.Zero);
            }
            """));

        Assert.Equal("Test.dll: Program.Main() at IL_0000: ldsfld of System.IntPtr.Zero is not supported: ChipSharp compiles " +
            "the static fields of the program's own types.", error.Message);
    }

    /// <summary>The module's globals as the printer writes them, in order.</summary>
    private static string[] Globals(TirModule module) =>
        [.. TirPrinter.Print(module).Split('\n').Where(line => line.StartsWith("global ", StringComparison.Ordinal))];
}
