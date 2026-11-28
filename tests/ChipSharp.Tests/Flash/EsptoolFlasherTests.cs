using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Flasher.Esptool;
using ChipSharp.Flasher.Shared;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Flash;

/// <summary>
/// The flasher "esptool", for the ESP32-C5-DevKitC-1: the command line it runs (esptool 5's and 4's spelling), where it
/// finds esptool (CHIPSHARP_ESPTOOL, the PATH, a Python that has it) and what it says when there is none, which serial
/// port it takes (by USB ids), and a flash with a stand-in for esptool (<see cref="FakeTool"/>), which works and fails.
/// </summary>
public sealed class EsptoolFlasherTests : IDisposable
{
    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private static readonly EsptoolSettings C5 = new("esp32c5", "0x2000", 460800);

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void The_C5_is_flashed_by_esptool_at_0x2000_with_each_versions_command()
    {
        var board = Shipped.Load("esp32c5-devkitc");

        Assert.Equal("esptool", board.Flasher);
        Assert.Equal(C5, EsptoolSettings.Read(board));
        Assert.Equal(["--chip", "esp32c5", "-p", "COM5", "-b", "460800", "write-flash", "0x2000", @"C:\Blink\Blink.bin"],
            EsptoolFlasher.Arguments(C5, 5, "COM5", @"C:\Blink\Blink.bin"));
        Assert.Equal(["--chip", "esp32c5", "-p", "COM5", "-b", "460800", "write_flash", "0x2000", @"C:\Blink\Blink.bin"],
            EsptoolFlasher.Arguments(C5, 4, "COM5", @"C:\Blink\Blink.bin"));
        // a version that was not read: the newest spelling
        Assert.Equal("write-flash", EsptoolFlasher.Arguments(C5, null, "COM5", "x.bin")[6]);
        Assert.Equal(@"--chip esp32c5 -p COM7 -b 460800 write-flash 0x2000 ""C:\My programs\Blink.bin""",
            EsptoolFlasher.CommandLine(EsptoolFlasher.Arguments(C5, 5, "COM7", @"C:\My programs\Blink.bin")));

        Assert.Equal(5, EsptoolFlasher.MajorVersion("esptool v5.0.2"));
        Assert.Equal(4, EsptoolFlasher.MajorVersion("esptool.py v4.8.1\n4.8.1"));
        Assert.Equal(5, EsptoolFlasher.MajorVersion("esptool.py v5.1.0"));
        Assert.Null(EsptoolFlasher.MajorVersion("usage: esptool"));

        var registry = DefaultComponents.CreateFlasherRegistry();
        Assert.IsType<EsptoolFlasher>(registry.Get<IFlasher>("esptool"));
        Assert.Equal(["avrdude", "easypdkprog", "esptool", "stm8flash", "uf2-copy"], registry.Names<IFlasher>());
    }

    [Fact]
    public void A_board_file_gives_the_chip_the_offset_and_may_name_the_speed()
    {
        var targets = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("slow", """{ "inherits": "esp32c5-devkitc", "esptool": { "baud": 115200 } }"""),
            ("no-chip", """{ "inherits": "esp32c5-devkitc", "esptool": { "chip": null } }"""),
            ("bad-baud", """{ "inherits": "esp32c5-devkitc", "esptool": { "baud": "fast" } }""")));

        Assert.Equal(new EsptoolSettings("esp32c5", "0x2000", 115200), EsptoolSettings.Read(targets.Load("slow")));
        Assert.Equal(
            "Target 'no-chip' is flashed with esptool but does not say which chip it is (\"esptool\": { \"chip\": \"esp32c5\" }).",
            Assert.Throws<ChipSharpException>(() => EsptoolSettings.Read(targets.Load("no-chip"))).Message);
        Assert.Equal(
            "Target 'bad-baud': \"esptool.baud\" must be a number of bits a second, e.g. 460800, not 'fast'.",
            Assert.Throws<ChipSharpException>(() => EsptoolSettings.Read(targets.Load("bad-baud"))).Message);
    }

    [Fact]
    public void Esptool_is_found_by_CHIPSHARP_ESPTOOL_then_on_the_PATH_then_in_a_Python_that_has_it()
    {
        var onPath = Executable(Path.Combine(_scratch, "path", "esptool.exe"));
        var own = Executable(Path.Combine(_scratch, "own", "esptool.exe"));
        var script = Executable(Path.Combine(_scratch, "script", "esptool.py"));
        var py = Executable(Path.Combine(_scratch, "py", "py.exe"));
        var python = Executable(Path.Combine(_scratch, "python", "python.exe"));
        var probed = new List<string>();
        EsptoolTool Find(Func<string, bool> imports, params (string Name, string Value)[] variables) =>
            new EsptoolFlasher(name => variables.FirstOrDefault(v => v.Name == name).Value, () => [], imports).FindEsptool();
        string Folder(string path) => Path.GetDirectoryName(path)!;

        // the variable must hold it: a file, or the folder it is in; it wins over the PATH
        Assert.Equal(new EsptoolTool(own, [], "esptool.exe"),
            Find(_ => false, ("CHIPSHARP_ESPTOOL", own), ("PATH", Folder(onPath))));
        Assert.Equal(new EsptoolTool(own, [], "esptool.exe"), Find(_ => false, ("CHIPSHARP_ESPTOOL", $"\"{Folder(own)}\"")));
        var missing = Path.Combine(_scratch, "nowhere", "esptool.exe");
        Assert.StartsWith($"esptool was not found at '{missing}' (set by the environment variable CHIPSHARP_ESPTOOL). ",
            Assert.Throws<ChipSharpException>(() => Find(_ => true, ("CHIPSHARP_ESPTOOL", missing), ("PATH", Folder(py)))).Message);

        // the PATH before Python's module; esptool.exe before esptool.py
        Assert.Equal(new EsptoolTool(onPath, [], "esptool.exe"),
            Find(_ => true, ("PATH", $"{Folder(py)}{Path.PathSeparator}{Folder(onPath)}")));
        Assert.Equal(new EsptoolTool(py, [script], "esptool.py"),
            Find(_ => true, ("PATH", $"{Folder(py)}{Path.PathSeparator}{Folder(script)}")));

        // then py, then python: the first that imports esptool
        Assert.Equal(new EsptoolTool(py, ["-m", "esptool"], "py -m esptool"),
            Find(_ => true, ("PATH", $"{Folder(python)}{Path.PathSeparator}{Folder(py)}")));
        Assert.Equal(new EsptoolTool(python, ["-m", "esptool"], "python -m esptool"),
            Find(path => { probed.Add(path); return path == python; },
                ("PATH", $"{Folder(python)}{Path.PathSeparator}{Folder(py)}")));
        Assert.Equal([py, python], probed);
    }

    [Fact]
    public async Task Without_esptool_the_error_says_where_it_looked_and_how_to_get_it()
    {
        var python = Executable(Path.Combine(_scratch, "python", "python.exe"));
        var flasher = new EsptoolFlasher(name => name == "PATH" ? Path.GetDirectoryName(python) : null,
            () => throw new InvalidOperationException("The ports are not listed before esptool is found."), _ => false);

        var error = Assert.Throws<ChipSharpException>(flasher.FindEsptool);

        Assert.StartsWith("esptool was not found (looked on the PATH, and for a Python that has it). ", error.Message);
        var tools = Executable(Path.Combine(_scratch, "tools", "esptool.exe"));
        Assert.Equal(new EsptoolTool(tools, [], "esptool.exe"),
            new EsptoolFlasher(_ => null, () => [], _ => false, [Path.GetDirectoryName(tools)!]).FindEsptool());
        Assert.Contains("py -m pip install esptool", error.Message);
        Assert.EndsWith("set CHIPSHARP_ESPTOOL to its esptool.exe, or put its folder on the PATH.", error.Message);
        Assert.Equal(error.Message, (await Assert.ThrowsAsync<ChipSharpException>(() =>
            flasher.FlashAsync(Image(), Shipped.Load("esp32c5-devkitc"), new FlashOptions(), null, CancellationToken.None))).Message);
    }

    [Fact]
    public void The_port_is_the_C5s_USB_Serial_JTAG_then_its_UART_bridge_otherwise_the_error_says_what_to_plug_in()
    {
        SerialPortInfo bluetooth = new("COM3", "Standard Serial over Bluetooth link");
        SerialPortInfo jtag = new("COM6", "USB Serial Device", 0x303a, 0x1001);
        SerialPortInfo cp2102 = new("COM8", "Silicon Labs CP210x USB to UART Bridge", 0x10c4, 0xea60);
        SerialPortInfo ch343 = new("COM9", "USB-Enhanced-SERIAL CH343", 0x1a86, 0x55d3);
        SerialPortInfo uno = new("COM5", "Arduino Uno", 0x2341, 0x0043);

        Assert.Equal("COM6", EspPorts.Choose([bluetooth, uno, cp2102, jtag]));   // the chip's own port first
        Assert.Equal("COM8", EspPorts.Choose([bluetooth, cp2102, uno]));
        Assert.Equal("COM9", EspPorts.Choose([ch343]));

        Assert.Equal(
            "2 serial ports look like an ESP32 board's: COM8 (Silicon Labs CP210x USB to UART Bridge), COM9 " +
            "(USB-Enhanced-SERIAL CH343). Say which is the board's with --port, e.g. --port COM8.",
            Assert.Throws<ChipSharpException>(() => EspPorts.Choose([cp2102, ch343])).Message);
        var none = Assert.Throws<ChipSharpException>(() => EspPorts.Choose([bluetooth, uno]));
        Assert.StartsWith("None of the serial ports looks like an ESP32-C5 board's: COM3 (Standard Serial over Bluetooth link), " +
            "COM5 (Arduino Uno). Plug the board in by USB: either USB-C port of the ESP32-C5-DevKitC-1 works, the one marked " +
            "\"USB\"", none.Message);
        Assert.Contains("or give its port with --port, e.g. --port COM5.", none.Message);
        Assert.StartsWith("No serial port was found. Plug the board in by USB", Assert.Throws<ChipSharpException>(() => EspPorts.Choose([])).Message);
    }

    [Fact]
    public async Task A_flash_runs_esptool_on_the_port_and_reports_each_stage()
    {
        var esptool = new FakeTool(Path.Combine(_scratch, "esptool"), "esptool", 0, "esptool v5.0.2");
        var flasher = new EsptoolFlasher(name => name == "CHIPSHARP_ESPTOOL" ? esptool.Path : null,
            () => [new SerialPortInfo("COM3", "Standard Serial over Bluetooth link"), new SerialPortInfo("COM6", "USB Serial Device", 0x303a, 0x1001)],
            _ => false);
        var image = Image();
        var stages = new List<FlashProgress>();

        await flasher.FlashAsync(image, Shipped.Load("esp32c5-devkitc"), new FlashOptions(), new Stages(stages), CancellationToken.None);

        var commandLine = EsptoolFlasher.CommandLine(EsptoolFlasher.Arguments(C5, 5, "COM6", image));
        Assert.Equal(commandLine, esptool.CommandLine);
        Assert.Equal(
            [
                new FlashProgress($"COM6: esptool.cmd {commandLine}", 0),
                new FlashProgress("COM6: written and verified; the esp32c5-devkitc runs it", 1),
            ],
            stages);

        // esptool 4 gets write_flash; a port given is taken as it is, without listing the ports
        var old = new FakeTool(Path.Combine(_scratch, "esptool4"), "esptool", 0, "esptool.py v4.8.1");
        var given = new EsptoolFlasher(name => name == "CHIPSHARP_ESPTOOL" ? old.Path : null,
            () => throw new InvalidOperationException("A port was given."), _ => false);
        await given.FlashAsync(image, Shipped.Load("esp32c5-devkitc"), new FlashOptions(" COM12 "), null, CancellationToken.None);
        Assert.Equal(EsptoolFlasher.CommandLine(EsptoolFlasher.Arguments(C5, 4, "COM12", image)), old.CommandLine);
        Assert.Contains("write_flash 0x2000", old.CommandLine);
    }

    [Fact]
    public async Task A_failed_flash_says_what_is_likely_wrong_and_what_esptool_said()
    {
        var image = Image();
        async Task<string> Failure(params string[] said)
        {
            var esptool = new FakeTool(Path.Combine(_scratch, Guid.NewGuid().ToString("N")), "esptool", 2, said);
            var flasher = new EsptoolFlasher(name => name == "CHIPSHARP_ESPTOOL" ? esptool.Path : null, () => [], _ => false);
            return (await Assert.ThrowsAsync<ChipSharpException>(() => flasher.FlashAsync(image,
                Shipped.Load("esp32c5-devkitc"), new FlashOptions("COM6"), null, CancellationToken.None))).Message;
        }

        Assert.Equal(
            "esptool could not flash 'Blink.bin' to the board on COM6 (exit code 2). The chip on COM6 is not in download " +
            "mode: hold the BOOT button, press and release RESET, then release BOOT, and flash again (esptool normally " +
            "does this by itself through the USB port's reset lines); check that it is the board's port and the cable " +
            "carries data.\n" +
            "esptool said:\n" +
            "  A fatal error occurred: Failed to connect to ESP32-C5: No serial data received.",
            await Failure("A fatal error occurred: Failed to connect to ESP32-C5: No serial data received."));
        Assert.Contains("COM6 could not be opened", await Failure("could not open port 'COM6': PermissionError(13, 'Access is denied.')"));
        Assert.Contains("does not know the chip esp32c5: update it (py -m pip install -U esptool)",
            await Failure("esptool: error: argument --chip: invalid choice: 'esp32c5'"));
        Assert.Contains("The chip on that board is not a esp32c5", await Failure("A fatal error occurred: This chip is ESP32-S3 not ESP32-C5."));
        Assert.Equal("esptool could not flash 'Blink.bin' to the board on COM6 (exit code 2). esptool printed nothing.",
            await Failure());
    }

    /// <summary>An image to flash: its contents do not matter to a stand-in esptool.</summary>
    private string Image() => Executable(Path.Combine(_scratch, "program", "Blink.bin"), "image");

    private static string Executable(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Keeps every stage reported, in order, as it comes.</summary>
    private sealed class Stages(List<FlashProgress> stages) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => stages.Add(value);
    }
}
