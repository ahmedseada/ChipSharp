using System.Reflection.Emit;
using ChipSharp.Abstractions;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// The TIR that structs, enums and references become: a TIR struct per C# struct, struct values copied whole, fields
/// reached through addresses, this and every reference a ptr, an enum its integer; and the errors for what is left out.
/// </summary>
public sealed class IlToTirStructTests
{
    [Fact]
    public void Each_struct_is_a_TIR_struct_of_its_instance_fields_in_order_and_an_enum_is_its_integer()
    {
        // a static field is not part of the struct; an auto-property's is; an empty struct takes a byte
        var module = Lowered.Module("""
            enum Color : byte { Red, Green }

            struct Point
            {
                public int X, Y;
            }

            struct Sample
            {
                public static int Made;
                public byte Small;
                public long Big;
                public Color Color;
                public Point Where;
                public bool Ready { get; set; }
            }

            struct Empty
            {
            }

            static class C
            {
                static Sample sample;

                static int Run(Empty empty) => sample.Where.Y;
            }
            """, "C.Run");

        Assert.Equal("""
            module Test
            entrypoint C.Run

            struct Point { i32 X, i32 Y }
            struct Sample { i8 Small, i64 Big, i8 Color, Point Where, bool <Ready>k__BackingField }
            struct Empty { i8 <empty> }
            global Sample @C.sample

            function i32 C.Run(Empty %empty)
            IL_0000:
              %0 = addr @C.sample
              %1 = fieldaddr Sample %0, Where
              %2 = fieldaddr Point %1, Y
              %3 = load i32 [%2]
              return %3

            """.ReplaceLineEndings("\n"), TirPrinter.Print(module));
    }

    [Fact]
    public void A_struct_local_is_a_slot_reached_through_its_address_and_a_copy_is_a_load_and_store_of_it_whole()
    {
        // ldfld of a struct value (ldloc, not ldloca) puts the value in $temp_Point to read the field there
        Assert.Equal("""
            function i32 C.Run(i32 %x)
              $V_0: Point
              $V_1: Point
              $temp_Point: Point
            IL_0000:
              %0 = addr $V_0
              %1 = zero Point
              store Point [%0], %1
              %2 = addr $V_0
              %3 = fieldaddr Point %2, X
              store i32 [%3], %x
              %4 = load Point $V_0
              store $V_1, %4
              %5 = addr $V_1
              %6 = const i32 5
              %7 = fieldaddr Point %5, Y
              store i32 [%7], %6
              %8 = load Point $V_0
              store $temp_Point, %8
              %9 = addr $temp_Point
              %10 = fieldaddr Point %9, Y
              %11 = load i32 [%10]
              %12 = load Point $V_1
              store $temp_Point, %12
              %13 = addr $temp_Point
              %14 = fieldaddr Point %13, X
              %15 = load i32 [%14]
              %16 = add i32 %11, %15
              return %16

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            struct Point
            {
                public int X, Y;
            }

            static class C
            {
                static int Run(int x)
                {
                    Point p = default;
                    p.X = x;
                    var copy = p;
                    copy.Y = 5;
                    return p.Y + copy.X;
                }
            }
            """, "C.Run"));
    }

    [Fact]
    public void A_struct_method_takes_this_as_a_ptr_and_newobj_makes_the_struct_in_a_temporary_slot()
    {
        // C# calls the constructor on p's address itself; new Point(1, x).Sum needs newobj, then a local for this
        var module = Lowered.Module("""
            struct Point
            {
                public int X, Y;

                public Point(int x, int y)
                {
                    X = x;
                    Y = y;
                }

                public readonly int Sum => X + Y;

                public void Move(int by) => X += by;
            }

            static class C
            {
                static int Make(int x)
                {
                    var p = new Point(x, 2);
                    p.Move(3);
                    return new Point(1, x).Sum + p.Sum;
                }
            }
            """, "C.Make");

        Assert.Equal("""
            function i32 C.Make(i32 %x)
              $V_0: Point
              $V_1: Point
              $temp_Point: Point
            IL_0000:
              %0 = addr $V_0
              %1 = const i32 2
              call void Point..ctor(%0, %x, %1)
              %2 = addr $V_0
              %3 = const i32 3
              call void Point.Move(%2, %3)
              %4 = zero Point
              store $temp_Point, %4
              %5 = addr $temp_Point
              %6 = const i32 1
              call void Point..ctor(%5, %6, %x)
              %7 = load Point $temp_Point
              store $V_1, %7
              %8 = addr $V_1
              %9 = call i32 Point.get_Sum(%8)
              %10 = addr $V_0
              %11 = call i32 Point.get_Sum(%10)
              %12 = add i32 %9, %11
              return %12

            function void Point..ctor(ptr %this, i32 %x, i32 %y)
            IL_0000:
              %0 = fieldaddr Point %this, X
              store i32 [%0], %x
              %1 = fieldaddr Point %this, Y
              store i32 [%1], %y
              return

            function void Point.Move(ptr %this, i32 %by)
            IL_0000:
              %0 = fieldaddr Point %this, X
              %1 = load i32 [%0]
              %2 = add i32 %1, %by
              %3 = fieldaddr Point %this, X
              store i32 [%3], %2
              return

            function i32 Point.get_Sum(ptr %this)
            IL_0000:
              %0 = fieldaddr Point %this, X
              %1 = load i32 [%0]
              %2 = fieldaddr Point %this, Y
              %3 = load i32 [%2]
              %4 = add i32 %1, %3
              return %4

            """.ReplaceLineEndings("\n"), Print(module, "C.Make", "Point..ctor", "Point.Move", "Point.get_Sum"));
    }

    [Fact]
    public void Ref_and_out_arguments_are_addresses_and_what_a_reference_refers_to_says_how_wide_a_byte_is()
    {
        // x and y get slots for their addresses; a ref bool holds a bool, an out byte a byte
        var module = Lowered.Module("""
            static class C
            {
                static void Swap(ref int a, ref int b) => (a, b) = (b, a);

                static bool TryHalf(int x, out byte half)
                {
                    half = (byte)(x / 2);
                    return x % 2 == 0;
                }

                static void Toggle(ref bool flag) => flag = !flag;

                static int Run(int x, int y)
                {
                    Swap(ref x, ref y);
                    bool ok = TryHalf(x, out var half);
                    Toggle(ref ok);
                    return ok ? half : y;
                }
            }
            """, "C.Run");

        Assert.Equal("""
            function i32 C.Run(i32 %x, i32 %y)
              $x: i32
              $y: i32
              $V_0: bool
              $V_1: i8
            IL_0000:
              store $x, %x
              store $y, %y
              %0 = addr $x
              %1 = addr $y
              call void C.Swap(%0, %1)
              %2 = load i32 $x
              %3 = addr $V_1
              %4 = call bool C.TryHalf(%2, %3)
              store $V_0, %4
              %5 = addr $V_0
              call void C.Toggle(%5)
              %6 = load bool $V_0
              branch %6, IL_001e, IL_001c
            IL_001c:
              %7 = load i32 $y
              return %7
            IL_001e:
              %8 = load i8 $V_1
              %9 = uconvert i8 %8 to i32
              return %9

            function void C.Swap(ptr %a, ptr %b)
              $V_0: i32
              $V_1: i32
            IL_0000:
              %0 = load i32 [%b]
              store $V_0, %0
              %1 = load i32 [%a]
              store $V_1, %1
              %2 = load i32 $V_0
              store i32 [%a], %2
              %3 = load i32 $V_1
              store i32 [%b], %3
              return

            function bool C.TryHalf(i32 %x, ptr %half)
            IL_0000:
              %0 = const i32 2
              %1 = div i32 %x, %0
              %2 = convert i32 %1 to i8
              %3 = uconvert i8 %2 to i32
              %4 = convert i32 %3 to i8
              store i8 [%half], %4
              %5 = const i32 2
              %6 = rem i32 %x, %5
              %7 = const i32 0
              %8 = eq i32 %6, %7
              return %8

            function void C.Toggle(ptr %flag)
            IL_0000:
              %0 = load bool [%flag]
              %1 = convert bool %0 to i32
              %2 = const i32 0
              %3 = eq i32 %1, %2
              store bool [%flag], %3
              return

            """.ReplaceLineEndings("\n"), Print(module, "C.Run", "C.Swap", "C.TryHalf", "C.Toggle"));
    }

    [Fact]
    public void A_reference_that_waits_in_a_stack_slot_keeps_what_it_refers_to()
    {
        // both ways into IL_000f bring a reference to a bool, so stind.i1 stores a bool
        Assert.Equal("""
            function bool C.Run(i32 %x)
              $V_0: bool
              $stack0_ptr: ptr
            IL_0000:
              %0 = const bool false
              store $V_0, %0
              %1 = const i32 0
              %2 = gt i32 %x, %1
              branch %2, IL_000d, IL_0006
            IL_0006:
              %3 = addr @C.ready
              store $stack0_ptr, %3
              jump IL_000f
            IL_000d:
              %4 = addr $V_0
              store $stack0_ptr, %4
              jump IL_000f
            IL_000f:
              %5 = load ptr $stack0_ptr
              %6 = const bool true
              store bool [%5], %6
              %7 = load bool $V_0
              return %7

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            static class C
            {
                static bool ready;

                static bool Run(int x)
                {
                    bool local = false;
                    (x > 0 ? ref local : ref ready) = true;
                    return local;
                }
            }
            """, "C.Run"));
    }

    [Fact]
    public void A_ref_return_is_a_ptr()
    {
        var module = Lowered.Module("""
            static class C
            {
                static ref int Larger(ref int a, ref int b) => ref a > b ? ref a : ref b;

                static int Run(int a, int b)
                {
                    Larger(ref a, ref b) = 0;
                    return a + b;
                }
            }
            """, "C.Run");

        Assert.Equal("""
            function ptr C.Larger(ptr %a, ptr %b)
            IL_0000:
              %0 = load i32 [%a]
              %1 = load i32 [%b]
              %2 = gt i32 %0, %1
              branch %2, IL_0008, IL_0006
            IL_0006:
              return %b
            IL_0008:
              return %a

            """.ReplaceLineEndings("\n"), Print(module, "C.Larger"));
        Assert.Contains("""
              %2 = call ptr C.Larger(%0, %1)
              %3 = const i32 0
              store i32 [%2], %3
            """.ReplaceLineEndings("\n"), Print(module, "C.Run"));
    }

    [Fact]
    public void An_enum_is_its_integer_widened_by_its_own_sign()
    {
        var module = Lowered.Module("""
            using System;

            enum Level : sbyte { Low = -1, High = 1 }

            [Flags]
            enum Bits : ushort { A = 1, B = 0x8000 }

            static class C
            {
                static Level Next(Level level) => level == Level.Low ? Level.High : Level.Low;

                static bool Has(Bits bits) => (bits & Bits.B) != 0;

                static bool Run() => Has(Bits.A) && Next(Level.Low) == Level.High;
            }
            """, "C.Run");

        Assert.Equal("""
            function i8 C.Next(i8 %level)
            IL_0000:
              %0 = convert i8 %level to i32
              %1 = const i32 -1
              %2 = eq i32 %0, %1
              branch %2, IL_0006, IL_0004
            IL_0004:
              %3 = const i8 -1
              return %3
            IL_0006:
              %4 = const i8 1
              return %4

            function bool C.Has(i16 %bits)
            IL_0000:
              %0 = uconvert i16 %bits to i32
              %1 = const i32 32768
              %2 = and i32 %0, %1
              %3 = const i32 0
              %4 = ugt i32 %2, %3
              return %4

            """.ReplaceLineEndings("\n"), Print(module, "C.Next", "C.Has"));
    }

    [Fact]
    public void Structs_are_passed_and_returned_by_value_and_an_in_parameter_is_an_address()
    {
        // a is assigned (a field of it), so it is copied into a slot; b is only read
        var module = Lowered.Module("""
            struct Point
            {
                public int X, Y;
            }

            static class C
            {
                static Point Add(Point a, Point b)
                {
                    a.X += b.X;
                    return a;
                }

                static int Length(in Point p) => p.X * p.X;

                static int Run(Point p) => Length(Add(p, p));
            }
            """, "C.Run");

        Assert.Equal("""
            function i32 C.Run(Point %p)
              $V_0: Point
            IL_0000:
              %0 = call Point C.Add(%p, %p)
              store $V_0, %0
              %1 = addr $V_0
              %2 = call i32 C.Length(%1)
              return %2

            function Point C.Add(Point %a, Point %b)
              $a: Point
              $temp_Point: Point
            IL_0000:
              store $a, %a
              %0 = addr $a
              %1 = fieldaddr Point %0, X
              %2 = load i32 [%1]
              store $temp_Point, %b
              %3 = addr $temp_Point
              %4 = fieldaddr Point %3, X
              %5 = load i32 [%4]
              %6 = add i32 %2, %5
              store i32 [%1], %6
              %7 = load Point $a
              return %7

            function i32 C.Length(ptr %p)
            IL_0000:
              %0 = fieldaddr Point %p, X
              %1 = load i32 [%0]
              %2 = fieldaddr Point %p, X
              %3 = load i32 [%2]
              %4 = mul i32 %1, %3
              return %4

            """.ReplaceLineEndings("\n"), Print(module, "C.Run", "C.Add", "C.Length"));
    }

    [Fact]
    public void A_struct_static_field_is_a_global_whose_fields_are_reached_through_its_address()
    {
        Assert.Equal("""
            function i64 C.Tick(i32 %amount)
            IL_0000:
              %0 = addr @C.counter
              %1 = fieldaddr Counter %0, Count
              %2 = load i32 [%1]
              %3 = const i32 1
              %4 = add i32 %2, %3
              store i32 [%1], %4
              %5 = addr @C.counter
              %6 = fieldaddr Counter %5, Total
              %7 = load i64 [%6]
              %8 = convert i32 %amount to i64
              %9 = add i64 %7, %8
              store i64 [%6], %9
              %10 = addr @C.counter
              %11 = fieldaddr Counter %10, Total
              %12 = load i64 [%11]
              return %12

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            struct Counter
            {
                public int Count;
                public long Total;
            }

            static class C
            {
                static Counter counter;

                static long Tick(int amount)
                {
                    counter.Count++;
                    counter.Total += amount;
                    return counter.Total;
                }
            }
            """, "C.Tick"));
    }

    [Fact]
    public void A_volatile_field_of_a_struct_is_loaded_and_stored_volatile()
    {
        Assert.Equal("""
            function i32 C.Run()
            IL_0000:
              %0 = addr @C.flags
              %1 = const bool true
              %2 = fieldaddr Flags %0, Stop
              store volatile bool [%2], %1
              %3 = addr @C.flags
              %4 = fieldaddr Flags %3, Stop
              %5 = load volatile bool [%4]
              %6 = convert bool %5 to i32
              %7 = const i32 0
              %8 = ugt i32 %6, %7
              %9 = convert bool %8 to i32
              return %9

            """.ReplaceLineEndings("\n"), Lowered.Function("""
            struct Flags
            {
                public volatile bool Stop;
                public int Count;
            }

            static class C
            {
                static Flags flags;

                static int Run()
                {
                    flags.Stop = true;
                    return flags.Stop ? 1 : 0;
                }
            }
            """, "C.Run"));
    }

    [Fact]
    public void Creating_an_object_is_an_error_where_creating_a_struct_is_not()
    {
        var assembly = Lowered.Compile("""
            class Box
            {
                public int Size;
            }

            class Program
            {
                static void Main() => System.Console.WriteLine(new Box().Size);
            }
            """);

        var error = Assert.Throws<ChipSharpException>(() => IlToTir.Lower(new IlProgram(assembly, [assembly.EntryPoint!])));

        Assert.Equal("Test.dll: Program.Main() at IL_0000: newobj creates an object of Box, and objects are not " +
            "supported yet (they arrive after M0); structs are.", error.Message);
    }

    [Fact]
    public void A_struct_with_a_field_of_an_unsupported_type_is_an_error_that_names_the_field()
    {
        const string Source = """
            struct Person
            {
                public string Name;
                public int Age;

                public int Older(int years) => Age + years;
            }

            static class C
            {
                static int Age(Person person) => person.Age;
            }
            """;

        Assert.Equal("Test.dll: C.Age(Person): parameter 'person' has type Person, which is not supported yet (the field " +
            "Person.Name has type string).", Error(Source, "C.Age").Message);
        Assert.Equal("Test.dll: Person.Older(int32): the field Person.Name has type string, which is not supported yet.",
            Error(Source, "Person.Older").Message);
    }

    [Fact]
    public void Turning_a_reference_into_a_number_is_an_error()
    {
        var error = Error("""
            static class C
            {
                static unsafe long Address()
                {
                    int x = 0;
                    return (long)&x;
                }
            }
            """, "C.Address");

        Assert.Equal("Test.dll: C.Address() at IL_0004: conv.u works on numbers, but the evaluation stack has a " +
            "reference (&) (turning a reference into a number is unsafe code, which M0 does not compile).", error.Message);
    }

    [Fact]
    public void A_byte_through_a_reference_to_a_bool_or_a_byte_is_an_error()
    {
        // two ways into IL_0009: one with a reference to a bool, one to a byte; no C# does this
        var emitted = new EmittedAssembly();
        emitted.Method("F", typeof(int), [typeof(bool)], il =>
        {
            il.DeclareLocal(typeof(bool));
            il.DeclareLocal(typeof(byte));
            var (other, join) = (il.DefineLabel(), il.DefineLabel());
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brtrue_S, other);
            il.Emit(OpCodes.Ldloca_S, (byte)0);
            il.Emit(OpCodes.Br_S, join);
            il.MarkLabel(other);
            il.Emit(OpCodes.Ldloca_S, (byte)1);
            il.MarkLabel(join);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Stind_I1);
            il.Emit(OpCodes.Ldloc_1);
            il.Emit(OpCodes.Ret);
        });

        var error = Assert.Throws<ChipSharpException>(() => Lowered.Lower(Lowered.Program(emitted.Read(), "Program.F")));

        Assert.Equal("Emitted.dll: Program.F(bool) at IL_000a: stind.i1 reads or writes a byte through a reference that " +
            "may refer to a bool or to a byte, and nothing tells which.", error.Message);
    }

    private static ChipSharpException Error(string source, string entryPoint) =>
        Assert.Throws<ChipSharpException>(() => IlToTir.Lower(Lowered.Program(Lowered.Compile(source), entryPoint)));

    /// <summary>The functions named <paramref name="names"/>, in that order, as the printer writes them.</summary>
    private static string Print(TirModule module, params string[] names) =>
        string.Join("\n", names.Select(name => TirPrinter.Print(module.Functions.Single(f => f.Name == name))));
}
