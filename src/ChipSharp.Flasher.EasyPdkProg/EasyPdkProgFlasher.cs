using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;

namespace ChipSharp.Flasher.EasyPdkProg;

/// <summary>
/// The flasher "easypdkprog": writes a .hex into a Padauk chip (the PFS154) in the socket of the Easy PDK programmer,
/// Free-PDK's open-source programmer (https://free-pdk.github.io), with its command-line tool:
/// <code>
/// easypdkprog -n PFS154 -p COM7 write Blink.hex
/// </code>
/// The programmer is an STM32F072 on USB, a serial port to the computer (ST's virtual COM port ids, 0483:5740, its
/// product "Easy PDK Programmer"). easypdkprog erases the chip (the PFS154 is flash, MTP), checks it is blank, writes the
/// program, reads it back, runs the program's calibrations and writes the fuse word when the program has one. The IC is
/// the target's "easypdkprog.ic" (<c>-n</c>, as <c>easypdkprog list</c> names it). The port is
/// <see cref="FlashOptions.Port"/>, else the one serial port with the programmer's USB ids, else, when no port's ids are
/// known (macOS), easypdkprog's own search (<see cref="ProgrammerPorts.Choose"/>). easypdkprog itself is found by
/// <see cref="FindEasyPdkProg"/>.
/// <para>
/// Calibration: the PFS154's internal oscillator (IHRC, 16 MHz) is trimmed by its register IHRCR (I/O 0x0b). easypdkprog
/// trims it for the program, after writing it, when the program asks with a marker: Free-PDK's
/// <c>EASY_PDK_CALIBRATE_IHRC(frequency, millivolts)</c>, ten consecutive <c>and a, #k</c> words (PDK14: 0x2c00 | k),
/// which do nothing but clobber A and the flags: 'R' (0x52), 'C' (0x43), 1 (the IHRC; 2 would be the ILRC), the
/// system clock's frequency in hertz in 4 bytes and the supply during calibration in millivolts in 2, each low byte
/// first, then IHRCR's address, 0x0b (<see cref="IhrcCalibrationMarker"/>). easypdkprog finds the words in the image,
/// writes a loop in their place that it times through PA3 (out) and PA4 (in), runs the chip at that supply, tunes IHRCR
/// until the system clock is the frequency (within 10%, else "Calibration failed"), and then writes the ten words again
/// as <c>nop, nop, mov a, #trim, nop, nop, mov ihrcr, a, nop, nop, nop, nop</c> (a word can be written again where its
/// bits only go from 1 to 0, and a NOP is 0). ChipSharp always lets it (it never passes <c>--nocalibrate</c>), and shows
/// each result. Without easypdkprog's calibration (in the simulator, or written with <c>--nocalibrate</c>) the words
/// stay ANDs and IHRCR keeps what it had; the PFS154's factory trim for 16 MHz is a <c>ret k</c> at word 0x7ed, which
/// easypdkprog never erases (it leaves words 0x7e0-0x7ef alone) and chipsim gives.
/// </para>
/// <para>
/// So the PDK14 runtime's start-up, before main and before anything else (interrupts off, PA3 and PA4 left as they are
/// after reset), is: <c>call 0x7ed; mov ihrcr, a</c> (the factory trim); <c>mov a, #0x3c; mov clkmd, a</c> for the
/// pfs154-bare's IHRC / 8 (CLKMD 0x3c: IHRC and ILRC on, IHRC / 8, the watchdog off; another divider for another
/// "clock"); then, at once, the marker for the target's "clockHz" at 5000 mV (the Easy PDK programmer's 5.0 V), as
/// inline assembly that SDCC keeps word for word: for 2 MHz <c>and a, #0x52 / #0x43 / #0x01 / #0x80 / #0x84 / #0x1e /
/// #0x00 / #0x88 / #0x13 / #0x0b</c>. The marker must reach the image exactly once for the IHRC, in code that runs once,
/// and nothing may rely on A across it.
/// </para>
/// </summary>
public sealed partial class EasyPdkProgFlasher : IFlasher
{
    /// <summary>The environment variable that points at easypdkprog (easypdkprog.exe, or its folder).</summary>
    public const string EasyPdkProgVariable = "CHIPSHARP_EASYPDKPROG";

    /// <summary>Where ChipSharp looks for easypdkprog on Windows after the PATH, as for Zig: D:\Tools\easypdkprog.</summary>
    public const string ToolsFolder = @"D:\Tools\easypdkprog";

    private readonly Func<string, string?> _environment;
    private readonly Func<IReadOnlyList<SerialPortInfo>> _ports;

    public EasyPdkProgFlasher()
        : this(Environment.GetEnvironmentVariable, SerialPorts.List)
    {
    }

    /// <summary>
    /// For tests: reads environment variables (CHIPSHARP_EASYPDKPROG, PATH, LOCALAPPDATA, ProgramFiles) through
    /// <paramref name="environment"/>, and lists the serial ports with <paramref name="ports"/>.
    /// </summary>
    internal EasyPdkProgFlasher(Func<string, string?> environment, Func<IReadOnlyList<SerialPortInfo>> ports)
    {
        _environment = environment;
        _ports = ports;
    }

    public string Name => "easypdkprog";

    /// <summary>What to do when easypdkprog is missing.</summary>
    internal static string Advice =>
        "easypdkprog writes a program into a Padauk chip in the socket of the Easy PDK programmer (Free-PDK's: " +
        "https://free-pdk.github.io). Download it from https://github.com/free-pdk/easy-pdk-programmer-software/releases " +
        $"(for Windows EASYPDKPROG_WIN_<date>_<version>.zip), unpack it (e.g. to {ToolsFolder}, where ChipSharp finds it) " +
        $"and set {EasyPdkProgVariable} to its {ProgrammerTool.FileName("easypdkprog")}, or put its folder on the PATH.";

    /// <summary>
    /// easypdkprog: the environment variable CHIPSHARP_EASYPDKPROG (which must hold it: the file, or its folder), else
    /// easypdkprog on the PATH, else on Windows where its zip is usually unpacked: D:\Tools\easypdkprog,
    /// %LOCALAPPDATA%\Programs\easypdkprog, Program Files\easypdkprog (each also with the zip's own EASYPDKPROG folder
    /// in it); else an error that says where it looked and how to get it.
    /// </summary>
    public string FindEasyPdkProg()
    {
        if (ProgrammerTool.FromVariable("easypdkprog", EasyPdkProgVariable, _environment, Advice) is { } set)
        {
            return set;
        }

        var folders = Folders();
        var unpacked = folders.SelectMany(folder => new[] { folder, Path.Combine(folder, "EASYPDKPROG") });
        if ((ProgrammerTool.OnPath("easypdkprog", _environment) ?? ProgrammerTool.InFolders("easypdkprog", unpacked))
            is { } found)
        {
            return found;
        }

        var looked = folders.Count == 0 ? "" : $" and in {string.Join(", ", folders.Select(folder => $"'{folder}'"))}";
        throw new ChipSharpException($"easypdkprog was not found (looked on the PATH{looked}). {Advice}");
    }

    public async Task FlashAsync(string imagePath, TargetDescription target, FlashOptions options,
        IProgress<FlashProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        var settings = EasyPdkProgSettings.Read(target);
        var easypdkprog = FindEasyPdkProg();
        var port = string.IsNullOrWhiteSpace(options.Port) ? ProgrammerPorts.Choose(_ports()) : options.Port.Trim();
        var hex = Path.GetFullPath(imagePath);
        var arguments = Arguments(settings, port, hex);
        var where = port ?? "the Easy PDK programmer";

        progress?.Report(new FlashProgress($"{where}: easypdkprog {ProgrammerTool.CommandLine(arguments)}", 0));
        var (exitCode, output) = await ProgrammerTool.RunAsync("easypdkprog", easypdkprog, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (exitCode != 0 || ProgrammerTool.Says(output, "Nothing to write"))
        {
            throw new ChipSharpException(Failure(settings, port, hex, exitCode, output));
        }

        foreach (var calibration in Calibrations(output))
        {
            progress?.Report(new FlashProgress($"{where}: {calibration}", 1));
        }

        var start = ProgrammerTool.CommandLine(port is null ? ["start"] : ["-p", PortArgument(port), "start"]);
        progress?.Report(new FlashProgress(
            $"{where}: written and verified; the {target.Name} runs it out of the programmer's socket (in it: " +
            $"easypdkprog {start})", 1));
    }

    /// <summary>
    /// easypdkprog's arguments: the IC (<c>-n</c>), the programmer's port when it is known (<c>-p</c>; COM10 and up as
    /// \\.\COM10, which Windows needs), and the write of the file (Intel HEX: easypdkprog reads only data records, at
    /// byte addresses, two bytes a word, low byte first). Erasing, the blank check, verifying and calibrating are
    /// easypdkprog's defaults.
    /// </summary>
    public static IReadOnlyList<string> Arguments(EasyPdkProgSettings settings, string? port, string hexPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(hexPath);

        List<string> arguments = ["-n", settings.Ic];
        if (!string.IsNullOrWhiteSpace(port))
        {
            arguments.AddRange(["-p", PortArgument(port)]);
        }

        arguments.AddRange(["write", hexPath]);
        return arguments;
    }

    /// <summary>
    /// The port as easypdkprog opens it: Windows' COM10 and up only as \\.\COM10 (easypdkprog opens the name it is given
    /// as a file, as its own search does), every other name as it is.
    /// </summary>
    public static string PortArgument(string port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);

        var match = ComPort().Match(port.Trim());
        return match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) >= 10
            ? $@"\\.\{port.Trim()}"
            : port.Trim();
    }

    /// <summary>
    /// The ten PDK14 words of the marker that asks easypdkprog to trim the IHRC (Free-PDK's
    /// <c>EASY_PDK_CALIBRATE_IHRC</c>): <c>and a, #k</c> (0x2c00 | k) for 'R', 'C', 1, the system clock's
    /// <paramref name="frequency"/> in hertz (4 bytes) and the <paramref name="millivolts"/> of the supply during
    /// calibration (2 bytes), each low byte first, and IHRCR's address (0x0b). In the .hex each word is two bytes, low
    /// first (0x52, 0x2c, 0x43, 0x2c, ...).
    /// </summary>
    public static IReadOnlyList<ushort> IhrcCalibrationMarker(uint frequency, ushort millivolts)
    {
        const ushort AndA = 0x2c00;
        byte[] values =
        [
            (byte)'R', (byte)'C', 1,
            (byte)frequency, (byte)(frequency >> 8), (byte)(frequency >> 16), (byte)(frequency >> 24),
            (byte)millivolts, (byte)(millivolts >> 8),
            0x0b,
        ];
        return [.. values.Select(value => (ushort)(AndA | value))];
    }

    /// <summary>
    /// The calibrations easypdkprog ran, from what it printed (<c>* IHRC SYSCLK=2000000Hz @ 5.00V ... calibration
    /// result: 1998750Hz (0x84)  done.</c>), each as a line to show: "IHRC calibrated: 1998750 Hz for 2000000 Hz at
    /// 5.00 V (IHRCR 0x84)", or "bandgap calibrated (BGTR 0x..)".
    /// </summary>
    internal static IReadOnlyList<string> Calibrations(string output) =>
    [
        .. CalibrationLine().Matches(output).Select(match => match.Groups["kind"].Value switch
        {
            "BandGap" => $"bandgap calibrated (BGTR 0x{match.Groups["value"].Value.ToLowerInvariant()})",
            var kind => $"{kind} calibrated: {match.Groups["measured"].Value} Hz for {match.Groups["wanted"].Value} Hz at " +
                $"{match.Groups["volts"].Value} V ({kind}R 0x{match.Groups["value"].Value.ToLowerInvariant()})",
        }),
    ];

    /// <summary>
    /// What a failed easypdkprog says: which file, which IC, where and the exit code, what is likely wrong (from what
    /// easypdkprog printed: no programmer, another IC in the socket, its voltages, the calibration), and its last lines.
    /// </summary>
    internal static string Failure(EasyPdkProgSettings settings, string? port, string hexPath, int exitCode, string output)
    {
        var hint = Hint(settings, port, output);
        return string.Create(CultureInfo.InvariantCulture,
            $"easypdkprog could not write '{Path.GetFileName(hexPath)}' into the {settings.Ic} in the Easy PDK " +
            $"programmer{(port is null ? "" : $" on {port}")} (exit code {exitCode}).") +
            (hint is null ? "" : " " + hint) + ProgrammerTool.Said("easypdkprog", output);
    }

    private static string? Hint(EasyPdkProgSettings settings, string? port, string output)
    {
        bool Says(params string[] texts) => ProgrammerTool.Says(output, texts);
        var probe = ProgrammerTool.CommandLine(port is null ? ["probe"] : ["-p", PortArgument(port), "probe"]);

        if (Says("protocol mismatch"))
        {
            return "The programmer's firmware does not speak this easypdkprog's protocol: update the firmware with the " +
                "EASYPDKPROG.dfu that comes with easypdkprog (its Firmware\\README: dfu-util, the programmer's button held " +
                "while it is plugged in), or use the easypdkprog of the programmer's firmware.";
        }

        if (Says("No programmer found"))
        {
            return "No Easy PDK programmer answered on any serial port: plug it in by USB (Windows 10 and later have its " +
                "driver; older Windows need ST's virtual COM port driver, in easypdkprog's Windows_STM32VCPDriver folder), " +
                $"or give its port with --port, e.g. --port {SerialPorts.Example}.";
        }

        if (Says("connecting to programmer on port"))
        {
            return $"The programmer did not answer on {port}: check that it is the Easy PDK programmer's port (without " +
                "--port, chipsharp flash takes the one serial port with its USB ids, 0483:5740) and that nothing else " +
                "has it open, such as a serial monitor or easypdkprog start.";
        }

        if (Says("Unknown IC NAME"))
        {
            return $"easypdkprog does not know the IC '{settings.Ic}' (the target's \"easypdkprog.ic\"): easypdkprog list " +
                "shows those it does.";
        }

        if (Says("not implemented"))
        {
            return $"This easypdkprog cannot write the {settings.Ic}: update it.";
        }

        if (Says("not ihex8", "Invalid input file"))
        {
            return "easypdkprog reads Intel HEX with data and end-of-file records only, at byte addresses below 0x2000 " +
                "(two bytes a word): give it the .hex chipsharp build wrote.";
        }

        if (Says("setup vpp/vdd failed", "setup high vpp/high vdd failed"))
        {
            return "The programmer could not make the chip's programming voltages: plug it into another USB port (or a " +
                "powered hub), and check that nothing shorts the chip in the socket.";
        }

        if (Says("Unknown calibration"))
        {
            return "The program asks for a calibration this easypdkprog does not know: update easypdkprog and the " +
                "programmer's firmware.";
        }

        if (Says("Calibration failed", "out of range"))
        {
            return "The calibration of the program's clock did not reach its frequency: the start-up must reach the " +
                "calibration right after setting CLKMD to a clock the IHRC gives, leaving PA3 and PA4 (through which the " +
                "programmer times it) alone until then. The chip holds the program with the calibration loop still in " +
                "it: flash it again.";
        }

        if (Says("verify failed"))
        {
            return "What easypdkprog read back from the chip differs from the file: flash it again; if it keeps failing, " +
                "check that the chip sits well in the socket.";
        }

        if (Says("wrong icid", "ack failed", "Write failed", "Erase failed", "Blank check", "not blank"))
        {
            return $"The chip in the programmer's socket did not answer as a {settings.Ic} does: check that it is one, " +
                $"seated the right way round (pin 1 where the socket shows it); easypdkprog {probe} says which IC is there.";
        }

        return null;
    }

    /// <summary>The folders easypdkprog's zip is usually unpacked to on Windows (none elsewhere: there the PATH has it).</summary>
    private List<string> Folders()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var folders = new List<string> { ToolsFolder };
        void Add(string? root, params string[] parts)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                folders.Add(Path.Combine([root.Trim('"'), .. parts]));
            }
        }

        Add(_environment("LOCALAPPDATA"), "Programs", "easypdkprog");
        Add(_environment("ProgramFiles"), "easypdkprog");
        return folders;
    }

    [GeneratedRegex(@"^COM(\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex ComPort();

    [GeneratedRegex(@"\* (?<kind>IHRC|ILRC|BandGap) (?:SYSCLK=(?<wanted>\d+)Hz @ (?<volts>[0-9.]+)V )?\.\.\. calibration result: (?:(?<measured>\d+)Hz )?\(0x(?<value>[0-9A-Fa-f]{1,2})\)")]
    private static partial Regex CalibrationLine();
}

/// <summary>easypdkprog's settings for a board: the IC in the programmer's socket, as easypdkprog names it (<c>-n</c>).</summary>
/// <param name="Ic">The IC, e.g. "PFS154" (<c>easypdkprog list</c> names them).</param>
public sealed record EasyPdkProgSettings(string Ic)
{
    /// <summary>The settings of <paramref name="target"/>: "easypdkprog.ic".</summary>
    public static EasyPdkProgSettings Read(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target.Setting("easypdkprog.ic") is { Length: > 0 } ic
            ? new EasyPdkProgSettings(ic)
            : throw new ChipSharpException(
                $"Target '{target.Name}' is flashed with easypdkprog but does not say which IC it is, as easypdkprog " +
                "names it (\"easypdkprog\": { \"ic\": \"PFS154\" }).");
    }
}

/// <summary>Which of this computer's serial ports (<see cref="SerialPorts.List"/>) the Easy PDK programmer is on.</summary>
public static class ProgrammerPorts
{
    /// <summary>
    /// The programmer's USB ids: its firmware's (an STM32F072's) are ST's virtual COM port's, which other STM32 boards
    /// with a USB serial port have too.
    /// </summary>
    public const int VendorId = 0x0483, ProductId = 0x5740;

    /// <summary>
    /// The one port of <paramref name="ports"/> with the programmer's USB ids. Null when no port's ids are known (macOS
    /// names them only): easypdkprog then tries the ports itself, asking each for its version. Otherwise an error that
    /// lists the ports and says what to do: plug the programmer in, or name its port with --port.
    /// </summary>
    public static string? Choose(IReadOnlyList<SerialPortInfo> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        var programmers = ports.Where(port => port.IsUsb(VendorId, ProductId)).ToList();
        if (programmers.Count == 1)
        {
            return programmers[0].Name;
        }

        if (programmers.Count == 0 && ports.Count > 0 && ports.All(port => port.VendorId is null))
        {
            return null;
        }

        static string Listed(IEnumerable<SerialPortInfo> list) => string.Join(", ", list);
        throw new ChipSharpException(programmers.Count > 1
            ? string.Create(CultureInfo.InvariantCulture,
                $"{programmers.Count} serial ports have the Easy PDK programmer's USB ids (0483:5740, ST's virtual COM " +
                $"port's, which other STM32 boards have too): {Listed(programmers)}. Say which is the programmer's with " +
                $"--port, e.g. --port {programmers[0].Name}.")
            : ports.Count == 0
                ? "No serial port was found: plug the Easy PDK programmer in by USB, or give its port with --port (e.g. " +
                  $"--port {SerialPorts.Example}). On Windows its port shows in Device Manager, under \"Ports (COM & LPT)\", " +
                  "as a USB serial device."
                : $"None of the serial ports is the Easy PDK programmer's (USB 0483:5740): {Listed(ports)}. Plug it in by " +
                  $"USB, or give its port with --port, e.g. --port {ports[^1].Name}.");
    }
}
