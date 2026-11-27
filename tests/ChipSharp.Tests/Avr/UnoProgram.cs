using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Image.Hex;
using ChipSharp.Runtime.Atmega328p;
using ChipSharp.Toolchain.Clang;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// Programs for the Arduino Uno on its runtime (<see cref="Atmega328pRuntime"/>, on the AVR runtime), built as a build
/// builds them (the toolchain "zig", the linker script, the .hex): C that calls the runtime's functions itself, or C#
/// built the whole way by the build pipeline.
/// </summary>
internal static class UnoProgram
{
    /// <summary>
    /// The declarations a C test program starts with: the runtime functions it calls, with C#'s types (an int is a long
    /// in AVR's C); and <c>print</c> and <c>print_number</c>, which write to USART0 through the runtime.
    /// </summary>
    public const string Prelude = """
        typedef long i32;
        typedef unsigned long u32;
        typedef long long i64;

        void ChipSharp_Runtime_PinOutput(i32 pin);
        void ChipSharp_Runtime_PinInput(i32 pin);
        void ChipSharp_Runtime_PinWrite(i32 pin, i32 high);
        void ChipSharp_Runtime_PinToggle(i32 pin);
        i32 ChipSharp_Runtime_PinRead(i32 pin);
        void ChipSharp_Runtime_PinWatch(i32 pin);
        void ChipSharp_Runtime_PinWatchRising(i32 pin);
        void ChipSharp_Runtime_PinWatchFalling(i32 pin);
        void ChipSharp_Runtime_PinUnwatch(i32 pin);
        void ChipSharp_Runtime_DelayMilliseconds(i32 milliseconds);
        void ChipSharp_Runtime_DelayMicroseconds(i32 microseconds);
        void ChipSharp_Runtime_TimerEveryMilliseconds(i32 milliseconds);
        void ChipSharp_Runtime_TimerEveryMicroseconds(i32 microseconds);
        void ChipSharp_Runtime_TimerAfterMilliseconds(i32 milliseconds);
        void ChipSharp_Runtime_TimerAfterMicroseconds(i32 microseconds);
        void ChipSharp_Runtime_TimerStop(void);
        i64 ChipSharp_Runtime_ClockUtcTicks(void);
        void ChipSharp_Runtime_ClockStart(i64 ticks);
        i64 ChipSharp_Runtime_ClockTimestamp(void);
        i64 ChipSharp_Runtime_ClockTickCount64(void);
        i64 ChipSharp_Runtime_ClockElapsedTicks(i64 start, i64 end);
        u32 ChipSharp_Runtime_RegisterRead(u32 address);
        void ChipSharp_Runtime_RegisterWrite(u32 address, u32 value);
        void ChipSharp_Runtime_UartOpen(i32 port, i32 tx, i32 rx, i32 baud);
        void ChipSharp_Runtime_UartWriteByte(i32 port, i32 value);
        i32 ChipSharp_Runtime_UartReadByte(i32 port);
        i32 ChipSharp_Runtime_UartAvailable(i32 port);
        void ChipSharp_Runtime_UartWatch(i32 port);
        void ChipSharp_Runtime_UartUnwatch(i32 port);
        i32 ChipSharp_Runtime_ConsoleRead(void);
        void chipsharp_console_write(const char *bytes, unsigned int count);

        static void print(const char *text)
        {
            unsigned int count = 0;
            while (text[count] != 0) count++;
            chipsharp_console_write(text, count);
        }

        static void print_number(i64 value)
        {
            char text[21];
            int at = sizeof text;
            unsigned long long magnitude = value < 0 ? 0ull - (unsigned long long)value : (unsigned long long)value;
            do { text[--at] = (char)('0' + magnitude % 10); magnitude /= 10; } while (magnitude != 0);
            if (value < 0) text[--at] = '-';
            chipsharp_console_write(text + at, sizeof text - at);
        }

        """;

    /// <summary>The Uno, as its target file says.</summary>
    public static TargetDescription Uno => AvrTestProgram.Uno;

    /// <summary>
    /// The runtime and <paramref name="program"/> (C with chipsharp_entry, after <see cref="Prelude"/>) built for the Uno
    /// in <paramref name="folder"/>: the .hex, and the .elf it was made from, with the memory report.
    /// </summary>
    public static AvrProgram BuildC(string folder, string name, string program)
    {
        var uno = Uno;
        var runtime = new Atmega328pRuntime();
        var toolchain = new ClangToolchain(new ClangToolchainOptions
        {
            Driver = ClangDriver.Zig, OutputFolder = Path.Combine(folder, name), ProgramName = name, UseCache = false,
        });
        var binary = toolchain.Build(new GeneratedCode(
        [
            .. runtime.Startup(uno).Files, .. runtime.InterruptEntry(uno).Files,
            new GeneratedFile("program.c", Prelude + program),
        ]), uno);
        var hex = Path.ChangeExtension(binary.Path, ".hex");
        using (var output = File.Create(hex))
        {
            new IntelHexImageFormat().Package(binary, uno, output);
        }

        return new AvrProgram(binary.Path, hex, MemoryReport.Read(binary, uno));
    }

    /// <summary>The C# program <paramref name="source"/> built for the Uno by the build pipeline, as chipsharp build -t uno does.</summary>
    public static AvrProgram BuildCSharp(string folder, string name, string source)
    {
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, name + ".dll");
        File.WriteAllBytes(dll, CSharpCompiler.Compile([source], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], name));
        var result = ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = "uno",
            TargetsFolder = Path.Combine(AppContext.BaseDirectory, "targets"),
            OutputFolder = Path.Combine(folder, name),
            KeepIntermediates = true,
            UseCache = false,
        });
        var elf = Assert.Single(result.IntermediatePaths, path => path.EndsWith(".elf", StringComparison.Ordinal));
        return new AvrProgram(elf, result.OutputPath, result.Memory);
    }
}
