using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;
using ChipSharp.Flasher.WchLink;
using ChipSharp.Tests.Support;
using static ChipSharp.Tests.Flash.WchLinkTestBoard;

namespace ChipSharp.Tests.Flash;

/// <summary>
/// The flasher "minichlink", for a CH32V003 board whose file names it instead of wlink: the command line it runs, where it
/// finds minichlink (CHIPSHARP_MINICHLINK, the PATH) and what it says when there is none, that it takes a .bin alone,
/// and a flash with a stand-in for minichlink (<see cref="FakeTool"/>), which works and which fails: no WCH-LinkE, one
/// whose driver libusb cannot use, a locked chip, the wires.
/// </summary>
public sealed class MinichlinkFlasherTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void Minichlink_writes_the_bin_at_the_chips_flash_and_restarts_it()
    {
        Assert.Equal("minichlink", Board("ch32-test-minichlink").Flasher);
        Assert.Equal(["-w", @"C:\Blink\Blink.bin", "flash", "-b"], MinichlinkFlasher.Arguments(@"C:\Blink\Blink.bin"));
        Assert.Equal(["-l", "F93D8F067E10", "-w", @"C:\Blink\Blink.bin", "flash", "-b"],
            MinichlinkFlasher.Arguments(@"C:\Blink\Blink.bin", " F93D8F067E10 "));
        Assert.Equal(["-u"], MinichlinkFlasher.UnbrickArguments());
    }

    [Fact]
    public void Minichlink_is_found_by_CHIPSHARP_MINICHLINK_then_on_the_PATH()
    {
        var onPath = File(Path.Combine(_scratch, "path", "minichlink.exe"));
        var own = File(Path.Combine(_scratch, "own", "minichlink.exe"));
        string Find(params (string Name, string Value)[] variables) =>
            new MinichlinkFlasher(name => variables.FirstOrDefault(v => v.Name == name).Value, () => [], () => []).FindMinichlink();

        Assert.Equal(own, Find(("CHIPSHARP_MINICHLINK", own), ("PATH", Path.GetDirectoryName(onPath)!)));
        Assert.Equal(own, Find(("CHIPSHARP_MINICHLINK", Path.GetDirectoryName(own)!)));
        Assert.Equal(onPath, Find(("PATH", Path.GetDirectoryName(onPath)!)));
    }

    [Fact]
    public async Task Without_minichlink_the_error_says_where_it_looked_and_how_to_get_it()
    {
        var flasher = new MinichlinkFlasher(name => name == "PATH" ? Path.Combine(_scratch, "empty") : null,
            () => throw new InvalidOperationException("The USB devices are not listed before minichlink is found."),
            () => []);

        var error = Assert.Throws<ChipSharpException>(flasher.FindMinichlink);

        var looked = OperatingSystem.IsWindows() ? $" and in '{MinichlinkFlasher.ToolsFolder}'" : "";
        Assert.StartsWith($"minichlink was not found (looked on the PATH{looked}). minichlink writes a raw .bin into a WCH " +
            "RISC-V chip (the CH32V003) through a WCH-LinkE wired to its SWIO pin. Take minichlink.exe (with its " +
            "libusb-1.0.dll) from https://github.com/cnlohr/ch32v003fun/tree/master/minichlink", error.Message);
        Assert.EndsWith("On Windows the WCH-LinkE needs WinUSB on its interface 0 (with Zadig, https://zadig.akeo.ie: Options " +
            "> List All Devices, the WCH-Link's Interface 0, WinUSB, Replace Driver; or ch32v003fun's " +
            @"misc\drivers_for_WCH-LinkE\WCH-Link_(Interface_0).inf).", error.Message);
        Assert.Equal(error.Message, (await Assert.ThrowsAsync<ChipSharpException>(() => flasher.FlashAsync(Image("Blink.bin"),
            Board("ch32-test-minichlink"), new FlashOptions(), null, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task Minichlink_takes_a_bin_alone()
    {
        var flasher = new MinichlinkFlasher(_ => null, () => [], () => []);

        var error = await Assert.ThrowsAsync<ChipSharpException>(() => flasher.FlashAsync(Image("Blink.hex"),
            Board("ch32-test-minichlink"), new FlashOptions(), null, CancellationToken.None));

        Assert.Equal("minichlink writes raw binaries alone, and 'Blink.hex' is not a .bin: give it the .bin chipsharp build " +
            "wrote (target 'ch32-test-minichlink' must have \"image\": \"bin\").", error.Message);
    }

    [Fact]
    public async Task A_flash_runs_minichlink_and_reports_each_stage()
    {
        var minichlink = new FakeTool(Path.Combine(_scratch, "minichlink"), "minichlink", 0,
            "Found WCH Link", "WCH Programmer is LinkE version 2.15", "Interface Setup", "Detected CH32V003",
            "Writing image", "Image written.");
        var bin = Image("Blink.bin");
        var stages = new List<string>();

        await new MinichlinkFlasher(name => name == "CHIPSHARP_MINICHLINK" ? minichlink.Path : null, () => [Riscv],
                () => [LinkPort])
            .FlashAsync(bin, Board("ch32-test-minichlink"), new FlashOptions(), new Stages(stages), CancellationToken.None);

        Assert.Equal($"-w {bin} flash -b", minichlink.CommandLine);
        Assert.Equal(
            [
                $"WCH-LinkE: minichlink -w {ChipSharpCli.Quoted(bin)} flash -b",
                "WCH-LinkE: written, and the chip restarted; the ch32-test-minichlink runs it",
            ],
            stages);
    }

    [Fact]
    public async Task A_failed_flash_says_what_is_likely_wrong_and_what_minichlink_said()
    {
        var bin = Image("Blink.bin");
        async Task<string> Failure(UsbDeviceInfo[]? plugged, params string[] said)
        {
            var minichlink = new FakeTool(Path.Combine(_scratch, Guid.NewGuid().ToString("N")), "minichlink", -13, said);
            var flasher = new MinichlinkFlasher(name => name == "CHIPSHARP_MINICHLINK" ? minichlink.Path : null,
                () => plugged, () => []);
            return (await Assert.ThrowsAsync<ChipSharpException>(() => flasher.FlashAsync(bin, Board("ch32-test-minichlink"),
                new FlashOptions(), null, CancellationToken.None))).Message;
        }

        const string NoProgrammer = "Error: Could not initialize any supported programmers";

        Assert.StartsWith("minichlink could not write 'Blink.bin' through the WCH-LinkE (exit code -13). No WCH-LinkE is " +
            "plugged in", await Failure([], NoProgrammer));
        // WCH's driver on the probe's interface: libusb cannot open it
        var wchDriver = await Failure([Riscv], NoProgrammer);
        Assert.Contains(OperatingSystem.IsWindows()
                ? "The WCH-Link (USB 1a86:8010) is plugged in, but its interface 0 has the driver WCHLinkDrv, which " +
                  "minichlink (through libusb) cannot open it with: install WinUSB on its interface 0 (with Zadig"
                : "The WCH-Link (USB 1a86:8010) is plugged in, but minichlink could not open it",
            wchDriver);
        Assert.Contains("The WCH-Link (USB 1a86:8010) is plugged in, but minichlink could not open it: close what else may have it",
            await Failure([Riscv with { InterfaceDrivers = new Dictionary<int, string?> { [0] = "WINUSB" } }], NoProgrammer));
        Assert.Contains("switch it to its RISC-V mode (WCH-LinkUtility's mode setting, or wlink mode-switch --rv)",
            await Failure([Dap], NoProgrammer));

        // a locked chip: the command that clears it
        Assert.Equal(
            "minichlink could not write 'Blink.bin' through the WCH-LinkE (exit code -13). The chip's flash is " +
            "read-protected, and minichlink writes it only once it is unlocked: clear it, which erases it, with minichlink " +
            "-u (the board powered from the WCH-LinkE's 3V3), then flash it again.\n" +
            "minichlink said:\n" +
            "  Detected CH32V003\n" +
            "  WARNING: Your part appears to have flash locked (read protected). Cannot program unless unlocked.\n" +
            "  Error: Fault writing image.",
            await Failure([Riscv], "Detected CH32V003",
                "WARNING: Your part appears to have flash locked (read protected). Cannot program unless unlocked.",
                "Error: Fault writing image."));

        // the WCH-LinkE is there, the chip does not answer: the wires
        Assert.Contains("The WCH-LinkE did not reach the chip: wire its SWDIO pin to the chip's SWIO pin (the CH32V003's PD1)",
            await Failure([Riscv], "Found WCH Link", "Could not setup interface."));
        Assert.Contains("The file is bigger than the chip's flash",
            await Failure([Riscv], "Error: binary doesn't fit into this memory area"));
        Assert.EndsWith("(exit code -13). minichlink printed nothing.", await Failure([Riscv]));
    }

    private TargetDescription Board(string name) => Load(Path.Combine(_scratch, "targets"), name);

    /// <summary>An image to flash: its contents do not matter to a stand-in minichlink.</summary>
    private string Image(string name) => File(Path.Combine(_scratch, "program", name), "image");

    private static string File(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Keeps every stage reported, in order, as it comes.</summary>
    private sealed class Stages(List<string> stages) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => stages.Add(value.Stage);
    }
}
