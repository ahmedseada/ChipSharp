using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>
/// ChipSharp.Hardware's buses on a target (HardwareBuses): I2c and Spi need the capability "i2c" or "spi", which the
/// Pico has and the PC and "tiny" do not; the constant bus, pins, clock and mode of I2c.Open and Spi.Open must be ones
/// the target's tables give; a program that uses them makes no object, so a target without a heap takes it too.
/// </summary>
public sealed class BusCheckTests : IDisposable
{
    /// <summary>Every method of both buses, each span on the stack.</summary>
    private const string BothBuses = """
        using System;
        using ChipSharp.Hardware;

        class Program
        {
            static int found;

            static void Main()
            {
                Span<byte> command = stackalloc byte[2];
                Span<byte> answer = stackalloc byte[6];
                I2c.Open(0, sda: 4, scl: 5, frequency: 400_000);
                if (I2c.WriteRead(0, 0x68, command[..1], answer) && I2c.Write(0, 0x68, command) && I2c.Read(0, 0x68, answer)
                    && I2c.Probe(0, 0x76))
                {
                    found = I2c.Scan(0, answer);
                }

                Spi.Open(1, sck: 10, tx: 11, rx: 12, frequency: 1_000_000, mode: 3);
                Spi.Write(1, command);
                Spi.Read(1, answer);
                Spi.Transfer(1, command, answer[..2]);
            }
        }
        """;

    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private TargetDescription Tiny => new TargetLoader(TinyChip.Targets(_scratch)).Load(TinyChip.Name);

    [Fact]
    public void The_Pico_has_two_I2C_and_two_SPI_buses_with_their_pins_and_clocks()
    {
        var pico = Shipped.Load("pico");

        Assert.True(pico.HasCapability("i2c"));
        Assert.True(pico.HasCapability("spi"));
        Assert.Equal("1000000", Frequency(pico, "i2c", 1));
        Assert.Equal("62500000", Frequency(pico, "spi", 1));
        Assert.False(Shipped.Load("host").HasCapability("i2c"));
        Assert.False(Shipped.Load("host").HasCapability("spi"));
        Assert.False(Tiny.HasCapability("i2c"));

        // GPIO function 3 of pin n is I2C(n / 2 % 2), SDA when n is even; function 1 is SPI(n / 8 % 2), RX, CSn, SCK and
        // TX by turns
        for (var pin = 0; pin < 30; pin++)
        {
            Assert.Contains(pin, Pins(pico, "i2c", pin / 2 % 2, pin % 2 == 0 ? "sda" : "scl"));
            var role = (pin % 4) switch { 0 => "rx", 2 => "sck", 3 => "tx", _ => null };
            if (role is not null)
            {
                Assert.Contains(pin, Pins(pico, "spi", pin / 8 % 2, role));
            }
        }

        Assert.Equal(30, new[] { "sda", "scl" }.Sum(role => Pins(pico, "i2c", 0, role).Count + Pins(pico, "i2c", 1, role).Count));
        Assert.Equal(22, new[] { "sck", "tx", "rx" }.Sum(role => Pins(pico, "spi", 0, role).Count + Pins(pico, "spi", 1, role).Count));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pico")]
    public void Every_method_of_both_buses_compiles_for_the_Pico_and_for_any_target(string? target)
    {
        var analysis = Check(BothBuses, target is null ? null : Shipped.Load(target));

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void A_Pico_without_a_heap_takes_both_buses_they_make_no_object()
    {
        var noHeap = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");

        var analysis = Check(BothBuses, noHeap);

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void On_a_target_without_the_buses_each_use_is_a_place_that_needs_one()
    {
        var analysis = Check(BothBuses, Tiny);

        // six calls of I2c's methods and four of Spi's, each a problem where it calls
        Assert.False(analysis.IsSupported);
        Assert.Equal(
        [
            .. Enumerable.Repeat("ChipSharp.Hardware.I2c needs an I2C bus, and the target 'tiny' has none. Build the " +
                "program for a chip that has one: its target file says \"i2c\": true among its capabilities, as the " +
                "Pico's does.", 6),
            .. Enumerable.Repeat("ChipSharp.Hardware.Spi needs an SPI bus, and the target 'tiny' has none. Build the " +
                "program for a chip that has one: its target file says \"spi\": true among its capabilities, as the " +
                "Pico's does.", 4),
        ], analysis.Problems.Select(problem => Message(problem.Line)));
        Assert.All(analysis.Problems, problem => Assert.Equal(IlOpCode.Call,
            problem.Method.Body!.Instructions.Single(instruction => instruction.Offset == problem.Offset).OpCode));

        // the PC has no buses either: chipsharp check -t host says so too
        Assert.All(Problems(BothBuses, Shipped.Load("host")), line => Assert.Contains("and the target 'host' has none", line));
    }

    [Fact]
    public void Constant_pins_a_bus_does_not_have_are_named_with_the_pins_it_has()
    {
        var problems = Problems("""
            using ChipSharp.Hardware;

            I2c.Open(0, sda: 6, scl: 5, frequency: 100_000);
            I2c.Open(1, sda: 2, scl: 4, frequency: 100_000);
            Spi.Open(0, sck: 10, tx: 7, rx: 5, frequency: 1_000_000, mode: 0);
            Spi.Open(1, sck: 14, tx: -1, rx: -1, frequency: 1_000_000, mode: 0);
            """, Shipped.Load("pico"));

        Assert.Equal(
        [
            "GPIO 6 is not an SDA pin of I2C bus 0 (I2c.Open's sda) on target 'pico'. Its SDA pins are GPIO 0, 4, 8, 12, " +
                "16, 20, 24 and 28.",
            "GPIO 4 is not an SCL pin of I2C bus 1 (I2c.Open's scl) on target 'pico'. Its SCL pins are GPIO 3, 7, 11, 15, " +
                "19, 23 and 27.",
            "GPIO 10 is not an SCK pin of SPI bus 0 (Spi.Open's sck) on target 'pico'. Its SCK pins are GPIO 2, 6, 18 and 22.",
            "GPIO 5 is not an RX pin of SPI bus 0 (Spi.Open's rx) on target 'pico'. Its RX pins are GPIO 0, 4, 16 and 20 " +
                "(-1 for none).",
        ], problems.Select(Message));
    }

    [Fact]
    public void A_bus_a_clock_and_a_mode_the_chip_does_not_have_are_named_too()
    {
        var problems = Problems("""
            using ChipSharp.Hardware;

            I2c.Open(2, sda: 4, scl: 5, frequency: 100_000);
            I2c.Open(0, sda: 4, scl: 5, frequency: 3_400_000);
            Spi.Open(0, sck: 2, tx: 3, rx: 4, frequency: 1_000, mode: 4);
            Spi.Open(-1, sck: 2, tx: 3, rx: 4, frequency: 62_500_000, mode: 3);
            """, Shipped.Load("pico"));

        Assert.Equal(
        [
            "I2C bus 2 (I2c.Open's bus) does not exist on target 'pico'. Its I2C buses are 0 and 1.",
            "an I2C clock of 3400000 Hz (I2c.Open's frequency) is not one target 'pico' makes. It makes 2000 to 1000000 Hz.",
            "an SPI clock of 1000 Hz (Spi.Open's frequency) is not one target 'pico' makes. It makes 2000 to 62500000 Hz.",
            "SPI mode 4 (Spi.Open's mode) does not exist. The mode is 0, 1, 2 or 3; mode 0 suits most devices.",
            "SPI bus -1 (Spi.Open's bus) does not exist on target 'pico'. Its SPI buses are 0 and 1.",
        ], problems.Select(Message));
    }

    [Fact]
    public void Pins_known_only_when_the_program_runs_are_the_chips_to_check()
    {
        // a parameter, and a constant chosen by a branch: the runtime checks them when the program runs
        var analysis = Check("""
            using ChipSharp.Hardware;

            class Program
            {
                static volatile bool other;

                static void Main()
                {
                    Open(6);
                    I2c.Open(0, sda: other ? 6 : 8, scl: 5, frequency: 100_000);
                    Spi.Open(0, sck: 2, tx: other ? 4 : 5, rx: -1, frequency: 1_000_000, mode: 0);
                }

                static void Open(int sda) => I2c.Open(0, sda, scl: 5, frequency: 100_000);
            }
            """, Shipped.Load("pico"));

        Assert.True(analysis.IsSupported, analysis.Details);
    }

    [Fact]
    public void Lcd_needs_an_I2C_bus_and_takes_a_Pico_without_a_heap()
    {
        const string Display = """
            using ChipSharp.Hardware;

            I2c.Open(1, sda: 26, scl: 27, frequency: 100_000);
            if (Lcd.Open(1, 0x27, 16, 2))
            {
                Lcd.Line(0, "ready");
                Lcd.Write(0, 1, 42);
            }
            """;
        var noHeap = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");

        Assert.True(Check(Display, Shipped.Load("pico")).IsSupported);
        Assert.True(Check(Display, noHeap).IsSupported);

        // on a chip without I2C, each of Lcd's calls needs a bus, as I2c's does
        Assert.Equal(
        [
            "ChipSharp.Hardware.I2c needs an I2C bus, and the target 'tiny' has none. Build the program for a chip that " +
                "has one: its target file says \"i2c\": true among its capabilities, as the Pico's does.",
            .. Enumerable.Repeat("ChipSharp.Hardware.Lcd needs an I2C bus, and the target 'tiny' has none. Build the " +
                "program for a chip that has one: its target file says \"i2c\": true among its capabilities, as the " +
                "Pico's does.", 3),
        ], Problems(Display, Tiny).Select(Message));
    }

    [Fact]
    public void A_constant_size_no_HD44780_shows_and_a_bus_the_chip_does_not_have_are_named_for_Lcd_Open()
    {
        var problems = Problems("""
            using ChipSharp.Hardware;

            Lcd.Open(1, 0x27, 41, 2);
            Lcd.Open(1, 0x27, 21, 4);
            Lcd.Open(1, 0x27, 16, 5);
            Lcd.Open(1, 0x27, 0, 1);
            Lcd.Open(2, 0x27, 16, 2);
            Lcd.Open(1, 0x27, 40, 2);
            Lcd.Open(0, 0x3F, 20, 4);
            Lcd.Open(0, 0x3F, 40, 1);
            """, Shipped.Load("pico"));

        const string Shows = "It shows 1 to 4 rows of 1 to 40 columns, 20 on more than two rows: e.g. 16 columns and 2 " +
            "rows, or 20 and 4.";
        Assert.Equal(
        [
            $"an LCD of 41 by 2 characters (Lcd.Open's columns and rows) is not one an HD44780 shows. {Shows}",
            $"an LCD of 21 by 4 characters (Lcd.Open's columns and rows) is not one an HD44780 shows. {Shows}",
            $"an LCD of 16 by 5 characters (Lcd.Open's columns and rows) is not one an HD44780 shows. {Shows}",
            $"an LCD of 0 by 1 characters (Lcd.Open's columns and rows) is not one an HD44780 shows. {Shows}",
            "I2C bus 2 (Lcd.Open's bus) does not exist on target 'pico'. Its I2C buses are 0 and 1.",
        ], problems.Select(Message));
    }

    [Fact]
    public async Task Chipsharp_check_names_the_bus_problems_for_the_target_it_is_given()
    {
        var dll = Path.Combine(_scratch, "Buses.dll");
        Directory.CreateDirectory(_scratch);
        await File.WriteAllBytesAsync(dll, CSharpCompiler.Compile(["""
            using ChipSharp.Hardware;

            I2c.Open(0, sda: 4, scl: 6, frequency: 100_000);
            """], [PicoBuild.HardwareAssembly], "Buses"));

        var (pico, picoOutput, picoError) = await ChipSharpCli.RunAsync("check", dll, "-t", "pico");
        var (host, _, hostError) = await ChipSharpCli.RunAsync("check", dll, "-t", "host");

        Assert.Equal(1, pico);
        Assert.Equal("", picoOutput);
        Assert.True(picoError.Contains("GPIO 6 is not an SCL pin of I2C bus 0 (I2c.Open's scl) on target 'pico': 1 place " +
            "in the top-level statements (IL_0008). Its SCL pins are GPIO 1, 5, 9, 13, 17, 21, 25 and 29.",
            StringComparison.Ordinal), picoError);
        Assert.Equal(1, host);
        Assert.Contains("ChipSharp.Hardware.I2c needs an I2C bus, and the target 'host' has none", hostError);
    }

    private static string Frequency(TargetDescription target, string bus, int index) =>
        Assert.IsType<System.Text.Json.Nodes.JsonArray>(target.Settings[bus]!["frequency"])[index]!.ToString();

    private static List<int> Pins(TargetDescription target, string bus, int number, string role) =>
        [.. Assert.IsType<System.Text.Json.Nodes.JsonArray>(target.Settings[bus]!["buses"]![number]![role])
            .Select(pin => (int)pin!)];

    /// <summary>A problem's message, without its method and place.</summary>
    private static string Message(string line) => line[(line.IndexOf("): ", StringComparison.Ordinal) + 3)..];

    private static AnalysisResult Check(string source, TargetDescription? target) =>
        WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly], "Test"),
            "Test.dll"), target);

    /// <summary>The problems one per place, as the details list them.</summary>
    private static string[] Problems(string source, TargetDescription? target)
    {
        var analysis = Check(source, target);
        Assert.False(analysis.IsSupported);
        return [.. analysis.Problems.Select(problem => problem.Line)];
    }
}
