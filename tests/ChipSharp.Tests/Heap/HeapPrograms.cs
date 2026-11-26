using ChipSharp.Ir;

namespace ChipSharp.Tests.Heap;

/// <summary>
/// The programs the heap and garbage collector tests run, the same on the PC and the Pico: each prints what it checks,
/// one number a line, so that a reference the collector lost shows as a wrong number (its object's memory taken by
/// another object) or as a stop.
/// </summary>
internal static class HeapPrograms
{
    /// <summary>
    /// Objects that live through many collections, each reachable another way, while <paramref name="churn"/> short-lived
    /// nodes (in chains of ten, which die together) fill the heap again and again:
    /// <list type="bullet">
    /// <item>a list of <paramref name="listLength"/> nodes, reachable only from a static field, then from each other;</item>
    /// <item>a binary tree of depth <paramref name="treeDepth"/>, reachable only from a local of Main;</item>
    /// <item>a node reachable only from the other end of a cycle of two, in a static field too.</item>
    /// </list>
    /// Prints: the sum of the list's values, the tree's, the cycle's, then the collections, the bytes made, the bytes in
    /// use and the heap's size (<see cref="HeapProgram.PrintStatistics"/>).
    /// </summary>
    public static TirModule Survivors(int churn, int listLength, int treeDepth)
    {
        var p = new HeapProgram("Survivors");
        var list = p.Module.AddGlobal("Test.list", TirType.Ref);
        var cycle = p.Module.AddGlobal("Test.cycle", TirType.Ref);
        var build = Build(p);
        var sum = Sum(p);
        var churner = Churn(p);

        var ir = p.Ir;
        HeapProgram.For(ir, "i", listLength, i => ir.Store(ir.AddressOf(list), p.NewNode(ir, i(), ir.Load(TirType.Ref, ir.AddressOf(list)))));
        var tree = p.Main.AddSlot("tree", TirType.Ref);
        ir.Store(tree, ir.Call(build, ir.Const(TirType.I32, treeDepth)));
        var a = p.NewNode(ir, ir.Const(TirType.I32, 1000));
        var b = p.NewNode(ir, ir.Const(TirType.I32, 2000), a);
        p.SetField(ir, a, HeapProgram.NextField, b);
        ir.Store(ir.AddressOf(cycle), a);

        ir.Call(churner, ir.Const(TirType.I32, churn));

        p.Print(ir, ir.Call(sum, ir.Load(TirType.Ref, ir.AddressOf(list))));
        p.Print(ir, ir.Call(sum, ir.Load(tree)));
        var first = ir.Load(TirType.Ref, ir.AddressOf(cycle));
        p.Print(ir, ir.Binary(TirBinaryOp.Add, p.Value(ir, first), p.Value(ir, p.Next(ir, first))));
        p.PrintStatistics(ir);
        return p.Finish();
    }

    /// <summary>
    /// Four nodes that only Main's temporaries hold, across a call that makes <paramref name="churn"/> nodes: LLVM keeps
    /// such values in callee-saved registers (rbx, rsi, rdi, ... on the PC; r4-r7 on the Pico), which the collector reads
    /// from where it stored them. Then a recursion <paramref name="depth"/> calls deep in which each call holds a node
    /// across the call below it, the deepest one churning again. Prints the four values, then the sum of the recursion's.
    /// </summary>
    public static TirModule AcrossCalls(int churn, int depth)
    {
        var p = new HeapProgram("AcrossCalls");
        var churner = Churn(p);
        var deep = Deep(p, churner);
        var ir = p.Ir;
        TirValue[] held = [.. new[] { 11111, 22222, 33333, 44444 }.Select(value => p.NewNode(ir, ir.Const(TirType.I32, value)))];
        ir.Call(churner, ir.Const(TirType.I32, churn));
        foreach (var node in held)
        {
            p.Print(ir, p.Value(ir, node));
        }

        p.Print(ir, ir.Call(deep, ir.Const(TirType.I32, depth), ir.Const(TirType.I32, churn)));
        p.PrintStatistics(ir);
        return p.Finish();
    }

    /// <summary>
    /// Objects reachable only through arrays and structs: an array of <paramref name="count"/> nodes in a static field; an
    /// array of Test.Pair structs (a key and a node) in a local; a static Test.Pair; an object of Test.Holder, whose
    /// fields are a Test.Pair and a fixed array of three references; and an int[] of <paramref name="count"/> numbers;
    /// then <paramref name="churn"/> nodes. Prints the sums of the nodes' values through each of them, the int[]'s sum,
    /// the arrays' lengths, then the statistics.
    /// </summary>
    public static TirModule Arrays(int count, int churn)
    {
        var p = new HeapProgram("Arrays");
        var module = p.Module;
        var pair = new TirStructType("Test.Pair", ("Key", TirType.I32), ("Item", TirType.Ref));
        var three = new TirArrayType(TirType.Ref, 3);
        var holderType = TirTypeDescriptor.ObjectStruct("Test.Holder", ("Pair", pair), ("Three", three));
        var holder = module.AddClass("Test.Holder", holderType, p.Object);
        var nodes = module.AddArrayType("Test.Node[]", TirType.Ref);
        nodes.ElementDescriptor = p.Node;
        var pairs = module.AddArrayType("Test.Pair[]", pair);
        var ints = module.AddArrayType("int[]", TirType.I32);
        var nodeArray = module.AddGlobal("Test.nodes", TirType.Ref);
        var staticPair = module.AddGlobal("Test.pair", pair);
        var staticHolder = module.AddGlobal("Test.holder", TirType.Ref);
        var churner = Churn(p);
        var ir = p.Ir;


        // the nodes: element i holds a node of value i
        ir.Store(ir.AddressOf(nodeArray), ir.NewArray(nodes, ir.Const(TirType.I32, count)));
        HeapProgram.For(ir, "fill", count, i =>
        {
            var array = ir.Load(TirType.Ref, ir.AddressOf(nodeArray));
            var index = i();
            ir.Store(ir.ElementAddress(ir.ArrayData(array, TirType.Ref), TirType.Ref, index), p.NewNode(ir, index));
        });

        // the pairs: key i and a node of value 2i, in a local
        var pairArray = p.Main.AddSlot("pairs", TirType.Ref);
        ir.Store(pairArray, ir.NewArray(pairs, ir.Const(TirType.I32, count)));
        HeapProgram.For(ir, "pair", count, i =>
        {
            var index = i();
            var element = ir.ElementAddress(ir.ArrayData(ir.Load(pairArray), pair), pair, index);
            ir.Store(ir.FieldAddress(element, pair, 0), index);
            ir.Store(ir.FieldAddress(element, pair, 1),
                p.NewNode(ir, ir.Binary(TirBinaryOp.Mul, index, ir.Const(TirType.I32, 2))));
        });

        // the static pair and the holder
        ir.Store(ir.FieldAddress(ir.AddressOf(staticPair), pair, 1), p.NewNode(ir, ir.Const(TirType.I32, 9999)));
        var made = ir.NewObject(holder);
        ir.Store(ir.FieldAddress(ir.FieldAddress(made, holderType, 1), pair, 1), p.NewNode(ir, ir.Const(TirType.I32, 100)));
        for (var k = 0; k < 3; k++)
        {
            var slot = ir.ElementAddress(ir.FieldAddress(made, holderType, 2), TirType.Ref, ir.Const(TirType.I32, k));
            ir.Store(slot, p.NewNode(ir, ir.Const(TirType.I32, 1 << (k + 1))));
        }

        ir.Store(ir.AddressOf(staticHolder), made);

        // the numbers: element i is i, in a local
        var numbers = p.Main.AddSlot("numbers", TirType.Ref);
        ir.Store(numbers, ir.NewArray(ints, ir.Const(TirType.I32, count)));
        HeapProgram.For(ir, "numbers", count, i =>
        {
            var index = i();
            ir.Store(ir.ElementAddress(ir.ArrayData(ir.Load(numbers), TirType.I32), TirType.I32, index), index);
        });

        ir.Call(churner, ir.Const(TirType.I32, churn));

        // the sums, read back after the collections
        var sums = p.Main.AddSlot("sums", new TirArrayType(TirType.I32, 3));
        HeapProgram.For(ir, "sum", count, i =>
        {
            var index = i();
            var node = ir.Load(TirType.Ref, ir.ElementAddress(ir.ArrayData(ir.Load(TirType.Ref, ir.AddressOf(nodeArray)),
                TirType.Ref), TirType.Ref, index));
            var element = ir.ElementAddress(ir.ArrayData(ir.Load(pairArray), pair), pair, index);
            var pairSum = ir.Binary(TirBinaryOp.Add, ir.Load(TirType.I32, ir.FieldAddress(element, pair, 0)),
                p.Value(ir, ir.Load(TirType.Ref, ir.FieldAddress(element, pair, 1))));
            var number = ir.Load(TirType.I32, ir.ElementAddress(ir.ArrayData(ir.Load(numbers), TirType.I32), TirType.I32, index));
            TirValue[] parts = [p.Value(ir, node), pairSum, number];
            for (var k = 0; k < 3; k++)
            {
                var total = ir.ElementAddress(ir.AddressOf(sums), TirType.I32, ir.Const(TirType.I32, k));
                ir.Store(total, ir.Binary(TirBinaryOp.Add, ir.Load(TirType.I32, total), parts[k]));
            }
        });

        for (var k = 0; k < 3; k++)
        {
            p.Print(ir, ir.Load(TirType.I32, ir.ElementAddress(ir.AddressOf(sums), TirType.I32, ir.Const(TirType.I32, k))));
        }

        p.Print(ir, p.Value(ir, ir.Load(TirType.Ref, ir.FieldAddress(ir.AddressOf(staticPair), pair, 1))));
        var kept = ir.Load(TirType.Ref, ir.AddressOf(staticHolder));
        TirValue held = p.Value(ir, ir.Load(TirType.Ref, ir.FieldAddress(ir.FieldAddress(kept, holderType, 1), pair, 1)));
        for (var k = 0; k < 3; k++)
        {
            var slot = ir.ElementAddress(ir.FieldAddress(kept, holderType, 2), TirType.Ref, ir.Const(TirType.I32, k));
            held = ir.Binary(TirBinaryOp.Add, held, p.Value(ir, ir.Load(TirType.Ref, slot)));
        }

        p.Print(ir, held);
        p.Print(ir, ir.ArrayLength(ir.Load(TirType.Ref, ir.AddressOf(nodeArray))));
        p.Print(ir, ir.ArrayLength(ir.Load(numbers)));
        p.PrintStatistics(ir);
        return p.Finish();
    }

    /// <summary>
    /// <paramref name="count"/> garbage cycles: two nodes that refer to each other, and each to itself. Then a collection.
    /// Prints the collections and the bytes still in use (none of the cycles), then the statistics.
    /// </summary>
    public static TirModule Cycles(int count)
    {
        var p = new HeapProgram("Cycles");
        var ir = p.Ir;
        HeapProgram.For(ir, "i", count, i =>
        {
            var a = p.NewNode(ir, i());
            var b = p.NewNode(ir, ir.Const(TirType.I32, 1), a);
            p.SetField(ir, a, HeapProgram.NextField, b);
            p.SetField(ir, a, HeapProgram.OtherField, a);
            p.SetField(ir, b, HeapProgram.OtherField, b);
        });
        p.Collect(ir);
        p.PrintStatistics(ir);
        return p.Finish();
    }

    /// <summary>
    /// Nodes that all stay alive, in a list in a static field, until the heap is full: the program stops with .NET's
    /// OutOfMemoryException message. With <paramref name="array"/>, one array of int.MaxValue ints instead, out of memory
    /// at once. Prints 1 first.
    /// </summary>
    public static TirModule OutOfMemory(bool array)
    {
        var p = new HeapProgram(array ? "OutOfMemoryArray" : "OutOfMemory");
        var ir = p.Ir;
        p.Print(ir, ir.Const(TirType.I32, 1));
        if (array)
        {
            p.Print(ir, ir.ArrayLength(ir.NewArray(p.Module.AddArrayType("int[]", TirType.I32), ir.Const(TirType.I32, int.MaxValue))));
            return p.Finish();
        }

        var list = p.Module.AddGlobal("Test.list", TirType.Ref);
        HeapProgram.For(ir, "i", int.MaxValue, i => ir.Store(ir.AddressOf(list), p.NewNode(ir, i(), ir.Load(TirType.Ref, ir.AddressOf(list)))));
        p.Print(ir, ir.Const(TirType.I32, 2));
        return p.Finish();
    }

    /// <summary>A null check of a null node, which <c>Test.Nothing()</c> returns: prints 1, then stops; 2 is never printed.</summary>
    public static TirModule NullReference()
    {
        var p = new HeapProgram("NullReference");
        var nothing = p.Module.AddFunction("Test.Nothing", TirType.Ref);
        var body = new TirBuilder(nothing.AddBlock("entry"));
        body.Return(body.Zero(TirType.Ref));
        var ir = p.Ir;
        p.NewNode(ir, ir.Const(TirType.I32, 7));
        p.Print(ir, ir.Const(TirType.I32, 1));
        var node = ir.Call(nothing);
        ir.NullCheck(node);
        p.Print(ir, p.Value(ir, node));
        p.Print(ir, ir.Const(TirType.I32, 2));
        return p.Finish();
    }

    /// <summary>An array of -1 ints, whose length comes from a function: prints 1, then stops; 2 is never printed.</summary>
    public static TirModule NegativeLength()
    {
        var p = new HeapProgram("NegativeLength");
        var minusOne = p.Module.AddFunction("Test.MinusOne", TirType.I32);
        var body = new TirBuilder(minusOne.AddBlock("entry"));
        body.Return(body.Const(TirType.I32, -1));
        var ir = p.Ir;
        p.Print(ir, ir.Const(TirType.I32, 1));
        p.Print(ir, ir.ArrayLength(ir.NewArray(p.Module.AddArrayType("int[]", TirType.I32), ir.Call(minusOne))));
        p.Print(ir, ir.Const(TirType.I32, 2));
        return p.Finish();
    }

    /// <summary>
    /// For the Pico: the timer interrupt (IRQ 1, as rp2040.json says) after 100 µs, whose handler makes a node, which a
    /// handler never may. Prints 1, then waits; the handler stops the program, so 2 is never printed.
    /// </summary>
    public static TirModule AllocationInInterrupt()
    {
        var p = new HeapProgram("AllocationInInterrupt");
        var module = p.Module;
        var handler = module.AddFunction("<Interrupt timer>", TirType.Void);
        handler.ExportName = "chipsharp_isr_irq1";
        var clear = module.AddExternalFunction("chipsharp_clear_timer_event", TirType.Void);
        var inside = new TirBuilder(handler.AddBlock("entry"));
        inside.Call(clear);
        var made = module.AddGlobal("Test.made", TirType.Ref);
        inside.Store(inside.AddressOf(made), p.NewNode(inside, inside.Const(TirType.I32, 5)));
        inside.Return();

        var ir = p.Ir;
        p.NewNode(ir, ir.Const(TirType.I32, 1)); // the heap is in use before the interrupt comes
        p.Print(ir, ir.Const(TirType.I32, 1));
        ir.Call(p.Runtime("TimerAfterMicroseconds", TirType.Void, TirType.I32), ir.Const(TirType.I32, 100));
        ir.Call(p.Runtime("DelayMilliseconds", TirType.Void, TirType.I32), ir.Const(TirType.I32, 10));
        p.Print(ir, ir.Const(TirType.I32, 2));
        return p.Finish();
    }

    /// <summary>
    /// <c>i32 Test.Deep(i32 depth, i32 churn)</c>: makes a node of value depth, calls itself one level less deep (the
    /// deepest call churns instead), and returns the sum of the values of the nodes of every level, read after the
    /// call.
    /// </summary>
    private static TirFunction Deep(HeapProgram p, TirFunction churner)
    {
        var function = p.Module.AddFunction("Test.Deep", TirType.I32, ("depth", TirType.I32), ("churn", TirType.I32));
        var (depth, churn) = (function.Parameters[0], function.Parameters[1]);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var bottom = function.AddBlock("bottom");
        var deeper = function.AddBlock("deeper");
        ir.Branch(ir.Compare(TirCompareOp.Eq, depth, ir.Const(TirType.I32, 0)), bottom, deeper);

        ir.Block = bottom;
        ir.Call(churner, churn);
        ir.Return(ir.Const(TirType.I32, 0));

        ir.Block = deeper;
        var node = p.NewNode(ir, depth);
        var below = ir.Call(function, ir.Binary(TirBinaryOp.Sub, depth, ir.Const(TirType.I32, 1)), churn);
        ir.Return(ir.Binary(TirBinaryOp.Add, below, p.Value(ir, node)));
        return function;
    }

    /// <summary>The sum of the values a list or tree of nodes holds (through Next and Other).</summary>
    public static long SumOfTree(int depth) => depth == 0 ? 0 : depth + 2 * SumOfTree(depth - 1);

    /// <summary>
    /// <c>ref Test.Build(i32 depth)</c>: a binary tree of nodes, Next and Other, each with its depth as its value; null
    /// for depth 0.
    /// </summary>
    private static TirFunction Build(HeapProgram p)
    {
        var function = p.Module.AddFunction("Test.Build", TirType.Ref, ("depth", TirType.I32));
        var depth = function.Parameters[0];
        var ir = new TirBuilder(function.AddBlock("entry"));
        var leaf = function.AddBlock("leaf");
        var inner = function.AddBlock("inner");
        ir.Branch(ir.Compare(TirCompareOp.Eq, depth, ir.Const(TirType.I32, 0)), leaf, inner);

        ir.Block = leaf;
        ir.Return(ir.Zero(TirType.Ref));

        ir.Block = inner;
        var below = ir.Binary(TirBinaryOp.Sub, depth, ir.Const(TirType.I32, 1));
        var node = p.NewNode(ir, depth, ir.Call(function, below));
        p.SetField(ir, node, HeapProgram.OtherField, ir.Call(function, below));
        ir.Return(node);
        return function;
    }

    /// <summary><c>i32 Test.Sum(ref node)</c>: the values of the node and of every node it reaches through Next and Other.</summary>
    private static TirFunction Sum(HeapProgram p)
    {
        var function = p.Module.AddFunction("Test.Sum", TirType.I32, ("node", TirType.Ref));
        var node = function.Parameters[0];
        var ir = new TirBuilder(function.AddBlock("entry"));
        var none = function.AddBlock("none");
        var some = function.AddBlock("some");
        ir.Branch(ir.Compare(TirCompareOp.Eq, node, ir.Zero(TirType.Ref)), none, some);

        ir.Block = none;
        ir.Return(ir.Const(TirType.I32, 0));

        ir.Block = some;
        var next = ir.Call(function, p.Next(ir, node));
        var other = ir.Call(function, ir.Load(TirType.Ref, ir.FieldAddress(node, p.NodeType, HeapProgram.OtherField)));
        ir.Return(ir.Binary(TirBinaryOp.Add, p.Value(ir, node), ir.Binary(TirBinaryOp.Add, next, other)));
        return function;
    }

    /// <summary>
    /// <c>void Test.Churn(i32 count)</c>: makes count nodes that soon are garbage, in chains of ten (each links to the one
    /// before it), each with its number as its value.
    /// </summary>
    public static TirFunction Churn(HeapProgram p)
    {
        var function = p.Module.AddFunction("Test.Churn", TirType.Void, ("count", TirType.I32));
        var ir = new TirBuilder(function.AddBlock("entry"));
        var last = function.AddSlot("last", TirType.Ref);
        HeapProgram.For(ir, "i", function.Parameters[0], i =>
        {
            var fresh = function.AddBlock($"fresh");
            var chain = function.AddBlock($"chain");
            var next = function.AddBlock($"next");
            var number = i();
            var node = p.NewNode(ir, number, ir.Load(last));
            ir.Store(last, node);
            ir.Branch(ir.Compare(TirCompareOp.Eq, ir.Binary(TirBinaryOp.RemUnsigned, number, ir.Const(TirType.I32, 10)),
                ir.Const(TirType.I32, 9)), fresh, chain);

            ir.Block = fresh;
            ir.Store(last, ir.Zero(TirType.Ref)); // the chain of ten is garbage now
            ir.Jump(next);

            ir.Block = chain;
            ir.Jump(next);

            ir.Block = next;
        });
        ir.Return();
        return function;
    }
}
