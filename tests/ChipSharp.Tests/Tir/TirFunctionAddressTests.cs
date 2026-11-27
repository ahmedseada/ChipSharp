using ChipSharp.Backend.Llvm;
using ChipSharp.Ir;
using ChipSharp.Tests.Llvm;

namespace ChipSharp.Tests.Tir;

/// <summary>
/// The address of a function (<see cref="TirFunctionAddress"/>), what a delegate holds and <see cref="TirCallIndirect"/>
/// calls: how it prints, what the verifier refuses (an external function's, another module's), and the LLVM it becomes.
/// </summary>
public sealed class TirFunctionAddressTests
{
    private readonly TirModule _module = TirSamples.WithMain(new TirModule("Test"));

    /// <summary><c>i32 Program.Call(i32 %x)</c>: calls Program.Twice through its address.</summary>
    private TirFunction AddCall()
    {
        var twice = _module.AddFunction("Program.Twice", TirType.I32, ("x", TirType.I32));
        var body = new TirBuilder(twice.AddBlock("entry"));
        body.Return(body.Binary(TirBinaryOp.Add, twice.Parameters[0], twice.Parameters[0]));

        var call = _module.AddFunction("Program.Call", TirType.I32, ("x", TirType.I32));
        var ir = new TirBuilder(call.AddBlock("entry"));
        ir.Return(ir.CallIndirect(TirType.I32, ir.AddressOf(twice), call.Parameters[0]));
        return call;
    }

    [Fact]
    public void A_function_address_prints_with_the_function_it_names()
    {
        var call = AddCall();

        Assert.Empty(TirVerifier.FindErrors(_module));
        Assert.Equal("""
            function i32 Program.Call(i32 %x)
            entry:
              %0 = funcaddr Program.Twice
              %1 = call indirect i32 %0(%x)
              return %1

            """.ReplaceLineEndings("\n"), TirPrinter.Print(call));
    }

    [Fact]
    public void Only_a_defined_function_of_the_module_has_an_address()
    {
        var external = _module.AddExternalFunction("ChipSharp.Runtime.WriteLineInt32", TirType.Void, ("value", TirType.I32));
        var other = new TirModule("Other").AddFunction("Other.F", TirType.Void);
        var function = _module.AddFunction("Program.F", TirType.Void);
        var ir = new TirBuilder(function.AddBlock("entry"));
        ir.AddressOf(external);
        ir.AddressOf(other);
        ir.Return();

        Assert.Equal(
        [
            "Function 'Program.F', block 'entry', '%0 = funcaddr ChipSharp.Runtime.WriteLineInt32': takes the address of " +
                "'ChipSharp.Runtime.WriteLineInt32', an external function; only a defined function's address can be taken " +
                "(call the external from one).",
            "Function 'Program.F', block 'entry', '%1 = funcaddr Other.F': takes the address of 'Other.F', which is not a " +
                "function of module 'Test'.",
        ], TirVerifier.FindErrors(_module));
    }

    [Fact]
    public void LLVM_calls_the_function_by_its_symbol()
    {
        AddCall();

        var ll = Assert.Single(new LlvmCodeGenerator().Generate(_module, LlvmTargets.Host).Files).Content;

        // the address is the symbol itself: an indirect call of it is a call of the function, which LLVM sees through
        Assert.Contains("  %t0 = call i32 @Program_Twice(i32 %p.x)\n", ll, StringComparison.Ordinal);
    }
}
