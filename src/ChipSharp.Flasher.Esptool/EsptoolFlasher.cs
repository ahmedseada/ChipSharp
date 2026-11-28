using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;

namespace ChipSharp.Flasher.Esptool;

/// <summary>
/// The flasher "esptool": writes an ESP image (<c>.bin</c>) into an Espressif board's flash with esptool, through its USB
/// port, which esptool uses to reset the chip into the ROM's download mode:
/// <code>
/// esptool --chip esp32c5 -p COM5 -b 460800 write-flash 0x2000 Blink.bin
/// </code>
/// The target's "esptool" settings give the chip (<c>--chip</c>), the flash offset the image goes to (0x2000: where the
/// ESP32-C5's ROM looks for it) and the speed (<c>-b</c>). esptool 5 spells the command <c>write-flash</c>, esptool 4
/// <c>write_flash</c>; <c>version</c> says which it is. The port is <see cref="FlashOptions.Port"/>, else the one that
/// looks like an ESP32 board's (<see cref="EspPorts.Choose"/>). esptool itself is found by <see cref="FindEsptool"/>.
/// </summary>
public sealed class EsptoolFlasher : IFlasher
{
    /// <summary>The environment variable that points at esptool (esptool.exe, esptool.py, or their folder).</summary>
    public const string EsptoolVariable = "CHIPSHARP_ESPTOOL";

    private readonly EsptoolLocator _locator;
    private readonly Func<IReadOnlyList<SerialPortInfo>> _ports;

    public EsptoolFlasher()
        : this(Environment.GetEnvironmentVariable, SerialPorts.List, EsptoolLocator.Imports)
    {
    }

    /// <summary>
    /// For tests: reads environment variables through <paramref name="environment"/>, lists the serial ports with
    /// <paramref name="ports"/> and asks <paramref name="canImportEsptool"/> whether a Python has esptool.
    /// </summary>
    internal EsptoolFlasher(Func<string, string?> environment, Func<IReadOnlyList<SerialPortInfo>> ports,
        Func<string, bool> canImportEsptool)
    {
        _locator = new EsptoolLocator(environment, canImportEsptool);
        _ports = ports;
    }

    public string Name => "esptool";

    /// <summary>esptool: the variable CHIPSHARP_ESPTOOL, else the PATH, else a Python that has it; else an error that says how to get it.</summary>
    public EsptoolTool FindEsptool() => _locator.Find();

    public async Task FlashAsync(string imagePath, TargetDescription target, FlashOptions options,
        IProgress<FlashProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        var settings = EsptoolSettings.Read(target);
        var esptool = FindEsptool();
        var port = string.IsNullOrWhiteSpace(options.Port) ? EspPorts.Choose(_ports()) : options.Port.Trim();
        var image = Path.GetFullPath(imagePath);

        var (_, said) = await ProgrammerTool.RunAsync("esptool", esptool.Path, [.. esptool.Prefix, "version"], cancellationToken)
            .ConfigureAwait(false);
        var major = MajorVersion(said);
        var arguments = Arguments(settings, major, port, image);

        progress?.Report(new FlashProgress($"{port}: {esptool.Display} {CommandLine(arguments)}", 0));
        var (exitCode, output) = await ProgrammerTool.RunAsync("esptool", esptool.Path, [.. esptool.Prefix, .. arguments],
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new ChipSharpException(Failure(settings, port, image, exitCode, output));
        }

        progress?.Report(new FlashProgress($"{port}: written and verified; the {target.Name} runs it", 1));
    }

    /// <summary>
    /// esptool's arguments: <c>--chip</c>, the port, the speed, then the write of the image at the offset:
    /// <c>write-flash</c> for esptool 5 and later (and when the version is not known), <c>write_flash</c> for esptool 4
    /// and earlier.
    /// </summary>
    public static IReadOnlyList<string> Arguments(EsptoolSettings settings, int? majorVersion, string port, string imagePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        return
        [
            "--chip", settings.Chip, "-p", port, "-b", settings.Baud.ToString(CultureInfo.InvariantCulture),
            majorVersion is < 5 ? "write_flash" : "write-flash", settings.Offset, imagePath,
        ];
    }

    /// <summary>The major version in what <c>esptool version</c> prints ("esptool v5.0.0", "esptool.py v4.8.1"); null when there is none.</summary>
    public static int? MajorVersion(string output)
    {
        var match = Regex.Match(output ?? "", @"(?<![\w.])v?(\d+)\.\d+");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The arguments as a command line shows them: those with a space in quotes.</summary>
    public static string CommandLine(IEnumerable<string> arguments) => ProgrammerTool.CommandLine(arguments);

    /// <summary>What a failed esptool says: which file and port, the exit code, what is likely wrong, and esptool's last lines.</summary>
    internal static string Failure(EsptoolSettings settings, string port, string imagePath, int exitCode, string output)
    {
        var hint = Hint(settings, port, output);
        return string.Create(CultureInfo.InvariantCulture,
            $"esptool could not flash '{Path.GetFileName(imagePath)}' to the board on {port} (exit code {exitCode}).") +
            (hint is null ? "" : " " + hint) + ProgrammerTool.Said("esptool", output);
    }

    private static string? Hint(EsptoolSettings settings, string port, string output)
    {
        bool Says(params string[] texts) => ProgrammerTool.Says(output, texts);

        if (Says("could not open port", "permissionerror", "access is denied", "resource busy"))
        {
            return $"{port} could not be opened: check that it is the board's port and that nothing else has it open, such " +
                "as a serial monitor.";
        }

        if (Says("invalid choice", "is not a valid chip", "unsupported chip", "no such command", "invalid value for '--chip'"))
        {
            return $"This esptool does not know the chip {settings.Chip}: update it (py -m pip install -U esptool).";
        }

        if (Says("this chip is", "chip type"))
        {
            return $"The chip on that board is not a {settings.Chip}: flash it with its own board's target (-t).";
        }

        if (Says("failed to connect", "wrong boot mode", "no serial data received", "timed out waiting for packet header",
                "write timeout", "serial exception"))
        {
            return $"The chip on {port} is not in download mode: hold the BOOT button, press and release RESET, then release " +
                "BOOT, and flash again (esptool normally does this by itself through the USB port's reset lines); check that " +
                "it is the board's port and the cable carries data.";
        }

        if (Says("md5", "verification failed", "hash of data verified failed"))
        {
            return "What esptool read back from the flash differs from the file: try again (another USB cable or port).";
        }

        return null;
    }
}

/// <summary>esptool's settings for a board: the chip (<c>--chip</c>), the flash offset of the image and the speed (<c>-b</c>).</summary>
/// <param name="Chip">The chip, as esptool names it, e.g. "esp32c5".</param>
/// <param name="Offset">Where in the flash the image goes, e.g. "0x2000": the ESP32-C5's ROM loads its image from there.</param>
/// <param name="Baud">The serial port's speed, e.g. 460800.</param>
public sealed record EsptoolSettings(string Chip, string Offset, int Baud)
{
    /// <summary>The speed when the target gives none.</summary>
    public const int DefaultBaud = 460800;

    /// <summary>The settings of <paramref name="target"/>: "esptool.chip", "esptool.offset" and, optionally, "esptool.baud".</summary>
    public static EsptoolSettings Read(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);

        string Required(string key, string example) =>
            target.Setting(key) is { Length: > 0 } value
                ? value
                : throw new ChipSharpException($"Target '{target.Name}' is flashed with esptool but does not say {example}.");

        var chip = Required("esptool.chip", "which chip it is (\"esptool\": { \"chip\": \"esp32c5\" })");
        var offset = Required("esptool.offset", "where the image goes in the flash (\"esptool\": { \"offset\": \"0x2000\" })");
        var baudText = target.Setting("esptool.baud");
        var baud = DefaultBaud;
        if (baudText is not null && !(int.TryParse(baudText, NumberStyles.None, CultureInfo.InvariantCulture, out baud) && baud > 0))
        {
            throw new ChipSharpException(
                $"Target '{target.Name}': \"esptool.baud\" must be a number of bits a second, e.g. 460800, not '{baudText}'.");
        }

        return new EsptoolSettings(chip, offset, baud);
    }
}
