using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Flasher.EasyPdkProg;
using ChipSharp.Flasher.Shared;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Flash;

/// <summary>
/// The flasher "easypdkprog", for the PFS154 (pfs154-bare) in the Easy PDK programmer: the command line it runs, where it
/// finds easypdkprog (CHIPSHARP_EASYPDKPROG, the PATH, the folders its zip is unpacked to) and what it says when there
/// is none, which serial port it takes (the programmer's USB ids, from the registry's), the calibrations it shows, and a
/// flash with a stand-in for easypdkprog (<see cref="FakeTool"/>), which works and which fails.
/// </summary>
public sealed class EasyPdkProgFlasherTests : IDisposable
{
    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private static readonly EasyPdkProgSettings Pfs154 = new("PFS154");

    /// <summary>The programmer's port, as Windows lists it: ST's virtual COM port ids, the friendly name Windows gives.</summary>
    private static readonly SerialPortInfo Programmer = new("COM7", "USB Serial Device", 0x0483, 0x5740);

    private static readonly SerialPortInfo Uno = new("COM5", "Arduino Uno", 0x2341, 0x0043);

    private static readonly SerialPortInfo Bluetooth = new("COM3", "Standard Serial over Bluetooth link");

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void The_PFS154_is_written_by_easypdkprog_with_its_IC_name_and_the_programmers_port()
    {
        var board = Shipped.Load("pfs154-bare");

        Assert.Equal("easypdkprog", board.Flasher);
        Assert.Equal("hex", board.Image);
        Assert.Equal(Pfs154, EasyPdkProgSettings.Read(board));
        Assert.Equal(["-n", "PFS154", "-p", "COM7", "write", @"C:\Blink\Blink.hex"],
            EasyPdkProgFlasher.Arguments(Pfs154, "COM7", @"C:\Blink\Blink.hex"));
        // without a port easypdkprog searches the serial ports itself
        Assert.Equal(["-n", "PFS154", "write", @"C:\Blink\Blink.hex"],
            EasyPdkProgFlasher.Arguments(Pfs154, null, @"C:\Blink\Blink.hex"));
        // easypdkprog opens the port by its name, and Windows opens COM10 and up only as \\.\COM10
        Assert.Equal(["-n", "PFS154", "-p", @"\\.\COM12", "write", @"C:\Blink\Blink.hex"],
            EasyPdkProgFlasher.Arguments(Pfs154, "COM12", @"C:\Blink\Blink.hex"));
        Assert.Equal("COM9", EasyPdkProgFlasher.PortArgument("COM9"));
        Assert.Equal(@"\\.\COM10", EasyPdkProgFlasher.PortArgument(" COM10 "));
        Assert.Equal("/dev/ttyACM0", EasyPdkProgFlasher.PortArgument("/dev/ttyACM0"));
        Assert.Equal(@"-n PFS154 -p COM7 write ""C:\My programs\Blink.hex""",
            ProgrammerTool.CommandLine(EasyPdkProgFlasher.Arguments(Pfs154, "COM7", @"C:\My programs\Blink.hex")));

        Assert.IsType<EasyPdkProgFlasher>(DefaultComponents.CreateFlasherRegistry().Get<IFlasher>("easypdkprog"));
    }

    [Fact]
    public void A_board_file_names_the_IC_as_easypdkprog_does()
    {
        var targets = new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
            ("pfs173-bare", """{ "inherits": "pfs154-bare", "easypdkprog": { "ic": "PFS173" } }"""),
            ("no-ic", """{ "inherits": "pfs154-bare", "easypdkprog": { "ic": null } }""")));

        Assert.Equal(new EasyPdkProgSettings("PFS173"), EasyPdkProgSettings.Read(targets.Load("pfs173-bare")));
        Assert.Equal(
            "Target 'no-ic' is flashed with easypdkprog but does not say which IC it is, as easypdkprog names it " +
            "(\"easypdkprog\": { \"ic\": \"PFS154\" }).",
            Assert.Throws<ChipSharpException>(() => EasyPdkProgSettings.Read(targets.Load("no-ic"))).Message);
    }

    [Fact]
    public void Easypdkprog_is_found_by_CHIPSHARP_EASYPDKPROG_then_on_the_PATH_then_where_its_zip_is_unpacked()
    {
        var onPath = File(Path.Combine(_scratch, "path", "easypdkprog.exe"));
        var own = File(Path.Combine(_scratch, "own", "easypdkprog.exe"));
        var local = Path.Combine(_scratch, "local");
        var programFiles = Path.Combine(_scratch, "program files");
        // the zip unpacked into its own folder: easypdkprog in EASYPDKPROG, as the zip has it
        var unpacked = File(Path.Combine(local, "Programs", "easypdkprog", "EASYPDKPROG", "easypdkprog.exe"));
        var installed = File(Path.Combine(programFiles, "easypdkprog", "easypdkprog.exe"));
        string Find(params (string Name, string Value)[] variables) =>
            new EasyPdkProgFlasher(name => variables.FirstOrDefault(v => v.Name == name).Value, () => []).FindEasyPdkProg();

        // the variable must hold it: a file, or the folder it is in
        Assert.Equal(own, Find(("CHIPSHARP_EASYPDKPROG", own), ("PATH", Path.GetDirectoryName(onPath)!)));
        Assert.Equal(own, Find(("CHIPSHARP_EASYPDKPROG", $"\"{Path.GetDirectoryName(own)}\"")));
        var missing = Path.Combine(_scratch, "nowhere", "easypdkprog.exe");
        Assert.StartsWith(
            $"easypdkprog was not found at '{missing}' (set by the environment variable CHIPSHARP_EASYPDKPROG). ",
            Assert.Throws<ChipSharpException>(() => Find(("CHIPSHARP_EASYPDKPROG", missing), ("PATH", Path.GetDirectoryName(onPath)!))).Message);

        // the PATH before the folders; of the folders %LOCALAPPDATA%\Programs before Program Files
        Assert.Equal(onPath, Find(("PATH", Path.GetDirectoryName(onPath)!), ("LOCALAPPDATA", local)));
        Assert.Equal(unpacked, Find(("PATH", Path.Combine(_scratch, "empty")), ("LOCALAPPDATA", local), ("ProgramFiles", programFiles)));
        Assert.Equal(installed, Find(("ProgramFiles", programFiles)));
    }

    [Fact]
    public async Task Without_easypdkprog_the_error_says_where_it_looked_and_how_to_get_it()
    {
        var local = Path.Combine(_scratch, "local");
        var programFiles = Path.Combine(_scratch, "program files");
        var flasher = new EasyPdkProgFlasher(name => name switch
        {
            "LOCALAPPDATA" => local,
            "ProgramFiles" => programFiles,
            "PATH" => Path.Combine(_scratch, "empty"),
            _ => null,
        }, () => throw new InvalidOperationException("The ports are not listed before easypdkprog is found."));

        var error = Assert.Throws<ChipSharpException>(flasher.FindEasyPdkProg);

        Assert.StartsWith(
            $"easypdkprog was not found (looked on the PATH and in '{EasyPdkProgFlasher.ToolsFolder}', " +
            $"'{Path.Combine(local, "Programs", "easypdkprog")}', '{Path.Combine(programFiles, "easypdkprog")}'). " +
            "easypdkprog writes a program into a Padauk chip in the socket of the Easy PDK programmer",
            error.Message);
        Assert.Contains("https://github.com/free-pdk/easy-pdk-programmer-software/releases", error.Message);
        Assert.EndsWith("set CHIPSHARP_EASYPDKPROG to its easypdkprog.exe, or put its folder on the PATH.", error.Message);
        Assert.Equal(error.Message, (await Assert.ThrowsAsync<ChipSharpException>(() =>
            flasher.FlashAsync(Hex(), Shipped.Load("pfs154-bare"), new FlashOptions(), null, CancellationToken.None))).Message);
    }

    [Fact]
    public void The_port_is_the_one_with_the_programmers_USB_ids_otherwise_the_error_lists_them()
    {
        SerialPortInfo blackPill = new("COM9", "USB Serial Device", 0x0483, 0x5740);

        Assert.Equal("COM7", ProgrammerPorts.Choose([Bluetooth, Uno, Programmer]));
        Assert.True(Programmer.IsUsb(ProgrammerPorts.VendorId, ProgrammerPorts.ProductId));
        Assert.False(Uno.IsUsb(ProgrammerPorts.VendorId, ProgrammerPorts.ProductId));
        // where no port's ids are known (macOS names them only), easypdkprog asks each port itself
        Assert.Null(ProgrammerPorts.Choose([new SerialPortInfo("/dev/cu.usbmodem1234567855AA1")]));

        Assert.Equal(
            "2 serial ports have the Easy PDK programmer's USB ids (0483:5740, ST's virtual COM port's, which other STM32 " +
            "boards have too): COM7 (USB Serial Device), COM9 (USB Serial Device). Say which is the programmer's with " +
            "--port, e.g. --port COM7.",
            Assert.Throws<ChipSharpException>(() => ProgrammerPorts.Choose([Programmer, blackPill])).Message);
        Assert.Equal(
            "None of the serial ports is the Easy PDK programmer's (USB 0483:5740): COM3 (Standard Serial over " +
            "Bluetooth link), COM5 (Arduino Uno). Plug it in by USB, or give its port with --port, e.g. --port COM5.",
            Assert.Throws<ChipSharpException>(() => ProgrammerPorts.Choose([Bluetooth, Uno])).Message);
        Assert.StartsWith("No serial port was found: plug the Easy PDK programmer in by USB, or give its port with --port",
            Assert.Throws<ChipSharpException>(() => ProgrammerPorts.Choose([])).Message);
    }

    [Fact]
    public void Windows_names_the_programmers_port_and_ids_as_the_registry_has_them()
    {
        // Enum\USB\VID_0483&PID_5740\<serial>, FriendlyName "USB Serial Device (COM7)" (Windows' own usbser driver)
        Assert.Equal((0x0483, 0x5740), SerialPorts.UsbIds("VID_0483&PID_5740"));
        Assert.Equal("USB Serial Device", SerialPorts.Description("USB Serial Device (COM7)"));
        Assert.Equal("STMicroelectronics Virtual COM Port", SerialPorts.Description("STMicroelectronics Virtual COM Port (COM11)"));
    }

    [Fact]
    public void The_IHRC_calibration_marker_is_the_ten_words_easypdkprog_looks_for()
    {
        // easypdkprog's search for the PDK14 (fpdkiccalib.c): AND A,'R' 0x2c52, AND A,'C' 0x2c43, then AND A,* for the
        // type, the frequency's 4 bytes, the millivolts' 2 and the register; the pfs154-bare's 2 MHz at 5.0 V
        Assert.Equal(
            [0x2c52, 0x2c43, 0x2c01, 0x2c80, 0x2c84, 0x2c1e, 0x2c00, 0x2c88, 0x2c13, 0x2c0b],
            EasyPdkProgFlasher.IhrcCalibrationMarker(2_000_000, 5000).Select(word => (int)word));
        Assert.Equal(2_000_000, Shipped.Load("pfs154-bare").Settings["clockHz"]!.GetValue<int>());
        Assert.Equal([0x2c00, 0x2c12, 0x2c7a, 0x2c00, 0x2ce4, 0x2c0c],
            EasyPdkProgFlasher.IhrcCalibrationMarker(8_000_000, 3300).Skip(3).Take(6).Select(word => (int)word));
    }

    [Fact]
    public void The_calibrations_easypdkprog_ran_are_shown_from_what_it_printed()
    {
        Assert.Equal(
            [
                "IHRC calibrated: 1998750 Hz for 2000000 Hz at 5.00 V (IHRCR 0x84)",
                "ILRC calibrated: 61250 Hz for 60000 Hz at 3.30 V (ILRCR 0x3b)",
                "bandgap calibrated (BGTR 0x9c)",
            ],
            EasyPdkProgFlasher.Calibrations(string.Join('\n',
                "Erasing IC... done.",
                "Writing IC (174 words)... done.",
                "Calibrating IC",
                "* IHRC SYSCLK=2000000Hz @ 5.00V ... calibration result: 1998750Hz (0x84)  done.",
                "* ILRC SYSCLK=60000Hz @ 3.30V ... calibration result: 61250Hz (0x3B)  done.",
                "* BandGap ... calibration result: (0x9C)  done.")));
        Assert.Empty(EasyPdkProgFlasher.Calibrations("Erasing IC... done.\nWriting IC (174 words)... done."));
    }

    [Fact]
    public async Task A_flash_runs_easypdkprog_on_the_programmers_port_and_shows_the_calibration()
    {
        var easypdkprog = new FakeTool(Path.Combine(_scratch, "easypdkprog"), "easypdkprog", 0,
            "Erasing IC... done.", "Writing IC (174 words)... done.", "Calibrating IC",
            "* IHRC SYSCLK=2000000Hz @ 5.00V ... calibration result: 2001250Hz (0x84)  done.");
        var flasher = new EasyPdkProgFlasher(name => name == "CHIPSHARP_EASYPDKPROG" ? easypdkprog.Path : null,
            () => [Bluetooth, Programmer]);
        var hex = Hex();
        var stages = new List<FlashProgress>();

        await flasher.FlashAsync(hex, Shipped.Load("pfs154-bare"), new FlashOptions(), new Stages(stages), CancellationToken.None);

        var commandLine = ProgrammerTool.CommandLine(EasyPdkProgFlasher.Arguments(Pfs154, "COM7", hex));
        Assert.Equal(commandLine, easypdkprog.CommandLine);
        Assert.Equal(
            [
                new FlashProgress($"COM7: easypdkprog {commandLine}", 0),
                new FlashProgress("COM7: IHRC calibrated: 2001250 Hz for 2000000 Hz at 5.00 V (IHRCR 0x84)", 1),
                new FlashProgress("COM7: written and verified; the pfs154-bare runs it out of the programmer's socket " +
                    "(in it: easypdkprog -p COM7 start)", 1),
            ],
            stages);

        // a port given is taken as it is, without listing the ports
        var given = new EasyPdkProgFlasher(name => name == "CHIPSHARP_EASYPDKPROG" ? easypdkprog.Path : null,
            () => throw new InvalidOperationException("A port was given."));
        await given.FlashAsync(hex, Shipped.Load("pfs154-bare"), new FlashOptions(" COM12 "), null, CancellationToken.None);
        Assert.Equal($@"-n PFS154 -p \\.\COM12 write {hex}", easypdkprog.CommandLine);

        // no port's ids known: easypdkprog searches, and the stages say the programmer
        stages.Clear();
        var searched = new EasyPdkProgFlasher(name => name == "CHIPSHARP_EASYPDKPROG" ? easypdkprog.Path : null,
            () => [new SerialPortInfo("/dev/cu.usbmodem1234567855AA1")]);
        await searched.FlashAsync(hex, Shipped.Load("pfs154-bare"), new FlashOptions(), new Stages(stages), CancellationToken.None);
        Assert.Equal($"-n PFS154 write {hex}", easypdkprog.CommandLine);
        Assert.Equal($"the Easy PDK programmer: easypdkprog -n PFS154 write {hex}", stages[0].Stage);
        Assert.EndsWith("(in it: easypdkprog start)", stages[^1].Stage);
    }

    [Fact]
    public async Task A_failed_flash_says_what_is_likely_wrong_and_what_easypdkprog_said()
    {
        var hex = Hex();
        async Task<string> Failure(string? port, int exitCode, params string[] said)
        {
            var easypdkprog = new FakeTool(Path.Combine(_scratch, Guid.NewGuid().ToString("N")), "easypdkprog", exitCode, said);
            var flasher = new EasyPdkProgFlasher(name => name == "CHIPSHARP_EASYPDKPROG" ? easypdkprog.Path : null,
                () => [new SerialPortInfo("/dev/cu.usbmodem1")]);
            return (await Assert.ThrowsAsync<ChipSharpException>(() =>
                flasher.FlashAsync(hex, Shipped.Load("pfs154-bare"), new FlashOptions(port), null, CancellationToken.None))).Message;
        }

        Assert.Equal(
            "easypdkprog could not write 'Blink.hex' into the PFS154 in the Easy PDK programmer on COM7 (exit code -4). " +
            "The chip in the programmer's socket did not answer as a PFS154 does: check that it is one, seated the right " +
            "way round (pin 1 where the socket shows it); easypdkprog -p COM7 probe says which IC is there.\n" +
            "easypdkprog said:\n" +
            "  Erasing IC... FPDK_ERROR: command ack failed / wrong icid",
            await Failure("COM7", -4, "Erasing IC... FPDK_ERROR: command ack failed / wrong icid"));
        Assert.StartsWith(
            "easypdkprog could not write 'Blink.hex' into the PFS154 in the Easy PDK programmer (exit code -1). No Easy " +
            "PDK programmer answered on any serial port: plug it in by USB",
            await Failure(null, -1, "No programmer found"));
        Assert.Contains("The programmer did not answer on COM7: check that it is the Easy PDK programmer's port",
            await Failure("COM7", -1, "Error -2 connecting to programmer on port: COM7"));
        Assert.Contains("update the firmware with the EASYPDKPROG.dfu",
            await Failure("COM7", -1, "Error -3 (Programmer protocol mismatch - Please update programmer firmware) " +
                "connecting to programmer on port: COM7"));
        Assert.Contains("The calibration of the program's clock did not reach its frequency",
            await Failure("COM7", -18, "* IHRC SYSCLK=2000000Hz @ 5.00V ... calibration result: 2600000Hz (0xFF)  out of range.",
                "ERROR: Calibration failed"));
        Assert.Contains("could not make the chip's programming voltages",
            await Failure("COM7", -4, "FPDK_ERROR: setup high vpp/high vdd failed"));
        Assert.Equal(
            "easypdkprog could not write 'Blink.hex' into the PFS154 in the Easy PDK programmer on COM7 (exit code 1). " +
            "easypdkprog printed nothing.",
            await Failure("COM7", 1));
        // nothing written is no flash, whatever its exit code
        Assert.StartsWith("easypdkprog could not write 'Blink.hex' into the PFS154 in the Easy PDK programmer on COM7 (exit code 0).",
            await Failure("COM7", 0, "Nothing to write"));
    }

    /// <summary>An image to flash: its contents do not matter to a stand-in easypdkprog.</summary>
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
