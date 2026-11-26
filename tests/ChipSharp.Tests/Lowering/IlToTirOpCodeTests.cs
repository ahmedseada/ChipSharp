using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;
using ChipSharp.Tests.Host;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// Every instruction of <see cref="SupportedSubset.OpCodes"/>, short and long forms alike, in IL written directly
/// (C# never emits nop or the long forms in small methods). Each method is pure, so .NET can run it in this process;
/// the same calls compiled by ChipSharp must print what .NET computes.
/// </summary>
public sealed partial class IlToTirOpCodeTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void Every_instruction_of_the_subset_is_in_the_program_and_becomes_well_formed_TIR()
    {
        var program = OpCodeProgram.Build();
        var assembly = program.Assembly.Read();
        var used = assembly.Methods.SelectMany(m => m.Body!.Instructions).Select(i => i.OpCode).ToHashSet();

        var module = Lowered.Lower(Lowered.Program(assembly, "Program.Main"));

        Assert.Empty(SupportedSubset.OpCodes.Except(used));
        Assert.Equal(assembly.Methods.Count, module.Functions.Count(f => f.Kind == TirFunctionKind.Defined
            && !f.Name.StartsWith("ChipSharp.Array.", StringComparison.Ordinal)));
        Assert.Empty(TirVerifier.FindErrors(module));
    }

    [HostFact]
    public async Task Every_instruction_computes_what_dotnet_computes()
    {
        var program = OpCodeProgram.Build();
        var module = Lowered.Lower(Lowered.Program(program.Assembly.Read(), "Program.Main"));

        var run = await HostRun.BuildAndRunAsync(module, _scratch);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(program.RunOnDotnet(), run.Text);
    }

    [Fact]
    public void Nop_and_the_long_forms_translate_like_the_short_forms()
    {
        var emitted = new EmittedAssembly();
        emitted.Method("F", typeof(int), [typeof(int), typeof(int)], il =>
        {
            il.DeclareLocal(typeof(int));
            var end = il.DefineLabel();
            il.Emit(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg, (short)0);
            il.Emit(OpCodes.Ldc_I4, 1000);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, (short)0);
            il.Emit(OpCodes.Ldloc, (short)0);
            il.Emit(OpCodes.Starg, (short)1);
            il.Emit(OpCodes.Br, end);
            il.MarkLabel(end);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ret);
        });
        var assembly = emitted.Read();

        var function = Lowered.Lower(Lowered.Program(assembly, "Program.F")).Functions[0];

        Assert.Equal(
            [IlOpCode.Nop, IlOpCode.Ldarg, IlOpCode.Ldc_I4, IlOpCode.Add, IlOpCode.Stloc, IlOpCode.Ldloc, IlOpCode.Starg,
                IlOpCode.Br, IlOpCode.Ldarg_1, IlOpCode.Ret],
            assembly.Methods[0].Body!.Instructions.Select(i => i.OpCode));
        Assert.Equal("""
            function i32 Program.F(i32 %a, i32 %b)
              $b: i32
              $V_0: i32
            IL_0000:
              store $b, %b
              %0 = const i32 1000
              %1 = add i32 %a, %0
              store $V_0, %1
              %2 = load i32 $V_0
              store $b, %2
              jump IL_001c
            IL_001c:
              %3 = load i32 $b
              return %3

            """.ReplaceLineEndings("\n"), TirPrinter.Print(function));
    }

    [Fact]
    public void A_value_waiting_in_a_stack_slot_is_loaded_when_it_becomes_a_bool()
    {
        var emitted = new EmittedAssembly();
        emitted.Method("F", typeof(bool), [typeof(int)], il => EmitSlotToBool(il));

        var function = Lowered.Lower(Lowered.Program(emitted.Read(), "Program.F")).Functions[0];

        Assert.Equal("""
            function bool Program.F(i32 %a)
              $stack0_i32: i32
            IL_0000:
              %0 = const i32 0
              %1 = ne i32 %a, %0
              branch %1, IL_0006, IL_0003
            IL_0003:
              %2 = const i32 0
              store $stack0_i32, %2
              jump IL_0007
            IL_0006:
              %3 = const i32 2
              store $stack0_i32, %3
              jump IL_0007
            IL_0007:
              %4 = load i32 $stack0_i32
              %5 = const i32 0
              %6 = ne i32 %4, %5
              return %6

            """.ReplaceLineEndings("\n"), TirPrinter.Print(function));
    }

    /// <summary>bool F(int a) => a != 0 ? 2 : 0, the int32 2 or 0 arriving at the ret through a stack slot.</summary>
    private static void EmitSlotToBool(ILGenerator il)
    {
        var (two, join) = (il.DefineLabel(), il.DefineLabel());
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue_S, two);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Br_S, join);
        il.MarkLabel(two);
        il.Emit(OpCodes.Ldc_I4_2);
        il.MarkLabel(join);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Pure static methods that together use every instruction of the subset (but for the static fields, whose values
    /// carry over from call to call in .NET too), and a Main that calls each with a few inputs and prints the result
    /// with Console.WriteLine.
    /// </summary>
    private sealed partial class OpCodeProgram
    {
        private static readonly MethodInfo WriteLineInt32 = WriteLine(typeof(int));

        private readonly List<(MethodBuilder Method, Type[] Parameters, object[] Arguments)> _calls = [];

        private OpCodeProgram()
        {
        }

        public EmittedAssembly Assembly { get; } = new("OpCodes");

        public static OpCodeProgram Build()
        {
            var program = new OpCodeProgram();
            program.AddStaticFields();
            program.AddStructsAndReferences();
            program.AddArithmetic();
            program.AddConversions();
            program.AddFloats();
            program.AddComparisonsAndBranches();
            program.AddArgumentsLocalsAndConstants();
            program.AddArrays();
            program.AddText();
            program.AddClasses();
            program.AddMain();
            return program;
        }

        /// <summary>What .NET prints for the same calls, computed by running the methods in this process.</summary>
        public string RunOnDotnet()
        {
            var type = Assembly.LoadIntoDotnet().GetType("Program")!;
            var text = new StringBuilder();
            foreach (var (method, parameters, arguments) in _calls)
            {
                var result = type.GetMethod(method.Name, parameters)!.Invoke(null, arguments);
                text.Append(string.Format(CultureInfo.InvariantCulture, "{0}", result)).Append("\r\n");
            }

            return text.ToString();
        }

        private void AddArithmetic()
        {
            Binary("Add", typeof(int), OpCodes.Add, [int.MaxValue, 1], [-5, 3]);
            Binary("Sub", typeof(int), OpCodes.Sub, [int.MinValue, 1], [3, 10]);
            Binary("Mul", typeof(int), OpCodes.Mul, [100_000, 100_000], [-7, 6]);
            Binary("Div", typeof(int), OpCodes.Div, [-17, 5], [17, -5]);
            Binary("DivUn", typeof(uint), OpCodes.Div_Un, [4_000_000_000u, 7u], [7u, 2u]);
            Binary("Rem", typeof(int), OpCodes.Rem, [-17, 5], [17, -5]);
            Binary("RemUn", typeof(uint), OpCodes.Rem_Un, [4_000_000_000u, 7u]);
            Binary("And", typeof(int), OpCodes.And, [0x5A, 0x0F], [-1, 12345]);
            Binary("Or", typeof(int), OpCodes.Or, [0x50, 0x0F]);
            Binary("Xor", typeof(int), OpCodes.Xor, [0x5A, 0x0F], [-1, 12345]);
            Binary("Add64", typeof(long), OpCodes.Add, [long.MaxValue, 1L]);
            Binary("Mul64", typeof(long), OpCodes.Mul, [3_000_000_000L, 3L]);
            Binary("Div64", typeof(long), OpCodes.Div, [-1_234_567_890_123L, 1000L]);
            Binary("DivUn64", typeof(ulong), OpCodes.Div_Un, [ulong.MaxValue, 10UL]);
            Binary("Rem64", typeof(long), OpCodes.Rem, [-1_234_567_890_123L, 1000L]);
            Binary("RemUn64", typeof(ulong), OpCodes.Rem_Un, [ulong.MaxValue, 10UL]);
            Binary("Xor64", typeof(long), OpCodes.Xor, [-1L, 0xFFL]);

            Shift("Shl", typeof(int), OpCodes.Shl, [1, 31], [-100, 3]);
            Shift("Shr", typeof(int), OpCodes.Shr, [-100, 2], [int.MinValue, 31]);
            Shift("ShrUn", typeof(uint), OpCodes.Shr_Un, [4_000_000_000u, 4], [uint.MaxValue, 31]);
            Shift("Shl64", typeof(long), OpCodes.Shl, [1L, 63], [1L, 40]);
            Shift("Shr64", typeof(long), OpCodes.Shr, [-1_099_511_627_776L, 20]);
            Shift("ShrUn64", typeof(ulong), OpCodes.Shr_Un, [ulong.MaxValue, 60]);

            Unary("Neg", typeof(int), typeof(int), [OpCodes.Neg], [5], [int.MinValue]);
            Unary("Not", typeof(int), typeof(int), [OpCodes.Not], [0], [12345]);
            Unary("Neg64", typeof(long), typeof(long), [OpCodes.Neg], [5_000_000_000L]);
            Unary("Not64", typeof(long), typeof(long), [OpCodes.Not], [0L]);

            // native int with native int or int32 (sign-extended), shown as a long
            Add("NativeAdd", typeof(long), [typeof(long), typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Conv_I8);
                il.Emit(OpCodes.Ret);
            }, [5L, -3], [-5L, 1]);
            Add("NativeShl", typeof(long), [typeof(int), typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Conv_I8);
                il.Emit(OpCodes.Ret);
            }, [3, 4]);
        }

        private void AddConversions()
        {
            Unary("ConvI1", typeof(int), typeof(int), [OpCodes.Conv_I1], [200], [-129], [127]);
            Unary("ConvU1", typeof(int), typeof(int), [OpCodes.Conv_U1], [200], [-1], [261]);
            Unary("ConvI2", typeof(int), typeof(int), [OpCodes.Conv_I2], [40_000], [-32_769]);
            Unary("ConvU2", typeof(int), typeof(int), [OpCodes.Conv_U2], [-1], [70_000]);
            Unary("ConvI4", typeof(int), typeof(int), [OpCodes.Conv_I4], [-7]);
            Unary("ConvU4", typeof(uint), typeof(int), [OpCodes.Conv_U4], [-7]);
            Unary("ConvI1From64", typeof(int), typeof(long), [OpCodes.Conv_I1], [0x1FFL]);
            Unary("ConvU2From64", typeof(int), typeof(long), [OpCodes.Conv_U2], [-1L]);
            Unary("ConvI4From64", typeof(int), typeof(long), [OpCodes.Conv_I4], [0x1_0000_0005L], [-1L]);
            Unary("ConvU4From64", typeof(uint), typeof(long), [OpCodes.Conv_U4], [0x1_FFFF_FFFFL]);
            Unary("ConvI8", typeof(long), typeof(int), [OpCodes.Conv_I8], [-1], [int.MaxValue]);
            Unary("ConvU8", typeof(ulong), typeof(int), [OpCodes.Conv_U8], [-1], [5]);
            Unary("ConvI", typeof(long), typeof(int), [OpCodes.Conv_I, OpCodes.Conv_I8], [-5]);
            Unary("ConvU", typeof(ulong), typeof(int), [OpCodes.Conv_U, OpCodes.Conv_U8], [-1]);
            Unary("ConvIFrom64", typeof(long), typeof(long), [OpCodes.Conv_I, OpCodes.Conv_I8], [-5L]);
            Unary("ConvUFrom64", typeof(ulong), typeof(long), [OpCodes.Conv_U, OpCodes.Conv_U8], [7L]);
            Unary("ConvU8FromNative", typeof(ulong), typeof(int), [OpCodes.Conv_I, OpCodes.Conv_U8], [-1]);
            Unary("ConvI4FromNative", typeof(int), typeof(int), [OpCodes.Conv_I, OpCodes.Conv_I4], [-9]);

            // small integers and bools: widened by their own sign when loaded, cut down when stored or returned
            Add("SmallArguments", typeof(int), [typeof(sbyte), typeof(byte), typeof(short), typeof(ushort)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ret);
            }, [(sbyte)-5, (byte)200, (short)-300, (ushort)60_000]);
            Unary("ToByte", typeof(byte), typeof(int), [], [300], [-1]);
            Unary("ToSByte", typeof(sbyte), typeof(int), [], [200]);
            Unary("ToShort", typeof(short), typeof(int), [], [40_000]);
            Unary("ToUShort", typeof(ushort), typeof(int), [], [-2]);
            Unary("ToBool", typeof(bool), typeof(int), [], [0], [1], [7]);
            Add("ByteLocal", typeof(int), [typeof(int)], il =>
            {
                il.DeclareLocal(typeof(byte));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ret);
            }, [300], [-1]);
            Add("Int16Local", typeof(int), [typeof(int)], il =>
            {
                il.DeclareLocal(typeof(short));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ret);
            }, [40_000]);
            Add("BoolLocal", typeof(int), [typeof(int)], il =>
            {
                il.DeclareLocal(typeof(bool));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4_5);
                il.Emit(OpCodes.Cgt);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldc_I4_S, (sbyte)10);
                il.Emit(OpCodes.Mul);
                il.Emit(OpCodes.Ret);
            }, [7], [3]);
            Unary("NotBool", typeof(bool), typeof(bool), [OpCodes.Ldc_I4_0, OpCodes.Ceq], [true], [false]);
        }

        /// <summary>Floats and doubles: constants, arithmetic, conversions both ways, comparisons and branches.</summary>
        private void AddFloats()
        {
            Binary("AddR8", typeof(double), OpCodes.Add, [0.1, 0.2], [1e308, 1e308]);
            Binary("SubR4", typeof(float), OpCodes.Sub, [0.1f, 0.3f]);
            Binary("MulR4", typeof(float), OpCodes.Mul, [4097f, 4097f], [1e-30f, 1e-20f]);
            Binary("DivR8", typeof(double), OpCodes.Div, [1.0, 3.0], [-1.0, 0.0], [0.0, 0.0]);
            Binary("RemR4", typeof(float), OpCodes.Rem, [7.5f, -2f], [-0f, 1f]);
            Unary("NegR8", typeof(double), typeof(double), [OpCodes.Neg], [0.0], [-2.5]);
            Unary("NegR4", typeof(float), typeof(float), [OpCodes.Neg], [-0f]);
            Compare("CeqR8", typeof(double), OpCodes.Ceq, [double.NaN, double.NaN], [-0.0, 0.0]);
            Compare("CgtR4", typeof(float), OpCodes.Cgt, [float.NaN, 1f], [2f, 1f]);
            Compare("CgtUnR4", typeof(float), OpCodes.Cgt_Un, [float.NaN, 1f], [1f, 2f]);
            Compare("CltUnR8", typeof(double), OpCodes.Clt_Un, [double.NaN, 1.0], [2.0, 1.0]);
            foreach (var branch in new[] { OpCodes.Bge_Un, OpCodes.Blt, OpCodes.Bne_Un_S })
            {
                Add(Name(branch) + "R8", typeof(int), [typeof(double), typeof(double)], il =>
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    Choose(il, branch);
                }, [double.NaN, 1.0], [0.5, 1.0], [1.0, 1.0]);
            }

            Unary("ConvR4", typeof(float), typeof(double), [OpCodes.Conv_R4], [0.1], [1e300]);
            Unary("ConvR8", typeof(double), typeof(float), [OpCodes.Conv_R8], [0.1f]);
            Unary("ConvR4FromInt32", typeof(float), typeof(int), [OpCodes.Conv_R4], [16777217]);
            Unary("ConvR8FromInt64", typeof(double), typeof(long), [OpCodes.Conv_R8], [long.MaxValue]);
            Unary("ConvRUnFromInt32", typeof(double), typeof(int), [OpCodes.Conv_R_Un, OpCodes.Conv_R8], [-1]);
            Unary("ConvRUnR4FromInt64", typeof(float), typeof(long), [OpCodes.Conv_R_Un, OpCodes.Conv_R4],
                [unchecked((long)0x8000_0080_0000_0001)]);
            Unary("ConvI4FromR8", typeof(int), typeof(double), [OpCodes.Conv_I4], [1e10], [double.NaN], [-2.9]);
            Unary("ConvU1FromR4", typeof(int), typeof(float), [OpCodes.Conv_U1], [300.5f], [-1f]);
            Unary("ConvI2FromR8", typeof(int), typeof(double), [OpCodes.Conv_I2], [-40000.5]);
            Unary("ConvU8FromR8", typeof(ulong), typeof(double), [OpCodes.Conv_U8], [-1.0], [1e19]);
            Unary("ConvUFromR4", typeof(ulong), typeof(float), [OpCodes.Conv_U, OpCodes.Conv_U8], [-5f], [1e30f]);

            // a float met with a double is widened; a double returned as a float is rounded
            Add("FloatWithDouble", typeof(double), [typeof(float), typeof(double)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ret);
            }, [0.1f, 0.2]);
            Unary("ReturnedAsFloat", typeof(float), typeof(double), [], [0.1]);
            Add("FloatConstants", typeof(double), [], il =>
            {
                il.Emit(OpCodes.Ldc_R4, 0.1f);
                il.Emit(OpCodes.Ldc_R8, 0.2);
                il.Emit(OpCodes.Mul);
                il.Emit(OpCodes.Ret);
            }, [[]]);
        }

        private void AddComparisonsAndBranches()
        {
            object[][] pairs = [[-1, 1], [1, -1], [2, 2]];
            Compare("Ceq", typeof(int), OpCodes.Ceq, pairs);
            Compare("Cgt", typeof(int), OpCodes.Cgt, pairs);
            Compare("CgtUn", typeof(int), OpCodes.Cgt_Un, pairs);
            Compare("Clt", typeof(int), OpCodes.Clt, pairs);
            Compare("CltUn", typeof(int), OpCodes.Clt_Un, pairs);
            Compare("CgtUn64", typeof(long), OpCodes.Cgt_Un, [-1L, 1L]);
            Add("Less", typeof(bool), [typeof(long), typeof(long)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Clt);
                il.Emit(OpCodes.Ret);
            }, [-5L, 5L], [5L, -5L]);

            OpCode[] branches =
            [
                OpCodes.Beq_S, OpCodes.Bge_S, OpCodes.Bgt_S, OpCodes.Ble_S, OpCodes.Blt_S, OpCodes.Bne_Un_S,
                OpCodes.Bge_Un_S, OpCodes.Bgt_Un_S, OpCodes.Ble_Un_S, OpCodes.Blt_Un_S,
                OpCodes.Beq, OpCodes.Bge, OpCodes.Bgt, OpCodes.Ble, OpCodes.Blt, OpCodes.Bne_Un,
                OpCodes.Bge_Un, OpCodes.Bgt_Un, OpCodes.Ble_Un, OpCodes.Blt_Un,
            ];
            foreach (var branch in branches)
            {
                Add(Name(branch), typeof(int), [typeof(int), typeof(int)], il =>
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    Choose(il, branch);
                }, pairs);
            }

            Add("Bge64", typeof(int), [typeof(long), typeof(long)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                Choose(il, OpCodes.Bge);
            }, [-1L, 1L], [1L, -1L]);

            foreach (var branch in new[] { OpCodes.Brtrue_S, OpCodes.Brfalse_S, OpCodes.Brtrue, OpCodes.Brfalse })
            {
                Add(Name(branch), typeof(int), [typeof(int)], il =>
                {
                    il.Emit(OpCodes.Ldarg_0);
                    Choose(il, branch);
                }, [0], [5], [-1]);
            }

            Add("BrtrueInt64", typeof(int), [typeof(long)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                Choose(il, OpCodes.Brtrue_S);
            }, [0x1_0000_0000L], [0L]);
            Add("BrfalseBool", typeof(int), [typeof(bool)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                Choose(il, OpCodes.Brfalse_S);
            }, [true], [false]);

            // br and br.s, over code nothing reaches
            Add("Jumps", typeof(int), [typeof(int)], il =>
            {
                var (middle, end) = (il.DefineLabel(), il.DefineLabel());
                il.Emit(OpCodes.Br_S, middle);
                il.Emit(OpCodes.Ldc_I4_M1);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(middle);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Br, end);
                il.MarkLabel(end);
                il.Emit(OpCodes.Ret);
            }, [42]);

            // switch: cases 0 to 2, anything else (negative numbers too) falls through
            Add("Switch", typeof(int), [typeof(int)], il =>
            {
                Label[] cases = [il.DefineLabel(), il.DefineLabel(), il.DefineLabel()];
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Switch, cases);
                il.Emit(OpCodes.Ldc_I4_M1);
                il.Emit(OpCodes.Ret);
                for (var i = 0; i < cases.Length; i++)
                {
                    il.MarkLabel(cases[i]);
                    il.Emit(OpCodes.Ldc_I4, 10 * (i + 1));
                    il.Emit(OpCodes.Ret);
                }
            }, [-1], [0], [1], [2], [3]);

            // a value under a ?: that both ways in leave on the stack
            Add("Select", typeof(int), [typeof(int), typeof(bool)], il =>
            {
                var (other, join) = (il.DefineLabel(), il.DefineLabel());
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Brtrue_S, other);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Br_S, join);
                il.MarkLabel(other);
                il.Emit(OpCodes.Ldc_I4, 100);
                il.MarkLabel(join);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ret);
            }, [5, true], [5, false]);
            Add("SlotToBool", typeof(bool), [typeof(int)], EmitSlotToBool, [0], [5]);

            // a loop that starts at IL_0000 and assigns its parameter
            Add("CountDown", typeof(int), [typeof(int)], il =>
            {
                var top = il.DefineLabel();
                il.MarkLabel(top);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Starg_S, (byte)0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4_3);
                il.Emit(OpCodes.Bgt, top);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ret);
            }, [10], [1]);
        }

        private void AddArgumentsLocalsAndConstants()
        {
            Add("Arguments", typeof(int), [typeof(int), typeof(int), typeof(int), typeof(int), typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldarg_S, (byte)4);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldarg, (short)0);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ret);
            }, [1, 20, 300, 4000, 50000]);
            Add("Assign", typeof(int), [typeof(int), typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Starg_S, (byte)0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Mul);
                il.Emit(OpCodes.Starg, (short)1);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Ret);
            }, [5, 7]);
            Add("Locals", typeof(int), [typeof(int)], il =>
            {
                for (var i = 0; i < 6; i++)
                {
                    il.DeclareLocal(typeof(int));
                }

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc_1);
                il.Emit(OpCodes.Ldloc_1);
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc_2);
                il.Emit(OpCodes.Ldloc_2);
                il.Emit(OpCodes.Ldc_I4_3);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc_3);
                il.Emit(OpCodes.Ldloc_3);
                il.Emit(OpCodes.Ldc_I4_4);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc_S, (byte)4);
                il.Emit(OpCodes.Ldloc_S, (byte)4);
                il.Emit(OpCodes.Ldc_I4_5);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, (short)5);
                il.Emit(OpCodes.Ldloc, (short)5);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ret);
            }, [10]);
            Add("Constants", typeof(int), [], il =>
            {
                OpCode[] small =
                [
                    OpCodes.Ldc_I4_M1, OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1, OpCodes.Ldc_I4_2, OpCodes.Ldc_I4_3,
                    OpCodes.Ldc_I4_4, OpCodes.Ldc_I4_5, OpCodes.Ldc_I4_6, OpCodes.Ldc_I4_7, OpCodes.Ldc_I4_8,
                ];
                il.Emit(OpCodes.Ldc_I4_S, (sbyte)-100);
                foreach (var constant in small)
                {
                    il.Emit(constant);
                    il.Emit(OpCodes.Add);
                }

                il.Emit(OpCodes.Ldc_I4, 100_000);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ret);
            }, [[]]);
            Add("Constant64", typeof(long), [], il =>
            {
                il.Emit(OpCodes.Ldc_I8, -1_234_567_890_123L);
                il.Emit(OpCodes.Ret);
            }, [[]]);

            // dup, pop and nop, and a call from one method of the program to another
            var square = Add("Square", typeof(int), [typeof(int)], il =>
            {
                il.Emit(OpCodes.Nop);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Mul);
                il.Emit(OpCodes.Ldc_I4_7);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ret);
            }, [-12]);
            Add("CallsSquare", typeof(int), [typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, square);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ret);
            }, [9]);
        }

        /// <summary>ldsfld, stsfld and volatile.: static fields that keep their values from one call to the next.</summary>
        private void AddStaticFields()
        {
            var total = Assembly.Field("total", typeof(long));
            Add("AddToTotal", typeof(long), [typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldsfld, total);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Conv_I8);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Stsfld, total);
                il.Emit(OpCodes.Ret);
            }, [5], [-12], [int.MaxValue]);

            // the int32 is cut to the byte's 8 bits on the way in, and zero-extended on the way out
            var small = Assembly.Field("small", typeof(byte));
            Add("StoreByte", typeof(int), [typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stsfld, small);
                il.Emit(OpCodes.Ldsfld, small);
                il.Emit(OpCodes.Ret);
            }, [300], [-1]);

            var ticks = Assembly.Field("ticks", typeof(int), isVolatile: true);
            Add("Tick", typeof(int), [], il =>
            {
                il.Emit(OpCodes.Volatile);
                il.Emit(OpCodes.Ldsfld, ticks);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Volatile);
                il.Emit(OpCodes.Stsfld, ticks);
                il.Emit(OpCodes.Volatile);
                il.Emit(OpCodes.Ldsfld, ticks);
                il.Emit(OpCodes.Ret);
            }, [[], []]);
        }

        /// <summary>
        /// A struct Pair { int A; long B; } with a constructor and a method, and the instructions on structs and
        /// references: fields, addresses, initobj, ldobj, stobj, cpobj, newobj, and ldind and stind of every width.
        /// </summary>
        private void AddStructsAndReferences()
        {
            var pair = Assembly.Struct("Pair");
            var a = pair.DefineField("A", typeof(int), FieldAttributes.Public);
            var b = pair.DefineField("B", typeof(long), FieldAttributes.Public);
            var constructor = pair.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard,
                [typeof(int), typeof(long)]);
            var body = constructor.GetILGenerator();
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Ldarg_1);
            body.Emit(OpCodes.Stfld, a);
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Ldarg_2);
            body.Emit(OpCodes.Stfld, b);
            body.Emit(OpCodes.Ret);
            var sum = pair.DefineMethod("Sum", MethodAttributes.Public, typeof(long), []);
            body = sum.GetILGenerator();
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Ldfld, a);
            body.Emit(OpCodes.Conv_I8);
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Ldfld, b);
            body.Emit(OpCodes.Add);
            body.Emit(OpCodes.Ret);

            // initobj, stfld through a local's address (short and long ldloca), ldflda, ldfld of a value and of an address
            Add("StructFields", typeof(int), [typeof(int), typeof(int)], il =>
            {
                il.DeclareLocal(pair);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Initobj, pair);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stfld, a);
                il.Emit(OpCodes.Ldloca, (short)0);
                il.Emit(OpCodes.Ldflda, b);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Conv_I8);
                il.Emit(OpCodes.Stind_I8);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldfld, a);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldfld, b);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Ret);
            }, [50, 8], [-3, 4]);

            // newobj makes a Pair, a call runs its method on the local's address
            Add("NewStruct", typeof(long), [typeof(int), typeof(int)], il =>
            {
                il.DeclareLocal(pair);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Conv_I8);
                il.Emit(OpCodes.Newobj, constructor);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Call, sum);
                il.Emit(OpCodes.Ret);
            }, [7, 3_000_000], [-1, -1]);

            // ldobj and stobj copy V_0 to V_1; changing V_0 leaves V_1 as it was; cpobj copies V_1 back to V_0
            Add("CopyStruct", typeof(int), [typeof(int)], il =>
            {
                il.DeclareLocal(pair);
                il.DeclareLocal(pair);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I8, 7L);
                il.Emit(OpCodes.Call, constructor);
                il.Emit(OpCodes.Ldloca_S, (byte)1);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldobj, pair);
                il.Emit(OpCodes.Stobj, pair);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldc_I4, 100);
                il.Emit(OpCodes.Stfld, a);
                il.Emit(OpCodes.Ldloca_S, (byte)1);
                il.Emit(OpCodes.Ldfld, a);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldfld, a);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldloca_S, (byte)1);
                il.Emit(OpCodes.Cpobj, pair);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Ldfld, a);
                il.Emit(OpCodes.Mul);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ret);
            }, [5]);

            // stind and ldind of every width through locals' addresses: narrowed, then sign- or zero-extended back
            Add("Indirect", typeof(long), [typeof(int)], il =>
            {
                Type[] types =
                [
                    typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long),
                    typeof(nint), typeof(bool),
                ];
                foreach (var type in types)
                {
                    il.DeclareLocal(type);
                }

                (OpCode Store, OpCode Load, OpCode Widen)[] accesses =
                [
                    (OpCodes.Stind_I1, OpCodes.Ldind_I1, OpCodes.Conv_I8), (OpCodes.Stind_I1, OpCodes.Ldind_U1, OpCodes.Conv_U8),
                    (OpCodes.Stind_I2, OpCodes.Ldind_I2, OpCodes.Conv_I8), (OpCodes.Stind_I2, OpCodes.Ldind_U2, OpCodes.Conv_U8),
                    (OpCodes.Stind_I4, OpCodes.Ldind_I4, OpCodes.Conv_I8), (OpCodes.Stind_I4, OpCodes.Ldind_U4, OpCodes.Conv_U8),
                    (OpCodes.Stind_I8, OpCodes.Ldind_I8, OpCodes.Nop), (OpCodes.Stind_I, OpCodes.Ldind_I, OpCodes.Conv_I8),
                    (OpCodes.Stind_I1, OpCodes.Ldind_U1, OpCodes.Conv_U8),
                ];
                for (var i = 0; i < accesses.Length; i++)
                {
                    il.Emit(OpCodes.Ldloca_S, (byte)i);
                    il.Emit(OpCodes.Ldarg_0);
                    if (types[i] == typeof(long))
                    {
                        il.Emit(OpCodes.Conv_I8);
                    }
                    else if (types[i] == typeof(nint))
                    {
                        il.Emit(OpCodes.Conv_I);
                    }
                    else if (types[i] == typeof(bool))
                    {
                        // a bool is 0 or 1
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Cgt_Un);
                    }

                    il.Emit(accesses[i].Store);
                }

                il.Emit(OpCodes.Ldc_I8, 0L);
                for (var i = 0; i < accesses.Length; i++)
                {
                    il.Emit(OpCodes.Ldloca_S, (byte)i);
                    il.Emit(accesses[i].Load);
                    il.Emit(accesses[i].Widen);
                    il.Emit(OpCodes.Ldc_I8, 1L << (4 * i));
                    il.Emit(OpCodes.Mul);
                    il.Emit(OpCodes.Add);
                }

                il.Emit(OpCodes.Ret);
            }, [200], [-1], [0x12345]);

            // ldarga (short and long) hands the arguments to a method that swaps them through their references
            var swap = Assembly.Method("Swap", typeof(void), [typeof(int).MakeByRefType(), typeof(int).MakeByRefType()], il =>
            {
                il.DeclareLocal(typeof(int));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldind_I4);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldind_I4);
                il.Emit(OpCodes.Stind_I4);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Stind_I4);
                il.Emit(OpCodes.Ret);
            });
            Add("SwapArguments", typeof(int), [typeof(int), typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldarga_S, (byte)0);
                il.Emit(OpCodes.Ldarga, (short)1);
                il.Emit(OpCodes.Call, swap);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4_S, (sbyte)10);
                il.Emit(OpCodes.Mul);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ret);
            }, [1, 2]);

            // ldsflda: a static Pair's field, written and read through its address
            var stored = Assembly.Field("stored", pair);
            Add("StaticAddress", typeof(int), [typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldsflda, stored);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stfld, a);
                il.Emit(OpCodes.Ldsflda, stored);
                il.Emit(OpCodes.Ldflda, a);
                il.Emit(OpCodes.Ldind_I4);
                il.Emit(OpCodes.Ret);
            }, [-42]);
        }

        /// <summary>Main: each call, its result printed with the WriteLine for its type.</summary>
        private void AddMain() => Assembly.Method("Main", typeof(void), [], il =>
        {
            foreach (var (method, parameters, arguments) in _calls)
            {
                foreach (var argument in arguments)
                {
                    switch (argument)
                    {
                        case null:
                            il.Emit(OpCodes.Ldnull);
                            break;
                        case string text:
                            il.Emit(OpCodes.Ldstr, text);
                            break;
                        case long l:
                            il.Emit(OpCodes.Ldc_I8, l);
                            break;
                        case ulong u:
                            il.Emit(OpCodes.Ldc_I8, unchecked((long)u));
                            break;
                        case bool b:
                            il.Emit(OpCodes.Ldc_I4, b ? 1 : 0);
                            break;
                        case uint u:
                            il.Emit(OpCodes.Ldc_I4, unchecked((int)u));
                            break;
                        case float f:
                            il.Emit(OpCodes.Ldc_R4, f);
                            break;
                        case double d:
                            il.Emit(OpCodes.Ldc_R8, d);
                            break;
                        default:
                            il.Emit(OpCodes.Ldc_I4, System.Convert.ToInt32(argument, CultureInfo.InvariantCulture));
                            break;
                    }
                }

                il.Emit(OpCodes.Call, method);
                var result = method.ReturnType;
                il.Emit(OpCodes.Call, result == typeof(bool) || result == typeof(uint) || result == typeof(long)
                    || result == typeof(ulong) || result == typeof(float) || result == typeof(double)
                    || result == typeof(string)
                    ? WriteLine(result)
                    : WriteLineInt32);
            }

            il.Emit(OpCodes.Ret);
        });

        private MethodBuilder Add(string name, Type returnType, Type[] parameters, Action<ILGenerator> body,
            params object[][] calls)
        {
            var method = Assembly.Method(name, returnType, parameters, body);
            foreach (var arguments in calls)
            {
                _calls.Add((method, parameters, arguments.Select((a, i) => System.Convert.ChangeType(a, parameters[i],
                    CultureInfo.InvariantCulture)).ToArray()));
            }

            return method;
        }

        private void Binary(string name, Type type, OpCode op, params object[][] calls) =>
            Add(name, type, [type, type], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(op);
                il.Emit(OpCodes.Ret);
            }, calls);

        /// <summary>ceq, cgt, clt and the .un forms: the int32 0 or 1 they push is the result.</summary>
        private void Compare(string name, Type type, OpCode op, params object[][] calls) =>
            Add(name, typeof(int), [type, type], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(op);
                il.Emit(OpCodes.Ret);
            }, calls);

        private void Shift(string name, Type type, OpCode op, params object[][] calls) =>
            Add(name, type, [type, typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(op);
                il.Emit(OpCodes.Ret);
            }, calls);

        private void Unary(string name, Type returnType, Type parameter, OpCode[] ops, params object[][] calls) =>
            Add(name, returnType, [parameter], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                foreach (var op in ops)
                {
                    il.Emit(op);
                }

                il.Emit(OpCodes.Ret);
            }, calls);

        /// <summary>Ends a method with "<paramref name="branch"/> to return 1, else return 0".</summary>
        private static void Choose(ILGenerator il, OpCode branch)
        {
            var taken = il.DefineLabel();
            il.Emit(branch, taken);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(taken);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
        }

        private static string Name(OpCode op) =>
            string.Concat(op.Name!.Split('.').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

        private static MethodInfo WriteLine(Type type) => typeof(Console).GetMethod("WriteLine", [type])!;
    }
}
