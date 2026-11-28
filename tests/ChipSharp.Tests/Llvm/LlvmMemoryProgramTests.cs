using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Image.Uf2;
using ChipSharp.Ir;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;
using ChipSharp.Tests.Tir;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Llvm;

/// <summary>
/// TIR programs that use memory (globals, structs and arrays in slots, addresses, volatile accesses), built and run
/// on the PC, and one on the simulated Pico: they print what the same C# prints on .NET.
/// </summary>
public sealed class LlvmMemoryProgramTests : IDisposable
{
    private static readonly TirStructType Point = TirMemorySamples.Point;
    private static readonly TirStructType Line = TirMemorySamples.Line;

    /// <summary><c>struct Node { byte Tag; void* Next; int Value; }</c>: 12 bytes with 4-byte pointers, 24 with 8-byte ones.</summary>
    private static readonly TirStructType Node =
        new("Program.Node", ("Tag", TirType.I8), ("Next", TirType.Ptr), ("Value", TirType.I32));

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [HostFact]
    public async Task A_global_counter_counts_the_calls_of_a_function_called_in_a_loop()
    {
        // static int counter = 5; static long total;
        // for (int i = 0; i < 10; i++) { Increment(); total += i; } Console.WriteLine(counter); Console.WriteLine(total);
        var program = new TestProgram("Counter");
        var (counter, increment) = TirMemorySamples.AddCounter(program.Module);
        var total = program.Module.AddGlobal("Program.total", TirType.I64);
        var ir = program.Ir;
        Loop(program, "i", 0, 10, i =>
        {
            ir.Call(increment);
            var address = ir.AddressOf(total);
            ir.Store(address, ir.Binary(TirBinaryOp.Add, ir.Load(TirType.I64, address), ir.Convert(i, TirType.I64)));
        });
        program.WriteLineInt32(ir.Load(TirType.I32, ir.AddressOf(counter)));
        program.WriteLineInt64(ir.Load(TirType.I64, ir.AddressOf(total)));

        var output = await RunOnThePcAsync(program.Finish());

        Assert.Equal("15\r\n45\r\n", output);
    }

    [HostFact]
    public async Task A_struct_in_a_slot_keeps_the_fields_written_and_is_copied_passed_and_returned_whole()
    {
        var program = new TestProgram("Structs");
        var module = program.Module;
        var lengthSquared = TirMemorySamples.AddLengthSquared(module);
        var swap = TirMemorySamples.AddSwap(module);
        var origin = module.AddGlobal("Program.origin", Point);
        var p = program.Main.AddSlot("p", Point);
        var line = program.Main.AddSlot("line", Line);
        var copy = program.Main.AddSlot("copy", Point);
        var ir = program.Ir;

        program.WriteLineInt32(ir.Call(lengthSquared, ir.Const(TirType.I32, 3), ir.Const(TirType.I32, 4)));

        // Point p; p.X = 3; p.Y = 4; Line line; line.From = p; line.To = Swap(p); line.Color = 200;
        var pAddress = ir.AddressOf(p);
        ir.Store(ir.FieldAddress(pAddress, Point, "X"), ir.Const(TirType.I32, 3));
        ir.Store(ir.FieldAddress(pAddress, Point, "Y"), ir.Const(TirType.I32, 4));
        var lineAddress = ir.AddressOf(line);
        ir.Store(ir.FieldAddress(lineAddress, Line, "From"), ir.Load(Point, pAddress));
        ir.Store(ir.FieldAddress(lineAddress, Line, "To"), ir.Call(swap, ir.Load(p)));
        ir.Store(ir.FieldAddress(lineAddress, Line, "Color"), ir.Const(TirType.I8, 200));
        foreach (var (end, coordinate) in new[] { ("From", "X"), ("From", "Y"), ("To", "X"), ("To", "Y") })
        {
            var point = ir.FieldAddress(lineAddress, Line, end);
            program.WriteLineInt32(ir.Load(TirType.I32, ir.FieldAddress(point, Point, coordinate)));
        }

        var color = ir.Load(TirType.I8, ir.FieldAddress(lineAddress, Line, "Color"));
        program.WriteLineInt32(ir.Convert(color, TirType.I32, unsigned: true));

        // static Point origin; origin.Y = -9; Point copy = origin;
        var originAddress = ir.AddressOf(origin);
        ir.Store(ir.FieldAddress(originAddress, Point, "Y"), ir.Const(TirType.I32, -9));
        ir.Store(copy, ir.Load(Point, originAddress));
        var copyAddress = ir.AddressOf(copy);
        program.WriteLineInt32(ir.Load(TirType.I32, ir.FieldAddress(copyAddress, Point, "X")));
        program.WriteLineInt32(ir.Load(TirType.I32, ir.FieldAddress(copyAddress, Point, "Y")));

        // line.To = default;
        var to = ir.FieldAddress(lineAddress, Line, "To");
        ir.Store(to, ir.Zero(Point));
        program.WriteLineInt32(ir.Load(TirType.I32, ir.FieldAddress(to, Point, "Y")));
        var from = ir.FieldAddress(lineAddress, Line, "From");
        program.WriteLineInt32(ir.Load(TirType.I32, ir.FieldAddress(from, Point, "Y")));

        // sizeof(Line), sizeof(Node): Node holds a pointer
        program.WriteLineInt32(ir.SizeOf(Line));
        program.WriteLineInt32(ir.SizeOf(Node));

        var output = await RunOnThePcAsync(program.Finish());

        Assert.Equal("25\r\n3\r\n4\r\n4\r\n3\r\n200\r\n0\r\n-9\r\n0\r\n4\r\n20\r\n24\r\n", output);
    }

    [HostFact]
    public async Task A_fixed_array_in_a_slot_is_filled_in_a_loop_and_summed()
    {
        var program = new TestProgram("Arrays");
        var module = program.Module;
        var sumOfSquares = TirMemorySamples.AddSumOfSquares(module);
        var points = program.Main.AddSlot("points", new TirArrayType(Point, 3));
        var bytes = module.AddGlobal("Program.bytes", new TirArrayType(TirType.I8, 5));
        var ir = program.Ir;

        program.WriteLineInt32(ir.Call(sumOfSquares));

        // for (int i = 0; i < 3; i++) points[i].Y = i * 10;
        Loop(program, "i", 0, 3, i =>
        {
            var element = ir.ElementAddress(ir.AddressOf(points), Point, i);
            ir.Store(ir.FieldAddress(element, Point, "Y"), ir.Binary(TirBinaryOp.Mul, i, ir.Const(TirType.I32, 10)));
        });
        var pointsAddress = ir.AddressOf(points);
        var second = ir.ElementAddress(pointsAddress, Point, ir.Const(TirType.I64, 2));
        program.WriteLineInt32(ir.Load(TirType.I32, ir.FieldAddress(second, Point, "Y")));
        var first = ir.ElementAddress(pointsAddress, Point, ir.Const(TirType.NativeInt, 1));
        program.WriteLineInt32(ir.Load(TirType.I32, ir.FieldAddress(first, Point, "X")));

        // static sbyte[5] bytes; bytes[3] = -7; then read it as element -1 from the address of bytes[4]
        var bytesAddress = ir.AddressOf(bytes);
        ir.Store(ir.ElementAddress(bytesAddress, TirType.I8, ir.Const(TirType.I32, 3)), ir.Const(TirType.I8, -7));
        var last = ir.ElementAddress(bytesAddress, TirType.I8, ir.Const(TirType.I32, 4));
        var before = ir.ElementAddress(last, TirType.I8, ir.Const(TirType.I32, -1));
        program.WriteLineInt32(ir.Convert(ir.Load(TirType.I8, before), TirType.I32));

        var output = await RunOnThePcAsync(program.Finish());

        Assert.Equal("285\r\n20\r\n0\r\n-7\r\n", output);
    }

    [HostFact]
    public async Task Floats_in_memory_compute_and_compare_unordered_like_IL()
    {
        var program = new TestProgram("Floats");
        var harmonic = program.Module.AddGlobal("Program.harmonic", TirType.F64);
        var sample = new TirStructType("Program.Sample", ("Tag", TirType.I8), ("Value", TirType.F32));
        var slot = program.Main.AddSlot("sample", sample);
        var ir = program.Ir;

        // static double harmonic; for (int k = 1; k < 11; k++) harmonic += 1.0 / k; Console.WriteLine((int)(harmonic * 1000));
        Loop(program, "k", 1, 11, k =>
        {
            var address = ir.AddressOf(harmonic);
            var term = ir.Binary(TirBinaryOp.Div, ir.Const(TirType.F64, 1.0), ir.Convert(k, TirType.F64));
            ir.Store(address, ir.Binary(TirBinaryOp.Add, ir.Load(TirType.F64, address), term));
        });
        var h = ir.Load(TirType.F64, ir.AddressOf(harmonic));
        program.WriteLineInt32(ir.Convert(ir.Binary(TirBinaryOp.Mul, h, ir.Const(TirType.F64, 1000.0)), TirType.I32));

        // what IL's clt.un, clt, cgt.un, bge.un and ble.un give (C#'s !(a >= b) and the like compile to them)
        var nan = ir.Binary(TirBinaryOp.Div, ir.Const(TirType.F64, 0.0), ir.Const(TirType.F64, 0.0));
        var one = ir.Const(TirType.F64, 1.0);
        var three = ir.Const(TirType.F64, 3.0);
        program.WriteLineBool(ir.Compare(TirCompareOp.LtUnsigned, nan, one)); //   !(NaN >= 1.0)
        program.WriteLineBool(ir.Compare(TirCompareOp.Lt, nan, one)); //            NaN < 1.0
        program.WriteLineBool(ir.Compare(TirCompareOp.LtUnsigned, h, three)); //    !(h >= 3.0)
        program.WriteLineBool(ir.Compare(TirCompareOp.GtUnsigned, h, three)); //    !(h <= 3.0)
        program.WriteLineBool(ir.Compare(TirCompareOp.GeUnsigned, nan, h)); //      !(NaN < h)
        program.WriteLineBool(ir.Compare(TirCompareOp.LeUnsigned, three, h)); //    !(3.0 > h)

        // Sample sample; sample.Value = 2.5f;
        // Console.WriteLine(!(sample.Value <= 2f)); Console.WriteLine((int)(sample.Value * 4));
        var value = ir.FieldAddress(ir.AddressOf(slot), sample, "Value");
        ir.Store(value, ir.Const(TirType.F32, 2.5));
        var f = ir.Load(TirType.F32, value);
        program.WriteLineBool(ir.Compare(TirCompareOp.GtUnsigned, f, ir.Const(TirType.F32, 2.0)));
        program.WriteLineInt32(ir.Convert(ir.Binary(TirBinaryOp.Mul, f, ir.Const(TirType.F32, 4.0)), TirType.I32));

        var output = await RunOnThePcAsync(program.Finish());

        Assert.Equal("2928\r\nTrue\r\nFalse\r\nTrue\r\nFalse\r\nTrue\r\nFalse\r\nTrue\r\n10\r\n", output);
    }

    [HostFact]
    public async Task An_address_survives_a_trip_through_a_number_and_volatile_accesses_reach_memory()
    {
        var program = new TestProgram("Addresses");
        var registers = program.Module.AddGlobal("Program.registers", new TirArrayType(TirType.I32, 4));
        var ir = program.Ir;

        var start = ir.AddressOf(registers);
        var number = ir.PtrToInt(start, TirType.NativeInt);
        var third = ir.IntToPtr(ir.Binary(TirBinaryOp.Add, number, ir.Const(TirType.NativeInt, 8)));
        ir.Store(third, ir.Const(TirType.I32, 42), isVolatile: true);
        var element = ir.ElementAddress(start, TirType.I32, ir.Const(TirType.I32, 2));
        program.WriteLineInt32(ir.Load(TirType.I32, element));
        program.WriteLineInt32(ir.Load(TirType.I32, third, isVolatile: true));
        program.WriteLineBool(ir.Compare(TirCompareOp.Eq, third, element));
        program.WriteLineBool(ir.Compare(TirCompareOp.LtUnsigned, start, third));
        program.WriteLineBool(ir.Compare(TirCompareOp.GeUnsigned, start, third));
        var distance = ir.Binary(TirBinaryOp.Sub, ir.PtrToInt(element, TirType.NativeInt), number);
        program.WriteLineInt64(ir.Convert(distance, TirType.I64));

        var output = await RunOnThePcAsync(program.Finish());

        Assert.Equal("42\r\n42\r\nTrue\r\nTrue\r\nFalse\r\n8\r\n", output);
    }

    [SimulatorFact]
    public async Task Globals_structs_arrays_and_volatile_accesses_work_on_the_simulated_pico()
    {
        var program = new TestProgram("PicoMemory");
        var module = program.Module;
        var (counter, increment) = TirMemorySamples.AddCounter(module);
        var total = module.AddGlobal("Program.total", TirType.I64);
        var lengthSquared = TirMemorySamples.AddLengthSquared(module);
        var swap = TirMemorySamples.AddSwap(module);
        var sumOfSquares = TirMemorySamples.AddSumOfSquares(module);
        var toggle = TirMemorySamples.AddRegisterToggle(module);
        var register = module.AddGlobal("Program.register", TirType.I32, new TirConst(TirType.I32, 0x0F));
        var p = program.Main.AddSlot("p", Point);
        var ir = program.Ir;

        Loop(program, "i", 0, 10, i =>
        {
            ir.Call(increment);
            var address = ir.AddressOf(total);
            ir.Store(address, ir.Binary(TirBinaryOp.Add, ir.Load(TirType.I64, address), ir.Convert(i, TirType.I64)));
        });
        program.WriteLineInt32(ir.Load(TirType.I32, ir.AddressOf(counter)));
        program.WriteLineInt64(ir.Load(TirType.I64, ir.AddressOf(total)));
        program.WriteLineInt32(ir.Call(lengthSquared, ir.Const(TirType.I32, 3), ir.Const(TirType.I32, 4)));
        var pAddress = ir.AddressOf(p);
        ir.Store(ir.FieldAddress(pAddress, Point, "X"), ir.Const(TirType.I32, 3));
        ir.Store(ir.FieldAddress(pAddress, Point, "Y"), ir.Const(TirType.I32, -4));
        ir.Store(p, ir.Call(swap, ir.Load(p)));
        program.WriteLineInt32(ir.Load(TirType.I32, ir.FieldAddress(ir.AddressOf(p), Point, "X")));
        program.WriteLineInt32(ir.Call(sumOfSquares));

        // a "register" in RAM, reached through its address as a 32-bit number, as hardware registers are
        var registerNumber = ir.PtrToInt(ir.AddressOf(register), TirType.I32);
        var returned = ir.Call(toggle, registerNumber, ir.Const(TirType.I32, 0xFF));
        program.WriteLineUInt32(ir.Load(TirType.I32, ir.AddressOf(register)));
        var registerAddress = ir.Convert(registerNumber, TirType.NativeInt, unsigned: true);
        program.WriteLineBool(ir.Compare(TirCompareOp.Eq, returned, registerAddress));
        program.WriteLineInt32(ir.SizeOf(Node));

        var uf2 = BuildForPico(program.Finish(), _scratch);
        var run = await Rp2040Simulator.RunAsync(uf2, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal("15\r\n45\r\n25\r\n-4\r\n285\r\n240\r\nTrue\r\n12\r\n", Encoding.ASCII.GetString(run.Uart0));
    }

    /// <summary>
    /// Builds and runs the program on the PC twice, at -O0 (every alloca, load, store and call as written) and at
    /// -O2; it must exit with 0 and print the same both times. Returns what it printed.
    /// </summary>
    private async Task<string> RunOnThePcAsync(TirModule module)
    {
        var unoptimized = await LlvmTools.BuildAndRunAsync(module, Path.Combine(_scratch, "O0"), "-O0");
        var optimized = await LlvmTools.BuildAndRunAsync(module, Path.Combine(_scratch, "O2"), "-O2");

        Assert.True(unoptimized.ExitCode == 0, unoptimized.ToString());
        Assert.True(optimized.ExitCode == 0, optimized.ToString());
        Assert.Equal(optimized.Output, unoptimized.Output);
        return optimized.Output;
    }

    /// <summary>
    /// <c>for (int name = from; name &lt; to; name++) body(name);</c> in Main, the loop variable in a slot of that name;
    /// the builder goes on after the loop. The body may add blocks of its own.
    /// </summary>
    private static void Loop(TestProgram program, string name, int from, int to, Action<TirValue> body)
    {
        var (ir, main) = (program.Ir, program.Main);
        var variable = main.AddSlot(name, TirType.I32);
        var test = main.AddBlock(name + ".test");
        var loop = main.AddBlock(name + ".body");
        var after = main.AddBlock(name + ".done");

        ir.Store(variable, ir.Const(TirType.I32, from));
        ir.Jump(test);

        ir.Block = test;
        ir.Branch(ir.Compare(TirCompareOp.Lt, ir.Load(variable), ir.Const(TirType.I32, to)), loop, after);

        ir.Block = loop;
        body(ir.Load(variable));
        ir.Store(variable, ir.Binary(TirBinaryOp.Add, ir.Load(variable), ir.Const(TirType.I32, 1)));
        ir.Jump(test);

        ir.Block = after;
    }

    /// <summary>
    /// Builds TIR for the shipped pico target as chipsharp build does: the llvm code generator, the rp2040 runtime, the
    /// clang toolchain and the uf2 image format. Returns the .uf2.
    /// </summary>
    private static string BuildForPico(TirModule module, string folder)
    {
        var target = PicoBuild.Pico;
        IArchRuntime runtime = new Rp2040Runtime();
        var code = new GeneratedCode(
        [
            .. new LlvmCodeGenerator().Generate(module, target).Files,
            .. runtime.Startup(target).Files,
            .. runtime.InterruptEntry(target).Files,
            .. runtime.Atomics(target).Files,
        ]);
        var binary = new ClangToolchain(new ClangToolchainOptions { OutputFolder = folder }).Build(code, target);

        var uf2 = Path.ChangeExtension(binary.Path, ".uf2");
        using var output = File.Create(uf2);
        new Uf2ImageFormat().Package(binary, target, output);
        return uf2;
    }
}
