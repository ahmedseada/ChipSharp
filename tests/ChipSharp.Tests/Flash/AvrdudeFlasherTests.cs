using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Flasher.Avrdude;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;

namespace ChipSharp.Tests.Flash;

/// <summary>
/// The flasher "avrdude", for the Arduino Uno: the command line it runs (as the Arduino IDE's), where it finds avrdude
/// (CHIPSHARP_AVRDUDE, the PATH, the Arduino IDE's copy) and what it says when there is none, which serial port it
/// takes, and a flash with a stand-in for avrdude (<see cref="FakeAvrdude"/>), which works and which fails.
/// </summary>
public sealed class AvrdudeFlasherTests : IDisposable
{
    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private static readonly AvrdudeSettings Uno = new("arduino", "atmega328p", 115200);

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void The_Uno_is_flashed_by_avrdude_through_its_bootloader_with_the_Arduino_IDEs_command_line()
    {
        var uno = Shipped.Load("uno");

        Assert.Equal("avrdude", uno.Flasher);
        Assert.Equal(Uno, AvrdudeSettings.Read(uno));
        Assert.Equal(
            ["-c", "arduino", "-p", "atmega328p", "-P", "COM5", "-b", "115200", "-D", "-U", @"flash:w:C:\Blink\Blink.hex:i"],
            AvrdudeFlasher.Arguments(Uno, "COM5", @"C:\Blink\Blink.hex"));
        // the Arduino IDE's copy keeps its avrdude.conf in etc, next to bin
        Assert.Equal(["-C", @"C:\avrdude\etc\avrdude.conf", "-c", "arduino"],
            AvrdudeFlasher.Arguments(Uno, "COM5", @"C:\Blink\Blink.hex", @"C:\avrdude\etc\avrdude.conf").Take(4));
        Assert.Equal(
            @"-c arduino -p atmega328p -P COM7 -b 115200 -D -U ""flash:w:C:\My programs\Blink.hex:i""",
            AvrdudeFlasher.CommandLine(AvrdudeFlasher.Arguments(Uno, "COM7", @"C:\My programs\Blink.hex")));

        var registry = DefaultComponents.CreateFlasherRegistry();
        Assert.IsType<AvrdudeFlasher>(registry.Get<IFlasher>("avrdude"));
        Assert.Equal(["avrdude", "uf2-copy"], registry.Names<IFlasher>());
    }

    [Fact]
    public void A_board_file_gives_the_bootloaders_programmer_and_speed_and_may_name_the_part()
    {
        var targets = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("nano-old", """{ "inherits": "uno", "avrdude": { "baud": 57600, "part": "m328p" } }"""),
            ("no-programmer", """{ "inherits": "uno", "avrdude": { "programmer": null } }"""),
            ("no-baud", """{ "inherits": "uno", "avrdude": { "baud": null } }"""),
            ("bad-baud", """{ "inherits": "uno", "avrdude": { "baud": "fast" } }""")));

        Assert.Equal(new AvrdudeSettings("arduino", "m328p", 57600), AvrdudeSettings.Read(targets.Load("nano-old")));
        Assert.Equal(
            "Target 'no-programmer' is flashed with avrdude but does not say how avrdude talks to its bootloader " +
            "(\"avrdude\": { \"programmer\": \"arduino\" } for an Uno's).",
            Assert.Throws<ChipSharpException>(() => AvrdudeSettings.Read(targets.Load("no-programmer"))).Message);
        Assert.Equal(
            "Target 'no-baud' is flashed with avrdude but does not say the speed its bootloader listens at " +
            "(\"avrdude\": { \"baud\": 115200 } for an Uno's).",
            Assert.Throws<ChipSharpException>(() => AvrdudeSettings.Read(targets.Load("no-baud"))).Message);
        Assert.Equal(
            "Target 'bad-baud': \"avrdude.baud\" must be a number of bits a second, e.g. 115200, not 'fast'.",
            Assert.Throws<ChipSharpException>(() => AvrdudeSettings.Read(targets.Load("bad-baud"))).Message);
    }

    [Fact]
    public void Avrdude_is_found_by_CHIPSHARP_AVRDUDE_then_on_the_PATH_then_in_the_Arduino_IDEs_folders()
    {
        var onPath = Executable(Path.Combine(_scratch, "path", "avrdude.exe"));
        var own = Executable(Path.Combine(_scratch, "own", "avrdude.exe"));
        var local = Path.Combine(_scratch, "local");
        var tools = Path.Combine(local, "Arduino15", "packages", "arduino", "tools", "avrdude");
        Executable(Path.Combine(tools, "6.3.0-arduino17", "bin", "avrdude.exe"));
        var newest = Executable(Path.Combine(tools, "7.1-arduino.1", "bin", "avrdude.exe"));
        var config = Executable(Path.Combine(tools, "7.1-arduino.1", "etc", "avrdude.conf"));
        Executable(Path.Combine(tools, "6.10-arduino1", "bin", "avrdude.exe"));
        AvrdudeTool Find(params (string Name, string Value)[] variables) =>
            new AvrdudeFlasher(name => variables.FirstOrDefault(v => v.Name == name).Value, () => []).FindAvrdude();

        // the variable must hold it: a file, or the folder it is in
        Assert.Equal(new AvrdudeTool(own, null), Find(("CHIPSHARP_AVRDUDE", own), ("PATH", Path.GetDirectoryName(onPath)!)));
        Assert.Equal(new AvrdudeTool(own, null), Find(("CHIPSHARP_AVRDUDE", $"\"{Path.GetDirectoryName(own)}\"")));
        var missing = Path.Combine(_scratch, "nowhere", "avrdude.exe");
        var wrong = Assert.Throws<ChipSharpException>(() => Find(("CHIPSHARP_AVRDUDE", missing), ("LOCALAPPDATA", local)));
        Assert.StartsWith($"avrdude was not found at '{missing}' (set by the environment variable CHIPSHARP_AVRDUDE). ",
            wrong.Message);

        // the PATH before the IDE's; of the IDE's versions the newest, with its configuration file from etc
        Assert.Equal(new AvrdudeTool(onPath, null), Find(("PATH", Path.GetDirectoryName(onPath)!), ("LOCALAPPDATA", local)));
        Assert.Equal(new AvrdudeTool(newest, config), Find(("PATH", Path.Combine(_scratch, "empty")), ("LOCALAPPDATA", local)));
    }

    [Fact]
    public async Task Without_avrdude_the_error_says_where_it_looked_and_how_to_get_it()
    {
        var local = Path.Combine(_scratch, "local");
        var flasher = new AvrdudeFlasher(name => name switch
        {
            "LOCALAPPDATA" => local,
            "PATH" => Path.Combine(_scratch, "empty"),
            _ => null,
        }, () => throw new InvalidOperationException("The ports are not listed before avrdude is found."));

        var error = Assert.Throws<ChipSharpException>(flasher.FindAvrdude);

        Assert.StartsWith(
            "avrdude was not found (looked on the PATH and in the Arduino IDE's folders " +
            $"('{Path.Combine(local, "Arduino15", "packages", "arduino", "tools", "avrdude")}'",
            error.Message);
        Assert.Contains("Get it with the Arduino IDE (https://www.arduino.cc/en/software", error.Message);
        Assert.Contains("https://github.com/avrdudes/avrdude/releases", error.Message);
        Assert.EndsWith("set CHIPSHARP_AVRDUDE to its avrdude.exe, or put its folder on the PATH.", error.Message);
        Assert.Equal(error.Message, (await Assert.ThrowsAsync<ChipSharpException>(() =>
            flasher.FlashAsync(Hex(), Shipped.Load("uno"), new FlashOptions(), null, CancellationToken.None))).Message);
    }

    [Fact]
    public void The_port_is_the_one_that_looks_like_an_Arduinos_otherwise_the_error_lists_them()
    {
        SerialPortInfo bluetooth = new("COM3", "Standard Serial over Bluetooth link");
        SerialPortInfo uno = new("COM5", "Arduino Uno", 0x2341, 0x0043);
        SerialPortInfo clone = new("COM7", "USB-SERIAL CH340", 0x1a86, 0x7523);
        SerialPortInfo other = new("COM9", "USB Serial Device", 0x1234, 0x5678);

        Assert.Equal("COM5", SerialPorts.Choose([bluetooth, uno, other]));
        Assert.Equal("COM7", SerialPorts.Choose([clone, bluetooth]));
        Assert.True(new SerialPortInfo("COM4", "Arduino Mega 2560").LooksLikeArduino);    // no ids: by its name
        Assert.True(new SerialPortInfo("/dev/ttyACM0").LooksLikeArduino);                 // Linux's USB serial ports
        Assert.True(new SerialPortInfo("COM8", "Arduino Uno R4", 0x2341, 0x1002).LooksLikeArduino);
        Assert.True(new SerialPortInfo("COM6", "FT232R USB UART", 0x0403, 0x6001).LooksLikeArduino);
        Assert.False(new SerialPortInfo("COM1", "Communications Port").LooksLikeArduino);

        Assert.Equal(
            "2 serial ports look like an Arduino's: COM5 (Arduino Uno), COM7 (USB-SERIAL CH340). Say which is the " +
            "board's with --port, e.g. --port COM5.",
            Assert.Throws<ChipSharpException>(() => SerialPorts.Choose([uno, clone])).Message);
        Assert.Equal(
            "None of the serial ports looks like an Arduino's: COM3 (Standard Serial over Bluetooth link), COM9 (USB " +
            "Serial Device). Plug the board in by USB, or give its port with --port, e.g. --port COM9.",
            Assert.Throws<ChipSharpException>(() => SerialPorts.Choose([bluetooth, other])).Message);
        Assert.StartsWith("No serial port was found: plug the board in by USB, or give its port with --port (e.g. --port ",
            Assert.Throws<ChipSharpException>(() => SerialPorts.Choose([])).Message);
    }

    [Fact]
    public void Windows_names_ports_and_USB_devices_as_the_registry_has_them()
    {
        Assert.Equal("Arduino Uno", SerialPorts.Description("Arduino Uno (COM5)"));
        Assert.Equal("USB-SERIAL CH340", SerialPorts.Description("USB-SERIAL CH340 (COM12) "));
        Assert.Null(SerialPorts.Description(" "));
        Assert.Equal((0x2341, 0x0043), SerialPorts.UsbIds("VID_2341&PID_0043"));
        Assert.Equal((0x2341, 0x8036), SerialPorts.UsbIds("VID_2341&PID_8036&MI_00"));
        Assert.Equal((0x0403, 0x6001), SerialPorts.UsbIds("VID_0403+PID_6001+A50285BIA"));
        Assert.Null(SerialPorts.UsbIds("{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000"));

        // whatever this computer has: each port once, by name
        var ports = SerialPorts.List();
        Assert.Equal(ports.Count, ports.Select(port => port.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task A_flash_runs_avrdude_on_the_port_and_reports_each_stage()
    {
        var avrdude = new FakeAvrdude(Path.Combine(_scratch, "avrdude"), 0, "avrdude: 1234 bytes of flash verified");
        var flasher = new AvrdudeFlasher(name => name == "CHIPSHARP_AVRDUDE" ? avrdude.Path : null,
            () => [new SerialPortInfo("COM3", "Standard Serial over Bluetooth link"), new SerialPortInfo("COM5", "Arduino Uno", 0x2341, 0x0043)]);
        var hex = Hex();
        var stages = new List<FlashProgress>();

        await flasher.FlashAsync(hex, Shipped.Load("uno"), new FlashOptions(), new Stages(stages), CancellationToken.None);

        var commandLine = AvrdudeFlasher.CommandLine(AvrdudeFlasher.Arguments(Uno, "COM5", hex));
        Assert.Equal(commandLine, avrdude.CommandLine);
        Assert.Equal(
            [
                new FlashProgress($"COM5: avrdude {commandLine}", 0),
                new FlashProgress("COM5: written and verified; the uno runs it", 1),
            ],
            stages);

        // a port given is taken as it is, without listing the ports
        var given = new AvrdudeFlasher(name => name == "CHIPSHARP_AVRDUDE" ? avrdude.Path : null,
            () => throw new InvalidOperationException("A port was given."));
        await given.FlashAsync(hex, Shipped.Load("uno"), new FlashOptions(" COM12 "), null, CancellationToken.None);
        Assert.Equal(AvrdudeFlasher.CommandLine(AvrdudeFlasher.Arguments(Uno, "COM12", hex)), avrdude.CommandLine);
    }

    [Fact]
    public async Task A_failed_flash_says_what_is_likely_wrong_and_what_avrdude_said()
    {
        var hex = Hex();
        async Task<string> Failure(params string[] said)
        {
            var avrdude = new FakeAvrdude(Path.Combine(_scratch, Guid.NewGuid().ToString("N")), 1, said);
            var flasher = new AvrdudeFlasher(name => name == "CHIPSHARP_AVRDUDE" ? avrdude.Path : null, () => []);
            return (await Assert.ThrowsAsync<ChipSharpException>(() =>
                flasher.FlashAsync(hex, Shipped.Load("uno"), new FlashOptions("COM5"), null, CancellationToken.None))).Message;
        }

        Assert.Equal(
            "avrdude could not flash 'Blink.hex' to the board on COM5 (exit code 1). The board on COM5 did not answer as " +
            "its bootloader should: check that it is the target's board on that port (another board's bootloader may " +
            "need another programmer or speed than arduino at 115200 baud: the target's \"avrdude\" settings), close " +
            "any serial monitor that has the port, and unplug the board and plug it in again.\n" +
            "avrdude said:\n" +
            "  avrdude: stk500_recv(): programmer is not responding\n" +
            "  avrdude: stk500_getsync() attempt 10 of 10: not in sync: resp=0x00",
            await Failure("avrdude: stk500_recv(): programmer is not responding",
                "avrdude: stk500_getsync() attempt 10 of 10: not in sync: resp=0x00"));
        Assert.StartsWith(
            "avrdude could not flash 'Blink.hex' to the board on COM5 (exit code 1). COM5 could not be opened: check that " +
            "it is the board's port",
            await Failure("avrdude: ser_open(): can't open device \"\\\\.\\COM5\": Access is denied."));
        Assert.Contains("The chip on that board is not the target's (atmega328p)",
            await Failure("avrdude: Expected signature for ATmega328P is 1E 95 0F"));
        Assert.Equal("avrdude could not flash 'Blink.hex' to the board on COM5 (exit code 1). avrdude printed nothing.",
            await Failure());
    }

    /// <summary>An image to flash: its contents do not matter to a stand-in avrdude.</summary>
    private string Hex() => Executable(Path.Combine(_scratch, "program", "Blink.hex"), ":00000001FF\r\n");

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
