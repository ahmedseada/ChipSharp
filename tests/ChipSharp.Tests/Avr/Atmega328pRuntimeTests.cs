using System.Reflection;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Build;
using ChipSharp.Frontend;
using ChipSharp.Runtime.Atmega328p;
using ChipSharp.Runtime.Avr;
using ChipSharp.Runtime.Windows;
using ChipSharp.Tests.Llvm;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The atmega328p runtime (the Arduino Uno's): its files on top of the avr runtime's, the functions programs call, the
/// interrupts it takes from the vector table and the event numbers it calls the program's methods by, and its C
/// compiled for the chip.
/// </summary>
public sealed class Atmega328pRuntimeTests : IDisposable
{
    private static readonly string[] OwnFiles =
    [
        Atmega328pRuntime.SourceFileName, Atmega328pRuntime.UartFileName, Atmega328pRuntime.InterruptsFileName,
        Atmega328pRuntime.EventsFileName,
    ];

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private static string Own(string fileName) => Atmega328pRuntime.Source(fileName).Content;

    [Fact]
    public void The_Uno_is_built_with_the_atmega328p_runtime_which_a_build_finds_by_its_name()
    {
        var uno = UnoProgram.Uno;
        var build = new BuildContext(new BuildOptions(), uno, "Program",
            Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N")));

        Assert.Equal("atmega328p", uno.Runtime);
        Assert.IsType<Atmega328pRuntime>(DefaultComponents.CreateRegistry(build).Get<IArchRuntime>(uno.Runtime!));
    }

    [Fact]
    public void The_runtime_is_the_avr_files_plus_the_chips_own_and_the_shared_console()
    {
        var uno = UnoProgram.Uno;
        var runtime = new Atmega328pRuntime();
        var avr = new AvrRuntime();

        var files = runtime.Startup(uno).Files;

        Assert.Equal(
            [.. avr.Startup(uno).Files.Select(f => f.FileName), Atmega328pRuntime.SourceFileName,
                Atmega328pRuntime.ConsoleFileName, Atmega328pRuntime.UartFileName],
            files.Select(f => f.FileName));
        Assert.Equal([AvrRuntime.VectorsFileName, Atmega328pRuntime.InterruptsFileName, Atmega328pRuntime.EventsFileName],
            runtime.InterruptEntry(uno).Files.Select(f => f.FileName));
        Assert.Empty(runtime.Atomics(uno).Files);
        Assert.Equal(WindowsX64Runtime.Console, files.Single(f => f.FileName == Atmega328pRuntime.ConsoleFileName).Content);
        Assert.All(OwnFiles, name => Assert.DoesNotContain("#include", Own(name)));
    }

    [Fact]
    public void The_runtime_defines_every_function_of_the_hardware_the_clock_the_serial_port_and_the_console()
    {
        var c = Own(Atmega328pRuntime.SourceFileName) + Own(Atmega328pRuntime.EventsFileName) + Own(Atmega328pRuntime.UartFileName);
        var hardware = KnownExternals.All.Where(f => f.LibraryMethod.StartsWith("ChipSharp.Hardware.", StringComparison.Ordinal))
            .Select(f => f.DisplayName).ToList();
        var clock = Methods(LibrarySubstitutes.RuntimeType).Where(name => name.StartsWith("Clock", StringComparison.Ordinal));
        var serial = Methods(LibrarySubstitutes.ChipRuntimeType)
            .Where(name => name.StartsWith("Uart", StringComparison.Ordinal) || name == "ConsoleRead");
        List<string> all = [.. hardware, .. clock.Concat(serial).Select(name => "ChipSharp.Runtime." + name)];

        Assert.Equal(7, clock.Count());
        Assert.Equal(7, serial.Count());
        Assert.All(all, function => Assert.Matches(
            new Regex($@"\n(i32|u32|i64|void) {Regex.Escape(LlvmCodeGenerator.SymbolName(function))}\("), c));
        // and what the shared console and the avr runtime call
        Assert.Contains("void chipsharp_console_write(const char *bytes, unsigned int count)", c);
        Assert.Contains("void chipsharp_failure_write(const char *text, unsigned int count)", c);
        Assert.Contains("void chipsharp_before_halt(void)", c);
        Assert.Contains("void chipsharp_chip_init(void)", c);
    }

    /// <summary>The public static methods of one of ChipSharp.Library's runtime types, by name.</summary>
    private static List<string> Methods(string type) => [.. LibrarySubstitutes.Assembly.FindType(type)!.Methods
        .Where(method => method.IsStatic && (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public)
        .Select(method => method.Name)];

    [Fact]
    public void The_events_are_numbered_past_the_vector_table_and_the_runtimes_handlers_call_them_by_those_numbers()
    {
        var uno = UnoProgram.Uno;
        var vectors = AvrRuntime.VectorCount(uno);
        var interrupts = Interrupts.ForTarget(uno);
        var events = Own(Atmega328pRuntime.EventsFileName);
        var uart = Own(Atmega328pRuntime.UartFileName);
        var entries = Own(Atmega328pRuntime.InterruptsFileName);

        Assert.Equal(["pin", "timer", "uart"], interrupts.Keys.Order(StringComparer.Ordinal));
        Assert.All(interrupts.Values, i => Assert.True(i.Number >= vectors && i.Clear is null, i.ToString()));
        Assert.Contains($"void {interrupts["pin"].Symbol}(void)", events);
        Assert.Contains($"void {interrupts["timer"].Symbol}(void)", events);
        Assert.Contains($"void {interrupts["uart"].Symbol}(void)", uart);
        Assert.Contains($"    {interrupts["pin"].Symbol}();", events);
        Assert.Contains($"    {interrupts["timer"].Symbol}();", events);
        Assert.Contains($"        {interrupts["uart"].Symbol}();", uart);

        // the chip's vectors the runtime takes from the avr runtime's table (INT0, INT1, PCINT0-2, TIMER1_COMPB, USART_RX),
        // each with its source, as chipsharp_interrupt counts them; and TIMER1_OVF, the clock's, in C
        Assert.Equal(["1 0", "2 1", "3 2", "4 3", "5 4", "12 5", "18 6"],
            Regex.Matches(entries, @"^\s+entry (\d+), (\d+)$", RegexOptions.Multiline).Select(m => $"{m.Groups[1]} {m.Groups[2]}"));
        Assert.Contains("__attribute__((signal)) void __vector_13(void)", Own(Atmega328pRuntime.SourceFileName));
        Assert.Contains(AvrRuntime.Handler(13), AvrRuntime.Vectors(uno.Name, vectors));
    }

    [Fact]
    public void The_Unos_capabilities_are_the_console_both_ways_the_clock_and_one_serial_port_on_D1_and_D0()
    {
        var uno = UnoProgram.Uno;

        Assert.True(uno.HasConsole);
        Assert.True(uno.HasCapability("consoleInput"));
        Assert.True(uno.HasCapability("clock"));
        Assert.False(uno.HasHeap);
        Assert.Equal("1", uno.Capabilities["uart"]);
        Assert.Equal("[1]", uno.Settings["pinFunctions"]!["uart0.tx"]!.ToJsonString());
        Assert.Equal("[0]", uno.Settings["pinFunctions"]!["uart0.rx"]!.ToJsonString());
        Assert.All(["adc", "pwm", "i2c", "spi"], name => Assert.False(uno.Capabilities.ContainsKey(name), name));
    }

    /// <summary>
    /// Each of the runtime's own files compiles for the ATmega328P as a build compiles it, without a warning; what they
    /// leave to the rest of the program is the avr runtime's (the failure stop, the multiply and divide helpers) and what
    /// they give one another.
    /// </summary>
    [AvrFact]
    public async Task The_runtimes_files_compile_for_the_atmega328p_and_need_only_the_avr_runtime_and_each_other()
    {
        Directory.CreateDirectory(_scratch);
        var zig = AvrTools.Zig.FindZig();
        var flags = Uno.Flags;
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var undefined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in OwnFiles)
        {
            await File.WriteAllTextAsync(Path.Combine(_scratch, name), Own(name));
            var compiled = await LlvmTools.RunAsync(zig,
                ["cc", .. flags, "-Wall", "-Wextra", "-Werror", "-c", name, "-o", name + ".o"], _scratch);
            Assert.True(compiled.ExitCode == 0, compiled.ToString());
            var symbols = await LlvmTools.RunAsync(PicoTools.LlvmTool("llvm-nm"), [name + ".o"], _scratch);
            Assert.True(symbols.ExitCode == 0, symbols.ToString());
            foreach (var parts in symbols.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' ')))
            {
                (parts[0] == "U" ? undefined : defined).Add(parts[^1]);
            }
        }

        undefined.ExceptWith(defined);
        Assert.Equal(
            [
                "__do_clear_bss", "__do_copy_data",                         // what clang's AVR code says it needs of start-up
                "__muldi3", "__mulsi3", "__udivdi3", "__udivmodsi4",        // the avr runtime's helpers
                "chipsharp_halt_failure",                                   // and its failure stop
            ],
            undefined.Order(StringComparer.Ordinal));
        Assert.Contains("chipsharp_interrupt", defined);
        Assert.Contains("chipsharp_interrupt_hooks", defined);
    }

    /// <summary>What a build passes zig cc for the Uno.</summary>
    private static class Uno
    {
        public static string[] Flags
        {
            get
            {
                var settings = ChipSharp.Toolchain.Clang.ChipSettings.Read(UnoProgram.Uno);
                return
                [
                    $"--target={settings.ZigTarget}", .. settings.ProcessorFlags(ChipSharp.Toolchain.Clang.ClangDriver.Zig),
                    .. settings.ClangFlags,
                ];
            }
        }
    }
}
