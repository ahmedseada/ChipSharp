using ChipSharp.Ir;

namespace ChipSharp.Tests.Tir;

/// <summary>Small well-formed functions the TIR tests share.</summary>
internal static class TirSamples
{
    /// <summary><c>static int Max(int a, int b) { int result; if (a &gt; b) result = a; else result = b; return result; }</c></summary>
    public static TirFunction AddMax(TirModule module)
    {
        var max = module.AddFunction("Program.Max", TirType.I32, ("a", TirType.I32), ("b", TirType.I32));
        var (a, b) = (max.Parameters[0], max.Parameters[1]);
        var result = max.AddSlot("result", TirType.I32);
        var entry = max.AddBlock("entry");
        var then = max.AddBlock("then");
        var otherwise = max.AddBlock("else");
        var join = max.AddBlock("join");

        var ir = new TirBuilder(entry);
        ir.Branch(ir.Compare(TirCompareOp.Gt, a, b), then, otherwise);

        ir.Block = then;
        ir.Store(result, a);
        ir.Jump(join);

        ir.Block = otherwise;
        ir.Store(result, b);
        ir.Jump(join);

        ir.Block = join;
        ir.Return(ir.Load(result));
        return max;
    }

    /// <summary><c>static int Sum(int n) { int sum = 0; for (int i = 0; i &lt; n; i++) sum += i; return sum; }</c></summary>
    public static TirFunction AddSum(TirModule module)
    {
        var sum = module.AddFunction("Program.Sum", TirType.I32, ("n", TirType.I32));
        var n = sum.Parameters[0];
        var i = sum.AddSlot("i", TirType.I32);
        var total = sum.AddSlot("sum", TirType.I32);
        var entry = sum.AddBlock("entry");
        var test = sum.AddBlock("test");
        var body = sum.AddBlock("body");
        var done = sum.AddBlock("done");

        var ir = new TirBuilder(entry);
        var zero = ir.Const(TirType.I32, 0);
        ir.Store(i, zero);
        ir.Store(total, zero);
        ir.Jump(test);

        ir.Block = test;
        ir.Branch(ir.Compare(TirCompareOp.Lt, ir.Load(i), n), body, done);

        ir.Block = body;
        var current = ir.Load(total);
        var index = ir.Load(i);
        ir.Store(total, ir.Binary(TirBinaryOp.Add, current, index));
        ir.Store(i, ir.Binary(TirBinaryOp.Add, index, ir.Const(TirType.I32, 1)));
        ir.Jump(test);

        ir.Block = done;
        ir.Return(ir.Load(total));
        return sum;
    }

    /// <summary>Adds an empty <c>Program.Main</c> and makes it the entry point, so a module of samples verifies.</summary>
    public static TirModule WithMain(TirModule module)
    {
        var main = module.AddFunction("Program.Main", TirType.Void);
        new TirBuilder(main.AddBlock("entry")).Return();
        module.EntryPoint = main;
        return module;
    }
}
