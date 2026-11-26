using System.Text.RegularExpressions;
using ChipSharp.Ir;
using ChipSharp.Tests.Tir;

namespace ChipSharp.Tests.Llvm;

/// <summary>TIR the LLVM tests share: for golden text, for clang to compile, and for programs that run.</summary>
internal static class LlvmSamples
{
    /// <summary>Every TIR type a value can have.</summary>
    public static readonly TirType[] ValueTypes =
    [
        TirType.Bool, TirType.I8, TirType.I16, TirType.I32, TirType.I64, TirType.NativeInt, TirType.F32, TirType.F64,
    ];

    public static readonly TirType[] IntegerTypes = [.. ValueTypes.Where(t => t.IsInteger)];

    public static readonly TirBinaryOp[] FloatBinaryOps =
        [TirBinaryOp.Add, TirBinaryOp.Sub, TirBinaryOp.Mul, TirBinaryOp.Div, TirBinaryOp.Rem];

    public static readonly TirCompareOp[] FloatCompareOps =
        [TirCompareOp.Eq, TirCompareOp.Ne, TirCompareOp.Lt, TirCompareOp.Le, TirCompareOp.Gt, TirCompareOp.Ge];

    /// <summary><c>static int Square(int n) => n * n;</c></summary>
    public static TirFunction AddSquare(TirModule module)
    {
        var square = module.AddFunction("Program.Square", TirType.I32, ("n", TirType.I32));
        var b = new TirBuilder(square.AddBlock("entry"));
        var n = square.Parameters[0];
        b.Return(b.Binary(TirBinaryOp.Mul, n, n));
        return square;
    }

    /// <summary><c>static void Main() => Console.WriteLine(Square(42));</c>, the print being the runtime's.</summary>
    public static TirModule SquareProgram()
    {
        var module = new TirModule("Demo");
        var square = AddSquare(module);
        var main = module.AddFunction("Program.Main", TirType.Void);
        var writeLine = module.AddExternalFunction("ChipSharp.Runtime.WriteLineInt32", TirType.Void,
            ("value", TirType.I32));
        module.EntryPoint = main;

        var b = new TirBuilder(main.AddBlock("entry"));
        b.Call(writeLine, b.Call(square, b.Const(TirType.I32, 42)));
        b.Return();
        return module;
    }

    /// <summary>A void function of two parameters of one type that applies each operation to them.</summary>
    /// <param name="keep">
    /// Stores every result into a slot, so that a compile at -O0 still has to make code for each operation
    /// (an unused result is dropped even there).
    /// </param>
    public static TirFunction AddBinaryOps(TirModule module, string name, TirType type, IEnumerable<TirBinaryOp> ops,
        bool keep = false)
    {
        var function = module.AddFunction(name, TirType.Void, ("a", type), ("b", type));
        var result = keep ? function.AddSlot("result", type) : null;
        var b = new TirBuilder(function.AddBlock("entry"));
        foreach (var op in ops)
        {
            var value = b.Binary(op, function.Parameters[0], function.Parameters[1]);
            if (result is not null)
            {
                b.Store(result, value);
            }
        }

        b.Return();
        return function;
    }

    /// <summary>A void function of two parameters of one type that compares them with each operation.</summary>
    /// <param name="keep">Stores every result into a slot, as in <see cref="AddBinaryOps"/>.</param>
    public static TirFunction AddCompares(TirModule module, string name, TirType type, IEnumerable<TirCompareOp> ops,
        bool keep = false)
    {
        var function = module.AddFunction(name, TirType.Void, ("a", type), ("b", type));
        var result = keep ? function.AddSlot("result", TirType.Bool) : null;
        var b = new TirBuilder(function.AddBlock("entry"));
        foreach (var op in ops)
        {
            var value = b.Compare(op, function.Parameters[0], function.Parameters[1]);
            if (result is not null)
            {
                b.Store(result, value);
            }
        }

        b.Return();
        return function;
    }

    /// <summary><c>Program.Convert</c>: returns its parameter converted to <paramref name="to"/>.</summary>
    public static TirFunction AddConvert(TirModule module, TirType from, TirType to, bool unsigned)
    {
        var function = module.AddFunction("Program.Convert", to, ("x", from));
        var b = new TirBuilder(function.AddBlock("entry"));
        b.Return(b.Convert(function.Parameters[0], to, unsigned));
        return function;
    }

    /// <summary>One slot per type, and a constant stored into each.</summary>
    public static TirFunction AddConstants(TirModule module)
    {
        var function = module.AddFunction("Program.Constants", TirType.Void);
        var b = new TirBuilder(function.AddBlock("entry"));
        void Store(string slot, TirConst value) => b.Store(function.AddSlot(slot, value.Type), value);

        Store("flag", b.Const(true));
        Store("b", b.Const(TirType.I8, 255));
        Store("s", b.Const(TirType.I16, short.MinValue));
        Store("i", b.Const(TirType.I32, uint.MaxValue));
        Store("l", b.Const(TirType.I64, long.MinValue));
        Store("n", b.Const(TirType.NativeInt, -5));
        Store("f", b.Const(TirType.F32, 0.1));
        Store("f2", b.Const(TirType.F32, 1.5));
        Store("d", b.Const(TirType.F64, 0.1));
        Store("negativeZero", b.Const(TirType.F64, -0.0));
        Store("infinity", b.Const(TirType.F64, double.PositiveInfinity));
        b.Return();
        return function;
    }

    /// <summary><c>static long Native(nint a, int x) { nint n = a + x + -5; return n; }</c></summary>
    public static TirFunction AddNative(TirModule module)
    {
        var function = module.AddFunction("Program.Native", TirType.I64, ("a", TirType.NativeInt), ("x", TirType.I32));
        var (a, x) = (function.Parameters[0], function.Parameters[1]);
        var n = function.AddSlot("n", TirType.NativeInt);
        var b = new TirBuilder(function.AddBlock("entry"));
        var sum = b.Binary(TirBinaryOp.Add, a, b.Convert(x, TirType.NativeInt));
        b.Store(n, b.Binary(TirBinaryOp.Add, sum, b.Const(TirType.NativeInt, -5)));
        b.Return(b.Convert(b.Load(n), TirType.I64));
        return function;
    }

    /// <summary>
    /// <c>static int CountTo(int n) { int count = 0; do count++; while (count &lt; n); return count; }</c>, with the loop
    /// at the very first block, so the TIR entry block is a jump target. It relies on the slot starting at zero.
    /// </summary>
    public static TirFunction AddCountTo(TirModule module)
    {
        var function = module.AddFunction("Program.CountTo", TirType.I32, ("n", TirType.I32));
        var count = function.AddSlot("count", TirType.I32);
        var entry = function.AddBlock("entry");
        var done = function.AddBlock("done");

        var b = new TirBuilder(entry);
        var next = b.Binary(TirBinaryOp.Add, b.Load(count), b.Const(TirType.I32, 1));
        b.Store(count, next);
        b.Branch(b.Compare(TirCompareOp.Lt, next, function.Parameters[0]), entry, done);

        b.Block = done;
        b.Return(b.Load(count));
        return function;
    }

    /// <summary>Names that only become unique LLVM names with help: clashing parameters, a block named like a temporary.</summary>
    public static TirFunction AddNames(TirModule module)
    {
        var function = module.AddFunction("Program.Names", TirType.I32,
            ("a.b", TirType.I32), ("a_b", TirType.I32), ("1x", TirType.I32));
        var slot = function.AddSlot("a.b", TirType.I32);
        var first = function.AddBlock("allocas");
        var t0 = function.AddBlock("t0");
        var last = function.AddBlock("1st");

        var b = new TirBuilder(first);
        b.Store(slot, b.Binary(TirBinaryOp.Add, function.Parameters[0], function.Parameters[1]));
        b.Jump(t0);

        b.Block = t0;
        b.Jump(last);

        b.Block = last;
        b.Return(b.Binary(TirBinaryOp.Add, b.Load(slot), function.Parameters[2]));
        return function;
    }

    /// <summary>Every type a slot, a global, a load or a store can have: the value types, ptr, structs and arrays.</summary>
    public static readonly TirType[] MemoryTypes =
    [
        .. ValueTypes, TirType.Ptr, TirMemorySamples.Point, TirMemorySamples.Line, TirMemorySamples.TenInts,
        new TirArrayType(TirMemorySamples.Point, 3), new TirArrayType(new TirArrayType(TirType.Bool, 3), 2),
    ];

    /// <summary>
    /// A global of every memory type (scalars with a value, the rest zero) and <c>Program.Accesses</c>, which for
    /// each type takes the address of a slot and of the global and loads and stores through them, plainly and
    /// volatile, then stores zero of the type.
    /// </summary>
    public static TirFunction AddAccesses(TirModule module)
    {
        var function = module.AddFunction("Program.Accesses", TirType.Void);
        var b = new TirBuilder(function.AddBlock("entry"));
        for (var i = 0; i < MemoryTypes.Length; i++)
        {
            var type = MemoryTypes[i];
            var initial = type.IsInteger || type == TirType.Bool ? new TirConst(type, 1)
                : type.IsFloat ? new TirConst(type, 1.5)
                : null;
            var global = module.AddGlobal($"Program.global{i}", type, initial);
            var slot = function.AddSlot($"local{i}", type);

            var globalAddress = b.AddressOf(global);
            var slotAddress = b.AddressOf(slot);
            b.Store(slotAddress, b.Load(type, globalAddress));
            b.Store(globalAddress, b.Load(type, slotAddress, isVolatile: true), isVolatile: true);
            b.Store(slotAddress, b.Zero(type));
        }

        b.Return();
        return function;
    }

    /// <summary>
    /// <c>Program.Addresses</c>: field addresses into nested structs, element addresses with each index type,
    /// pointer comparisons, and ptrtoint and inttoptr with every integer type, each result kept in a slot.
    /// </summary>
    public static TirFunction AddAddresses(TirModule module)
    {
        var function = module.AddFunction("Program.Addresses", TirType.Void, ("p", TirType.Ptr));
        var p = function.Parameters[0];
        var lines = function.AddSlot("lines", new TirArrayType(TirMemorySamples.Line, 4));
        var keep = function.AddSlot("keep", TirType.Ptr);
        var same = function.AddSlot("same", TirType.Bool);
        var b = new TirBuilder(function.AddBlock("entry"));

        var linesAddress = b.AddressOf(lines);
        foreach (var index in new TirValue[]
                 {
                     b.Const(TirType.I32, 1), b.Const(TirType.I64, 2), b.Const(TirType.NativeInt, 3),
                 })
        {
            var line = b.ElementAddress(linesAddress, TirMemorySamples.Line, index);
            var to = b.FieldAddress(line, TirMemorySamples.Line, "To");
            b.Store(b.FieldAddress(to, TirMemorySamples.Point, "Y"), b.Const(TirType.I32, 7));
            b.Store(b.FieldAddress(line, TirMemorySamples.Line, "Color"), b.Const(TirType.I8, 3));
            b.Store(keep, b.ElementAddress(p, TirType.I16, index));
        }

        foreach (var op in new[] { TirCompareOp.Eq, TirCompareOp.Ne, TirCompareOp.LtUnsigned, TirCompareOp.LeUnsigned,
                     TirCompareOp.GtUnsigned, TirCompareOp.GeUnsigned })
        {
            b.Store(same, b.Compare(op, p, linesAddress));
        }

        foreach (var type in IntegerTypes)
        {
            var number = function.AddSlot($"number{type.Name}", type);
            b.Store(number, b.PtrToInt(p, type));
            b.Store(keep, b.IntToPtr(b.Load(number)));
        }

        b.Return();
        return function;
    }

    /// <summary>One module with every sample and every instruction kind on every type, for clang to compile.</summary>
    public static TirModule Everything()
    {
        var module = SquareProgram();
        TirSamples.AddMax(module);
        TirSamples.AddSum(module);
        AddConstants(module);
        AddNative(module);
        AddCountTo(module);
        AddNames(module);
        TirMemorySamples.AddCounter(module);
        TirMemorySamples.AddLengthSquared(module);
        TirMemorySamples.AddSwap(module);
        TirMemorySamples.AddSumOfSquares(module);
        TirMemorySamples.AddRegisterToggle(module);
        AddAccesses(module);
        AddAddresses(module);

        AddBinaryOps(module, "Program.Bools", TirType.Bool, [TirBinaryOp.And, TirBinaryOp.Or, TirBinaryOp.Xor], keep: true);
        AddCompares(module, "Program.Bools", TirType.Bool, [TirCompareOp.Eq, TirCompareOp.Ne], keep: true);
        foreach (var type in IntegerTypes)
        {
            AddBinaryOps(module, "Program.Integers", type, Enum.GetValues<TirBinaryOp>(), keep: true);
            AddCompares(module, "Program.Integers", type, Enum.GetValues<TirCompareOp>(), keep: true);
        }

        foreach (var type in new[] { TirType.F32, TirType.F64 })
        {
            AddBinaryOps(module, "Program.Floats", type, FloatBinaryOps, keep: true);
            AddCompares(module, "Program.Floats", type, Enum.GetValues<TirCompareOp>(), keep: true);
        }

        foreach (var from in ValueTypes)
        {
            foreach (var to in ValueTypes.Where(t => t != TirType.Bool))
            {
                AddConvert(module, from, to, unsigned: false);
                AddConvert(module, from, to, unsigned: true);
            }
        }

        return module;
    }

    /// <summary>The <c>define</c> of one function, from its first line to its closing brace.</summary>
    public static string Define(string ll, string symbol)
    {
        var match = Regex.Match(ll, $@"^define [^\n]* @{Regex.Escape(symbol)}\(.*?\n}}\n",
            RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, $"There is no define for @{symbol} in:\n{ll}");
        return match.Value;
    }

    public static string Text(string text) => text.ReplaceLineEndings("\n");
}
