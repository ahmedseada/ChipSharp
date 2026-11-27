using System.Reflection;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Hardware;
using ChipSharp.Ir;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Runtime.Windows;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;
using ChipSharp.Backend.Llvm;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// ChipSharp.Hardware's buses, I2c and Spi: their methods (short names, ints and spans), which run on a chip only; in a
/// program they are ChipSharp.Library's C#, which hands each span to the chip's runtime (ChipRuntime) as a reference
/// to its first byte and a length, and the RP2040's runtime has every one of those functions.
/// </summary>
public sealed class BusLibraryTests
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private static IEnumerable<MethodInfo> BusMethods => new[] { typeof(I2c), typeof(Spi) }
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));

    private static List<IlMethod> ChipRuntimeFunctions => [.. LibrarySubstitutes.Assembly
        .FindType(LibrarySubstitutes.ChipRuntimeType)!.Methods.Where(method => method.IsStatic
            && (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public)];

    [Fact]
    public void The_buses_are_I2c_and_Spi_with_ints_and_spans_and_no_Set_names()
    {
        Assert.Equal(
            [
                "I2c.Open(Int32 bus, Int32 sda, Int32 scl, Int32 frequency)", "I2c.Probe(Int32 bus, Int32 address)",
                "I2c.Read(Int32 bus, Int32 address, Span`1 data)", "I2c.Scan(Int32 bus, Span`1 addresses)",
                "I2c.Write(Int32 bus, Int32 address, ReadOnlySpan`1 data)",
                "I2c.WriteRead(Int32 bus, Int32 address, ReadOnlySpan`1 write, Span`1 read)",
                "Spi.Open(Int32 bus, Int32 sck, Int32 tx, Int32 rx, Int32 frequency, Int32 mode)",
                "Spi.Read(Int32 bus, Span`1 data)", "Spi.Transfer(Int32 bus, ReadOnlySpan`1 write, Span`1 read)",
                "Spi.Write(Int32 bus, ReadOnlySpan`1 data)",
            ],
            BusMethods.Select(m => $"{m.DeclaringType!.Name}.{m.Name}(" +
                    $"{string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
                .Order(StringComparer.Ordinal));

        // spans of bytes only, and I2C's transfers say whether the device answered
        Assert.All(BusMethods.SelectMany(m => m.GetParameters()).Where(p => p.ParameterType.IsGenericType),
            p => Assert.Equal(typeof(byte), p.ParameterType.GetGenericArguments().Single()));
        Assert.All(BusMethods.Where(m => m.DeclaringType == typeof(I2c) && m.Name is not ("Open" or "Scan")),
            m => Assert.Equal(typeof(bool), m.ReturnType));
    }

    [Fact]
    public void On_dotnet_every_method_says_it_runs_on_a_chip()
    {
        byte[] bytes = [1, 2];
        (string Name, Action Call)[] calls =
        [
            ("I2c.Open", () => I2c.Open(0, 4, 5, 100_000)),
            ("I2c.Write", () => I2c.Write(0, 0x68, bytes)),
            ("I2c.Read", () => I2c.Read(0, 0x68, bytes)),
            ("I2c.WriteRead", () => I2c.WriteRead(0, 0x68, bytes, bytes)),
            ("I2c.Probe", () => I2c.Probe(0, 0x68)),
            ("I2c.Scan", () => I2c.Scan(0, bytes)),
            ("Spi.Open", () => Spi.Open(0, 18, 19, 16, 1_000_000, 0)),
            ("Spi.Write", () => Spi.Write(0, bytes)),
            ("Spi.Read", () => Spi.Read(0, bytes)),
            ("Spi.Transfer", () => Spi.Transfer(0, bytes, bytes)),
        ];

        Assert.Equal(BusMethods.Count(), calls.Length);
        Assert.All(calls, call => Assert.StartsWith($"ChipSharp.Hardware.{call.Name} runs on a chip, not on .NET",
            Assert.Throws<PlatformNotSupportedException>(call.Call).Message));
    }

    [Fact]
    public void In_a_program_each_method_is_the_librarys_CSharp_which_calls_the_chips_runtime_functions()
    {
        var source = """
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static int found;

                static void Main()
                {
                    Span<byte> buffer = stackalloc byte[4];
                    I2c.Open(0, 4, 5, 100_000);
                    if (I2c.WriteRead(0, 0x68, buffer[..1], buffer) && I2c.Write(0, 0x68, buffer) && I2c.Read(1, 0x76, buffer)
                        && I2c.Probe(0, 0x50))
                    {
                        found = I2c.Scan(0, buffer);
                    }

                    Spi.Open(0, 18, 19, -1, 1_000_000, 0);
                    Spi.Write(0, buffer);
                    Spi.Read(0, buffer);
                    Spi.Transfer(0, buffer, buffer);
                }
            }
            """;
        var assembly = IlAssembly.Load(CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly], "Buses"), "Buses.dll");

        var program = WholeProgramPass.Analyze(assembly);
        var module = IlToTir.Lower(program);

        Assert.Contains(program.Methods, method => method.DisplayName == "ChipSharp.Library.I2c.WriteRead");
        Assert.Contains(program.Methods, method => method.DisplayName == "ChipSharp.Library.Spi.Transfer");
        var externals = module.Functions.Where(f => f.Kind == TirFunctionKind.External).ToDictionary(f => f.Name);
        Assert.Equal(
            ["ChipSharp.Runtime.I2cOpen", "ChipSharp.Runtime.I2cRead", "ChipSharp.Runtime.I2cWrite",
                "ChipSharp.Runtime.SpiOpen", "ChipSharp.Runtime.SpiRead", "ChipSharp.Runtime.SpiTransfer",
                "ChipSharp.Runtime.SpiWrite"],
            externals.Keys.Where(name => name.Contains("I2c") || name.Contains("Spi")).Order(StringComparer.Ordinal));

        // a span is the address of its first byte and its length; a bool an int
        var write = externals["ChipSharp.Runtime.I2cWrite"];
        Assert.Equal([TirType.I32, TirType.I32, TirType.Ptr, TirType.I32, TirType.I32], write.Parameters.Select(p => p.Type));
        Assert.Equal(TirType.I32, write.ReturnType);
        Assert.Equal([TirType.I32, TirType.Ptr, TirType.I32, TirType.Ptr, TirType.I32],
            externals["ChipSharp.Runtime.SpiTransfer"].Parameters.Select(p => p.Type));

        // and no heap: the buffers are on the stack
        Assert.False(module.UsesHeap);
    }

    [Fact]
    public void The_RP2040s_runtime_has_every_function_of_ChipRuntime_and_the_PCs_none()
    {
        var pico = new Rp2040Runtime().Startup(Targets.Load("pico")).Files;
        var buses = pico.Single(f => f.FileName == Rp2040Runtime.I2cFileName).Content
            + pico.Single(f => f.FileName == Rp2040Runtime.SpiFileName).Content;
        var windows = string.Concat(new WindowsX64Runtime().Startup(Targets.Load("host")).Files.Select(f => f.Content));

        Assert.Equal(["I2cOpen", "I2cWrite", "I2cRead", "SpiOpen", "SpiWrite", "SpiRead", "SpiTransfer"],
            ChipRuntimeFunctions.Select(method => method.Name));
        Assert.All(ChipRuntimeFunctions, method =>
        {
            var symbol = LlvmCodeGenerator.SymbolName($"ChipSharp.Runtime.{method.Name}");
            Assert.Contains($" {symbol}(int bus, ", buses);
            Assert.DoesNotContain(symbol, windows);
        });

        // each is a runtime function of the library, as ChipSharp.Library.Runtime's are
        Assert.All(ChipRuntimeFunctions, method => Assert.True(LibrarySubstitutes.TryMapRuntimeFunction(
            new IlMethodReference(method.DeclaringType.Type, method.Name, method.Signature, [], null), out _)));
    }

    [Fact]
    public void On_dotnet_each_function_of_ChipRuntime_says_it_runs_on_a_chip()
    {
        var functions = typeof(global::ChipSharp.Library.SubstituteAttribute).Assembly
            .GetType("ChipSharp.Library.ChipRuntime")!.GetMethods(BindingFlags.Public | BindingFlags.Static);

        Assert.Equal(7, functions.Length);
        Assert.All(functions, function =>
        {
            var arguments = function.GetParameters().Select(p => p.ParameterType == typeof(bool) ? true
                : p.ParameterType.IsByRef ? (object)(byte)0 : 0).ToArray();
            var error = Assert.Throws<TargetInvocationException>(() => function.Invoke(null, arguments));
            Assert.Contains("runs on a chip, not on .NET", Assert.IsType<PlatformNotSupportedException>(error.InnerException).Message);
        });
    }
}
