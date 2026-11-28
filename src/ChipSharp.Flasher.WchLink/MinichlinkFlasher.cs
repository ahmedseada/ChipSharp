using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;

namespace ChipSharp.Flasher.WchLink;

/// <summary>
/// The flasher "minichlink": writes a raw .bin into a WCH RISC-V chip (the CH32V003) through a WCH-LinkE, with
/// ch32v003fun's minichlink (https://github.com/cnlohr/ch32v003fun/tree/master/minichlink), for a board file that names it
/// instead of wlink (<c>"flasher": "minichlink"</c>):
/// <code>
/// minichlink -w Blink.bin flash -b
/// </code>
/// <c>-w</c> writes the file at the chip's flash (<c>flash</c>: 0x08000000 on the CH32V003, where the .bin starts), after
/// halting the chip and unlocking and erasing the flash; <c>-b</c> then restarts it out of the halt, and it runs the
/// program. minichlink reads raw binaries only, so the board's image must be "bin". It finds the WCH-LinkE itself (on
/// Windows through libusb: WinUSB on the LinkE's interface 0); <see cref="FlashOptions.Port"/> is the one to use when more
/// than one is plugged in (<c>-l</c>: its USB serial number). A read-protected chip it does not write: the error then gives
/// the command that clears it. minichlink itself is found by <see cref="FindMinichlink"/>.
/// </summary>
public sealed class MinichlinkFlasher : IFlasher
{
    /// <summary>The environment variable that points at minichlink (minichlink.exe, or its folder).</summary>
    public const string MinichlinkVariable = "CHIPSHARP_MINICHLINK";

    /// <summary>Where ChipSharp looks for minichlink on Windows after the PATH, as for wlink: D:\Tools\minichlink.</summary>
    public const string ToolsFolder = @"D:\Tools\minichlink";

    /// <summary>On Windows, the driver the WCH-LinkE's interface 0 needs for minichlink (libusb), for messages.</summary>
    internal const string Driver =
        "WinUSB on its interface 0 (with Zadig, https://zadig.akeo.ie: Options > List All Devices, the WCH-Link's Interface 0, " +
        @"WinUSB, Replace Driver; or ch32v003fun's misc\drivers_for_WCH-LinkE\WCH-Link_(Interface_0).inf)";

    /// <summary>How to switch a WCH-LinkE from its ARM mode to its RISC-V mode, for messages.</summary>
    internal const string ModeSwitch = "WCH-LinkUtility's mode setting, or wlink mode-switch --rv";

    private readonly Func<string, string?> _environment;
    private readonly Func<IReadOnlyList<UsbDeviceInfo>?> _devices;
    private readonly Func<IReadOnlyList<SerialPortInfo>> _ports;
    private readonly string _toolsFolder;

    public MinichlinkFlasher()
        : this(Environment.GetEnvironmentVariable, () => UsbDevices.List(WchLinks.VendorId), SerialPorts.List)
    {
    }

    /// <summary>
    /// For tests: reads environment variables (CHIPSHARP_MINICHLINK, PATH) through <paramref name="environment"/>, lists
    /// WCH's USB devices plugged in with <paramref name="devices"/> (null: this computer cannot say) and the serial ports
    /// with <paramref name="ports"/>; and looks in <paramref name="toolsFolder"/> instead of <see cref="ToolsFolder"/>, so
    /// that what is installed on the computer running the tests does not decide them.
    /// </summary>
    internal MinichlinkFlasher(Func<string, string?> environment, Func<IReadOnlyList<UsbDeviceInfo>?> devices,
        Func<IReadOnlyList<SerialPortInfo>> ports, string? toolsFolder = null)
    {
        _environment = environment;
        _devices = devices;
        _ports = ports;
        _toolsFolder = toolsFolder ?? ToolsFolder;
    }

    public string Name => "minichlink";

    /// <summary>What to do when minichlink is missing.</summary>
    internal static string Advice =>
        "minichlink writes a raw .bin into a WCH RISC-V chip (the CH32V003) through a WCH-LinkE wired to its SWIO pin. Take " +
        "minichlink.exe (with its libusb-1.0.dll) from https://github.com/cnlohr/ch32v003fun/tree/master/minichlink (it " +
        "needs the Microsoft Visual C++ Redistributable), or build it there with make (it needs libusb-1.0); put it in " +
        $"{ToolsFolder}, where ChipSharp finds it, set {MinichlinkVariable} to its {ProgrammerTool.FileName("minichlink")}, " +
        $"or put its folder on the PATH. On Windows the WCH-LinkE needs {Driver}.";

    /// <summary>
    /// minichlink: the environment variable CHIPSHARP_MINICHLINK (which must hold it: the file, or its folder), else
    /// minichlink on the PATH, else on Windows D:\Tools\minichlink; else an error that says where it looked and how to get
    /// it.
    /// </summary>
    public string FindMinichlink()
    {
        if (ProgrammerTool.FromVariable("minichlink", MinichlinkVariable, _environment, Advice) is { } set)
        {
            return set;
        }

        string[] folders = OperatingSystem.IsWindows() ? [_toolsFolder] : [];
        if ((ProgrammerTool.OnPath("minichlink", _environment) ?? ProgrammerTool.InFolders("minichlink", folders)) is { } found)
        {
            return found;
        }

        var looked = folders.Length == 0 ? "" : $" and in {string.Join(", ", folders.Select(folder => $"'{folder}'"))}";
        throw new ChipSharpException($"minichlink was not found (looked on the PATH{looked}). {Advice}");
    }

    public async Task FlashAsync(string imagePath, TargetDescription target, FlashOptions options,
        IProgress<FlashProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        var image = Path.GetFullPath(imagePath);
        if (!string.Equals(Path.GetExtension(image), ".bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new ChipSharpException(
                $"minichlink writes raw binaries alone, and '{Path.GetFileName(image)}' is not a .bin: give it the .bin " +
                $"chipsharp build wrote (target '{target.Name}' must have \"image\": \"bin\").");
        }

        var minichlink = FindMinichlink();
        var serial = string.IsNullOrWhiteSpace(options.Port) ? null : options.Port.Trim();
        var arguments = Arguments(image, serial);
        var where = "WCH-LinkE" + (serial is null ? "" : $" {serial}");

        progress?.Report(new FlashProgress($"{where}: minichlink {ProgrammerTool.CommandLine(arguments)}", 0));
        var (exitCode, output) = await ProgrammerTool.RunAsync("minichlink", minichlink, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (exitCode != 0)
        {
            var links = _devices() is { } devices ? WchLinks.Among(devices) : null;
            throw new ChipSharpException(Failure(serial, links, image, exitCode, output));
        }

        progress?.Report(new FlashProgress($"{where}: written, and the chip restarted; the {target.Name} runs it", 1));
        if (WchLinks.ConsoleNote(target, _ports()) is { } console)
        {
            progress?.Report(new FlashProgress($"{where}: {console}", 1));
        }
    }

    /// <summary>
    /// minichlink's arguments: the WCH-LinkE's serial number when one is given (<c>-l</c>), the write of the file at the
    /// chip's flash (<c>-w</c> ... <c>flash</c>), and the restart out of the halt (<c>-b</c>).
    /// </summary>
    public static IReadOnlyList<string> Arguments(string binPath, string? serial = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binPath);

        return [.. Serial(serial), "-w", binPath, "flash", "-b"];
    }

    /// <summary>
    /// minichlink's arguments to clear a locked chip: <c>-u</c> erases the whole flash with the chip's power turned off
    /// and on by the WCH-LinkE (its 3V3 pin must power the board), which also unlocks it and reaches one whose program
    /// stops the debug pin.
    /// </summary>
    public static IReadOnlyList<string> UnbrickArguments(string? serial = null) => [.. Serial(serial), "-u"];

    /// <summary>
    /// What a failed minichlink says: which file, through which WCH-LinkE and the exit code, what is likely wrong (from
    /// what it printed, and the WCH-Links plugged in), and its last lines.
    /// </summary>
    internal static string Failure(string? serial, IReadOnlyList<(UsbDeviceInfo Device, WchLinkMode Mode)>? links,
        string binPath, int exitCode, string output)
    {
        var hint = Hint(serial, links, output);
        return string.Create(CultureInfo.InvariantCulture,
            $"minichlink could not write '{Path.GetFileName(binPath)}' through the WCH-LinkE{(serial is null ? "" : $" {serial}")} " +
            $"(exit code {exitCode}).") + (hint is null ? "" : " " + hint) + ProgrammerTool.Said("minichlink", output);
    }

    private static string? Hint(string? serial, IReadOnlyList<(UsbDeviceInfo Device, WchLinkMode Mode)>? links, string output)
    {
        bool Says(params string[] texts) => ProgrammerTool.Says(output, texts);
        var unbrick = $"minichlink {ProgrammerTool.CommandLine(UnbrickArguments(serial))}";

        if (Says("Could not initialize"))
        {
            return WchLinks.UsbHint("minichlink", links, Driver, ModeSwitch, needsWinUsb: true);
        }

        if (Says("flash locked", "read protected", "Error unlocking flash", "Flash is not unlocked"))
        {
            return "The chip's flash is read-protected, and minichlink writes it only once it is unlocked: clear it, which " +
                $"erases it, with {unbrick} (the board powered from the WCH-LinkE's 3V3), then flash it again.";
        }

        if (Says("doesn't fit"))
        {
            return "The file is bigger than the chip's flash: give it the .bin chipsharp build wrote for the chip's board.";
        }

        if (Says("Could not setup interface", "Setup chip failed", "Unknown chip type", "Flash timed out", "Fault writing image"))
        {
            return $"The WCH-LinkE did not reach the chip: wire {WchLinks.Wiring}; check that the board has power, and try " +
                "again. A program that sleeps or turns the debug pin off at once keeps the chip from answering: clear it " +
                $"with {unbrick} (the board powered from the WCH-LinkE's 3V3), then flash again.";
        }

        return null;
    }

    private static IEnumerable<string> Serial(string? serial) => string.IsNullOrWhiteSpace(serial) ? [] : ["-l", serial.Trim()];
}
