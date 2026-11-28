using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;

namespace ChipSharp.Flasher.Avrdude;

/// <summary>
/// The flasher "avrdude": uploads a .hex through the USB port of an Arduino with avrdude, as the Arduino IDE does. On
/// the Uno, the USB chip is a serial port to the ATmega328P, and the bootloader (optiboot, in the last 512 bytes of the
/// flash) runs for a moment after each reset, which opening the port causes; avrdude talks to it there:
/// <code>
/// avrdude -c arduino -p atmega328p -P COM5 -b 115200 -D -U flash:w:Blink.hex:i
/// </code>
/// The target's "avrdude" settings give the programmer (<c>-c</c>, the bootloader's protocol: "arduino" is STK500
/// version 1) and the speed (<c>-b</c>); its "mmcu" the chip (<c>-p</c>, or "avrdude.part"). <c>-D</c> leaves the
/// erasing to the bootloader, which erases each page as it writes it; then avrdude reads the flash back and compares.
/// The port is <see cref="FlashOptions.Port"/>, else the one serial port that looks like an Arduino's
/// (<see cref="ArduinoPorts.Choose"/>). avrdude itself is found by <see cref="FindAvrdude"/>.
/// </summary>
public sealed class AvrdudeFlasher : IFlasher
{
    /// <summary>The environment variable that points at avrdude (avrdude.exe, or its folder).</summary>
    public const string AvrdudeVariable = "CHIPSHARP_AVRDUDE";

    private readonly AvrdudeLocator _locator;
    private readonly Func<IReadOnlyList<SerialPortInfo>> _ports;

    public AvrdudeFlasher()
        : this(Environment.GetEnvironmentVariable, SerialPorts.List)
    {
    }

    /// <summary>
    /// For tests: reads environment variables (CHIPSHARP_AVRDUDE, PATH, LOCALAPPDATA, ...) through
    /// <paramref name="environment"/>, and lists the serial ports with <paramref name="ports"/>.
    /// </summary>
    internal AvrdudeFlasher(Func<string, string?> environment, Func<IReadOnlyList<SerialPortInfo>> ports)
    {
        _locator = new AvrdudeLocator(environment);
        _ports = ports;
    }

    public string Name => "avrdude";

    /// <summary>
    /// avrdude, and the configuration file to give it when it would not find its own: the environment variable
    /// CHIPSHARP_AVRDUDE (which must hold it), else avrdude on the PATH, else the copy the Arduino IDE installed (the
    /// newest), else an error that says where it looked and how to get avrdude.
    /// </summary>
    public AvrdudeTool FindAvrdude() => _locator.Find();

    public async Task FlashAsync(string imagePath, TargetDescription target, FlashOptions options,
        IProgress<FlashProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        var settings = AvrdudeSettings.Read(target);
        var avrdude = FindAvrdude();
        var port = string.IsNullOrWhiteSpace(options.Port) ? ArduinoPorts.Choose(_ports()) : options.Port.Trim();
        var hex = Path.GetFullPath(imagePath);
        var arguments = Arguments(settings, port, hex, avrdude.ConfigFile);

        progress?.Report(new FlashProgress($"{port}: avrdude {CommandLine(arguments)}", 0));
        var (exitCode, output) = await ProgrammerTool.RunAsync("avrdude", avrdude.Path, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new ChipSharpException(Failure(settings, port, hex, exitCode, output));
        }

        progress?.Report(new FlashProgress($"{port}: written and verified; the {target.Name} runs it", 1));
    }

    /// <summary>
    /// avrdude's arguments: the configuration file first when there is one to give (<c>-C</c>), then the programmer,
    /// the chip, the port, the speed, <c>-D</c> (no chip erase: the bootloader erases what it writes) and the write of
    /// the flash from the Intel HEX file (<c>-U flash:w:&lt;file&gt;:i</c>).
    /// </summary>
    public static IReadOnlyList<string> Arguments(AvrdudeSettings settings, string port, string hexPath,
        string? configFile = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(hexPath);

        List<string> arguments = configFile is null ? [] : ["-C", configFile];
        arguments.AddRange(
        [
            "-c", settings.Programmer, "-p", settings.Part, "-P", port,
            "-b", settings.Baud.ToString(CultureInfo.InvariantCulture), "-D", "-U", $"flash:w:{hexPath}:i",
        ]);
        return arguments;
    }

    /// <summary>The arguments as a command line shows them: those with a space in quotes.</summary>
    public static string CommandLine(IEnumerable<string> arguments) => ProgrammerTool.CommandLine(arguments);

    /// <summary>
    /// What a failed avrdude says: which file, which port and the exit code, what is likely wrong (from what avrdude
    /// printed: the port would not open, the bootloader did not answer, the chip is another), and avrdude's last lines.
    /// </summary>
    internal static string Failure(AvrdudeSettings settings, string port, string hexPath, int exitCode, string output)
    {
        var hint = Hint(settings, port, output);
        return string.Create(CultureInfo.InvariantCulture,
            $"avrdude could not flash '{Path.GetFileName(hexPath)}' to the board on {port} (exit code {exitCode}).") +
            (hint is null ? "" : " " + hint) + ProgrammerTool.Said("avrdude", output);
    }

    private static string? Hint(AvrdudeSettings settings, string port, string output)
    {
        bool Says(params string[] texts) => ProgrammerTool.Says(output, texts);

        if (Says("can't open device", "cannot open port", "could not open port", "ser_open()"))
        {
            return $"{port} could not be opened: check that it is the board's port (without --port, chipsharp flash takes " +
                "the one serial port that looks like an Arduino's) and that nothing else has it open, such as a serial monitor.";
        }

        if (Says("expected signature"))
        {
            return $"The chip on that board is not the target's ({settings.Part}): flash it with its own board's target (-t).";
        }

        if (Says("verification error", "content mismatch"))
        {
            return "What avrdude read back from the flash differs from the file: try again (another USB cable or port).";
        }

        if (Says("not in sync", "not responding", "getsync", "stk500_recv", "timeout", "timed out"))
        {
            return $"The board on {port} did not answer as its bootloader should: check that it is the target's board " +
                $"on that port (another board's bootloader may need another programmer or speed than {settings.Programmer} " +
                $"at {settings.Baud.ToString(CultureInfo.InvariantCulture)} baud: the target's \"avrdude\" settings), " +
                "close any serial monitor that has the port, and unplug the board and plug it in again.";
        }

        return null;
    }
}

/// <summary>avrdude's settings for a board: the programmer (<c>-c</c>), the chip (<c>-p</c>) and the speed (<c>-b</c>).</summary>
/// <param name="Programmer">How avrdude talks to the board, e.g. "arduino": the STK500 v1 protocol of the Uno's bootloader.</param>
/// <param name="Part">The chip, as avrdude names it, e.g. "atmega328p".</param>
/// <param name="Baud">The serial port's speed the bootloader listens at, e.g. 115200 (the Uno's optiboot).</param>
public sealed record AvrdudeSettings(string Programmer, string Part, int Baud)
{
    /// <summary>
    /// The settings of <paramref name="target"/>: "avrdude.programmer" and "avrdude.baud" (the board's: they are its
    /// bootloader's), and "avrdude.part", else the chip's "mmcu".
    /// </summary>
    public static AvrdudeSettings Read(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);

        string Required(string key, string example) =>
            target.Setting(key) is { Length: > 0 } value
                ? value
                : throw new ChipSharpException(
                    $"Target '{target.Name}' is flashed with avrdude but does not say {example}.");

        var programmer = Required("avrdude.programmer",
            "how avrdude talks to its bootloader (\"avrdude\": { \"programmer\": \"arduino\" } for an Uno's)");
        var part = target.Setting("avrdude.part") is { Length: > 0 } named
            ? named
            : Required("mmcu", "which chip it is (\"mmcu\", or \"avrdude\": { \"part\": \"atmega328p\" })");
        var baudText = Required("avrdude.baud",
            "the speed its bootloader listens at (\"avrdude\": { \"baud\": 115200 } for an Uno's)");
        return int.TryParse(baudText, NumberStyles.None, CultureInfo.InvariantCulture, out var baud) && baud > 0
            ? new AvrdudeSettings(programmer, part, baud)
            : throw new ChipSharpException(
                $"Target '{target.Name}': \"avrdude.baud\" must be a number of bits a second, e.g. 115200, not '{baudText}'.");
    }
}
