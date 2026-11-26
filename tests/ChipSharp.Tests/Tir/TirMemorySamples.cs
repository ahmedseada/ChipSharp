using ChipSharp.Ir;

namespace ChipSharp.Tests.Tir;

/// <summary>Small well-formed functions that use memory (globals, structs, arrays, addresses), shared by the tests.</summary>
internal static class TirMemorySamples
{
    /// <summary><c>struct Point { int X; int Y; }</c></summary>
    public static TirStructType Point { get; } = new("Program.Point", ("X", TirType.I32), ("Y", TirType.I32));

    /// <summary><c>struct Line { Point From; Point To; byte Color; }</c></summary>
    public static TirStructType Line { get; } = new("Program.Line", ("From", Point), ("To", Point), ("Color", TirType.I8));

    /// <summary>Ten ints, e.g. <c>fixed int squares[10]</c>.</summary>
    public static TirArrayType TenInts { get; } = new(TirType.I32, 10);

    /// <summary><c>static int counter = 5;</c> and <c>static void Increment() { counter++; }</c>.</summary>
    public static (TirGlobal Counter, TirFunction Increment) AddCounter(TirModule module)
    {
        var counter = module.AddGlobal("Program.counter", TirType.I32, new TirConst(TirType.I32, 5));
        var increment = module.AddFunction("Program.Increment", TirType.Void);
        var b = new TirBuilder(increment.AddBlock("entry"));
        var address = b.AddressOf(counter);
        b.Store(address, b.Binary(TirBinaryOp.Add, b.Load(TirType.I32, address), b.Const(TirType.I32, 1)));
        b.Return();
        return (counter, increment);
    }

    /// <summary><c>static int LengthSquared(int x, int y) { Point p; p.X = x; p.Y = y; return p.X * p.X + p.Y * p.Y; }</c></summary>
    public static TirFunction AddLengthSquared(TirModule module)
    {
        var function = module.AddFunction("Program.LengthSquared", TirType.I32, ("x", TirType.I32), ("y", TirType.I32));
        var p = function.AddSlot("p", Point);
        var b = new TirBuilder(function.AddBlock("entry"));
        var address = b.AddressOf(p);
        var x = b.FieldAddress(address, Point, "X");
        b.Store(x, function.Parameters[0]);
        var y = b.FieldAddress(address, Point, "Y");
        b.Store(y, function.Parameters[1]);
        var xValue = b.Load(TirType.I32, x);
        var yValue = b.Load(TirType.I32, y);
        var xSquared = b.Binary(TirBinaryOp.Mul, xValue, xValue);
        b.Return(b.Binary(TirBinaryOp.Add, xSquared, b.Binary(TirBinaryOp.Mul, yValue, yValue)));
        return function;
    }

    /// <summary>
    /// <c>static Point Swap(Point p) { Point q; q.X = p.Y; q.Y = p.X; return q; }</c>: a struct passed and returned by
    /// value.
    /// </summary>
    public static TirFunction AddSwap(TirModule module)
    {
        var function = module.AddFunction("Program.Swap", Point, ("p", Point));
        var p = function.AddSlot("p", Point);
        var q = function.AddSlot("q", Point);
        var b = new TirBuilder(function.AddBlock("entry"));
        b.Store(p, function.Parameters[0]);
        var from = b.AddressOf(p);
        var to = b.AddressOf(q);
        b.Store(b.FieldAddress(to, Point, 0), b.Load(TirType.I32, b.FieldAddress(from, Point, 1)));
        b.Store(b.FieldAddress(to, Point, 1), b.Load(TirType.I32, b.FieldAddress(from, Point, 0)));
        b.Return(b.Load(q));
        return function;
    }

    /// <summary>
    /// <c>static int SumOfSquares() { fixed int squares[10]; for (int i = 0; i &lt; 10; i++) squares[i] = i * i;
    /// int sum = 0; for (int i = 0; i &lt; 10; i++) sum += squares[i]; return sum; }</c>, 285. The slots start at zero.
    /// </summary>
    public static TirFunction AddSumOfSquares(TirModule module)
    {
        var function = module.AddFunction("Program.SumOfSquares", TirType.I32);
        var squares = function.AddSlot("squares", TenInts);
        var i = function.AddSlot("i", TirType.I32);
        var j = function.AddSlot("j", TirType.I32);
        var sum = function.AddSlot("sum", TirType.I32);
        var fill = function.AddBlock("fill");
        var fillBody = function.AddBlock("fillBody");
        var add = function.AddBlock("add");
        var addBody = function.AddBlock("addBody");
        var done = function.AddBlock("done");

        var b = new TirBuilder(fill);
        b.Branch(b.Compare(TirCompareOp.Lt, b.Load(i), b.Const(TirType.I32, 10)), fillBody, add);

        b.Block = fillBody;
        var index = b.Load(i);
        b.Store(b.ElementAddress(b.AddressOf(squares), TirType.I32, index), b.Binary(TirBinaryOp.Mul, index, index));
        b.Store(i, b.Binary(TirBinaryOp.Add, index, b.Const(TirType.I32, 1)));
        b.Jump(fill);

        b.Block = add;
        b.Branch(b.Compare(TirCompareOp.Lt, b.Load(j), b.Const(TirType.I32, 10)), addBody, done);

        b.Block = addBody;
        var other = b.Load(j);
        var element = b.Load(TirType.I32, b.ElementAddress(b.AddressOf(squares), TirType.I32, other));
        b.Store(sum, b.Binary(TirBinaryOp.Add, b.Load(sum), element));
        b.Store(j, b.Binary(TirBinaryOp.Add, other, b.Const(TirType.I32, 1)));
        b.Jump(add);

        b.Block = done;
        b.Return(b.Load(sum));
        return function;
    }

    /// <summary>
    /// A hardware register the way M2 reaches one: <c>static uint Toggle(uint address, uint bits)</c> reads the
    /// 32-bit register at a numeric address (volatile), writes it back with the bits flipped, and returns the
    /// register's address as a number again.
    /// </summary>
    public static TirFunction AddRegisterToggle(TirModule module)
    {
        var function = module.AddFunction("Program.Toggle", TirType.NativeInt, ("address", TirType.I32), ("bits", TirType.I32));
        var b = new TirBuilder(function.AddBlock("entry"));
        var register = b.IntToPtr(function.Parameters[0]);
        var value = b.Load(TirType.I32, register, isVolatile: true);
        b.Store(register, b.Binary(TirBinaryOp.Xor, value, function.Parameters[1]), isVolatile: true);
        b.Return(b.PtrToInt(register, TirType.NativeInt));
        return function;
    }
}
