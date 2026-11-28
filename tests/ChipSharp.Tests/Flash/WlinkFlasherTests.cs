using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Flasher.Shared;
using ChipSharp.Flasher.WchLink;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;
using static ChipSharp.Tests.Flash.WchLinkTestBoard;

namespace ChipSharp.Tests.Flash;

/// <summary>
/// The flasher "wlink", for a CH32V003 board (<see cref="WchLinkTestBoard"/>) through a WCH-LinkE: the command line it
/// runs (the chip, the .bin's address, the WCH-LinkE to use), where it finds wlink (CHIPSHARP_WLINK, the PATH, Cargo's
/// bin) and what it says when there is none, the WCH-Links plugged in told apart by their USB ids and the console's
/// serial port, and a flash with a stand-in for wlink (<see cref="FakeTool"/>), which works and which fails: no
/// WCH-LinkE, one in its ARM mode or without a driver, another chip, the wires, read-out protection.
/// </summary>
public sealed class WlinkFlasherTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The tools folder the flasher looks in instead of D:\Tools\..., which holds nothing: what is installed on
    /// this computer must not decide the tests.</summary>
    private string Tools => Path.Combine(_scratch, "tools");

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void Wlink_writes_the_bin_at_the_flash_origin_for_the_chip_the_board_names()
    {
        var board = Board();

        Assert.Equal(("bin", "wlink"), (board.Image, board.Flasher));
        Assert.Equal(new WlinkSettings("CH32V003"), WlinkSettings.Read(board));
        Assert.Equal("0x08000000", WchLinks.FlashOrigin(board));
        Assert.Equal(["--chip", "CH32V003", "flash", "--address", "0x08000000", @"C:\Blink\Blink.bin"],
            WlinkFlasher.Arguments("CH32V003", @"C:\Blink\Blink.bin", "0x08000000"));
        // a .hex or .elf has its addresses; more than one WCH-LinkE: the one given (--port: wlink list's index or serial)
        Assert.Equal(["--chip", "CH32V003", "flash", @"C:\Blink\Blink.hex"], WlinkFlasher.Arguments("CH32V003", @"C:\Blink\Blink.hex"));
        Assert.Equal(["-d", "F93D8F067E10", "--chip", "CH32V003", "flash", "--address", "0x08000000", @"C:\Blink\Blink.bin"],
            WlinkFlasher.Arguments("CH32V003", @"C:\Blink\Blink.bin", "0x08000000", "F93D8F067E10"));
        Assert.Equal(["--chip", "CH32V003", "unprotect"], WlinkFlasher.UnprotectArguments("CH32V003"));
        Assert.Equal(["-d", "1", "--chip", "CH32V003", "erase", "--method", "power-off"],
            WlinkFlasher.PowerOffEraseArguments("CH32V003", "1"));
        Assert.Equal(@"--chip CH32V003 flash --address 0x08000000 ""C:\My programs\Blink.bin""",
            ProgrammerTool.CommandLine(WlinkFlasher.Arguments("CH32V003", @"C:\My programs\Blink.bin", "0x08000000")));
        Assert.True(WlinkFlasher.IsRaw("Blink.bin"));
        Assert.False(WlinkFlasher.IsRaw("Blink.HEX"));
        Assert.False(WlinkFlasher.IsRaw("Blink.elf"));

        var registry = DefaultComponents.CreateFlasherRegistry();
        Assert.IsType<WlinkFlasher>(registry.Get<IFlasher>("wlink"));
        Assert.IsType<MinichlinkFlasher>(registry.Get<IFlasher>("minichlink"));
    }

    [Fact]
    public void A_board_file_names_the_chip_as_wlink_does_and_may_move_the_bin()
    {
        var targets = new TargetLoader(MoreTargets(
            ("ch32v203-board", """{ "inherits": "ch32-test-board", "wlink": { "chip": "CH32V20X", "address": "0x08001000" } }"""),
            ("no-chip", """{ "inherits": "ch32-test-board", "wlink": { "chip": null } }"""),
            ("bad-address", """{ "inherits": "ch32-test-board", "wlink": { "address": "flash" } }""")));

        Assert.Equal(new WlinkSettings("CH32V20X", "0x08001000"), WlinkSettings.Read(targets.Load("ch32v203-board")));
        Assert.Equal(
            "Target 'no-chip' is flashed with wlink but does not say which chip it is, as wlink names it " +
            "(\"wlink\": { \"chip\": \"CH32V003\" }).",
            Assert.Throws<ChipSharpException>(() => WlinkSettings.Read(targets.Load("no-chip"))).Message);
        Assert.Equal(
            "Target 'bad-address': \"wlink.address\" must be the address a .bin is written at, e.g. 0x08000000, not 'flash'.",
            Assert.Throws<ChipSharpException>(() => WlinkSettings.Read(targets.Load("bad-address"))).Message);
    }

    [Fact]
    public void Wlink_is_found_by_CHIPSHARP_WLINK_then_on_the_PATH_then_in_Cargos_bin()
    {
        var onPath = File(Path.Combine(_scratch, "path", "wlink.exe"));
        var own = File(Path.Combine(_scratch, "own", "wlink.exe"));
        var cargoHome = Path.Combine(_scratch, "cargo");
        var cargo = File(Path.Combine(cargoHome, "bin", "wlink.exe"));
        var user = Path.Combine(_scratch, "user");
        var userCargo = File(Path.Combine(user, ".cargo", "bin", "wlink.exe"));
        string Find(params (string Name, string Value)[] variables) =>
            new WlinkFlasher(name => variables.FirstOrDefault(v => v.Name == name).Value, () => [], () => [], Tools).FindWlink();

        Assert.Equal(own, Find(("CHIPSHARP_WLINK", own), ("PATH", Path.GetDirectoryName(onPath)!)));
        Assert.Equal(own, Find(("CHIPSHARP_WLINK", $"\"{Path.GetDirectoryName(own)!}\"")));
        var missing = Path.Combine(_scratch, "nowhere", "wlink.exe");
        Assert.StartsWith(
            $"wlink was not found at '{missing}' (set by the environment variable CHIPSHARP_WLINK). wlink writes a program",
            Assert.Throws<ChipSharpException>(() => Find(("CHIPSHARP_WLINK", missing))).Message);

        // the PATH before Cargo's bin; CARGO_HOME's before the user's .cargo
        var empty = Path.Combine(_scratch, "empty");
        Assert.Equal(onPath, Find(("PATH", Path.GetDirectoryName(onPath)!), ("CARGO_HOME", cargoHome)));
        Assert.Equal(cargo, Find(("PATH", empty), ("CARGO_HOME", cargoHome), ("USERPROFILE", user), ("HOME", user)));
        Assert.Equal(userCargo, Find(("PATH", empty), ("USERPROFILE", user), ("HOME", user)));
    }

    [Fact]
    public async Task Without_wlink_the_error_says_where_it_looked_and_how_to_get_it()
    {
        var user = Path.Combine(_scratch, "user");
        var flasher = new WlinkFlasher(name => name switch
            {
                "PATH" => Path.Combine(_scratch, "empty"),
                "USERPROFILE" or "HOME" => user,
                _ => null,
            },
            () => throw new InvalidOperationException("The USB devices are not listed before wlink is found."),
            () => throw new InvalidOperationException("The serial ports are not listed before wlink is found."), Tools);

        var error = Assert.Throws<ChipSharpException>(flasher.FindWlink);

        var looked = OperatingSystem.IsWindows() ? $"'{Tools}', " : "";
        Assert.StartsWith(
            $"wlink was not found (looked on the PATH and in {looked}'{Path.Combine(user, ".cargo", "bin")}'). wlink writes " +
            "a program into a WCH RISC-V chip (the CH32V003) through a WCH-LinkE wired to its SWIO pin. Download it from " +
            "https://github.com/ch32-rs/wlink/releases (for Windows wlink-<version>-win-x64.zip)",
            error.Message);
        Assert.Contains("cargo install --git https://github.com/ch32-rs/wlink", error.Message);
        Assert.Contains("set CHIPSHARP_WLINK to its wlink.exe, or put its folder on the PATH.", error.Message);
        Assert.EndsWith("On Windows the WCH-LinkE needs WCH's driver for its interface 0 (WCH-LinkUtility, " +
            "https://www.wch-ic.com/downloads/WCH-LinkUtility_ZIP.html, and MounRiver Studio install it) or WinUSB there " +
            "(with Zadig, https://zadig.akeo.ie: Options > List All Devices, the WCH-Link's Interface 0, WinUSB).", error.Message);
        Assert.Equal(error.Message, (await Assert.ThrowsAsync<ChipSharpException>(() =>
            flasher.FlashAsync(Image("Blink.bin"), Board(), new FlashOptions(), null, CancellationToken.None))).Message);
    }

    [Fact]
    public void The_WCH_Links_plugged_in_are_told_apart_by_mode_and_their_serial_port_is_the_console()
    {
        Assert.Equal([(Riscv, WchLinkMode.Riscv), (Dap, WchLinkMode.Dap)], WchLinks.Among([Ch340, Riscv, Dap]));
        Assert.Empty(WchLinks.Among([Ch340, new UsbDeviceInfo(0x0483, 0x3748)]));
        Assert.Equal("WCH-Link (USB 1a86:8010)", Riscv.ToString());

        // the registry's names of a WCH-LinkE and its interfaces: the device, its probe (0) and serial port (1, 2)
        Assert.Equal((0x1a86, 0x8010), SerialPorts.UsbIds("VID_1A86&PID_8010"));
        Assert.Equal((0x1a86, 0x8010), SerialPorts.UsbIds("VID_1A86&PID_8010&MI_00"));
        Assert.Null(UsbDevices.InterfaceNumber("VID_1A86&PID_8010"));
        Assert.Equal(0, UsbDevices.InterfaceNumber("VID_1A86&PID_8010&MI_00"));
        Assert.Equal(2, UsbDevices.InterfaceNumber("VID_1A86&PID_8010&mi_02"));
        Assert.Empty(new UsbDeviceInfo(0x1a86, 0x8010).InterfaceDrivers);

        // the console: the WCH-LinkE's serial port, for a board that has one
        Assert.Equal([LinkPort], WchLinks.ConsolePorts([Ch340Port, LinkPort]));
        var console = Board("ch32-test-console");
        Assert.Equal("the console is on COM7, the WCH-LinkE's serial port: wire its RX pin to the chip's TX (USART1's, PD5 " +
            "on the CH32V003)", WchLinks.ConsoleNote(console, [Ch340Port, LinkPort]));
        Assert.Equal("the console is on the serial port of one of the WCH-LinkEs (COM7 (WCH-Link SERIAL), COM9 (WCH-Link " +
            "SERIAL)): wire its RX pin to the chip's TX (USART1's, PD5 on the CH32V003)",
            WchLinks.ConsoleNote(console, [LinkPort, LinkPort with { Name = "COM9" }]));
        Assert.Null(WchLinks.ConsoleNote(console, [Ch340Port]));
        Assert.Null(WchLinks.ConsoleNote(Board(), [LinkPort]));   // no console
    }

    [Fact]
    public void WCHs_USB_devices_plugged_in_are_listed_with_the_drivers_of_their_interfaces()
    {
        // whatever this computer has: WCH's devices only, each once a device (not once an interface)
        var devices = UsbDevices.List(WchLinks.VendorId);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.NotNull(devices);
        }

        Assert.All(devices ?? [], device => Assert.Equal(WchLinks.VendorId, device.VendorId));
    }

    [Fact]
    public async Task A_flash_runs_wlink_with_the_chip_and_the_address_and_reports_each_stage()
    {
        var wlink = new FakeTool(Path.Combine(_scratch, "wlink"), "wlink", 0,
            "14:02:11 [INFO] Connected to WCH-Link v2.15(v35) (WCH-LinkE-CH32V305)", "14:02:11 [INFO] Attached chip: CH32V003",
            "14:02:12 [INFO] Flash done", "14:02:12 [INFO] Now reset...");
        var bin = Image("Blink.bin");
        WlinkFlasher Flasher(params SerialPortInfo[] ports) =>
            new(name => name == "CHIPSHARP_WLINK" ? wlink.Path : null, () => [Riscv], () => ports);
        var stages = new List<FlashProgress>();

        await Flasher(LinkPort).FlashAsync(bin, Board(), new FlashOptions(), new Stages(stages), CancellationToken.None);

        var commandLine = ProgrammerTool.CommandLine(WlinkFlasher.Arguments("CH32V003", bin, "0x08000000"));
        Assert.Equal(commandLine, wlink.CommandLine);
        Assert.Equal(
            [
                new FlashProgress($"WCH-LinkE: wlink {commandLine}", 0),
                new FlashProgress("WCH-LinkE: written, and the CH32V003 restarted; the ch32-test-board runs it", 1),
            ],
            stages);

        // a board with a console: on which port it is; --port is the WCH-LinkE (-d); a .hex says its own addresses
        stages.Clear();
        await Flasher(Ch340Port, LinkPort).FlashAsync(bin, Board("ch32-test-console"), new FlashOptions(" 1 "),
            new Stages(stages), CancellationToken.None);
        Assert.Equal($"-d 1 --chip CH32V003 flash --address 0x08000000 {bin}", wlink.CommandLine);
        Assert.Equal(
            [
                $"WCH-LinkE 1: wlink -d 1 --chip CH32V003 flash --address 0x08000000 {ChipSharpCli.Quoted(bin)}",
                "WCH-LinkE 1: written, and the CH32V003 restarted; the ch32-test-console runs it",
                "WCH-LinkE 1: the console is on COM7, the WCH-LinkE's serial port: wire its RX pin to the chip's TX " +
                    "(USART1's, PD5 on the CH32V003)",
            ],
            stages.Select(stage => stage.Stage));
        var hex = Image("Blink.hex");
        await Flasher().FlashAsync(hex, Board(), new FlashOptions(), null, CancellationToken.None);
        Assert.Equal($"--chip CH32V003 flash {hex}", wlink.CommandLine);
    }

    [Fact]
    public async Task A_failed_flash_says_what_is_likely_wrong_and_what_wlink_said()
    {
        var bin = Image("Blink.bin");
        async Task<string> Failure(UsbDeviceInfo[]? plugged, string? probe, params string[] said)
        {
            var wlink = new FakeTool(Path.Combine(_scratch, Guid.NewGuid().ToString("N")), "wlink", 1, said);
            var flasher = new WlinkFlasher(name => name == "CHIPSHARP_WLINK" ? wlink.Path : null, () => plugged, () => []);
            return (await Assert.ThrowsAsync<ChipSharpException>(() =>
                flasher.FlashAsync(bin, Board(), new FlashOptions(probe), null, CancellationToken.None))).Message;
        }

        const string NotFound = "Error: WCH-Link not found, please check your connection";

        // no WCH-LinkE: none plugged in, one in its ARM mode, one without a driver, one something else has
        Assert.Equal(
            "wlink could not write 'Blink.bin' into the CH32V003 through the WCH-LinkE (exit code 1). No WCH-LinkE is plugged " +
            "in (no USB device has its ids, 1a86:8010 in its RISC-V mode or 1a86:8012 in its ARM mode): plug it in by USB, " +
            "and wire it to the board: its SWDIO pin to the chip's SWIO pin (the CH32V003's PD1), GND to GND, and its 3V3 " +
            "(or 5V) to the board's VCC unless the board has power of its own.\n" +
            "wlink said:\n" +
            "  Error: WCH-Link not found, please check your connection",
            await Failure([Ch340], null, NotFound));
        Assert.Contains("The WCH-LinkDAP (USB 1a86:8012) is in its ARM mode (CMSIS-DAP), in which wlink cannot write a RISC-V " +
            "chip: switch it to its RISC-V mode (wlink mode-switch --rv, or WCH-LinkUtility's mode setting), then flash again.",
            await Failure([Dap], null, NotFound));
        Assert.Contains("The WCH-LinkE is in its ARM mode (CMSIS-DAP), in which wlink cannot write a RISC-V chip",
            await Failure([Dap], null, "Error: WCH-Link is connected, but is not in RV mode"));
        var noDriver = await Failure([Riscv with { InterfaceDrivers = new Dictionary<int, string?> { [0] = null } }], null, NotFound);
        Assert.Contains(OperatingSystem.IsWindows()
                ? "The WCH-Link (USB 1a86:8010) is plugged in, but Windows has no driver for its interface 0, which wlink " +
                  "talks to: install WCH's driver for its interface 0 (WCH-LinkUtility"
                : "The WCH-Link (USB 1a86:8010) is plugged in, but wlink could not open it",
            noDriver);
        Assert.Contains("The WCH-Link (USB 1a86:8010) is plugged in, but wlink could not open it: close what else may have it " +
            "(WCH-LinkUtility, MounRiver Studio, another wlink or minichlink), unplug it and plug it in again; on Windows it " +
            "needs WCH's driver", await Failure([Riscv], null, NotFound));
        Assert.Contains("wlink found no WCH-LinkE it could open: plug it in by USB, in its RISC-V mode",
            await Failure(null, null, NotFound));
        Assert.Contains("More than one WCH-Link is plugged in: say which with --port, its index or serial number as wlink list " +
            "shows them (e.g. --port 0).",
            await Failure([Riscv, Riscv], null, "Error: 2 WCH-Link probes attached; refusing to continue without an explicit probe."));

        // the WCH-LinkE is there: another chip, the wires, a protected chip
        Assert.Contains("The chip the WCH-LinkE reached is not a CH32V003 (the target's \"wlink.chip\"): flash it with its " +
            "own board's target (-t).", await Failure([Riscv], null, "Error: Chip mismatch: expected CH32V003, got CH32V20X"));
        var notAttached = await Failure([Riscv], "1",
            "Error: Probe is not attached to an MCU, or debug is not enabled. (hint: use wchisp to enable debug)");
        Assert.StartsWith("wlink could not write 'Blink.bin' into the CH32V003 through the WCH-LinkE 1 (exit code 1). The " +
            "WCH-LinkE did not reach the CH32V003: wire its SWDIO pin to the chip's SWIO pin (the CH32V003's PD1)", notAttached);
        Assert.Contains("erase it with wlink -d 1 --chip CH32V003 erase --method power-off (the board powered from the " +
            "WCH-LinkE's 3V3), then flash again.", notAttached);
        Assert.Contains("The WCH-LinkE did not reach the CH32V003", await Failure([Riscv], null, "Error: Operation timeout"));
        Assert.Contains("The CH32V003's flash is read-protected (or write-protected), and wlink could not unprotect it as it " +
            "does before writing: unprotect it, which erases it, with wlink --chip CH32V003 unprotect, or erase it with its " +
            "power turned off and on, wlink --chip CH32V003 erase --method power-off",
            await Failure([Riscv], null, "Flash is protected, unprotecting...", "Error: DMI Status Failed"));
        Assert.Contains("wlink does not know the chip the WCH-LinkE reached: update wlink",
            await Failure([Riscv], null, "Error: Unknown RISC-V Chip: 0x77"));
        Assert.Equal("wlink could not write 'Blink.bin' into the CH32V003 through the WCH-LinkE (exit code 1). wlink printed " +
            "nothing.", await Failure([Riscv], null));
    }

    private TargetDescription Board(string name = Name) => Load(Path.Combine(_scratch, "targets"), name);

    /// <summary>The test boards, and <paramref name="targets"/> beside them.</summary>
    private string MoreTargets(params (string Name, string Json)[] targets)
    {
        var folder = Write(Path.Combine(_scratch, "targets"));
        Build.TargetFiles.Write(Path.Combine(folder, "more"), targets);
        return folder;
    }

    /// <summary>An image to flash: its contents do not matter to a stand-in wlink.</summary>
    private string Image(string name) => File(Path.Combine(_scratch, "program", name), "image");

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
