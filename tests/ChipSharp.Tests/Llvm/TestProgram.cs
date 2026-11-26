using ChipSharp.Ir;

namespace ChipSharp.Tests.Llvm;

/// <summary>A void Main that prints through the runtime's console functions, which it declares as it uses them.</summary>
internal sealed class TestProgram
{
    private readonly Dictionary<string, TirFunction> _runtime = [];

    public TestProgram(string name)
    {
        Module = new TirModule(name);
        Main = Module.AddFunction("Program.Main", TirType.Void);
        Module.EntryPoint = Main;
        Ir = new TirBuilder(Main.AddBlock("entry"));
    }

    public TirModule Module { get; }

    public TirFunction Main { get; }

    /// <summary>Appends to Main; move it to another block of Main to continue there.</summary>
    public TirBuilder Ir { get; }

    public void WriteLineInt32(TirValue value) => Call("WriteLineInt32", value);
    public void WriteLineUInt32(TirValue value) => Call("WriteLineUInt32", value);
    public void WriteLineInt64(TirValue value) => Call("WriteLineInt64", value);
    public void WriteLineUInt64(TirValue value) => Call("WriteLineUInt64", value);

    /// <summary>The runtime takes a bool as an int, 0 or 1.</summary>
    public void WriteLineBool(TirValue value) => Call("WriteLineBool", Ir.Convert(value, TirType.I32));

    /// <summary>Ends Main where the builder is and returns the module.</summary>
    public TirModule Finish()
    {
        Ir.Return();
        return Module;
    }

    private void Call(string name, TirValue value)
    {
        if (!_runtime.TryGetValue(name, out var function))
        {
            function = Module.AddExternalFunction($"ChipSharp.Runtime.{name}", TirType.Void, ("value", value.Type));
            _runtime[name] = function;
        }

        Ir.Call(function, value);
    }
}
