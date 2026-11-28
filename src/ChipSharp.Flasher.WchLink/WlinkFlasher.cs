using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;

namespace ChipSharp.Flasher.WchLink;

/// <summary>
/// The flasher "wlink": writes a program into a WCH RISC-V chip (the CH32V003) through a WCH-LinkE, wired to the chip's
/// single-wire debug pin (SWIO, the CH32V003's PD1), with ch32-rs' wlink (https://github.com/ch32-rs/wlink):
/// <code>
/// wlink --chip CH32V003 flash --address 0x08000000 Blink.bin
/// </code>
/// The target's "wlink" settings give the chip, as wlink names it (<c>--chip</c>: wlink then refuses another chip on the
/// wire), and where a raw .bin goes (<c>--address</c>; by default the flash's origin, the region "link.code" names); a
/// .hex or .elf says its own addresses, so it is given none. wlink unprotects a read-protected chip by itself before
/// writing (which erases it), writes the flash, then resets the chip, which runs the program. The WCH-LinkE is a USB device
/// of its own, not a serial port: wlink finds it (on Windows through WCH's driver or WinUSB); <see cref="FlashOptions.Port"/>
/// is the one to use when more than one is plugged in (<c>-d</c>: its index or serial number, as <c>wlink list</c> shows
/// them). When wlink fails, the flasher says what is likely wrong, from what it printed and the WCH-Links plugged in
/// (<see cref="WchLinks"/>); when it works, on which serial port the console is (the WCH-LinkE's own). wlink itself is
/// found by <see cref="FindWlink"/>.
/// </summary>
public sealed class WlinkFlasher : IFlasher
{
    /// <summary>The environment variable that points at wlink (wlink.exe, or its folder).</summary>
    public const string WlinkVariable = "CHIPSHARP_WLINK";

    /// <summary>Where ChipSharp looks for wlink on Windows after the PATH, as for stm8flash: D:\Tools\wlink.</summary>
    public const string ToolsFolder = @"D:\Tools\wlink";

    /// <summary>On Windows, the driver the WCH-LinkE's interface 0 needs for wlink, for messages.</summary>
    internal const string Driver =
        "WCH's driver for its interface 0 (WCH-LinkUtility, https://www.wch-ic.com/downloads/WCH-LinkUtility_ZIP.html, " +
        "and MounRiver Studio install it) or WinUSB there (with Zadig, https://zadig.akeo.ie: Options > List All Devices, " +
        "the WCH-Link's Interface 0, WinUSB)";

    /// <summary>How to switch a WCH-LinkE from its ARM mode to its RISC-V mode, for messages.</summary>
    internal const string ModeSwitch = "wlink mode-switch --rv, or WCH-LinkUtility's mode setting";

    private readonly Func<string, string?> _environment;
    private readonly Func<IReadOnlyList<UsbDeviceInfo>?> _devices;
    private readonly Func<IReadOnlyList<SerialPortInfo>> _ports;

    public WlinkFlasher()
        : this(Environment.GetEnvironmentVariable, () => UsbDevices.List(WchLinks.VendorId), SerialPorts.List)
    {
    }

    /// <summary>
    /// For tests: reads environment variables (CHIPSHARP_WLINK, PATH, CARGO_HOME, USERPROFILE, HOME) through
    /// <paramref name="environment"/>, lists WCH's USB devices plugged in with <paramref name="devices"/> (null: this
    /// computer cannot say) and the serial ports with <paramref name="ports"/>.
    /// </summary>
    internal WlinkFlasher(Func<string, string?> environment, Func<IReadOnlyList<UsbDeviceInfo>?> devices,
        Func<IReadOnlyList<SerialPortInfo>> ports)
    {
        _environment = environment;
        _devices = devices;
        _ports = ports;
    }

    public string Name => "wlink";

    /// <summary>What to do when wlink is missing.</summary>
    internal static string Advice =>
        "wlink writes a program into a WCH RISC-V chip (the CH32V003) through a WCH-LinkE wired to its SWIO pin. Download " +
        "it from https://github.com/ch32-rs/wlink/releases (for Windows wlink-<version>-win-x64.zip) and unpack it (e.g. " +
        $"to {ToolsFolder}, where ChipSharp finds it), or build it with Rust (cargo install --git " +
        $"https://github.com/ch32-rs/wlink); or set {WlinkVariable} to its {ProgrammerTool.FileName("wlink")}, or put its " +
        $"folder on the PATH. On Windows the WCH-LinkE needs {Driver}.";

    /// <summary>
    /// wlink: the environment variable CHIPSHARP_WLINK (which must hold it: the file, or its folder), else wlink on the
    /// PATH, else on Windows D:\Tools\wlink, else the one Rust's <c>cargo install</c> put in Cargo's bin folder
    /// (%CARGO_HOME%\bin, else .cargo\bin in the user's folder); else an error that says where it looked and how to get it.
    /// </summary>
    public string FindWlink()
    {
        if (ProgrammerTool.FromVariable("wlink", WlinkVariable, _environment, Advice) is { } set)
        {
            return set;
        }

        var folders = Folders();
        if ((ProgrammerTool.OnPath("wlink", _environment) ?? ProgrammerTool.InFolders("wlink", folders)) is { } found)
        {
            return found;
        }

        var looked = folders.Count == 0 ? "" : $" and in {string.Join(", ", folders.Select(folder => $"'{folder}'"))}";
        throw new ChipSharpException($"wlink was not found (looked on the PATH{looked}). {Advice}");
    }

    public async Task FlashAsync(string imagePath, TargetDescription target, FlashOptions options,
        IProgress<FlashProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        var settings = WlinkSettings.Read(target);
        var wlink = FindWlink();
        var image = Path.GetFullPath(imagePath);
        var probe = string.IsNullOrWhiteSpace(options.Port) ? null : options.Port.Trim();
        var arguments = Arguments(settings.Chip, image, IsRaw(image) ? settings.Address ?? WchLinks.FlashOrigin(target) : null,
            probe);
        var where = "WCH-LinkE" + (probe is null ? "" : $" {probe}");

        progress?.Report(new FlashProgress($"{where}: wlink {ProgrammerTool.CommandLine(arguments)}", 0));
        var (exitCode, output) = await ProgrammerTool.RunAsync("wlink", wlink, arguments, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            var links = _devices() is { } devices ? WchLinks.Among(devices) : null;
            throw new ChipSharpException(Failure(settings.Chip, probe, links, image, exitCode, output));
        }

        progress?.Report(new FlashProgress($"{where}: written, and the {settings.Chip} restarted; the {target.Name} runs it", 1));
        if (WchLinks.ConsoleNote(target, _ports()) is { } console)
        {
            progress?.Report(new FlashProgress($"{where}: {console}", 1));
        }
    }

    /// <summary>
    /// wlink's arguments: the WCH-LinkE when one is given (<c>-d</c>), the chip (<c>--chip</c>), and the write of the file
    /// (<c>flash</c>), at <paramref name="address"/> for a raw .bin (<c>--address</c>; a .hex or .elf has its own). After
    /// writing wlink resets the chip, which runs the program.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string chip, string imagePath, string? address = null, string? probe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chip);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        string[] at = string.IsNullOrWhiteSpace(address) ? [] : ["--address", address];
        return [.. Probe(probe), "--chip", chip, "flash", .. at, imagePath];
    }

    /// <summary>
    /// wlink's arguments to unlock a read-protected chip (<c>unprotect</c>), which erases it; wlink does it by itself
    /// before writing, so this is for when that fails.
    /// </summary>
    public static IReadOnlyList<string> UnprotectArguments(string chip, string? probe = null) =>
        [.. Probe(probe), "--chip", chip, "unprotect"];

    /// <summary>
    /// wlink's arguments to erase the whole flash with the chip's power turned off and on by the WCH-LinkE (its 3V3 pin
    /// must power the board), which reaches a chip whose program stops the debug module, or sleeps, at once.
    /// </summary>
    public static IReadOnlyList<string> PowerOffEraseArguments(string chip, string? probe = null) =>
        [.. Probe(probe), "--chip", chip, "erase", "--method", "power-off"];

    /// <summary>True for a file wlink reads as raw bytes (a .bin), not as Intel HEX or ELF, which say their addresses.</summary>
    internal static bool IsRaw(string imagePath) =>
        !new[] { ".hex", ".ihex", ".ihx", ".elf" }.Contains(Path.GetExtension(imagePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What a failed wlink says: which file, which chip, through which WCH-LinkE and the exit code, what is likely wrong
    /// (from what it printed, and the WCH-Links plugged in: none, one in its ARM mode, one without a driver; the chip on
    /// the wire, the wires, read-out protection), and its last lines.
    /// </summary>
    internal static string Failure(string chip, string? probe, IReadOnlyList<(UsbDeviceInfo Device, WchLinkMode Mode)>? links,
        string imagePath, int exitCode, string output)
    {
        var hint = Hint(chip, probe, links, output);
        return string.Create(CultureInfo.InvariantCulture,
            $"wlink could not write '{Path.GetFileName(imagePath)}' into the {chip} through the WCH-LinkE" +
            $"{(probe is null ? "" : $" {probe}")} (exit code {exitCode}).") +
            (hint is null ? "" : " " + hint) + ProgrammerTool.Said("wlink", output);
    }

    private static string? Hint(string chip, string? probe, IReadOnlyList<(UsbDeviceInfo Device, WchLinkMode Mode)>? links,
        string output)
    {
        bool Says(params string[] texts) => ProgrammerTool.Says(output, texts);
        var powerOff = $"wlink {ProgrammerTool.CommandLine(PowerOffEraseArguments(chip, probe))}";

        if (Says("probes attached", "explicit probe"))
        {
            return "More than one WCH-Link is plugged in: say which with --port, its index or serial number as wlink list " +
                "shows them (e.g. --port 0).";
        }

        if (Says("not in RV mode"))
        {
            return $"The WCH-LinkE is in its ARM mode (CMSIS-DAP), in which wlink cannot write a RISC-V chip: switch it to " +
                $"its RISC-V mode ({ModeSwitch}), then flash again.";
        }

        if (Says("WCH-Link not found", "USB error", "Driver error", "Windows driver error", "LIBUSB_ERROR", "Access denied"))
        {
            return WchLinks.UsbHint("wlink", links, Driver, ModeSwitch);
        }

        if (Says("Chip mismatch", "does not match expected chip"))
        {
            return $"The chip the WCH-LinkE reached is not a {chip} (the target's \"wlink.chip\"): flash it with its own " +
                "board's target (-t).";
        }

        if (Says("doesn't support current chip", "doesn't support the choosen MCU", "please use WCH-LinkE"))
        {
            return $"This WCH-Link cannot write the {chip}: it takes a WCH-LinkE (a plain WCH-Link has no single-wire " +
                "debug); a WCH-LinkE with old firmware needs the firmware WCH-LinkUtility updates it to.";
        }

        // wlink unprotects a protected chip before writing ("Flash is protected, unprotecting...", "try to unprotect..."):
        // a failure after that is most likely the protection's
        if (Says("is protected", "write protected!", "Read protected: true", "Write protected: true", "unprotect"))
        {
            return $"The {chip}'s flash is read-protected (or write-protected), and wlink could not unprotect it as it " +
                $"does before writing: unprotect it, which erases it, with wlink {ProgrammerTool.CommandLine(UnprotectArguments(chip, probe))}, " +
                $"or erase it with its power turned off and on, {powerOff} (the board powered from the WCH-LinkE's 3V3), " +
                "then flash it again.";
        }

        if (Says("Unknown RISC-V Chip"))
        {
            return "wlink does not know the chip the WCH-LinkE reached: update wlink (and the WCH-LinkE's firmware, with " +
                "WCH-LinkUtility).";
        }

        if (Says("not attached", "Operation timeout", "DMI Status Failed", "DM is busy", "protocol error", "comand error",
                "command error"))
        {
            return $"The WCH-LinkE did not reach the {chip}: wire {WchLinks.Wiring}; check that the board has power, and try " +
                $"again. A program that sleeps or turns the debug pin off at once keeps the chip from answering: erase it " +
                $"with {powerOff} (the board powered from the WCH-LinkE's 3V3), then flash again.";
        }

        if (Says("empty ELF", "cannot read file as ELF", "section address overflow"))
        {
            return "wlink could not read the file as a program: give it the .bin chipsharp build wrote.";
        }

        return null;
    }

    private static IEnumerable<string> Probe(string? probe) => string.IsNullOrWhiteSpace(probe) ? [] : ["-d", probe.Trim()];

    /// <summary>
    /// The folders wlink is looked for in after the PATH: on Windows D:\Tools\wlink; everywhere Cargo's bin folder
    /// (%CARGO_HOME%\bin, else .cargo\bin in the user's folder), where cargo install puts it.
    /// </summary>
    private List<string> Folders()
    {
        var folders = OperatingSystem.IsWindows() ? new List<string> { ToolsFolder } : [];
        var cargo = _environment("CARGO_HOME") is { Length: > 0 } home
            ? Path.Combine(home.Trim('"'), "bin")
            : _environment(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") is { Length: > 0 } user
                ? Path.Combine(user.Trim('"'), ".cargo", "bin")
                : null;
        if (cargo is not null)
        {
            folders.Add(cargo);
        }

        return folders;
    }
}

/// <summary>wlink's settings for a board: the chip (<c>--chip</c>) and where a raw .bin goes (<c>--address</c>).</summary>
/// <param name="Chip">The chip, as wlink names it, e.g. "CH32V003".</param>
/// <param name="Address">Where a .bin is written, e.g. "0x08000000"; null for the flash's origin.</param>
public sealed record WlinkSettings(string Chip, string? Address = null)
{
    /// <summary>The settings of <paramref name="target"/>: "wlink.chip" and, optionally, "wlink.address".</summary>
    public static WlinkSettings Read(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var chip = target.Setting("wlink.chip") is { Length: > 0 } value
            ? value
            : throw new ChipSharpException(
                $"Target '{target.Name}' is flashed with wlink but does not say which chip it is, as wlink names it " +
                "(\"wlink\": { \"chip\": \"CH32V003\" }).");
        var address = target.Setting("wlink.address");
        if (address is not null && !IsNumber(address))
        {
            throw new ChipSharpException(
                $"Target '{target.Name}': \"wlink.address\" must be the address a .bin is written at, e.g. 0x08000000, not " +
                $"'{address}'.");
        }

        return new WlinkSettings(chip, address);
    }

    private static bool IsNumber(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)
            : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);
}
