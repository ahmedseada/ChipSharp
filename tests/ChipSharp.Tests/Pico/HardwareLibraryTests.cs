using System.Reflection;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Hardware;
using ChipSharp.Ir;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>ChipSharp.Hardware: every method is a library call ChipSharp compiles, and none runs on .NET.</summary>
public sealed class HardwareLibraryTests
{
    private static IEnumerable<MethodInfo> HardwareMethods => typeof(Gpio).Assembly.GetExportedTypes()
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));

    [Fact]
    public void The_library_is_pins_delays_and_registers_with_int_uint_and_bool_only()
    {
        Assert.Equal(
            [
                "Delay.Microseconds(Int32)", "Delay.Milliseconds(Int32)",
                "Gpio.Read(Int32)", "Gpio.SetInput(Int32)", "Gpio.SetOutput(Int32)", "Gpio.Toggle(Int32)",
                "Gpio.Write(Int32, Boolean)",
                "Registers.Read(UInt32)", "Registers.Write(UInt32, UInt32)",
            ],
            HardwareMethods.Select(m => $"{m.DeclaringType!.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void On_dotnet_every_method_says_it_runs_on_a_chip()
    {
        Assert.All(HardwareMethods, method =>
        {
            var arguments = method.GetParameters().Select(p => Activator.CreateInstance(p.ParameterType)).ToArray();

            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, arguments));

            var notSupported = Assert.IsType<PlatformNotSupportedException>(error.InnerException);
            Assert.StartsWith($"ChipSharp.Hardware.{method.DeclaringType!.Name}.{method.Name} runs on a chip, not on .NET",
                notSupported.Message);
        });
    }

    [Fact]
    public void Every_method_becomes_a_runtime_function_in_TIR()
    {
        var source = """
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Gpio.SetOutput(25);
                    Gpio.SetInput(3);
                    Gpio.Write(25, true);
                    Gpio.Toggle(25);
                    if (Gpio.Read(3))
                    {
                        Delay.Milliseconds(500);
                    }

                    Delay.Microseconds(20);
                    Registers.Write(0xd0000014, Registers.Read(0xd0000004) | 1u);
                }
            }
            """;
        var assembly = IlAssembly.Load(CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly], "Calls"), "Calls.dll");

        var module = IlToTir.Lower(WholeProgramPass.Analyze(assembly));

        var externals = module.Functions.Where(f => f.Kind == TirFunctionKind.External).ToList();
        Assert.Equal(
            [
                "ChipSharp.Runtime.GpioSetOutput", "ChipSharp.Runtime.GpioSetInput", "ChipSharp.Runtime.GpioWrite",
                "ChipSharp.Runtime.GpioToggle", "ChipSharp.Runtime.GpioRead", "ChipSharp.Runtime.DelayMilliseconds",
                "ChipSharp.Runtime.DelayMicroseconds", "ChipSharp.Runtime.RegisterRead", "ChipSharp.Runtime.RegisterWrite",
            ],
            externals.Select(f => f.Name));
        Assert.Equal(TirType.I32, externals.Single(f => f.Name == "ChipSharp.Runtime.GpioRead").ReturnType);
        Assert.Equal(TirType.I32, externals.Single(f => f.Name == "ChipSharp.Runtime.RegisterRead").ReturnType);
        Assert.Equal(
            HardwareMethods.Count(),
            KnownExternals.All.Count(f => f.LibraryMethod.StartsWith("ChipSharp.Hardware.", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_hardware_method_with_another_return_type_is_not_mistaken_for_a_known_one()
    {
        // the same names and parameters as ChipSharp.Hardware, but returning int: not the runtime's functions
        var library = """
            namespace ChipSharp.Hardware
            {
                public static class Gpio
                {
                    public static int Toggle(int pin) => pin;
                }
            }
            """;
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var fake = CSharpCompiler.CompileToFile(library, Path.Combine(folder, "FakeHardware.dll"));
            var program = CSharpCompiler.Compile(
                ["class Program { static void Main() => ChipSharp.Hardware.Gpio.Toggle(1); }"], [fake], "Fake");

            var error = Assert.Throws<ChipSharp.Abstractions.ChipSharpException>(
                () => WholeProgramPass.Analyze(IlAssembly.Load(program, "Fake.dll")));

            Assert.Contains("the library method ChipSharp.Hardware.Gpio.Toggle(int) is not available in M0", error.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
