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
    private static IEnumerable<MethodInfo> HardwareMethods => typeof(Pin).Assembly.GetExportedTypes()
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));

    [Fact]
    public void The_library_is_pins_timing_registers_serial_ports_analog_inputs_and_PWM_with_no_Set_names()
    {
        Assert.Equal(
            [
                "Adc.Input(Int32)", "Adc.Read(Int32)", "Adc.Temperature()", "Adc.Volts(Int32)",
                "Pin.Input(Int32)", "Pin.Output(Int32)", "Pin.Read(Int32)", "Pin.Toggle(Int32)", "Pin.Unwatch(Int32)",
                "Pin.Watch(Int32)", "Pin.WatchFalling(Int32)", "Pin.WatchRising(Int32)", "Pin.Write(Int32, Boolean)",
                "Pwm.Duty(Int32, Double)", "Pwm.Output(Int32, Int32)", "Pwm.PulseMicroseconds(Int32, Int32)",
                "Pwm.Stop(Int32)",
                "Registers.Read(UInt32)", "Registers.Write(UInt32, UInt32)",
                "Timing.AfterMicroseconds(Int32)", "Timing.AfterMilliseconds(Int32)", "Timing.Delay(TimeSpan)",
                "Timing.DelayMicroseconds(Int32)", "Timing.DelayMilliseconds(Int32)", "Timing.EveryMicroseconds(Int32)",
                "Timing.EveryMilliseconds(Int32)", "Timing.Stop()",
                "Uart.Available(Int32)", "Uart.Open(Int32, Int32, Int32, Int32)", "Uart.Read(Int32, Span`1)",
                "Uart.ReadByte(Int32)", "Uart.Unwatch(Int32)", "Uart.Watch(Int32)", "Uart.Write(Int32, ReadOnlySpan`1)",
                "Uart.WriteByte(Int32, Byte)",
            ],
            HardwareMethods.Select(m => $"{m.DeclaringType!.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void On_dotnet_every_method_says_it_runs_on_a_chip()
    {
        // (a span cannot be an object, so the methods that take one are called below)
        Assert.All(HardwareMethods.Where(method => !method.GetParameters().Any(p => p.ParameterType.IsByRefLike)), method =>
        {
            var arguments = method.GetParameters().Select(p => Activator.CreateInstance(p.ParameterType)).ToArray();

            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, arguments));

            var notSupported = Assert.IsType<PlatformNotSupportedException>(error.InnerException);
            Assert.StartsWith($"ChipSharp.Hardware.{method.DeclaringType!.Name}.{method.Name} runs on a chip, not on .NET",
                notSupported.Message);
        });
        Assert.StartsWith("ChipSharp.Hardware.Uart.Write runs on a chip, not on .NET",
            Assert.Throws<PlatformNotSupportedException>(() => Uart.Write(1, "hi"u8)).Message);
        Assert.StartsWith("ChipSharp.Hardware.Uart.Read runs on a chip, not on .NET",
            Assert.Throws<PlatformNotSupportedException>(() => Uart.Read(1, new byte[4])).Message);
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
                    Pin.Output(25);
                    Pin.Input(3);
                    Pin.Write(25, true);
                    Pin.Toggle(25);
                    if (Pin.Read(3))
                    {
                        Timing.DelayMilliseconds(500);
                    }

                    Timing.DelayMicroseconds(20);
                    Registers.Write(0xd0000014, Registers.Read(0xd0000004) | 1u);
                }
            }
            """;
        var assembly = IlAssembly.Load(CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly], "Calls"), "Calls.dll");

        var module = IlToTir.Lower(WholeProgramPass.Analyze(assembly));

        var externals = module.Functions.Where(f => f.Kind == TirFunctionKind.External).ToList();
        Assert.Equal(
            [
                "ChipSharp.Runtime.PinOutput", "ChipSharp.Runtime.PinInput", "ChipSharp.Runtime.PinWrite",
                "ChipSharp.Runtime.PinToggle", "ChipSharp.Runtime.PinRead", "ChipSharp.Runtime.DelayMilliseconds",
                "ChipSharp.Runtime.DelayMicroseconds", "ChipSharp.Runtime.RegisterRead", "ChipSharp.Runtime.RegisterWrite",
            ],
            externals.Select(f => f.Name));
        Assert.Equal(TirType.I32, externals.Single(f => f.Name == "ChipSharp.Runtime.PinRead").ReturnType);
        Assert.Equal(TirType.I32, externals.Single(f => f.Name == "ChipSharp.Runtime.RegisterRead").ReturnType);
        // all but Timing.Delay(TimeSpan), and Uart, Adc and Pwm, which are C# of ChipSharp's library
        // (LibrarySubstituteTests, SerialAnalogTests)
        Assert.Equal(
            HardwareMethods.Count(m => m.DeclaringType != typeof(Uart) && m.DeclaringType != typeof(Adc)
                && m.DeclaringType != typeof(Pwm)) - 1,
            KnownExternals.All.Count(f => f.LibraryMethod.StartsWith("ChipSharp.Hardware.", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_hardware_method_with_another_return_type_is_not_mistaken_for_a_known_one()
    {
        // the same names and parameters as ChipSharp.Hardware, but returning int: not the runtime's functions
        var library = """
            namespace ChipSharp.Hardware
            {
                public static class Pin
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
                ["class Program { static void Main() => ChipSharp.Hardware.Pin.Toggle(1); }"], [fake], "Fake");

            var error = Assert.Throws<ChipSharp.Abstractions.ChipSharpException>(
                () => WholeProgramPass.Analyze(IlAssembly.Load(program, "Fake.dll")));

            Assert.Contains("the library method ChipSharp.Hardware.Pin.Toggle(int) is not available yet", error.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
