using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Il;

public sealed class IlReaderTests
{
    private const string SquareProgram = """
        using System;

        class Program
        {
            static int Square(int n) => n * n;

            static void Main()
            {
                Console.WriteLine(Square(42));
            }
        }
        """;

    [Fact]
    public void Square_is_ldarg0_ldarg0_mul_ret()
    {
        var square = Method(Load(SquareProgram), "Program.Square");
        var body = square.Body!;

        Assert.Equal([IlOpCode.Ldarg_0, IlOpCode.Ldarg_0, IlOpCode.Mul, IlOpCode.Ret], OpCodes(body));
        Assert.Equal([0, 1, 2, 3], body.Instructions.Select(i => i.Offset));
        Assert.All(body.Instructions, i => Assert.Null(i.Operand));
        Assert.Equal(0, body.Instructions[0].ArgumentIndex);
        Assert.Equal(4, body.CodeSize);
        Assert.Equal(8, body.MaxStack);
        Assert.Empty(body.Locals);
        Assert.Empty(body.ExceptionRegions);

        Assert.True(square.IsStatic);
        Assert.Equal("Square", square.Name);
        Assert.Equal("Program", square.DeclaringType.FullName);
        Assert.Equal(IlType.Int32, square.ReturnType);
        Assert.Equal([new IlParameter(0, "n", IlType.Int32)], square.Parameters);
        Assert.Equal(new IlMethodSignature(false, IlType.Int32, [IlType.Int32]), square.Signature);
        Assert.Equal("Program.Square(int32)", square.ToString());
    }

    [Fact]
    public void Main_calls_square_and_the_external_Console_WriteLine()
    {
        var assembly = Load(SquareProgram);
        var main = Method(assembly, "Program.Main");
        var square = Method(assembly, "Program.Square");
        var body = main.Body!;

        Assert.Equal([IlOpCode.Ldc_I4_S, IlOpCode.Call, IlOpCode.Call, IlOpCode.Ret], OpCodes(body));
        Assert.Equal(new IlInt32Operand(42), body.Instructions[0].Operand);

        var callSquare = Assert.IsType<IlMethodOperand>(body.Instructions[1].Operand).Method;
        Assert.Same(square, callSquare.Definition);
        Assert.True(callSquare.IsDefinedInThisAssembly);
        Assert.Equal("Program.Square", callSquare.DisplayName);
        Assert.Equal("int32 Program::Square(int32)", callSquare.ToString());

        var writeLine = Assert.IsType<IlMethodOperand>(body.Instructions[2].Operand).Method;
        Assert.Equal(new IlNamedType("System", "Console", null, "System.Console"), writeLine.DeclaringType);
        Assert.Equal("WriteLine", writeLine.Name);
        Assert.Equal([IlType.Int32], writeLine.ParameterTypes);
        Assert.Equal(IlType.Void, writeLine.ReturnType);
        Assert.False(writeLine.HasThis);
        Assert.Empty(writeLine.GenericArguments);
        Assert.Null(writeLine.Definition);
        Assert.False(writeLine.IsDefinedInThisAssembly);
        Assert.Equal("System.Console.WriteLine", writeLine.DisplayName);
        Assert.Equal("void [System.Console]System.Console::WriteLine(int32)", writeLine.ToString());
    }

    [Fact]
    public void The_entry_point_is_Main_and_a_library_has_none()
    {
        var program = Load(SquareProgram);
        var main = Method(program, "Program.Main");

        Assert.Same(main, program.EntryPoint);
        Assert.True(main.IsEntryPoint);
        Assert.False(Method(program, "Program.Square").IsEntryPoint);
        Assert.Equal("Test", program.Name);

        Assert.Null(Load("static class Library { static int One() => 1; }").EntryPoint);
    }

    [Fact]
    public void If_else_branches_forward_to_the_else()
    {
        var body = Method(Load(FlowSource), "Flow.Max").Body!;

        // ldarg.0; ldarg.1; ble.s else; ldarg.0; ret; else: ldarg.1; ret
        Assert.Equal(
            [IlOpCode.Ldarg_0, IlOpCode.Ldarg_1, IlOpCode.Ble_S, IlOpCode.Ldarg_0, IlOpCode.Ret, IlOpCode.Ldarg_1, IlOpCode.Ret],
            OpCodes(body));
        var branch = body.Instructions[2];
        Assert.Equal(2, branch.Size);
        Assert.Equal(4, branch.NextOffset);
        Assert.Equal(new IlBranchOperand(6), branch.Operand);
        Assert.Equal(1, body.Instructions[body.IndexOf(6)].ArgumentIndex);
    }

    [Fact]
    public void A_small_loop_uses_short_branches_forward_and_back()
    {
        var body = Method(Load(FlowSource), "Flow.Sum").Body!;

        var jumpToCondition = Single(body, IlOpCode.Br_S);
        var loopBack = Single(body, IlOpCode.Blt_S);
        Assert.Equal(new IlBranchOperand(0x0e), jumpToCondition.Operand);
        Assert.Equal(new IlBranchOperand(0x06), loopBack.Operand);
        Assert.True(loopBack.Offset > 0x06);

        // the condition loads i (local 1) and n; the body starts by loading sum (local 0)
        Assert.Equal(1, body.Instructions[body.IndexOf(0x0e)].LocalIndex);
        Assert.Equal(0, body.Instructions[body.IndexOf(0x06)].LocalIndex);
    }

    [Fact]
    public void A_big_loop_uses_long_branches_forward_and_back()
    {
        var body = Method(Load(FlowSource), "Flow.Long").Body!;

        var jumpToCondition = Single(body, IlOpCode.Br);
        var loopBack = Single(body, IlOpCode.Blt);
        Assert.Equal(5, jumpToCondition.Size);
        Assert.Equal(new IlBranchOperand(0xc1), jumpToCondition.Operand);
        Assert.Equal(new IlBranchOperand(0x09), loopBack.Operand);
        Assert.Equal(0x09, jumpToCondition.NextOffset);
        Assert.Equal(IlOpCode.Ldloc_1, body.Instructions[body.IndexOf(0xc1)].OpCode);
    }

    [Fact]
    public void A_switch_statement_becomes_the_switch_opcode_with_absolute_targets()
    {
        var body = Method(Load(FlowSource), "Flow.Pick").Body!;

        var table = Single(body, IlOpCode.Switch);
        Assert.Equal(1 + 4 + (4 * 4), table.Size);
        Assert.Equal(new IlSwitchOperand([0x18, 0x1b, 0x1e, 0x21]), table.Operand);

        // case i loads 10 * (i + 1); anything else falls through to the default
        var cases = Assert.IsType<IlSwitchOperand>(table.Operand).Targets;
        Assert.Equal(
            [new IlInt32Operand(10), new IlInt32Operand(20), new IlInt32Operand(30), new IlInt32Operand(40)],
            cases.Select(t => body.Instructions[body.IndexOf(t)].Operand));
        Assert.Equal(IlOpCode.Br_S, body.Instructions[body.IndexOf(table.NextOffset)].OpCode);
    }

    [Fact]
    public void Constants_decode_to_their_values()
    {
        var assembly = Load("""
            static class Constants
            {
                static void Use(int x) { }
                static void Use(long x) { }
                static void Use(float x) { }
                static void Use(double x) { }
                static void Use(string x) { }

                static void All()
                {
                    Use(42); Use(-5); Use(1000); Use(10000000000L); Use(1.5f); Use(2.25); Use("hi \"there\"\n"); Use(7);
                }
            }
            """);
        var body = Method(assembly, "Constants.All").Body!;

        var loads = body.Instructions.Where(i => i.OpCode is not (IlOpCode.Call or IlOpCode.Ret));
        Assert.Equal(
        [
            (IlOpCode.Ldc_I4_S, new IlInt32Operand(42)),
            (IlOpCode.Ldc_I4_S, new IlInt32Operand(-5)),
            (IlOpCode.Ldc_I4, new IlInt32Operand(1000)),
            (IlOpCode.Ldc_I8, new IlInt64Operand(10000000000L)),
            (IlOpCode.Ldc_R4, new IlFloat32Operand(1.5f)),
            (IlOpCode.Ldc_R8, new IlFloat64Operand(2.25)),
            (IlOpCode.Ldstr, new IlStringOperand("hi \"there\"\n")),
            (IlOpCode.Ldc_I4_7, (IlOperand?)null),
        ], loads.Select(i => (i.OpCode, i.Operand)));

        // each call resolves to the overload for its constant's type
        var overloads = assembly.FindMethods("Constants.Use");
        Assert.Equal(5, overloads.Count);
        Assert.Equal(
            ["int32", "int32", "int32", "int64", "float32", "float64", "string", "int32"],
            body.Instructions.Select(i => i.Operand).OfType<IlMethodOperand>()
                .Select(m => Assert.Single(m.Method.Definition!.Parameters).Type.ToString()));
        Assert.Equal("IL_003e:  ldstr      \"hi \\\"there\\\"\\n\"", body.Instructions[12].ToString());
    }

    [Fact]
    public void Comparisons_use_the_two_byte_opcodes()
    {
        var assembly = Load("""
            static class Compare
            {
                static bool Eq(int a, int b) => a == b;
                static bool Lt(int a, int b) => a < b;
                static bool Gt(int a, int b) => a > b;
                static bool LtUn(uint a, uint b) => a < b;
                static bool GtUn(uint a, uint b) => a > b;
            }
            """);

        foreach (var (name, opCode) in new[]
        {
            ("Eq", IlOpCode.Ceq), ("Lt", IlOpCode.Clt), ("Gt", IlOpCode.Cgt), ("LtUn", IlOpCode.Clt_Un), ("GtUn", IlOpCode.Cgt_Un),
        })
        {
            var body = Method(assembly, $"Compare.{name}").Body!;
            Assert.Equal([IlOpCode.Ldarg_0, IlOpCode.Ldarg_1, opCode, IlOpCode.Ret], OpCodes(body));
            Assert.Equal(2, body.Instructions[2].Size);
            Assert.True(opCode.IsTwoByte());
            Assert.Equal(4, body.Instructions[3].Offset);
        }
    }

    [Fact]
    public void Locals_have_their_types()
    {
        var body = Method(Load("""
            static class Locals
            {
                static double Mix(int n)
                {
                    long total = 0;
                    double scale = 1.5;
                    bool flag = false;
                    for (int i = 0; i < n; i++)
                    {
                        total += i;
                        scale *= 2;
                        flag = !flag;
                    }

                    return flag ? total * scale : scale;
                }
            }
            """), "Locals.Mix").Body!;

        Assert.Equal(
            [new IlLocal(0, IlType.Int64), new IlLocal(1, IlType.Float64), new IlLocal(2, IlType.Bool), new IlLocal(3, IlType.Int32)],
            body.Locals);
        Assert.True(body.InitLocals);
        Assert.Equal(2, body.MaxStack);
        Assert.Equal([0, 1, 2, 3], body.Instructions.Where(i => i.OpCode.ToString().StartsWith("Stloc", StringComparison.Ordinal))
            .Select(i => i.LocalIndex!.Value).Distinct().Order());
        Assert.Equal(new IlFloat64Operand(1.5), Single(body, IlOpCode.Ldc_R8, 0).Operand);
    }

    [Fact]
    public void A_try_finally_exposes_its_exception_region()
    {
        var body = Method(Load("""
            using System;

            static class Guarded
            {
                static void Run()
                {
                    try
                    {
                        Console.WriteLine(1);
                    }
                    finally
                    {
                        Console.WriteLine(2);
                    }
                }
            }
            """), "Guarded.Run").BodyAsRead!;

        var region = Assert.Single(body.ExceptionRegions);
        Assert.Equal(new IlExceptionRegion(IlExceptionRegionKind.Finally, 0x00, 0x08, 0x08, 0x07, null, null), region);
        Assert.Equal(region.HandlerOffset, region.TryEnd);

        var leave = body.Instructions[body.IndexOf(region.TryEnd) - 1];
        Assert.Equal(IlOpCode.Leave_S, leave.OpCode);
        Assert.Equal(new IlBranchOperand(region.HandlerEnd), leave.Operand);
        Assert.Equal(IlOpCode.Endfinally, body.Instructions[body.IndexOf(region.HandlerEnd) - 1].OpCode);
    }

    [Fact]
    public void A_try_catch_names_the_caught_type()
    {
        var body = Method(Load("""
            using System;

            static class Guarded
            {
                static int Parse(string s)
                {
                    try
                    {
                        return int.Parse(s);
                    }
                    catch (FormatException)
                    {
                        return -1;
                    }
                }
            }
            """), "Guarded.Parse").Body!;

        var region = Assert.Single(body.ExceptionRegions);
        Assert.Equal(IlExceptionRegionKind.Catch, region.Kind);
        var caught = Assert.IsType<IlNamedType>(region.CatchType);
        Assert.Equal("System.FormatException", caught.FullName);
        Assert.False(caught.IsDefinedInThisAssembly);
        Assert.True(body.IndexOf(region.HandlerOffset) > 0);
    }

    [Fact]
    public void Nested_types_instance_methods_overloads_and_fields()
    {
        var assembly = Load("""
            namespace Demo
            {
                public class Outer
                {
                    public class Inner
                    {
                        public int Value;
                        public int Get() => Value;
                        public static int Twice(int x) => x * 2;
                        public static long Twice(long x) => x * 2;
                    }
                }
            }
            """);

        var inner = assembly.FindType("Demo.Outer+Inner")!;
        Assert.Equal("Inner", inner.Name);
        Assert.Equal("", inner.Namespace);
        Assert.Same(assembly.FindType("Demo.Outer"), inner.DeclaringType);
        Assert.Equal(["Value"], inner.Fields.Select(f => f.Name));
        Assert.Equal(["Get", "Twice", "Twice", ".ctor"], inner.Methods.Select(m => m.Name));

        var get = Method(assembly, "Demo.Outer+Inner.Get");
        Assert.False(get.IsStatic);
        Assert.True(get.Signature.HasThis);
        Assert.Empty(get.Parameters);
        var load = Assert.IsType<IlFieldOperand>(get.Body!.Instructions[1].Operand).Field;
        Assert.Same(inner.Fields[0], load.Definition);
        Assert.Equal(IlType.Int32, load.FieldType);
        Assert.Equal("int32 Demo.Outer+Inner::Value", load.ToString());

        var twice = assembly.FindMethods("Demo.Outer+Inner.Twice");
        Assert.Equal(["Demo.Outer+Inner.Twice(int32)", "Demo.Outer+Inner.Twice(int64)"], twice.Select(m => m.ToString()));
        Assert.Equal([IlType.Int32, IlType.Int64], twice.Select(m => m.ReturnType));

        var baseConstructor = Assert.IsType<IlMethodOperand>(Method(assembly, "Demo.Outer+Inner..ctor").Body!.Instructions[1].Operand).Method;
        Assert.True(baseConstructor.HasThis);
        Assert.Equal("System.Object..ctor", baseConstructor.DisplayName);
        Assert.False(baseConstructor.IsDefinedInThisAssembly);
    }

    [Fact]
    public void Static_fields_resolve_to_their_definitions()
    {
        var assembly = Load("""
            static class Counter
            {
                static int count;
                static void Bump() => count++;
            }
            """);
        var body = Method(assembly, "Counter.Bump").Body!;

        Assert.Equal([IlOpCode.Ldsfld, IlOpCode.Ldc_I4_1, IlOpCode.Add, IlOpCode.Stsfld, IlOpCode.Ret], OpCodes(body));
        var load = Assert.IsType<IlFieldOperand>(body.Instructions[0].Operand).Field;
        var store = Assert.IsType<IlFieldOperand>(body.Instructions[3].Operand).Field;
        Assert.Same(load, store);
        Assert.True(load.Definition!.IsStatic);
        Assert.Equal("Counter.count", load.DisplayName);
    }

    [Fact]
    public void Signature_types_are_represented()
    {
        var assembly = Load("""
            using System.Collections.Generic;

            static unsafe class Signatures
            {
                static void All<T>(bool a, char b, sbyte c, byte d, short e, ushort f, uint g, ulong h, float i, double j,
                    nint k, nuint l, object m, string n, int[] o, int[,] p, int* q, ref int r, List<int> s, T t,
                    delegate*<int, int> u)
                {
                }
            }
            """);
        var all = Method(assembly, "Signatures.All");

        Assert.Equal(
        [
            "bool", "char", "int8", "uint8", "int16", "uint16", "uint32", "uint64", "float32", "float64",
            "native int", "native uint", "object", "string", "int32[]", "int32[,]", "int32*", "int32&",
            "[System.Private.CoreLib]System.Collections.Generic.List`1<int32>", "!!0", "method int32 *(int32)",
        ], all.Parameters.Select(p => p.Type.ToString()));
        Assert.Equal(["T"], all.GenericParameters);
        Assert.Equal(1, all.Signature.GenericParameterCount);

        var list = Assert.IsType<IlGenericInstanceType>(all.Parameters[18].Type);
        Assert.Equal(new IlGenericInstanceType(new IlNamedType("System.Collections.Generic", "List`1", null, "System.Private.CoreLib"),
            [IlType.Int32]), list);
        Assert.Equal(new IlGenericParameterType(0, IsMethodParameter: true), all.Parameters[19].Type);
    }

    [Fact]
    public void A_generic_method_call_carries_its_type_arguments()
    {
        var assembly = Load("""
            static class Generic
            {
                static T Id<T>(T x) => x;
                static int UseId() => Id(5);
            }
            """);
        var call = Assert.IsType<IlMethodOperand>(Single(Method(assembly, "Generic.UseId").Body!, IlOpCode.Call).Operand).Method;

        // linked to the instance Id<int>, a method of its own made from Id<T>, with its signature
        Assert.Equal([IlType.Int32], call.GenericArguments);
        var instance = call.Definition!;
        Assert.Same(Method(assembly, "Generic.Id"), instance.GenericDefinition);
        Assert.Equal([IlType.Int32], instance.MethodArguments);
        Assert.Equal("Generic.Id<int>", instance.DisplayName);
        Assert.Equal(IlType.Int32, call.ReturnType);
        Assert.Equal("int32 Generic::Id<int32>(int32)", call.ToString());
        Assert.Null(Method(assembly, "Generic.Id").GenericDefinition);
    }

    [Fact]
    public void Every_use_of_a_token_shares_one_resolved_reference()
    {
        var body = Method(Load(FlowSource), "Flow.Long").Body!;

        var calls = body.Instructions.Where(i => i.OpCode == IlOpCode.Call)
            .Select(i => Assert.IsType<IlMethodOperand>(i.Operand).Method).ToList();
        Assert.Equal(20, calls.Count);
        Assert.All(calls, c => Assert.Same(calls[0], c));
    }

    [Fact]
    public void Index_of_finds_instructions_by_offset()
    {
        var body = Method(Load(FlowSource), "Flow.Max").Body!;

        Assert.Equal(Enumerable.Range(0, body.Instructions.Count), body.Instructions.Select(i => body.IndexOf(i.Offset)));
        Assert.Equal(-1, body.IndexOf(3));
        Assert.Equal(-1, body.IndexOf(100));
    }

    [Fact]
    public void Prints_a_method_like_ildasm()
    {
        var assembly = Load(SquareProgram);

        Assert.Equal(Text("""
            .method static int32 Program::Square(int32 n)
            {
              .maxstack 8
              IL_0000:  ldarg.0
              IL_0001:  ldarg.0
              IL_0002:  mul
              IL_0003:  ret
            }

            """), IlPrinter.Print(Method(assembly, "Program.Square")));
        Assert.Equal("IL_0002:  call       int32 Program::Square(int32)",
            Method(assembly, "Program.Main").Body!.Instructions[1].ToString());
    }

    [Fact]
    public void Prints_locals_regions_switches_and_argument_names()
    {
        var assembly = Load("""
            using System;

            class Shape
            {
                int sides;

                void Set(int value, int scale)
                {
                    int total = 0;
                    try
                    {
                        switch (value)
                        {
                            case 0: total = 1; break;
                            case 1: total = 2; break;
                            case 2: total = 4; break;
                        }
                    }
                    finally
                    {
                        Console.WriteLine(total);
                    }

                    scale += value;
                    sides = total * scale;
                }
            }
            """);

        Assert.Equal(Text("""
            .method instance void Shape::Set(int32 value, int32 scale)
            {
              .maxstack 3
              .locals init (int32 V_0)
              .try IL_0002 to IL_0022 finally handler IL_0022 to IL_0029
              IL_0000:  ldc.i4.0
              IL_0001:  stloc.0
              IL_0002:  ldarg.1
              IL_0003:  switch     (IL_0016, IL_001a, IL_001e)
              IL_0014:  leave.s    IL_0029
              IL_0016:  ldc.i4.1
              IL_0017:  stloc.0
              IL_0018:  leave.s    IL_0029
              IL_001a:  ldc.i4.2
              IL_001b:  stloc.0
              IL_001c:  leave.s    IL_0029
              IL_001e:  ldc.i4.4
              IL_001f:  stloc.0
              IL_0020:  leave.s    IL_0029
              IL_0022:  ldloc.0
              IL_0023:  call       void [System.Console]System.Console::WriteLine(int32)
              IL_0028:  endfinally
              IL_0029:  ldarg.2
              IL_002a:  ldarg.1
              IL_002b:  add
              IL_002c:  starg.s    scale
              IL_002e:  ldarg.0
              IL_002f:  ldloc.0
              IL_0030:  ldarg.2
              IL_0031:  mul
              IL_0032:  stfld      int32 Shape::sides
              IL_0037:  ret
            }

            """), IlPrinter.Print(Method(assembly, "Shape.Set")));
    }

    private const string FlowSource = """
        static class Flow
        {
            static int Max(int a, int b)
            {
                if (a > b)
                {
                    return a;
                }
                else
                {
                    return b;
                }
            }

            static int Sum(int n)
            {
                int sum = 0;
                for (int i = 0; i < n; i++)
                {
                    sum += i;
                }

                return sum;
            }

            static int Twice(int x) => x * 2;

            // a loop body longer than 127 bytes: the branches need their long forms
            static int Long(int n)
            {
                int sum = 0;
                for (int i = 0; i < n; i++)
                {
                    sum += Twice(i); sum += Twice(i); sum += Twice(i); sum += Twice(i); sum += Twice(i);
                    sum += Twice(i); sum += Twice(i); sum += Twice(i); sum += Twice(i); sum += Twice(i);
                    sum += Twice(i); sum += Twice(i); sum += Twice(i); sum += Twice(i); sum += Twice(i);
                    sum += Twice(i); sum += Twice(i); sum += Twice(i); sum += Twice(i); sum += Twice(i);
                }

                return sum;
            }

            static int Pick(int x)
            {
                switch (x)
                {
                    case 0: return 10;
                    case 1: return 20;
                    case 2: return 30;
                    case 3: return 40;
                    default: return -1;
                }
            }
        }
        """;

    internal static IlAssembly Load(string source) => IlAssembly.Load(CSharpCompiler.Compile(source), "Test.dll");

    internal static IlMethod Method(IlAssembly assembly, string displayName) => Assert.Single(assembly.FindMethods(displayName));

    private static IlOpCode[] OpCodes(IlMethodBody body) => [.. body.Instructions.Select(i => i.OpCode)];

    private static IlInstruction Single(IlMethodBody body, IlOpCode opCode) => Assert.Single(body.Instructions, i => i.OpCode == opCode);

    private static IlInstruction Single(IlMethodBody body, IlOpCode opCode, int index) =>
        body.Instructions.Where(i => i.OpCode == opCode).ElementAt(index);

    private static string Text(string text) => text.ReplaceLineEndings("\n");
}
