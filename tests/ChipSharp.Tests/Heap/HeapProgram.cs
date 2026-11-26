using ChipSharp.Ir;

namespace ChipSharp.Tests.Heap;

/// <summary>
/// A TIR program that makes objects, built by hand: a void <c>Test.Main</c>, the classes <c>System.Object</c> and
/// <c>Test.Node</c> (a Value, and two references, Next and Other), and the runtime functions it calls (Console.WriteLine
/// of numbers, the GC methods), declared as they are used.
/// </summary>
internal sealed class HeapProgram
{
    public const int ValueField = 1;
    public const int NextField = 2;
    public const int OtherField = 3;

    private readonly Dictionary<string, TirFunction> _runtime = [];

    public HeapProgram(string name)
    {
        Module = new TirModule(name);
        Object = Module.AddClass("System.Object", TirTypeDescriptor.ObjectStruct("System.Object"));
        NodeType = TirTypeDescriptor.ObjectStruct("Test.Node", ("Value", TirType.I32), ("Next", TirType.Ref),
            ("Other", TirType.Ref));
        Node = Module.AddClass("Test.Node", NodeType, Object);
        Main = Module.AddFunction("Test.Main", TirType.Void);
        Module.EntryPoint = Main;
        Ir = new TirBuilder(Main.AddBlock("entry"));
    }

    public TirModule Module { get; }

    public TirTypeDescriptor Object { get; }

    /// <summary>The object type of <see cref="Node"/>: { ptr &lt;type&gt;, i32 Value, ref Next, ref Other }.</summary>
    public TirStructType NodeType { get; }

    public TirTypeDescriptor Node { get; }

    public TirFunction Main { get; }

    /// <summary>Appends to Main (move it to another block, or another function's, to continue there).</summary>
    public TirBuilder Ir { get; }

    /// <summary>A new node with <paramref name="value"/>, and <paramref name="next"/> unless it is null.</summary>
    public TirValue NewNode(TirBuilder ir, TirValue value, TirValue? next = null)
    {
        var node = ir.NewObject(Node);
        ir.Store(ir.FieldAddress(node, NodeType, ValueField), value);
        if (next is not null)
        {
            ir.Store(ir.FieldAddress(node, NodeType, NextField), next);
        }

        return node;
    }

    public TirValue Value(TirBuilder ir, TirValue node) => ir.Load(TirType.I32, ir.FieldAddress(node, NodeType, ValueField));

    public TirValue Next(TirBuilder ir, TirValue node) => ir.Load(TirType.Ref, ir.FieldAddress(node, NodeType, NextField));

    public void SetField(TirBuilder ir, TirValue node, int field, TirValue value) =>
        ir.Store(ir.FieldAddress(node, NodeType, field), value);

    /// <summary>Console.WriteLine of an i32 or an i64.</summary>
    public void Print(TirBuilder ir, TirValue value) =>
        ir.Call(Runtime(value.Type == TirType.I64 ? "WriteLineInt64" : "WriteLineInt32", TirType.Void, value.Type), value);

    /// <summary>Prints how many collections ran, the bytes made, the bytes in use, and the heap's size, a line each.</summary>
    public void PrintStatistics(TirBuilder ir)
    {
        Print(ir, ir.Call(Runtime("GcCollectionCount", TirType.I32)));
        Print(ir, ir.Call(Runtime("GcTotalAllocatedBytes", TirType.I64)));
        Print(ir, ir.Call(Runtime("GcTotalMemory", TirType.I64)));
        Print(ir, ir.Call(Runtime("GcHeapSize", TirType.I64)));
    }

    public void Collect(TirBuilder ir) => ir.Call(Runtime("GcCollect", TirType.Void));

    /// <summary>The runtime function ChipSharp.Runtime.<paramref name="name"/>, declared once.</summary>
    public TirFunction Runtime(string name, TirType returns, params TirType[] parameters)
    {
        if (!_runtime.TryGetValue(name, out var function))
        {
            function = Module.AddExternalFunction($"ChipSharp.Runtime.{name}", returns,
                parameters.Select((type, i) => ($"value{i}", type)));
            _runtime[name] = function;
        }

        return function;
    }

    /// <summary>
    /// <c>for (i = 0; i &lt; count; i++) body(i)</c> where the builder is; afterwards it is in the block after the loop.
    /// <paramref name="body"/> reads i with the function it is given, in whatever block it is in then.
    /// </summary>
    public static void For(TirBuilder ir, string name, TirValue count, Action<Func<TirValue>> body)
    {
        var function = ir.Function;
        var counter = function.AddSlot(name + "_i", TirType.I32);
        var limit = function.AddSlot(name + "_limit", TirType.I32);
        var test = function.AddBlock(name + "_test");
        var loop = function.AddBlock(name + "_body");
        var done = function.AddBlock(name + "_done");
        ir.Store(counter, ir.Const(TirType.I32, 0));
        ir.Store(limit, count);
        ir.Jump(test);

        ir.Block = test;
        ir.Branch(ir.Compare(TirCompareOp.Lt, ir.Load(counter), ir.Load(limit)), loop, done);

        ir.Block = loop;
        body(() => ir.Load(counter));
        ir.Store(counter, ir.Binary(TirBinaryOp.Add, ir.Load(counter), ir.Const(TirType.I32, 1)));
        ir.Jump(test);

        ir.Block = done;
    }

    public static void For(TirBuilder ir, string name, int count, Action<Func<TirValue>> body) =>
        For(ir, name, ir.Const(TirType.I32, count), body);

    /// <summary>Ends Main where the builder is and returns the module.</summary>
    public TirModule Finish()
    {
        Ir.Return();
        return Module;
    }
}
