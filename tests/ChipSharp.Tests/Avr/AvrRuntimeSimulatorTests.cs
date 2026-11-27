using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Image.Hex;
using ChipSharp.Runtime.Avr;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The avr runtime built as a build builds it for the Arduino Uno (the toolchain "zig", the linker script it writes,
/// the .hex) and run in the avr8js simulator (tools/avr-sim): a program that multiplies, divides (8 to 64 bits), copies
/// and computes with floats and doubles, checked against .NET's results, lights D13 when every one is right and finishes; one that fails
/// stops at BREAK, which the simulator reports as a failure.
/// </summary>
public sealed class AvrRuntimeSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [AvrSimulatorFact]
    public async Task A_program_on_the_runtime_gets_dotnets_results_and_finishes()
    {
        var hex = Build("Computes", Checks());

        var run = await AvrSimulator.RunAsync(hex, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("the program has finished", run.Stopped);
        Assert.Equal("high", run.Pins.Last(pin => pin.Pin == "D13").State); // every result was .NET's
    }

    [AvrSimulatorFact]
    public async Task A_failure_stops_at_BREAK_which_the_simulator_reports()
    {
        var hex = Build("Fails", """
            void ChipSharp_Runtime_IndexOutOfRange(void);

            long chipsharp_entry(void)
            {
                ChipSharp_Runtime_IndexOutOfRange();
                return 0;
            }
            """);

        var run = await AvrSimulator.RunAsync(hex, 1000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains("breakpoint", run.Stopped);
    }

    /// <summary>The runtime and <paramref name="program"/> (chipsharp_entry in C) built for the Uno into a .hex.</summary>
    private string Build(string name, string program)
    {
        var uno = AvrTestProgram.Uno;
        var runtime = new AvrRuntime();
        var toolchain = new ClangToolchain(new ClangToolchainOptions
        {
            Driver = ClangDriver.Zig, OutputFolder = Path.Combine(_scratch, name), ProgramName = name, UseCache = false,
        });
        var binary = toolchain.Build(new GeneratedCode(
            [.. runtime.Startup(uno).Files, .. runtime.InterruptEntry(uno).Files, new GeneratedFile("program.c", program)]), uno);
        var hex = Path.ChangeExtension(binary.Path, ".hex");
        using var output = File.Create(hex);
        new IntelHexImageFormat().Package(binary, uno, output);
        return hex;
    }

    /// <summary>
    /// C that computes with the helpers and soft float from volatile values (nothing is folded at compile time) and
    /// compares each result's bits with .NET's, written into it; D13 (PB5) goes high when all are the same.
    /// </summary>
    private static string Checks()
    {
        long a = 1234567890123456789, b = -987654321;
        int c = 2000000000, d = -7;
        double x = 1.1, y = 3.3;
        float f = 2.5f, g = 0.1f;
        ushort e = 60000, h = 7;
        short i = -30000, j = 7;
        byte k = 250, l = 7;
        (string Expression, ulong Bits)[] checks =
        [
            ("(unsigned long long)(a / b)", (ulong)(a / b)),
            ("(unsigned long long)(a % b)", (ulong)(a % b)),
            ("(unsigned long long)a * (unsigned long long)b", unchecked((ulong)(a * b))),
            ("(unsigned long long)((unsigned long long)a / 1000u)", (ulong)a / 1000),
            ("(unsigned long)(c / d)", (uint)(c / d)),
            ("(unsigned long)(c % d)", (uint)(c % d)),
            ("(unsigned long)c * (unsigned long)d", unchecked((uint)(c * d))),
            ("bits64(x + y)", BitConverter.DoubleToUInt64Bits(x + y)),
            ("bits64(x - y)", BitConverter.DoubleToUInt64Bits(x - y)),
            ("bits64(x * y)", BitConverter.DoubleToUInt64Bits(x * y)),
            ("bits64(x / y)", BitConverter.DoubleToUInt64Bits(x / y)),
            ("bits64((double)a)", BitConverter.DoubleToUInt64Bits(a)),
            ("bits64((double)f)", BitConverter.DoubleToUInt64Bits(f)),
            ("(unsigned long long)(long long)(y * 1e15)", (ulong)(long)(y * 1e15)),
            ("(unsigned long)(x < y)", 1),
            ("bits32(f * g)", BitConverter.SingleToUInt32Bits(f * g)),
            ("bits32(f / g)", BitConverter.SingleToUInt32Bits(f / g)),
            ("bits32((float)x)", BitConverter.SingleToUInt32Bits((float)x)),
            ("(unsigned long)(long)(f * 1000.0f)", (uint)(int)(f * 1000.0f)),
            ("(unsigned long)(e / h)", (uint)(e / h)),
            ("(unsigned long)(e % h)", (uint)(e % h)),
            ("(unsigned long)(unsigned short)(i / j)", (ushort)(i / j)),
            ("(unsigned long)(unsigned short)(i % j)", (ushort)(i % j)),
            ("(unsigned long)(unsigned char)(k / l)", (uint)(k / l)),
            ("(unsigned long)(unsigned char)(k % l)", (uint)(k % l)),
            ("copied()", 1),
        ];

        var tests = string.Concat(checks.Select(check =>
            $"    wrong += (unsigned long long)({check.Expression}) != 0x{check.Bits:X}ull;\n"));
        return $$"""
            #define DDRB (*(volatile unsigned char *)0x24)
            #define PORTB (*(volatile unsigned char *)0x25)

            static unsigned long long bits64(double value) { union { double d; unsigned long long u; } v = { value }; return v.u; }
            static unsigned long bits32(float value) { union { float f; unsigned long u; } v = { value }; return v.u; }

            volatile long long a = {{a.ToString(CultureInfo.InvariantCulture)}}LL, b = {{b.ToString(CultureInfo.InvariantCulture)}}LL;
            volatile long c = {{c.ToString(CultureInfo.InvariantCulture)}}L, d = {{d.ToString(CultureInfo.InvariantCulture)}}L;
            volatile double x = 1.1, y = 3.3;
            volatile float f = 2.5f, g = 0.1f;
            volatile unsigned short e = 60000, h = 7;
            volatile short i = -30000, j = 7;
            volatile unsigned char k = 250, l = 7;

            // a struct this big is copied with a call to memcpy
            struct big { char bytes[100]; };
            struct big from = { { 1, 2, 3 } }, to;

            static int copied(void)
            {
                from.bytes[99] = 99;
                to = from;
                volatile char *copy = to.bytes;
                return copy[2] == 3 && copy[99] == 99;
            }

            long chipsharp_entry(void)
            {
                int wrong = 0;
            {{tests}}
                DDRB |= 1 << 5;
                if (wrong == 0)
                {
                    PORTB |= 1 << 5;
                }

                return wrong;
            }
            """;
    }
}
