using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Ir;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Tests.Llvm;
using ChipSharp.Tests.Tir;
using static ChipSharp.Tests.Llvm.LlvmSamples;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// What the code generator writes on an AVR so that LLVM 22 compiles it right: a copy stays the program's own call of
/// the runtime's memcpy or memmove (which read flash through bit 15), and LLVM makes none of its own; memset, which
/// takes a C int of 16 bits there, is LLVM's llvm.memset; and a function that passes arguments on the stack has a frame
/// pointer, so that no call sets the stack up through r31:r30. Nothing of it on another target.
/// </summary>
public sealed class AvrCallTests
{
    private const string NoBuiltins = "\"no-builtin-memcpy\" \"no-builtin-memmove\"";

    private static readonly LlvmCodeGenerator Generator = new();

    [Fact]
    public void Every_function_says_memcpy_and_memmove_are_no_builtins_and_so_do_their_declarations()
    {
        var ll = Generate(Copies(), AvrCode.Uno());

        Assert.Contains("declare ptr @memmove(ptr, ptr, i16) nounwind nobuiltin\n", ll, StringComparison.Ordinal);
        Assert.Contains("declare ptr @memcpy(ptr, ptr, i16) nounwind nobuiltin\n", ll, StringComparison.Ordinal);
        Assert.Contains($"define internal ptr @Program_Copy(ptr %p.to, ptr %p.from) nounwind {NoBuiltins} {{\n", ll,
            StringComparison.Ordinal);
        Assert.Contains($"define i32 @chipsharp_entry() nounwind {NoBuiltins} {{\n", ll, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_target_and_an_avr_whose_constants_are_in_ram_keep_the_builtins()
    {
        foreach (var target in new[] { LlvmTargets.Pico, AvrCode.Uno(("separateFlash", "false")) })
        {
            var ll = Generate(Copies(), target);

            Assert.DoesNotContain("builtin", ll, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_memset_is_llvm_memset_of_a_byte_whose_result_is_the_destination()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var memset = module.AddExternalFunction("memset", TirType.Ptr, ("destination", TirType.Ptr), ("value", TirType.I32),
            ("count", TirType.NativeInt));
        var clear = module.AddFunction("Program.Clear", TirType.Ptr, ("buffer", TirType.Ptr));
        var b = new TirBuilder(clear.AddBlock("entry"));
        b.Return(b.Call(memset, clear.Parameters[0], b.Const(TirType.I32, 0x1234), b.Const(TirType.NativeInt, 40)));

        var uno = Define(Generate(module, AvrCode.Uno()), "Program_Clear");
        var pico = Define(Generate(module, LlvmTargets.Pico), "Program_Clear");

        Assert.Contains(Text("""
              %t0 = trunc i32 4660 to i8
              call void @llvm.memset.p0.i16(ptr %p.buffer, i8 %t0, i16 40, i1 false)
              ret ptr %p.buffer
            """), uno, StringComparison.Ordinal);
        Assert.Contains("call ptr @memset(ptr %p.buffer, i32 4660, i32 40)", pico, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_that_passes_arguments_on_the_stack_gives_its_function_a_byte_of_stack()
    {
        // r25 down to r8: 18 bytes of arguments, each rounded up to even; a result of more than 8 bytes takes 2 more
        var big = new TirStructType("Program.Big", ("A", TirType.I64), ("B", TirType.I16));
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var eighteen = Caller(module, "Eighteen", TirType.I64, TirType.I64, TirType.I64, TirType.I16);
        var twenty = Caller(module, "Twenty", TirType.I64, TirType.I64, TirType.I64, TirType.I32);
        var odd = Caller(module, "Odd", TirType.I32, TirType.I64, TirType.I32, TirType.I8, TirType.I8, TirType.I8, TirType.I8);
        var returned = Caller(module, "Returned", big, TirType.I64, TirType.I64);
        var returnedMore = Caller(module, "ReturnedMore", big, TirType.I64, TirType.I64, TirType.I8);
        var frame = "  %avr.frame = alloca i8, align 1\n  store volatile i8 0, ptr %avr.frame, align 1\n";

        var uno = Generate(module, AvrCode.Uno());
        var pico = Generate(module, LlvmTargets.Pico);

        Assert.DoesNotContain(frame, Define(uno, eighteen), StringComparison.Ordinal);
        Assert.Contains(frame, Define(uno, twenty), StringComparison.Ordinal);
        Assert.Contains(frame, Define(uno, odd), StringComparison.Ordinal); // 16 bytes, but 8 + 4 + 2 + 2 + 2 + 2
        Assert.DoesNotContain(frame, Define(uno, returned), StringComparison.Ordinal); // 2 + 16
        Assert.Contains(frame, Define(uno, returnedMore), StringComparison.Ordinal); // 2 + 16 + 2
        Assert.DoesNotContain("avr.frame", pico, StringComparison.Ordinal);
    }

    /// <summary>
    /// The double printed as 2.6E-266 (chipsharp_float_text.c had a call of 22 bytes of arguments): LLVM kept the high
    /// half of the double in r31:r30 and set the stack up through them. With its frame, the function stores the
    /// arguments through Y, and no call sets the stack pointer through Z.
    /// </summary>
    [AvrFact]
    public async Task No_call_of_a_function_with_its_byte_of_stack_sets_the_stack_pointer_through_Z()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var sink = module.AddExternalFunction("Program.Sink", TirType.I32, ("negative", TirType.I32),
            ("biased", TirType.I16), ("allOnes", TirType.I16), ("low", TirType.I32), ("high", TirType.I32),
            ("fraction", TirType.I16), ("precision", TirType.I16), ("text", TirType.Ptr));
        var text = module.AddFunction("Program.Text", TirType.I32, ("bits", TirType.I64), ("text", TirType.Ptr));
        var b = new TirBuilder(text.AddBlock("entry"));
        var high = b.Convert(b.Binary(TirBinaryOp.ShrUnsigned, text.Parameters[0], b.Const(TirType.I64, 32)), TirType.I32);
        b.Return(b.Call(sink, b.Binary(TirBinaryOp.ShrUnsigned, high, b.Const(TirType.I32, 31)),
            b.Convert(b.Binary(TirBinaryOp.And, b.Binary(TirBinaryOp.ShrUnsigned, high, b.Const(TirType.I32, 20)),
                b.Const(TirType.I32, 0x7FF)), TirType.I16),
            b.Const(TirType.I16, 0x7FF), b.Convert(text.Parameters[0], TirType.I32),
            b.Binary(TirBinaryOp.And, high, b.Const(TirType.I32, 0xFFFFF)), b.Const(TirType.I16, 52),
            b.Const(TirType.I16, 17), text.Parameters[1]));
        text.ExportName = "chipsharp_test_text";

        var assembly = await Assembly(Generate(module, AvrCode.Uno()), "Text");

        Assert.Contains("call\tProgram_Sink", assembly, StringComparison.Ordinal);
        Assert.DoesNotContain("in\tr30, 61", assembly, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runtime's C, the shared files a chip's runtime adds too (the console, the text of floats), makes no call that
    /// sets the stack up through Z: none passes arguments on the stack from a function without a frame pointer.
    /// </summary>
    [AvrFact]
    public async Task No_function_of_the_runtime_sets_the_stack_pointer_through_Z()
    {
        GeneratedFile[] files =
        [
            .. AvrCoreProgram.RuntimeFiles, Rp2040Runtime.Source(Rp2040Runtime.ConsoleFileName),
            Rp2040Runtime.Source(Rp2040Runtime.FloatTextFileName),
        ];
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var file in files)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, file.FileName), file.Content);
            }

            var sources = files.Where(file => file.FileName.EndsWith(".c", StringComparison.Ordinal)).ToList();
            var results = await Task.WhenAll(sources.Select(file => LlvmTools.RunAsync(AvrTools.Zig.FindZig(),
                ["cc", .. AvrCoreProgram.CompileFlags, "-Wno-unused-command-line-argument", "-S", file.FileName, "-o",
                    file.FileName + ".s"], folder)));
            Assert.All(results, result => Assert.True(result.ExitCode == 0, result.ToString()));
            Assert.All(sources, file => Assert.DoesNotContain("in\tr30, 61",
                File.ReadAllText(Path.Combine(folder, file.FileName + ".s")), StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A loop that copies a table in flash into RAM stays a loop of LPM: LLVM's memcpy would read RAM.</summary>
    [AvrFact]
    public async Task A_loop_that_copies_a_table_in_flash_is_no_call_of_memcpy()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var table = module.AddArray("Program.Table", new TirArrayType(TirType.I8, 40),
            [.. Enumerable.Range(0, 40).Select(i => new TirConst(TirType.I8, i * 3))], isConstant: true);
        var copy = module.AddFunction("Program.Copy", TirType.Void, ("to", TirType.Ptr), ("count", TirType.I32));
        var i = copy.AddSlot("i", TirType.I32);
        var (entry, test, body, done) = (copy.AddBlock("entry"), copy.AddBlock("test"), copy.AddBlock("body"),
            copy.AddBlock("done"));
        var b = new TirBuilder(entry);
        b.Jump(test);
        b.Block = test;
        b.Branch(b.Compare(TirCompareOp.Lt, b.Load(i), copy.Parameters[1]), body, done);
        b.Block = body;
        var value = b.Load(TirType.I8, b.ElementAddress(b.AddressOf(table), TirType.I8, b.Load(i)));
        b.Store(b.ElementAddress(copy.Parameters[0], TirType.I8, b.Load(i)), value);
        b.Store(i, b.Binary(TirBinaryOp.Add, b.Load(i), b.Const(TirType.I32, 1)));
        b.Jump(test);
        b.Block = done;
        b.Return();
        copy.ExportName = "chipsharp_test_copy";

        var assembly = await Assembly(Generate(module, AvrCode.Uno()), "Copy");

        Assert.DoesNotContain("memcpy", assembly, StringComparison.Ordinal);
        Assert.Contains("lpm", assembly, StringComparison.Ordinal);
    }

    /// <summary>A program with copies: Program.Copy calls memmove and memcpy, which the frontend declares so.</summary>
    private static TirModule Copies()
    {
        var module = TirSamples.WithMain(new TirModule("Demo"));
        var memmove = module.AddExternalFunction("memmove", TirType.Ptr, ("destination", TirType.Ptr),
            ("source", TirType.Ptr), ("count", TirType.NativeInt));
        var memcpy = module.AddExternalFunction("memcpy", TirType.Ptr, ("destination", TirType.Ptr),
            ("source", TirType.Ptr), ("count", TirType.NativeInt));
        var copy = module.AddFunction("Program.Copy", TirType.Ptr, ("to", TirType.Ptr), ("from", TirType.Ptr));
        var b = new TirBuilder(copy.AddBlock("entry"));
        b.Call(memcpy, copy.Parameters[0], copy.Parameters[1], b.Const(TirType.NativeInt, 4));
        b.Return(b.Call(memmove, copy.Parameters[0], copy.Parameters[1], b.Const(TirType.NativeInt, 12)));
        return module;
    }

    /// <summary>
    /// Program.&lt;name&gt;, which calls a function that takes <paramref name="parameters"/>: an external one, or one of
    /// the module's for a struct it returns.
    /// </summary>
    private static string Caller(TirModule module, string name, TirType result, params TirType[] parameters)
    {
        (string, TirType)[] named = [.. parameters.Select((type, i) => ("p" + i, type))];
        var callee = result is TirStructType
            ? module.AddFunction("Program.Callee" + name, result, named)
            : module.AddExternalFunction("Program.Callee" + name, result, named);
        if (result is TirStructType)
        {
            var cb = new TirBuilder(callee.AddBlock("entry"));
            cb.Return(cb.Zero(result));
        }

        var caller = module.AddFunction("Program." + name, TirType.Void);
        var b = new TirBuilder(caller.AddBlock("entry"));
        b.Call(callee, [.. parameters.Select(type => (TirValue)b.Zero(type))]);
        b.Return();
        return "Program_" + name;
    }

    /// <summary>The AVR assembly Zig's clang makes of <paramref name="ll"/>, as the toolchain compiles it (-Os).</summary>
    private static async Task<string> Assembly(string ll, string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".ll"), ll);
            var result = await LlvmTools.RunAsync(AvrTools.Zig.FindZig(),
                ["cc", .. AvrCode.Atmega328P, "-Os", "-Wno-override-module", "-S", name + ".ll", "-o", name + ".s"], folder);
            Assert.True(result.ExitCode == 0, $"zig cc {name}.ll failed:\n{result}");
            return await File.ReadAllTextAsync(Path.Combine(folder, name + ".s"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Generate(TirModule module, TargetDescription target) =>
        Assert.Single(Generator.Generate(module, target).Files).Content;
}
