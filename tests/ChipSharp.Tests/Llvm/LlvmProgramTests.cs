using ChipSharp.Ir;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Support;
using ChipSharp.Tests.Tir;

namespace ChipSharp.Tests.Llvm;

/// <summary>
/// TIR programs built into Windows .exe files (generator, clang, lld, a small test runtime; no C runtime) and run:
/// what they print and return must be what the same C# prints and returns on .NET.
/// </summary>
public sealed class LlvmProgramTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [HostFact]
    public async Task Square_of_42_prints_1764()
    {
        var result = await LlvmTools.BuildAndRunAsync(LlvmSamples.SquareProgram(), _scratch);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1764\r\n", result.Output);
    }

    [HostFact]
    public async Task Max_takes_the_branch_that_stores_the_larger_value()
    {
        var program = new TestProgram("Max");
        var max = TirSamples.AddMax(program.Module);
        var ir = program.Ir;
        program.WriteLineInt32(ir.Call(max, ir.Const(TirType.I32, 3), ir.Const(TirType.I32, 7)));
        program.WriteLineInt32(ir.Call(max, ir.Const(TirType.I32, 9), ir.Const(TirType.I32, -2)));
        program.WriteLineInt32(ir.Call(max, ir.Const(TirType.I32, -5), ir.Const(TirType.I32, -5)));

        var result = await LlvmTools.BuildAndRunAsync(program.Finish(), _scratch);

        Assert.Equal("7\r\n9\r\n-5\r\n", result.Output);
    }

    [HostFact]
    public async Task Loops_sum_1_to_100_through_slots_and_count_from_the_first_block()
    {
        // int sum = 0; for (int i = 1; i <= 100; i++) sum += i; Console.WriteLine(sum); Console.WriteLine(CountTo(7));
        var program = new TestProgram("Loop");
        var countTo = LlvmSamples.AddCountTo(program.Module);
        var ir = program.Ir;
        var main = ir.Function;
        var i = main.AddSlot("i", TirType.I32);
        var sum = main.AddSlot("sum", TirType.I32);
        var test = main.AddBlock("test");
        var body = main.AddBlock("body");
        var done = main.AddBlock("done");

        ir.Store(i, ir.Const(TirType.I32, 1));
        ir.Store(sum, ir.Const(TirType.I32, 0));
        ir.Jump(test);

        ir.Block = test;
        ir.Branch(ir.Compare(TirCompareOp.Le, ir.Load(i), ir.Const(TirType.I32, 100)), body, done);

        ir.Block = body;
        var index = ir.Load(i);
        ir.Store(sum, ir.Binary(TirBinaryOp.Add, ir.Load(sum), index));
        ir.Store(i, ir.Binary(TirBinaryOp.Add, index, ir.Const(TirType.I32, 1)));
        ir.Jump(test);

        ir.Block = done;
        program.WriteLineInt32(ir.Load(sum));
        program.WriteLineInt32(ir.Call(countTo, ir.Const(TirType.I32, 7)));

        var result = await LlvmTools.BuildAndRunAsync(program.Finish(), _scratch);

        Assert.Equal("5050\r\n7\r\n", result.Output);
    }

    [HostFact]
    public async Task Signed_and_unsigned_operations_differ_as_in_CSharp()
    {
        var program = new TestProgram("Signs");
        var ir = program.Ir;
        var x = ir.Const(TirType.I32, -7);
        var y = ir.Const(TirType.I32, 2);
        var one = ir.Const(TirType.I32, 1);
        var b = ir.Const(TirType.I8, 255);
        program.WriteLineInt32(ir.Binary(TirBinaryOp.Div, x, y)); //                    -7 / 2
        program.WriteLineUInt32(ir.Binary(TirBinaryOp.DivUnsigned, x, y)); //         (uint)-7 / 2
        program.WriteLineInt32(ir.Binary(TirBinaryOp.Rem, x, y)); //                    -7 % 2
        program.WriteLineUInt32(ir.Binary(TirBinaryOp.RemUnsigned, x, y)); //         (uint)-7 % 2
        program.WriteLineInt32(ir.Binary(TirBinaryOp.Shr, x, one)); //                  -7 >> 1
        program.WriteLineUInt32(ir.Binary(TirBinaryOp.ShrUnsigned, x, one)); //       -7 >>> 1
        program.WriteLineBool(ir.Compare(TirCompareOp.Lt, x, y)); //                    -7 < 2
        program.WriteLineBool(ir.Compare(TirCompareOp.LtUnsigned, x, y)); //          (uint)-7 < 2
        program.WriteLineInt32(ir.Convert(b, TirType.I32)); //                          (int)(sbyte)-1
        program.WriteLineInt32(ir.Convert(b, TirType.I32, unsigned: true)); //         (int)(byte)255

        var result = await LlvmTools.BuildAndRunAsync(program.Finish(), _scratch);

        Assert.Equal("-3\r\n2147483644\r\n-1\r\n1\r\n-4\r\n2147483644\r\nTrue\r\nFalse\r\n-1\r\n255\r\n", result.Output);
    }

    [HostFact]
    public async Task Long_values_keep_all_64_bits()
    {
        var program = new TestProgram("Longs");
        var ir = program.Ir;
        var minusOne = ir.Const(TirType.I64, -1);
        program.WriteLineInt64(ir.Binary(TirBinaryOp.Mul, ir.Const(TirType.I64, 3_000_000_000), ir.Const(TirType.I64, 3)));
        program.WriteLineInt64(ir.Const(TirType.I64, long.MinValue));
        program.WriteLineUInt64(minusOne); //                                            ulong.MaxValue
        program.WriteLineUInt64(ir.Binary(TirBinaryOp.DivUnsigned, minusOne, ir.Const(TirType.I64, 10)));
        program.WriteLineInt64(ir.Binary(TirBinaryOp.Div, minusOne, ir.Const(TirType.I64, 10)));
        program.WriteLineInt64(ir.Convert(ir.Const(TirType.I32, -1), TirType.I64)); //  (long)-1
        program.WriteLineInt64(ir.Convert(ir.Const(TirType.I32, -1), TirType.I64, unsigned: true)); // (long)uint.MaxValue
        program.WriteLineInt32(ir.Convert(ir.Const(TirType.I64, 0x1_0000_0005), TirType.I32)); // (int)0x100000005L

        var result = await LlvmTools.BuildAndRunAsync(program.Finish(), _scratch);

        Assert.Equal("9000000000\r\n-9223372036854775808\r\n18446744073709551615\r\n1844674407370955161\r\n0\r\n" +
            "-1\r\n4294967295\r\n5\r\n", result.Output);
    }

    [HostFact]
    public async Task Floats_compute_convert_and_compare_like_CSharp()
    {
        var program = new TestProgram("Floats");
        var ir = program.Ir;
        var d = ir.Binary(TirBinaryOp.Mul, ir.Const(TirType.F64, 1.5), ir.Const(TirType.F64, 4.0));
        program.WriteLineInt32(ir.Convert(d, TirType.I32)); //                          (int)(1.5 * 4.0)
        var f = ir.Binary(TirBinaryOp.Add, ir.Const(TirType.F32, 2.5), ir.Const(TirType.F32, 0.25));
        program.WriteLineInt32(ir.Convert(ir.Binary(TirBinaryOp.Mul, f, ir.Const(TirType.F32, 4.0)), TirType.I32));
        var big = ir.Convert(ir.Const(TirType.I32, uint.MaxValue), TirType.F64, unsigned: true);
        var half = ir.Binary(TirBinaryOp.Div, big, ir.Const(TirType.F64, 2.0));
        program.WriteLineUInt32(ir.Convert(half, TirType.I32, unsigned: true)); //      (uint)(uint.MaxValue / 2.0)
        var negative = ir.Binary(TirBinaryOp.Mul, ir.Convert(ir.Const(TirType.I32, -3), TirType.F64), ir.Const(TirType.F64, 1.5));
        program.WriteLineInt32(ir.Convert(negative, TirType.I32)); //                   (int)(-3 * 1.5)
        var nan = ir.Binary(TirBinaryOp.Div, ir.Const(TirType.F64, 0.0), ir.Const(TirType.F64, 0.0));
        program.WriteLineBool(ir.Compare(TirCompareOp.Ne, nan, nan)); //                double.NaN != double.NaN
        program.WriteLineBool(ir.Compare(TirCompareOp.Eq, nan, nan)); //                double.NaN == double.NaN
        program.WriteLineBool(ir.Compare(TirCompareOp.Lt, ir.Const(TirType.F64, 0.1),
            ir.Convert(ir.Const(TirType.F32, 0.1), TirType.F64))); //                   0.1 < (double)0.1f
        program.WriteLineInt32(ir.Convert(ir.Convert(ir.Const(TirType.F64, 2.75), TirType.F32), TirType.I32));

        var result = await LlvmTools.BuildAndRunAsync(program.Finish(), _scratch);

        Assert.Equal("6\r\n11\r\n2147483647\r\n-4\r\nTrue\r\nFalse\r\nTrue\r\n2\r\n", result.Output);
    }

    [HostFact]
    public async Task The_int_that_Main_returns_is_the_exit_code()
    {
        var module = new TirModule("Exit");
        var main = module.AddFunction("Program.Main", TirType.I32);
        var ir = new TirBuilder(main.AddBlock("entry"));
        ir.Return(ir.Const(TirType.I32, 42));
        module.EntryPoint = main;

        var result = await LlvmTools.BuildAndRunAsync(module, _scratch);

        Assert.Equal(42, result.ExitCode);
        Assert.Equal("", result.Output);
    }
}
