using System.Reflection.Emit;
using ChipSharp.Abstractions;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// What the translation rejects, with the file, the method and, for an instruction, its offset. The whole-program
/// pass normally rejects these first; the translation must still never produce wrong TIR from them.
/// </summary>
public sealed class IlToTirErrorTests
{
    [Fact]
    public void An_instruction_outside_the_subset_is_named_with_its_offset()
    {
        var error = Error("""
            class Program
            {
                static void Main() => System.Console.WriteLine((object)1);
            }
            """);

        Assert.Equal("Test.dll: Program.Main() at IL_0001: box is not supported yet.",
            error.Message);
    }

    [Fact]
    public void A_parameter_or_result_of_an_unsupported_type_is_an_error()
    {
        const string Source = """
            static class C
            {
                static int Code(char c) => c;
                static char Letter(int x) => (char)x;
            }
            """;

        Assert.Equal("Test.dll: C.Code(char): parameter 'c' has type char, which is not supported yet " +
            "(ChipSharp compiles bool, whole numbers, float, double and fixed text (string)).", Error(Source, "C.Code").Message);
        Assert.Equal("Test.dll: C.Letter(int32): it returns char, which is not supported yet " +
            "(ChipSharp compiles bool, whole numbers, float, double and fixed text (string)).", Error(Source, "C.Letter").Message);
    }

    [Fact]
    public void A_local_of_an_unsupported_type_is_an_error()
    {
        var emitted = new EmittedAssembly();
        emitted.Method("F", typeof(void), [], il =>
        {
            il.DeclareLocal(typeof(int));
            il.DeclareLocal(typeof(char));
            il.Emit(OpCodes.Ret);
        });

        var error = Assert.Throws<ChipSharpException>(() => Lower(emitted.Read(), "Program.F"));

        // (an object is a ref now, for the heap, though the whole-program pass does not let a program have one yet)
        Assert.Equal("Emitted.dll: Program.F(): local V_1 has type char, which is not supported yet " +
            "(ChipSharp compiles bool, whole numbers, float, double and fixed text (string)).", error.Message);
    }

    [Fact]
    public void A_library_call_without_a_runtime_function_is_an_error_that_lists_the_supported_ones()
    {
        var error = Error("""
            class Program
            {
                static void Main() => System.Console.WriteLine(System.Math.Abs(-5));
            }
            """);

        Assert.Equal("Test.dll: Program.Main() at IL_0002: the call to System.Math.Abs(int32) is not supported: " +
            "of the library, ChipSharp can call System.Console.Write(string), System.Console.Write(int), " +
            "System.Console.Write(uint), System.Console.Write(long), System.Console.Write(ulong), " +
            "System.Console.Write(bool), System.Console.Write(float), System.Console.Write(double), " +
            "System.Console.WriteLine(), System.Console.WriteLine(string), System.Console.WriteLine(int), " +
            "System.Console.WriteLine(uint), System.Console.WriteLine(long), System.Console.WriteLine(ulong), " +
            "System.Console.WriteLine(bool), System.Console.WriteLine(float), System.Console.WriteLine(double), " +
            "System.Math.Abs(double), System.Math.Abs(float), System.Math.Min(double, double), " +
            "System.Math.Min(float, float), System.Math.Max(double, double), System.Math.Max(float, float), " +
            "System.Math.Sqrt(double), System.MathF.Abs(float), System.MathF.Min(float, float), " +
            "System.MathF.Max(float, float), System.MathF.Sqrt(float), " +
            "ChipSharp.Hardware.Pin.Output(int), ChipSharp.Hardware.Pin.Input(int), " +
            "ChipSharp.Hardware.Pin.Write(int, bool), ChipSharp.Hardware.Pin.Toggle(int), ChipSharp.Hardware.Pin.Read(int), " +
            "ChipSharp.Hardware.Timing.DelayMilliseconds(int), ChipSharp.Hardware.Timing.DelayMicroseconds(int), " +
            "ChipSharp.Hardware.Registers.Read(uint), ChipSharp.Hardware.Registers.Write(uint, uint), " +
            "ChipSharp.Hardware.Timing.EveryMilliseconds(int), ChipSharp.Hardware.Timing.EveryMicroseconds(int), " +
            "ChipSharp.Hardware.Timing.AfterMilliseconds(int), ChipSharp.Hardware.Timing.AfterMicroseconds(int), " +
            "ChipSharp.Hardware.Timing.Stop(), ChipSharp.Hardware.Pin.Watch(int), ChipSharp.Hardware.Pin.WatchRising(int), " +
            "ChipSharp.Hardware.Pin.WatchFalling(int), ChipSharp.Hardware.Pin.Unwatch(int).", error.Message);
    }

    [Fact]
    public void A_call_to_a_method_the_program_does_not_list_is_an_error()
    {
        var assembly = Lowered.Compile("""
            class Program
            {
                static int Square(int n) => n * n;
                static void Main() => System.Console.WriteLine(Square(3));
            }
            """);

        var error = Assert.Throws<ChipSharpException>(
            () => IlToTir.Lower(new IlProgram(assembly, [assembly.EntryPoint!])));

        Assert.Equal("Test.dll: Program.Main() at IL_0001: the call to Program.Square(int32) is not supported: " +
            "that method is not among the program's methods.", error.Message);
    }

    [Fact]
    public void Virtual_and_generic_methods_are_errors()
    {
        var assembly = Lowered.Compile("""
            class C
            {
                protected virtual int Field() => 1;
                static T Same<T>(T value) => value;
            }
            """);

        var instance = Assert.Throws<ChipSharpException>(
            () => IlToTir.Lower(new IlProgram(assembly, [.. assembly.FindMethods("C.Field")])));
        var generic = Assert.Throws<ChipSharpException>(
            () => IlToTir.Lower(new IlProgram(assembly, [.. assembly.FindMethods("C.Same")])));

        Assert.Equal("Test.dll: C.Field(): virtual methods (virtual, override, abstract) are not supported yet (they " +
            "arrive with M3, virtual calls).", instance.Message);
        Assert.Equal("Test.dll: C.Same(!!0): generic methods and the methods of generic types are not supported yet " +
            "(they arrive with M3, the heap).", generic.Message);
    }

    [Fact]
    public void Exception_handling_is_an_error_at_the_try()
    {
        var error = Error("""
            class Program
            {
                static void Main()
                {
                    try
                    {
                        System.Console.WriteLine(1);
                    }
                    finally
                    {
                        System.Console.WriteLine(2);
                    }
                }
            }
            """);

        Assert.Equal("Test.dll: Program.Main() at IL_0000: try, catch and finally are not supported yet " +
            "(they arrive later).", error.Message);
    }

    [Fact]
    public void An_entry_point_that_uses_its_args_is_an_error()
    {
        var error = Error("""
            class Program
            {
                static void Main(string[] args) => System.Console.WriteLine(args.Length);
            }
            """);

        Assert.Equal("Test.dll: Program.Main(string[]) at IL_0000: the entry point uses its string[] args, and arrays " +
            "of strings are not supported yet (ChipSharp leaves an unused args out).", error.Message);
    }

    [Fact]
    public void A_string_array_parameter_is_left_out_of_the_entry_point_only()
    {
        var assembly = Lowered.Compile("""
            class Program
            {
                static int Count(string[] items) => 0;
                static void Main(string[] args) => System.Console.WriteLine(1);
            }
            """);
        var main = assembly.EntryPoint!;
        var count = Assert.Single(assembly.FindMethods("Program.Count"));

        var module = Lowered.Lower(new IlProgram(assembly, [main]));
        var error = Assert.Throws<ChipSharpException>(() => IlToTir.Lower(new IlProgram(assembly, [main, count])));

        Assert.Empty(module.EntryPoint!.Parameters);
        Assert.Equal("Test.dll: Program.Count(string[]): parameter 'items' has type string[], which is not " +
            "supported yet (ChipSharp compiles bool, whole numbers, float, double and fixed text (string)).", error.Message);
    }

    [Fact]
    public void Ways_into_a_block_that_leave_different_stacks_are_an_error()
    {
        var emitted = new EmittedAssembly();
        emitted.Method("F", typeof(int), [typeof(int)], il =>
        {
            var join = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brtrue_S, join);
            il.Emit(OpCodes.Ldc_I4_1);
            il.MarkLabel(join);
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Ret);
        });

        var error = Assert.Throws<ChipSharpException>(() => Lower(emitted.Read(), "Program.F"));

        Assert.Equal("Emitted.dll: Program.F(int32) at IL_0003: control reaches IL_0004 with int32 on the evaluation " +
            "stack, but another way into IL_0004 has nothing.", error.Message);
    }

    [Fact]
    public void A_jump_back_with_a_value_on_the_stack_is_an_error()
    {
        // IL makes a block that only a later instruction jumps to start with an empty stack
        var emitted = new EmittedAssembly();
        emitted.Method("F", typeof(void), [], il =>
        {
            var (top, test) = (il.DefineLabel(), il.DefineLabel());
            il.Emit(OpCodes.Br_S, test);
            il.MarkLabel(top);
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Pop);
            il.MarkLabel(test);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Brtrue_S, top);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        });

        var error = Assert.Throws<ChipSharpException>(() => Lower(emitted.Read(), "Program.F"));

        Assert.Equal("Emitted.dll: Program.F() at IL_0006: control jumps back to IL_0002 with int32 on the " +
            "evaluation stack, but IL wants it empty there: no earlier instruction leads to IL_0002.", error.Message);
    }

    [Fact]
    public void Broken_IL_is_an_error_not_broken_TIR()
    {
        Assert.Equal("Emitted.dll: Program.F() at IL_0001: add needs a value, but the evaluation stack is empty.",
            EmittedError(typeof(int), [], OpCodes.Ldc_I4_1, OpCodes.Add, OpCodes.Ret));
        Assert.Equal("Emitted.dll: Program.F() at IL_0001: control runs past the end of the method.",
            EmittedError(typeof(void), [], OpCodes.Ldc_I4_1, OpCodes.Pop));
        Assert.Equal("Emitted.dll: Program.F(int32, int64) at IL_0002: add takes int32 and int64, which IL does not " +
            "allow together.", EmittedError(typeof(long), [typeof(int), typeof(long)],
                OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Add, OpCodes.Ret));
        Assert.Equal("Emitted.dll: Program.F(int64) at IL_0001: the return value has type int32, but the value on " +
            "the evaluation stack is int64.", EmittedError(typeof(int), [typeof(long)], OpCodes.Ldarg_0, OpCodes.Ret));
        Assert.Equal("Emitted.dll: Program.F(int32) at IL_0001: argument 1 of System.Console.WriteLine(long) has " +
            "type int64, but the value on the evaluation stack is int32.", EmittedError(typeof(void), [typeof(int)],
                OpCodes.Ldarg_0, (OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(long)])!), OpCodes.Ret));
    }

    private static ChipSharpException Error(string source, string? entryPoint = null) =>
        Assert.Throws<ChipSharpException>(() => IlToTir.Lower(Lowered.Program(Lowered.Compile(source), entryPoint)));

    private static void Lower(IlAssembly assembly, string entryPoint) =>
        IlToTir.Lower(Lowered.Program(assembly, entryPoint));

    /// <summary>The message of translating <c>Program.F</c> made of <paramref name="body"/>: opcodes, or (opcode, method) for a call.</summary>
    private static string EmittedError(Type returnType, Type[] parameters, params object[] body)
    {
        var emitted = new EmittedAssembly();
        emitted.Method("F", returnType, parameters, il =>
        {
            foreach (var step in body)
            {
                if (step is (OpCode call, System.Reflection.MethodInfo method))
                {
                    il.Emit(call, method);
                }
                else
                {
                    il.Emit((OpCode)step);
                }
            }
        });

        return Assert.Throws<ChipSharpException>(() => Lower(emitted.Read(), "Program.F")).Message;
    }
}
