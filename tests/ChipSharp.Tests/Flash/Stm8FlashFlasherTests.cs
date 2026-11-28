using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Flasher.Shared;
using ChipSharp.Flasher.Stm8Flash;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;

namespace ChipSharp.Tests.Flash;

/// <summary>
/// The flasher "stm8flash", for the STM8S103F3 (stm8s103-board) through an ST-LINK: the command line it runs, where it
/// finds stm8flash (CHIPSHARP_STM8FLASH, the PATH, sduino's copy) and what it says when there is none, which ST-LINK it
/// names (from the USB devices plugged in), and a flash with a stand-in for stm8flash (<see cref="FakeTool"/>), which
/// works and which fails: a locked chip, no ST-LINK, the SWIM wires.
/// </summary>
public sealed class Stm8FlashFlasherTests : IDisposable
{
    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    /// <summary>An ST-LINK V2 clone, as Windows lists it (the driver ST's WinUSB one).</summary>
    private static readonly UsbDeviceInfo V2 = new(0x0483, 0x3748, "STM32 STLink", "WINUSB");

    /// <summary>A Nucleo board's ST-LINK/V2-1.</summary>
    private static readonly UsbDeviceInfo V21 = new(0x0483, 0x374b, "ST-Link Debug", "usbccgp");

    private static readonly UsbDeviceInfo V3 = new(0x0483, 0x374f, "STLINK-V3", "usbccgp");

    /// <summary>The Easy PDK programmer: an ST USB id, no ST-LINK.</summary>
    private static readonly UsbDeviceInfo VirtualComPort = new(0x0483, 0x5740, "USB Serial Device", "usbser");

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void The_STM8S103_is_written_by_stm8flash_through_an_ST_LINK_V2()
    {
        var board = Shipped.Load("stm8s103-board");

        Assert.Equal("stm8flash", board.Flasher);
        Assert.Equal("hex", board.Image);
        Assert.Equal(new Stm8FlashSettings("stlinkv2", "stm8s103f3"), Stm8FlashSettings.Read(board));
        Assert.Equal(["-c", "stlinkv2", "-p", "stm8s103f3", "-s", "flash", "-w", @"C:\Blink\Blink.hex"],
            Stm8FlashFlasher.Arguments("stlinkv2", "stm8s103f3", @"C:\Blink\Blink.hex"));
        // more than one ST-LINK: the one with this serial number (--port)
        Assert.Equal(["-c", "stlinkv2", "-S", "48FF6B064975545228131887", "-p", "stm8s103f3", "-s", "flash", "-w", @"C:\Blink\Blink.hex"],
            Stm8FlashFlasher.Arguments("stlinkv2", "stm8s103f3", @"C:\Blink\Blink.hex", "48FF6B064975545228131887"));
        Assert.Equal(["-c", "stlinkv2", "-p", "stm8s103f3", "-u"], Stm8FlashFlasher.UnlockArguments("stlinkv2", "stm8s103f3"));
        Assert.Equal(@"-c stlinkv2 -p stm8s103f3 -s flash -w ""C:\My programs\Blink.hex""",
            ProgrammerTool.CommandLine(Stm8FlashFlasher.Arguments("stlinkv2", "stm8s103f3", @"C:\My programs\Blink.hex")));

        Assert.IsType<Stm8FlashFlasher>(DefaultComponents.CreateFlasherRegistry().Get<IFlasher>("stm8flash"));
    }

    [Fact]
    public void A_board_file_names_the_ST_LINK_and_the_part_as_stm8flash_does()
    {
        var targets = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("nucleo-8s208", """{ "inherits": "stm8s103-board", "stm8flash": { "programmer": "stlinkv21", "part": "stm8s208rb" } }"""),
            ("no-programmer", """{ "inherits": "stm8s103-board", "stm8flash": { "programmer": null } }"""),
            ("no-part", """{ "inherits": "stm8s103-board", "stm8flash": { "part": null } }""")));

        Assert.Equal(new Stm8FlashSettings("stlinkv21", "stm8s208rb"), Stm8FlashSettings.Read(targets.Load("nucleo-8s208")));
        Assert.Equal(
            "Target 'no-programmer' is flashed with stm8flash but does not say which ST-LINK stm8flash writes it through " +
            "(\"stm8flash\": { \"programmer\": \"stlinkv2\" }).",
            Assert.Throws<ChipSharpException>(() => Stm8FlashSettings.Read(targets.Load("no-programmer"))).Message);
        Assert.Equal(
            "Target 'no-part' is flashed with stm8flash but does not say which chip it is, as stm8flash names it " +
            "(\"stm8flash\": { \"part\": \"stm8s103f3\" }).",
            Assert.Throws<ChipSharpException>(() => Stm8FlashSettings.Read(targets.Load("no-part"))).Message);
    }

    [Fact]
    public void Stm8flash_is_found_by_CHIPSHARP_STM8FLASH_then_on_the_PATH_then_in_the_sduino_cores_tools()
    {
        var onPath = File(Path.Combine(_scratch, "path", "stm8flash.exe"));
        var own = File(Path.Combine(_scratch, "own", "stm8flash.exe"));
        var local = Path.Combine(_scratch, "local");
        var tools = Path.Combine(local, "Arduino15", "packages", "sduino", "tools", "STM8Tools");
        File(Path.Combine(tools, "2018.12.07", "win", "stm8flash.exe"));
        var newest = File(Path.Combine(tools, "2019.02.05", "win", "stm8flash.exe"));
        string Find(params (string Name, string Value)[] variables) =>
            new Stm8FlashFlasher(name => variables.FirstOrDefault(v => v.Name == name).Value, () => []).FindStm8Flash();

        Assert.Equal(own, Find(("CHIPSHARP_STM8FLASH", own), ("PATH", Path.GetDirectoryName(onPath)!)));
        Assert.Equal(own, Find(("CHIPSHARP_STM8FLASH", Path.GetDirectoryName(own)!)));
        var missing = Path.Combine(_scratch, "nowhere");
        Assert.StartsWith(
            $"stm8flash was not found at '{Path.Combine(missing, "stm8flash.exe")}' (set by the environment variable " +
            "CHIPSHARP_STM8FLASH). ",
            Assert.Throws<ChipSharpException>(() => Find(("CHIPSHARP_STM8FLASH", Path.Combine(missing, "stm8flash.exe")))).Message);

        // the PATH before sduino's; of sduino's versions the newest
        Assert.Equal(onPath, Find(("PATH", Path.GetDirectoryName(onPath)!), ("LOCALAPPDATA", local)));
        Assert.Equal(newest, Find(("PATH", Path.Combine(_scratch, "empty")), ("LOCALAPPDATA", local)));
    }

    [Fact]
    public async Task Without_stm8flash_the_error_says_where_it_looked_and_how_to_get_it()
    {
        var local = Path.Combine(_scratch, "local");
        var flasher = new Stm8FlashFlasher(name => name switch
        {
            "LOCALAPPDATA" => local,
            "PATH" => Path.Combine(_scratch, "empty"),
            _ => null,
        }, () => throw new InvalidOperationException("The USB devices are not listed before stm8flash is found."));

        var error = Assert.Throws<ChipSharpException>(flasher.FindStm8Flash);

        Assert.StartsWith(
            $"stm8flash was not found (looked on the PATH and in '{Stm8FlashFlasher.ToolsFolder}', " +
            $"'{Path.Combine(local, "Arduino15", "packages", "sduino", "tools", "STM8Tools")}'). stm8flash writes a program " +
            "into an STM8 chip through an ST-LINK V2",
            error.Message);
        Assert.Contains("https://github.com/vdudouyt/stm8flash", error.Message);
        Assert.Contains("https://github.com/tenbaht/sduino", error.Message);
        Assert.EndsWith("set CHIPSHARP_STM8FLASH to its stm8flash.exe, or put its folder on the PATH.", error.Message);
        Assert.Equal(error.Message, (await Assert.ThrowsAsync<ChipSharpException>(() =>
            flasher.FlashAsync(Hex(), Shipped.Load("stm8s103-board"), new FlashOptions(), null, CancellationToken.None))).Message);
    }

    [Fact]
    public void The_ST_LINK_named_is_the_boards_unless_another_kind_alone_is_plugged_in()
    {
        Assert.Equal([(V2, StLinks.Kinds[1]), (V21, StLinks.Kinds[2])], StLinks.Among([VirtualComPort, V2, V21]));
        Assert.Empty(StLinks.Among([VirtualComPort, new UsbDeviceInfo(0x1a86, 0x7523)]));

        string Programmer(params UsbDeviceInfo[]? plugged) =>
            StLinks.Programmer("stlinkv2", plugged is null ? null : StLinks.Among(plugged));
        Assert.Equal("stlinkv2", Programmer(null));             // this computer cannot say
        Assert.Equal("stlinkv2", Programmer());                 // none: stm8flash says so, and the hint
        Assert.Equal("stlinkv2", Programmer(V2));
        Assert.Equal("stlinkv2", Programmer(V21, V2));          // the board's kind is there
        Assert.Equal("stlinkv21", Programmer(V21));             // a Nucleo's, alone
        Assert.Equal("stlinkv3", Programmer(V3, V3));
        Assert.Equal("stlinkv2", Programmer(V21, V3));          // two other kinds: which is for the board to say

        Assert.Equal("ST-LINK/V2-1", StLinks.Name("stlinkv21"));
        Assert.Equal("espstlink", StLinks.Name("espstlink"));
        Assert.Equal("STM32 STLink (USB 0483:3748)", V2.ToString());
    }

    [Fact]
    public void The_USB_devices_plugged_in_are_listed_by_vendor()
    {
        // whatever this computer has: ST's devices only, each once a device (not once an interface)
        var devices = UsbDevices.List(StLinks.VendorId);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.NotNull(devices);
        }

        Assert.All(devices ?? [], device => Assert.Equal(StLinks.VendorId, device.VendorId));
    }

    [Fact]
    public async Task A_flash_runs_stm8flash_with_the_ST_LINK_plugged_in_and_reports_each_stage()
    {
        var stm8flash = new FakeTool(Path.Combine(_scratch, "stm8flash"), "stm8flash", 0,
            "Determine FLASH area", "Writing Intel hex file 238 bytes at 0x8000... OK", "Bytes written: 238");
        var hex = Hex();
        Stm8FlashFlasher Flasher(params UsbDeviceInfo[] plugged) =>
            new(name => name == "CHIPSHARP_STM8FLASH" ? stm8flash.Path : null, () => plugged);
        var stages = new List<FlashProgress>();

        await Flasher(VirtualComPort, V2).FlashAsync(hex, Shipped.Load("stm8s103-board"), new FlashOptions(),
            new Stages(stages), CancellationToken.None);

        var commandLine = ProgrammerTool.CommandLine(Stm8FlashFlasher.Arguments("stlinkv2", "stm8s103f3", hex));
        Assert.Equal(commandLine, stm8flash.CommandLine);
        Assert.Equal(
            [
                new FlashProgress($"ST-LINK/V2: stm8flash {commandLine}", 0),
                new FlashProgress("ST-LINK/V2: written, and the stm8s103f3 restarted; the stm8s103-board runs it", 1),
            ],
            stages);

        // a Nucleo's ST-LINK/V2-1 alone: stm8flash looks for its ids with -c stlinkv21
        stages.Clear();
        await Flasher(V21).FlashAsync(hex, Shipped.Load("stm8s103-board"), new FlashOptions(), new Stages(stages),
            CancellationToken.None);
        Assert.Equal($"-c stlinkv21 -p stm8s103f3 -s flash -w {hex}", stm8flash.CommandLine);
        Assert.StartsWith("ST-LINK/V2-1: stm8flash -c stlinkv21", stages[0].Stage);

        // --port is the ST-LINK's serial number
        await Flasher(V2, V2).FlashAsync(hex, Shipped.Load("stm8s103-board"), new FlashOptions(" 48FF6B06 "), null,
            CancellationToken.None);
        Assert.Equal($"-c stlinkv2 -S 48FF6B06 -p stm8s103f3 -s flash -w {hex}", stm8flash.CommandLine);
    }

    [Fact]
    public async Task A_failed_flash_says_what_is_likely_wrong_and_what_stm8flash_said()
    {
        var hex = Hex();
        async Task<string> Failure(UsbDeviceInfo[]? plugged, string? serial, params string[] said)
        {
            var stm8flash = new FakeTool(Path.Combine(_scratch, Guid.NewGuid().ToString("N")), "stm8flash", -1, said);
            var flasher = new Stm8FlashFlasher(name => name == "CHIPSHARP_STM8FLASH" ? stm8flash.Path : null, () => plugged);
            return (await Assert.ThrowsAsync<ChipSharpException>(() =>
                flasher.FlashAsync(hex, Shipped.Load("stm8s103-board"), new FlashOptions(serial), null, CancellationToken.None))).Message;
        }

        // the blue boards often come read-out protected: the unlock command, which erases the chip
        Assert.Equal(
            "stm8flash could not write 'Blink.hex' into the stm8s103f3 through the ST-LINK/V2 (exit code -1). The " +
            "stm8s103f3 is most likely read-out protected (ROP, an option byte), as many of these boards come from the " +
            "factory, or its flash is write-protected (UBC): unlock it, which erases it, with stm8flash -c stlinkv2 -p " +
            "stm8s103f3 -u, then flash it again.\n" +
            "stm8flash said:\n" +
            "  Determine FLASH area\n" +
            "  target page is write protected (UBC) or read-out protection is enabled",
            await Failure([V2], null, "Determine FLASH area",
                "target page is write protected (UBC) or read-out protection is enabled"));
        Assert.Contains("unlock it, which erases it, with stm8flash -c stlinkv2 -S 48FF6B06 -p stm8s103f3 -u",
            await Failure([V2, V2], "48FF6B06", "Writing Intel hex file 238 bytes at 0x8000... Tries exceeded"));

        // no ST-LINK it could open: none plugged in, one without a driver, one that something else has
        Assert.Contains("No ST-LINK is plugged in (no USB device has an ST-LINK's ids, 0483:3744, 0483:3748, 0483:374b, " +
            "0483:374f): plug the ST-LINK in by USB, and wire it to the board's SWIM header.",
            await Failure([VirtualComPort], null, "Could not open USB device."));
        var noDriver = await Failure([V2 with { Driver = null }], null, "Could not open USB device.");
        Assert.Contains(OperatingSystem.IsWindows()
                ? "The STM32 STLink (USB 0483:3748) is plugged in, but Windows has no driver for it: install ST's ST-LINK driver"
                : "The STM32 STLink (USB 0483:3748) is plugged in, but stm8flash could not open it",
            noDriver);
        Assert.Contains("The STM32 STLink (USB 0483:3748) is plugged in, but stm8flash could not open it: close what else may have it",
            await Failure([V2], null, "Could not open USB device."));
        Assert.Contains("stm8flash found no ST-LINK it could open: plug it in by USB",
            await Failure(null, null, "Could not open USB device."));
        Assert.Contains("The ST-LINKs plugged in are ST-Link Debug (USB 0483:374b), STLINK-V3 (USB 0483:374f), not the board's ST-LINK/V2",
            await Failure([V21, V3], null, "Could not open USB device."));
        Assert.Contains("No ST-LINK has the serial number 1234 (given with --port): stm8flash -c stlinkv2 -L lists",
            await Failure([V2, V2], "1234", "ERROR: No programmer with serial number 1234 found."));

        // the ST-LINK is there, the chip does not answer: the SWIM wires
        Assert.Contains("The ST-LINK/V2 did not reach the stm8s103f3 through SWIM: wire its SWIM pins to the board's header",
            await Failure([V2], null, "Error communicating with MCU. Please check your SWIM connection."));
        Assert.Equal("stm8flash could not write 'Blink.hex' into the stm8s103f3 through the ST-LINK/V2 (exit code -1). " +
            "stm8flash printed nothing.", await Failure([V2], null));
    }

    /// <summary>An image to flash: its contents do not matter to a stand-in stm8flash.</summary>
    private string Hex() => File(Path.Combine(_scratch, "program", "Blink.hex"), ":00000001FF\r\n");

    private static string File(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Keeps every stage reported, in order, as it comes.</summary>
    private sealed class Stages(List<FlashProgress> stages) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => stages.Add(value);
    }
}
