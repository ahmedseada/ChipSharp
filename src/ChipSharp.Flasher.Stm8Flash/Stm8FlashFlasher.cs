using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;

namespace ChipSharp.Flasher.Stm8Flash;

/// <summary>
/// The flasher "stm8flash": writes a .hex into an STM8 chip (the STM8S103F3) through the SWIM pin of an ST-LINK, with
/// the open-source stm8flash (https://github.com/vdudouyt/stm8flash):
/// <code>
/// stm8flash -c stlinkv2 -p stm8s103f3 -s flash -w Blink.hex
/// </code>
/// The target's "stm8flash" settings give the ST-LINK (<c>-c</c>: stlinkv2 for the cheap ST-LINK V2 clones, as for the
/// blue board) and the chip (<c>-p</c>, as <c>stm8flash -l</c> names it). <c>-s flash</c> writes the flash, from
/// 0x8000, where the .hex's addresses are; the .hex extension makes it read the file as Intel HEX. After writing,
/// stm8flash restarts the chip, which runs the program. The ST-LINK is a USB device of its own, not a serial port:
/// stm8flash finds it by its USB ids, which depend on its kind (-c), so the flasher looks for ST-LINKs plugged in
/// (<see cref="StLinks.Programmer"/>: another kind than the board's, alone, is taken instead), and tells, when stm8flash
/// fails, whether there is one. <see cref="FlashOptions.Port"/> is the ST-LINK's serial number (<c>-S</c>, as
/// <c>stm8flash -L</c> lists them), for when more than one is plugged in. stm8flash itself is found by
/// <see cref="FindStm8Flash"/>.
/// </summary>
public sealed class Stm8FlashFlasher : IFlasher
{
    /// <summary>The environment variable that points at stm8flash (stm8flash.exe, or its folder).</summary>
    public const string Stm8FlashVariable = "CHIPSHARP_STM8FLASH";

    /// <summary>Where ChipSharp looks for stm8flash on Windows after the PATH, as for Zig: D:\Tools\stm8flash.</summary>
    public const string ToolsFolder = @"D:\Tools\stm8flash";

    private readonly Func<string, string?> _environment;
    private readonly Func<IReadOnlyList<UsbDeviceInfo>?> _devices;

    public Stm8FlashFlasher()
        : this(Environment.GetEnvironmentVariable, () => UsbDevices.List(StLinks.VendorId))
    {
    }

    /// <summary>
    /// For tests: reads environment variables (CHIPSHARP_STM8FLASH, PATH, LOCALAPPDATA) through
    /// <paramref name="environment"/>, and lists ST's USB devices plugged in with <paramref name="devices"/> (null: this
    /// computer cannot say).
    /// </summary>
    internal Stm8FlashFlasher(Func<string, string?> environment, Func<IReadOnlyList<UsbDeviceInfo>?> devices)
    {
        _environment = environment;
        _devices = devices;
    }

    public string Name => "stm8flash";

    /// <summary>What to do when stm8flash is missing.</summary>
    internal static string Advice =>
        "stm8flash writes a program into an STM8 chip through an ST-LINK V2 (a clone will do), wired to the board's SWIM " +
        "header. Build it from https://github.com/vdudouyt/stm8flash (it needs libusb-1.0; some Linux distributions " +
        "package it), or on Windows take the stm8flash.exe (with its libusb-1.0.dll) that the sduino core for the Arduino " +
        "IDE installs (https://github.com/tenbaht/sduino), which ChipSharp finds there; or put it in " +
        $"{ToolsFolder}, set {Stm8FlashVariable} to its {ProgrammerTool.FileName("stm8flash")}, or put its folder on the PATH.";

    /// <summary>
    /// stm8flash: the environment variable CHIPSHARP_STM8FLASH (which must hold it: the file, or its folder), else
    /// stm8flash on the PATH, else on Windows D:\Tools\stm8flash, else the copy the sduino core installed (the
    /// Arduino IDE's Arduino15\packages\sduino\tools\STM8Tools\&lt;version&gt;\win, the newest first); else an error
    /// that says where it looked and how to get it.
    /// </summary>
    public string FindStm8Flash()
    {
        if (ProgrammerTool.FromVariable("stm8flash", Stm8FlashVariable, _environment, Advice) is { } set)
        {
            return set;
        }

        var (folders, sduino) = Folders();
        var copies = sduino is null
            ? []
            : ProgrammerTool.Versions(sduino).SelectMany(version => new[] { Path.Combine(version, "win"), Path.Combine(version, "tools", "win") });
        if ((ProgrammerTool.OnPath("stm8flash", _environment) ?? ProgrammerTool.InFolders("stm8flash", folders.Concat(copies)))
            is { } found)
        {
            return found;
        }

        var places = sduino is null ? folders : [.. folders, sduino];
        var looked = places.Count == 0 ? "" : $" and in {string.Join(", ", places.Select(folder => $"'{folder}'"))}";
        throw new ChipSharpException($"stm8flash was not found (looked on the PATH{looked}). {Advice}");
    }

    public async Task FlashAsync(string imagePath, TargetDescription target, FlashOptions options,
        IProgress<FlashProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        var settings = Stm8FlashSettings.Read(target);
        var stm8flash = FindStm8Flash();
        var stLinks = _devices() is { } devices ? StLinks.Among(devices) : null;
        var programmer = StLinks.Programmer(settings.Programmer, stLinks);
        var serial = string.IsNullOrWhiteSpace(options.Port) ? null : options.Port.Trim();
        var hex = Path.GetFullPath(imagePath);
        var arguments = Arguments(programmer, settings.Part, hex, serial);
        var where = StLinks.Name(programmer) + (serial is null ? "" : $" {serial}");

        progress?.Report(new FlashProgress($"{where}: stm8flash {ProgrammerTool.CommandLine(arguments)}", 0));
        var (exitCode, output) = await ProgrammerTool.RunAsync("stm8flash", stm8flash, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new ChipSharpException(Failure(settings.Part, programmer, serial, stLinks, hex, exitCode, output));
        }

        progress?.Report(new FlashProgress(
            $"{where}: written, and the {settings.Part} restarted; the {target.Name} runs it", 1));
    }

    /// <summary>
    /// stm8flash's arguments: the ST-LINK (<c>-c</c>, and <c>-S</c> its serial number when one is given), the chip
    /// (<c>-p</c>), the memory (<c>-s flash</c>) and the write of the file (<c>-w</c>; Intel HEX, by its extension).
    /// </summary>
    public static IReadOnlyList<string> Arguments(string programmer, string part, string hexPath, string? serial = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programmer);
        ArgumentException.ThrowIfNullOrWhiteSpace(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(hexPath);

        return [.. Programmer(programmer, serial), "-p", part, "-s", "flash", "-w", hexPath];
    }

    /// <summary>
    /// stm8flash's arguments to unlock the chip: <c>-u</c> writes the option bytes' factory values, which turns
    /// read-out protection off, and so erases the flash (the chip does when ROP goes off).
    /// </summary>
    public static IReadOnlyList<string> UnlockArguments(string programmer, string part, string? serial = null) =>
        [.. Programmer(programmer, serial), "-p", part, "-u"];

    /// <summary>
    /// What a failed stm8flash says: which file, which chip, through which ST-LINK and the exit code, what is likely
    /// wrong (from what it printed, and which ST-LINKs are plugged in: none, one it could not open, the chip locked, the
    /// SWIM wires), and its last lines.
    /// </summary>
    internal static string Failure(string part, string programmer, string? serial,
        IReadOnlyList<(UsbDeviceInfo Device, StLinkKind Kind)>? stLinks, string hexPath, int exitCode, string output)
    {
        var hint = Hint(part, programmer, serial, stLinks, output);
        return string.Create(CultureInfo.InvariantCulture,
            $"stm8flash could not write '{Path.GetFileName(hexPath)}' into the {part} through the " +
            $"{StLinks.Name(programmer)}{(serial is null ? "" : $" {serial}")} (exit code {exitCode}).") +
            (hint is null ? "" : " " + hint) + ProgrammerTool.Said("stm8flash", output);
    }

    private static string? Hint(string part, string programmer, string? serial,
        IReadOnlyList<(UsbDeviceInfo Device, StLinkKind Kind)>? stLinks, string output)
    {
        bool Says(params string[] texts) => ProgrammerTool.Says(output, texts);

        if (Says("read-out protection", "write protected", "Tries exceeded"))
        {
            return $"The {part} is most likely read-out protected (ROP, an option byte), as many of these boards come " +
                "from the factory, or its flash is write-protected (UBC): unlock it, which erases it, with stm8flash " +
                $"{ProgrammerTool.CommandLine(UnlockArguments(programmer, part, serial))}, then flash it again.";
        }

        if (Says("No programmer with serial number"))
        {
            return $"No ST-LINK has the serial number {serial} (given with --port): stm8flash -c {programmer} -L lists " +
                "those plugged in, with theirs.";
        }

        if (Says("Could not open USB device", "Couldn't initialize stlink", "LIBUSB_ERROR"))
        {
            return UsbHint(programmer, stLinks);
        }

        if (Says("Error communicating with MCU", "SWIM error", "IO error"))
        {
            return $"The {StLinks.Name(programmer)} did not reach the {part} through SWIM: wire its SWIM pins to the " +
                "board's header (3.3V to 3.3V, SWIM to SWIM, which is PD1, GND to GND, RST to NRST), check that the board " +
                "has power (the ST-LINK's 3.3V, or its own USB), and try again.";
        }

        if (Says("No valid part"))
        {
            return $"stm8flash does not know the part '{part}' (the target's \"stm8flash.part\"): stm8flash -l lists " +
                "those it does.";
        }

        if (Says("No valid programmer"))
        {
            return $"stm8flash does not know the programmer '{programmer}' (the target's \"stm8flash.programmer\"): " +
                $"it knows {string.Join(", ", StLinks.Kinds.Select(kind => kind.Programmer))} (and espstlink).";
        }

        if (Says("out of range", "Error while parsing IHEX"))
        {
            return $"The file has bytes stm8flash will not write into the {part}'s flash (from 0x8000): give it the .hex " +
                "chipsharp build wrote.";
        }

        return null;
    }

    /// <summary>Why stm8flash found no ST-LINK it could open, from those plugged in.</summary>
    private static string UsbHint(string programmer, IReadOnlyList<(UsbDeviceInfo Device, StLinkKind Kind)>? stLinks)
    {
        const string Driver = "ST's ST-LINK driver (STSW-LINK009) or, with Zadig, WinUSB: libusb, which stm8flash " +
            "uses, needs one";
        if (stLinks is null)
        {
            return "stm8flash found no ST-LINK it could open: plug it in by USB; on Windows it needs a driver, " + Driver + ".";
        }

        if (stLinks.Count == 0)
        {
            return "No ST-LINK is plugged in (no USB device has an ST-LINK's ids, " +
                $"{string.Join(", ", StLinks.Kinds.Select(kind => $"0483:{kind.ProductId:x4}"))}): plug the ST-LINK in by " +
                "USB, and wire it to the board's SWIM header.";
        }

        var named = stLinks.Where(link => link.Kind.Programmer == programmer).ToList();
        if (named.Count == 0)
        {
            return $"The ST-LINKs plugged in are {string.Join(", ", stLinks.Select(link => link.Device))}, not the " +
                $"board's {StLinks.Name(programmer)}: give the one to use (stm8flash's -c) in the target's \"stm8flash.programmer\" " +
                $"({string.Join(", ", StLinks.Kinds.Select(kind => $"{kind.Programmer}: {kind.Name}"))}).";
        }

        var device = named[0].Device;
        return OperatingSystem.IsWindows() && device.Driver is null
            ? $"The {device} is plugged in, but Windows has no driver for it: install {Driver}."
            : $"The {device} is plugged in, but stm8flash could not open it: close what else may have it (ST Visual " +
              "Programmer, STM32CubeProgrammer, OpenOCD), unplug it and plug it in again; on Windows it needs " + Driver + ".";
    }

    private static List<string> Programmer(string programmer, string? serial) =>
        string.IsNullOrWhiteSpace(serial) ? ["-c", programmer] : ["-c", programmer, "-S", serial];

    /// <summary>
    /// The folders stm8flash is looked for in after the PATH on Windows (none elsewhere: there the PATH has it), and the
    /// sduino core's folder of STM8Tools versions (null when there is no Arduino15 folder to look in).
    /// </summary>
    private (List<string> Folders, string? Sduino) Folders()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ([], null);
        }

        var local = _environment("LOCALAPPDATA");
        return ([ToolsFolder], string.IsNullOrWhiteSpace(local)
            ? null
            : Path.Combine(local.Trim('"'), "Arduino15", "packages", "sduino", "tools", "STM8Tools"));
    }
}

/// <summary>stm8flash's settings for a board: the ST-LINK (<c>-c</c>) and the chip (<c>-p</c>).</summary>
/// <param name="Programmer">The ST-LINK, as stm8flash names it: "stlinkv2" (the ST-LINK V2 and its clones), ...</param>
/// <param name="Part">The chip, as stm8flash names it, e.g. "stm8s103f3".</param>
public sealed record Stm8FlashSettings(string Programmer, string Part)
{
    /// <summary>The settings of <paramref name="target"/>: "stm8flash.programmer" and "stm8flash.part".</summary>
    public static Stm8FlashSettings Read(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);

        string Required(string key, string example) =>
            target.Setting(key) is { Length: > 0 } value
                ? value
                : throw new ChipSharpException(
                    $"Target '{target.Name}' is flashed with stm8flash but does not say {example}.");

        return new Stm8FlashSettings(
            Required("stm8flash.programmer",
                "which ST-LINK stm8flash writes it through (\"stm8flash\": { \"programmer\": \"stlinkv2\" })"),
            Required("stm8flash.part", "which chip it is, as stm8flash names it (\"stm8flash\": { \"part\": \"stm8s103f3\" })"));
    }
}

/// <summary>A kind of ST-LINK stm8flash talks to: its name there (<c>-c</c>), ST's name for it, its USB product id.</summary>
public sealed record StLinkKind(string Programmer, string Name, int ProductId);

/// <summary>The ST-LINKs stm8flash knows, by their USB ids, and which to give it.</summary>
public static class StLinks
{
    /// <summary>STMicroelectronics' USB vendor id, every ST-LINK's (and its clones').</summary>
    public const int VendorId = 0x0483;

    /// <summary>The kinds stm8flash knows (its -c), each with the one product id it looks for.</summary>
    public static readonly IReadOnlyList<StLinkKind> Kinds =
    [
        new("stlink", "ST-LINK/V1", 0x3744),
        new("stlinkv2", "ST-LINK/V2", 0x3748),
        new("stlinkv21", "ST-LINK/V2-1", 0x374b),
        new("stlinkv3", "STLINK-V3", 0x374f),
    ];

    /// <summary>The ST-LINKs among <paramref name="devices"/>, with their kind.</summary>
    public static IReadOnlyList<(UsbDeviceInfo Device, StLinkKind Kind)> Among(IEnumerable<UsbDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        return
        [
            .. devices.Where(device => device.VendorId == VendorId)
                .Select(device => (Device: device, Kind: Kinds.FirstOrDefault(kind => kind.ProductId == device.ProductId)))
                .Where(link => link.Kind is not null)
                .Select(link => (link.Device, link.Kind!)),
        ];
    }

    /// <summary>
    /// The ST-LINK to give stm8flash: the board's (<paramref name="board"/>) when one of its kind is plugged in, or
    /// when which are cannot be told (<paramref name="plugged"/> null) or none is; else the kind of those plugged in
    /// when they are all of one (a Nucleo board's ST-LINK/V2-1, an STLINK-V3MINI), since stm8flash looks for that
    /// kind's USB ids alone.
    /// </summary>
    public static string Programmer(string board, IReadOnlyList<(UsbDeviceInfo Device, StLinkKind Kind)>? plugged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(board);

        if (plugged is null || plugged.Count == 0 || plugged.Any(link => link.Kind.Programmer == board))
        {
            return board;
        }

        var kinds = plugged.Select(link => link.Kind.Programmer).Distinct().ToList();
        return kinds.Count == 1 ? kinds[0] : board;
    }

    /// <summary>ST's name for the programmer: "ST-LINK/V2" for stlinkv2; another name as it is.</summary>
    public static string Name(string programmer) =>
        Kinds.FirstOrDefault(kind => kind.Programmer == programmer)?.Name ?? programmer;
}
