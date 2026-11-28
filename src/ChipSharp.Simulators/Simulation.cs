using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ChipSharp.Simulators.Pdk14;
using ChipSharp.Simulators.Stm8;

namespace ChipSharp.Simulators;

/// <summary>Wrong arguments: what is wrong, for the usage message.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>An --input: a pin driven from outside, high or low, from the start or from a time on.</summary>
public sealed record PinInput(string Pin, bool High, double? AtMilliseconds);

/// <summary>A --send: bytes sent to the chip's serial port, from the start or from a time on.</summary>
public sealed record SerialSend(byte[] Bytes, double? AtMilliseconds);

/// <summary>What to simulate: the chip, the image, for how long, and what comes from outside.</summary>
public sealed partial class SimulationOptions
{
    /// <summary>The chip, as <see cref="Simulation.Chips"/> names it: "pfs154", "stm8s103".</summary>
    public required string Chip { get; init; }

    /// <summary>The Intel HEX file.</summary>
    public required string Image { get; init; }

    /// <summary>How long to run, in simulated milliseconds.</summary>
    public double Milliseconds { get; init; } = 1000;

    /// <summary>A file to write every byte the serial port sent to, or null.</summary>
    public string? SerialFile { get; init; }

    public IReadOnlyList<PinInput> Inputs { get; init; } = [];

    public IReadOnlyList<SerialSend> Sends { get; init; } = [];

    /// <summary>
    /// The options of the command line after the chip: the image, --ms, --uart1, --input and --send (see
    /// <see cref="Simulation.Usage"/>). Throws <see cref="UsageException"/> when they are wrong.
    /// </summary>
    public static SimulationOptions Parse(string chip, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? image = null;
        string? serial = null;
        var milliseconds = 1000.0;
        var inputs = new List<PinInput>();
        var sends = new List<SerialSend>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (image is not null)
                {
                    throw new UsageException($"only one image, please ('{image}' and '{argument}').");
                }

                image = argument;
                continue;
            }

            if (argument is not ("--ms" or "--uart1" or "--input" or "--send"))
            {
                throw new UsageException($"unknown option '{argument}'.");
            }

            if (++i == arguments.Count)
            {
                throw new UsageException($"{argument} needs a value.");
            }

            var value = arguments[i];
            switch (argument)
            {
                case "--ms":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds) ||
                        !(milliseconds > 0) || double.IsInfinity(milliseconds))
                    {
                        throw new UsageException($"--ms must be a positive number of milliseconds, not '{value}'.");
                    }

                    break;
                case "--uart1":
                    serial = value;
                    break;
                case "--input":
                    var input = InputPattern().Match(value);
                    if (!input.Success)
                    {
                        throw new UsageException("--input takes a pin, a level and maybe a time, e.g. PA3=high or " +
                            $"PA3=low@100ms, not '{value}'.");
                    }

                    inputs.Add(new PinInput(input.Groups[1].Value.ToUpperInvariant(),
                        input.Groups[2].Value.Equals("high", StringComparison.OrdinalIgnoreCase), Time(input.Groups[3])));
                    break;
                default:
                    var send = SendPattern().Match(value);
                    sends.Add(new SerialSend(Unescape(send.Groups[1].Value), Time(send.Groups[2])));
                    break;
            }
        }

        return new SimulationOptions
        {
            Chip = chip,
            Image = image ?? throw new UsageException("which .ihx image?"),
            Milliseconds = milliseconds,
            SerialFile = serial,
            Inputs = inputs,
            Sends = sends,
        };

        static double? Time(Group group) =>
            group.Success ? double.Parse(group.Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The bytes of --send's text: its UTF-8, with \r, \n, \t, \\ and \xHH as those bytes.</summary>
    public static byte[] Unescape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 == text.Length)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(text[i].ToString()));
                continue;
            }

            var next = text[++i];
            switch (next)
            {
                case 'r':
                    bytes.Add(0x0D);
                    break;
                case 'n':
                    bytes.Add(0x0A);
                    break;
                case 't':
                    bytes.Add(0x09);
                    break;
                case '\\':
                    bytes.Add(0x5C);
                    break;
                case 'x' when i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]):
                    bytes.Add(byte.Parse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                default:
                    throw new UsageException($"--send: '\\{next}' is not one of \\r, \\n, \\t, \\\\ and \\xHH.");
            }
        }

        return [.. bytes];
    }

    /// <summary>The bytes as --send takes them: printable ASCII as it is, the rest escaped.</summary>
    public static string Escape(IEnumerable<byte> bytes) => string.Concat(bytes.Select(b => b switch
    {
        0x0D => "\\r",
        0x0A => "\\n",
        0x09 => "\\t",
        0x5C => "\\\\",
        >= 0x20 and < 0x7F => ((char)b).ToString(),
        _ => $"\\x{b:x2}",
    }));

    [GeneratedRegex(@"^(P[A-Fa-f][0-7])=(high|low)(?:@(\d+(?:\.\d+)?)ms)?$", RegexOptions.IgnoreCase)]
    private static partial Regex InputPattern();

    [GeneratedRegex(@"^(.*?)(?:@(\d+(?:\.\d+)?)ms)?$", RegexOptions.Singleline)]
    private static partial Regex SendPattern();
}

/// <summary>What a run printed, how it ended, and the chip as it was at the end.</summary>
public sealed partial class SimulationResult(int exitCode, IReadOnlyList<string> lines, byte[] serial, SimulatedChip chip)
{
    /// <summary>0: the program ran (to its end, or for the whole time); 3: it went wrong.</summary>
    public int ExitCode { get; } = exitCode;

    /// <summary>Every line printed, the "booting" line first and the "stopped" line last.</summary>
    public IReadOnlyList<string> Lines { get; } = lines;

    /// <summary>The bytes the serial port sent.</summary>
    public byte[] Serial { get; } = serial;

    /// <summary>The chip, after the run: its memories and registers.</summary>
    public SimulatedChip Chip { get; } = chip;

    /// <summary>The lines, one after the other.</summary>
    public string Output => string.Join('\n', Lines);

    /// <summary>Why the run stopped: the last line, without its time.</summary>
    public string Stopped => Lines.Count > 0 && Lines[^1].Length > 16 ? Lines[^1][16..] : "";

    /// <summary>The lines printed after their time that start with <paramref name="prefix"/>, without the time.</summary>
    public IReadOnlyList<string> After(string prefix) =>
    [
        .. Lines.Where(line => line.Length > 16 && line[0] == '[' && line[16..].StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[16..]),
    ];

    /// <summary>The times, in simulated milliseconds, of the lines that start with <paramref name="prefix"/> after their time.</summary>
    public IReadOnlyList<double> Times(string prefix) =>
    [
        .. Lines.Where(line => line.Length > 16 && line[0] == '[' && line[16..].StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => double.Parse(line[1..11], CultureInfo.InvariantCulture)),
    ];

    /// <summary>Every pin change printed, in order (not an input's, nor a PWM output's edges).</summary>
    public IReadOnlyList<PinChange> Pins =>
    [
        .. Lines.Select(line => PinLine().Match(line)).Where(match => match.Success).Select(match => new PinChange(
            double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value, match.Groups[3].Value)),
    ];

    /// <summary>The lines the serial port wrote, without their time and line endings.</summary>
    public IReadOnlyList<string> SerialLines =>
        [.. Lines.Select(line => SerialLine().Match(line)).Where(match => match.Success).Select(match => match.Groups[1].Value)];

    public override string ToString() => $"exit code {ExitCode}\n{Output}";

    [GeneratedRegex(@"^\[\s*([0-9.]+) ms\] (P[A-F][0-7]) (?!PWM )(.+)$")]
    private static partial Regex PinLine();

    [GeneratedRegex(@"^\[\s*[0-9.]+ ms\] UART\d: (.*)$")]
    private static partial Regex SerialLine();
}

/// <summary>A pin change a run printed: when (simulated milliseconds), which pin (PA3), its new state ("high", "input pull-up").</summary>
public sealed record PinChange(double Milliseconds, string Pin, string State);

/// <summary>
/// Runs a program on a simulated chip, as tools/avr-sim/run.mjs does on the Uno: it prints every pin change and every
/// line the serial port writes, with the simulated time, drives pins and sends text from outside on a timeline, and
/// stops after the time given, or earlier when the program finishes or goes wrong (<see cref="Usage"/>).
/// </summary>
public static partial class Simulation
{
    /// <summary>The chips, by the name the command takes, and how each loads an image.</summary>
    public static IReadOnlyDictionary<string, Func<string, SimulatedChip>> Chips { get; } =
        new Dictionary<string, Func<string, SimulatedChip>>(StringComparer.OrdinalIgnoreCase)
        {
            ["pfs154"] = Pfs154.Load,
            ["stm8s103"] = Stm8s103.Load,
        };

    /// <summary>What the command line takes, and what it prints.</summary>
    public const string Usage = """
        Usage: chipsim <pfs154|stm8s103> <image.ihx> [--ms <simulated milliseconds, default 1000>]
                                                     [--uart1 <file to write UART1's bytes to>]
                                                     [--input <pin>=<high|low>[@<simulated milliseconds>ms]]...
                                                     [--send <text>[@<simulated milliseconds>ms]]...

        Runs an Intel HEX image (as SDCC writes it: sdcc -mpdk14, sdcc -mstm8) on a simulated Padauk PFS154 or STM8S103
        and prints what the program does, each with the simulated time.

        --input drives a pin from outside, as a button or another chip would, e.g. --input PA3=high, or
        --input PA3=low@100ms (the pin changes then, and the change is printed). Give a pin several times to change it
        several times. A pin no --input drives reads high when the program turns its pull-up on, else low.

        --send sends text to UART1's RX (the STM8S103's PD6), byte after byte at the port's speed (10 bits a byte), from
        that time (or the start), e.g. --send "on\n@100ms": its UTF-8 bytes, \r, \n, \t, \\ and \xHH standing for those
        bytes. Sends follow each other. A byte that arrives while the receiver is off is lost, as on the chip.

        Output lines:

            [    0.123 ms] PA3 high                  a pin changed: low or high when driven, else input or input pull-up
            [    0.200 ms] UART1 baud 9600           the program set UART1's speed
            [    1.234 ms] UART1: Hello              a line written to UART1 (without its line ending)
            [  100.000 ms] input: PA3 low            an --input with a time changed the pin from outside
            [  100.000 ms] input: UART1 "on\n"       a --send starts sending
            [   12.000 ms] PB4 PWM 1000.00 Hz, duty 25.00% (250.00 us high)
                                                     a pin a timer drives, measured from its edges: printed when a period
                                                     differs from the one before, instead of every edge
            [   15.000 ms] PB4 PWM steady low        a timer's pin with no edge for 2.5 periods
            [ 1000.000 ms] stopped: <why>            the last line

        The run stops after the simulated time, or earlier: when the program has finished (it sleeps with nothing left
        to wake it: stopsys or stopexe on the PFS154, halt or wfi on the STM8S103), or went wrong (the PFS154's reset
        instruction or its watchdog, the STM8S103's break or its IWDG, an instruction or an address the chip does not
        have, memory the image does not fill, an interrupt with no handler, a pop past the top of the stack).

        Exit code: 0 when the program ran (to its end or for the whole time), 1 when the image is not valid, 2 for wrong
        arguments, 3 when the program went wrong.
        """;

    /// <summary>
    /// Runs <paramref name="options"/>; every line printed also goes to <paramref name="echo"/>. <paramref name="setUp"/>
    /// can set up the chip before it starts (e.g. its <see cref="SimulatedChip.Hooks"/>). Throws
    /// <see cref="ImageException"/> for an image that is not valid, <see cref="UsageException"/> for wrong options.
    /// </summary>
    public static SimulationResult Run(SimulationOptions options, Action<string>? echo = null, Action<SimulatedChip>? setUp = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Chips.TryGetValue(options.Chip, out var load))
        {
            throw new UsageException($"there is no chip '{options.Chip}': {string.Join(", ", Chips.Keys)}.");
        }

        var chip = load(options.Image);
        var lines = new List<string>();
        void Log(string line)
        {
            lines.Add(line);
            echo?.Invoke(line);
        }

        var pins = options.Inputs.Select(input => (input, Pin(chip, input.Pin))).ToList();
        if ((options.Sends.Count > 0 || options.SerialFile is not null) && chip.SerialInput is null)
        {
            throw new UsageException($"the {chip.Name} has no UART for --send or --uart1.");
        }

        chip.Log = Log;
        setUp?.Invoke(chip);
        Log(string.Create(CultureInfo.InvariantCulture,
            $"booting {options.Image} on the {chip.Name} ({chip.ImageBytes} bytes of program memory), running " +
            $"{options.Milliseconds} ms of simulated time"));

        foreach (var (input, (port, bit)) in pins)
        {
            if (input.AtMilliseconds is not { } at)
            {
                port.DriveFromOutside(bit, input.High);
                continue;
            }

            chip.At(Tick(at), () =>
            {
                chip.Print($"input: {port.PinName(bit)} {(input.High ? "high" : "low")}");
                port.DriveFromOutside(bit, input.High);
            }, external: true);
        }

        if (options.Sends.Count > 0 && chip.SerialInput is { } serial)
        {
            ScheduleSends(chip, serial, options.Sends);
        }

        chip.Run(Tick(options.Milliseconds));
        chip.SerialOutput?.Finish();
        var sent = chip.SerialOutput?.Bytes.ToArray() ?? [];
        if (options.SerialFile is not null)
        {
            File.WriteAllBytes(options.SerialFile, sent);
        }

        chip.Print($"stopped: {chip.StopReason ?? string.Create(CultureInfo.InvariantCulture, $"{options.Milliseconds} ms of simulated time have passed")}");
        return new SimulationResult(chip.Failed ? 3 : 0, lines, sent, chip);
    }

    /// <summary>The command line: the chip, then <see cref="SimulationOptions.Parse"/>'s options. Returns the exit code.</summary>
    public static int Command(string[] arguments, TextWriter output, TextWriter errors)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(errors);
        try
        {
            if (arguments.Length == 0 || arguments[0] is "-h" or "--help")
            {
                output.WriteLine(Usage);
                return arguments.Length == 0 ? 2 : 0;
            }

            var options = SimulationOptions.Parse(arguments[0], arguments[1..]);
            return Run(options, output.WriteLine).ExitCode;
        }
        catch (UsageException e)
        {
            errors.WriteLine($"chipsim: {e.Message}");
            errors.WriteLine(Usage.Split('\n')[0]);
            return 2;
        }
        catch (ImageException e)
        {
            errors.WriteLine($"chipsim: {e.Message}");
            return 1;
        }
    }

    /// <summary>The ticks of a time in simulated milliseconds.</summary>
    public static long Tick(double milliseconds) => (long)Math.Round(milliseconds * SimulatedChip.TicksPerSecond / 1000);

    private static (GpioPort Port, int Bit) Pin(SimulatedChip chip, string name)
    {
        var port = chip.Port(name[1]);
        var bit = name[2] - '0';
        if (port is null || (port.Pins & (1 << bit)) == 0)
        {
            var all = chip.Ports.SelectMany(p => Enumerable.Range(0, 8).Where(b => (p.Pins & (1 << b)) != 0).Select(p.PinName));
            throw new UsageException($"the {chip.Name} has no pin {name}; it has {string.Join(", ", all)}.");
        }

        return (port, bit);
    }

    /// <summary>One send after the other, in the order given, each from its time (or once the one before is done).</summary>
    private static void ScheduleSends(SimulatedChip chip, ISerialInput serial, IReadOnlyList<SerialSend> sends)
    {
        var queue = new Queue<SerialSend>(sends);
        SerialSend? send = null;
        var next = 0;
        void SendByte()
        {
            if (send is null)
            {
                send = queue.Dequeue();
                next = 0;
                chip.Print($"input: {serial.Name} \"{SimulationOptions.Escape(send.Bytes)}\"");
            }

            if (next < send.Bytes.Length)
            {
                serial.Receive(send.Bytes[next++]);
            }

            var byteTicks = 10L * SimulatedChip.TicksPerSecond / (serial.BaudRate > 0 ? serial.BaudRate : 9600);
            if (next < send.Bytes.Length)
            {
                chip.At(chip.Ticks + byteTicks, SendByte, external: true);
            }
            else if (queue.Count > 0)
            {
                send = null;
                chip.At(Math.Max(chip.Ticks + byteTicks, Tick(queue.Peek().AtMilliseconds ?? 0)), SendByte, external: true);
            }
        }

        chip.At(Tick(sends[0].AtMilliseconds ?? 0), SendByte, external: true);
    }
}
