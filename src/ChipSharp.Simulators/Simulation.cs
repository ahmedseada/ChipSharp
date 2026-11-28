using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ChipSharp.Simulators.Ch32v003;
using ChipSharp.Simulators.Devices;
using ChipSharp.Simulators.Esp32c5;
using ChipSharp.Simulators.Pdk14;
using ChipSharp.Simulators.Stm8;

namespace ChipSharp.Simulators;

/// <summary>Wrong arguments: what is wrong, for the usage message.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>An --input: a pin driven from outside, high or low, from the start or from a time on.</summary>
public sealed record PinInput(string Pin, bool High, double? AtMilliseconds);

/// <summary>
/// A --send: bytes sent to a serial port of the chip (<paramref name="Port"/>, by its name: "UART0", "USB"; null for its
/// first), from the start or from a time on.
/// </summary>
public sealed record SerialSend(byte[] Bytes, double? AtMilliseconds, string? Port = null);

/// <summary>What to simulate: the chip, the image, for how long, and what comes from outside.</summary>
public sealed partial class SimulationOptions
{
    /// <summary>The chip, as <see cref="Simulation.Chips"/> names it: "pfs154", "stm8s103", "esp32c5", "ch32v003".</summary>
    public required string Chip { get; init; }

    /// <summary>
    /// The image: an Intel HEX file (the PFS154, the STM8S103), an ELF file or an ESP image (the ESP32-C5), an Intel HEX
    /// file, a .bin or an ELF file (the CH32V003).
    /// </summary>
    public required string Image { get; init; }

    /// <summary>How long to run, in simulated milliseconds.</summary>
    public double Milliseconds { get; init; } = 1000;

    /// <summary>Files to write every byte a serial port sent to, by the port's name ("UART1": --uart1, "USART1": --usart1).</summary>
    public IReadOnlyDictionary<string, string> SerialFiles { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<PinInput> Inputs { get; init; } = [];

    public IReadOnlyList<SerialSend> Sends { get; init; } = [];

    /// <summary>The devices on the chip's buses (--i2c, --spi).</summary>
    public IReadOnlyList<BusDevice> Devices { get; init; } = [];

    /// <summary>The voltages on its analog inputs (--adc).</summary>
    public IReadOnlyList<AnalogInput> Analog { get; init; } = [];

    /// <summary>The ESP32-C5's SPLL is running when the program starts (--pll-running): a bootloader powered and calibrated it.</summary>
    public bool PllRunning { get; init; }

    /// <summary>
    /// The ESP32-C5 program is an ESP image started as ESP-IDF's second-stage bootloader starts an app (--idf-bootloader): the
    /// image must pass the bootloader's checks, and the chip is left as the bootloader leaves it (PLL, watchdog).
    /// </summary>
    public bool IdfBootloader { get; init; }

    /// <summary>
    /// The options of the command line after the chip: the image, --ms, --uart0, --uart1, --usart1, --usb, --input,
    /// --send, --i2c, --spi and --adc
    /// (see <see cref="Simulation.Usage"/>). Throws <see cref="UsageException"/> when they are wrong.
    /// </summary>
    public static SimulationOptions Parse(string chip, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? image = null;
        var serialFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var milliseconds = 1000.0;
        var inputs = new List<PinInput>();
        var sends = new List<SerialSend>();
        var devices = new List<BusDevice>();
        var analog = new List<AnalogInput>();
        var pllRunning = false;
        var idfBootloader = false;
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

            if (argument is not ("--ms" or "--uart0" or "--uart1" or "--usart1" or "--usb" or "--input" or "--send" or "--i2c"
                or "--spi" or "--adc" or "--pll-running" or "--idf-bootloader"))
            {
                throw new UsageException($"unknown option '{argument}'.");
            }

            if (argument == "--pll-running")
            {
                pllRunning = true;
                continue;
            }

            if (argument == "--idf-bootloader")
            {
                idfBootloader = true;
                continue;
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
                case "--uart0" or "--uart1" or "--usart1" or "--usb":
                    serialFiles[argument[2..].ToUpperInvariant()] = value;
                    break;
                case "--i2c":
                    devices.Add(DeviceCatalog.ParseI2c(value));
                    break;
                case "--spi":
                    devices.Add(DeviceCatalog.ParseSpi(value));
                    break;
                case "--adc":
                    analog.Add(DeviceCatalog.ParseAnalog(value));
                    break;
                case "--input":
                    var input = InputPattern().Match(value);
                    if (!input.Success)
                    {
                        throw new UsageException("--input takes a pin, a level and maybe a time, e.g. PA3=high, " +
                            $"PA3=low@100ms or GPIO9=low, not '{value}'.");
                    }

                    inputs.Add(new PinInput(input.Groups[1].Value.ToUpperInvariant(),
                        input.Groups[2].Value.Equals("high", StringComparison.OrdinalIgnoreCase), Time(input.Groups[3])));
                    break;
                default:
                    var send = SendPattern().Match(value);
                    sends.Add(new SerialSend(Unescape(send.Groups[2].Value), Time(send.Groups[3]),
                        send.Groups[1].Success ? send.Groups[1].Value.ToUpperInvariant() : null));
                    break;
            }
        }

        return new SimulationOptions
        {
            Chip = chip,
            Image = image ?? throw new UsageException(chip.ToLowerInvariant() switch
            {
                "esp32c5" => "which image (.elf, or an ESP image)?",
                "ch32v003" => "which image (.hex, .bin or .elf)?",
                _ => "which .ihx image?",
            }),
            Milliseconds = milliseconds,
            SerialFiles = serialFiles,
            Inputs = inputs,
            Sends = sends,
            Devices = devices,
            Analog = analog,
            PllRunning = pllRunning,
            IdfBootloader = idfBootloader,
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

    [GeneratedRegex(@"^(P[A-Fa-f][0-7]|GPIO\d{1,2})=(high|low)(?:@(\d+(?:\.\d+)?)ms)?$", RegexOptions.IgnoreCase)]
    private static partial Regex InputPattern();

    [GeneratedRegex(@"^(?:(US?ART\d|USB)=)?(.*?)(?:@(\d+(?:\.\d+)?)ms)?$", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SendPattern();
}

/// <summary>What a run printed, how it ended, and the chip as it was at the end.</summary>
public sealed partial class SimulationResult(int exitCode, IReadOnlyList<string> lines, IReadOnlyDictionary<string, byte[]> serial,
    SimulatedChip chip)
{
    /// <summary>0: the program ran (to its end, or for the whole time); 3: it went wrong.</summary>
    public int ExitCode { get; } = exitCode;

    /// <summary>Every line printed, the "booting" line first and the "stopped" line last.</summary>
    public IReadOnlyList<string> Lines { get; } = lines;

    /// <summary>The bytes the chip's first serial port sent (UART1 on the STM8S103, UART0 on the ESP32-C5).</summary>
    public byte[] Serial { get; } = chip.SerialOutputs.Count > 0 ? serial[chip.SerialOutputs[0].Name] : [];

    /// <summary>The bytes every serial port sent, by its name ("UART0", "USB").</summary>
    public IReadOnlyDictionary<string, byte[]> SerialPorts { get; } = serial;

    /// <summary>The bytes the serial port <paramref name="port"/> sent; none for a port the chip does not have.</summary>
    public byte[] SerialOf(string port) => SerialPorts.TryGetValue(port, out var bytes) ? bytes : [];

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

    [GeneratedRegex(@"^\[\s*([0-9.]+) ms\] (P[A-F][0-7]|GPIO\d+) (?!PWM )(.+)$")]
    private static partial Regex PinLine();

    [GeneratedRegex(@"^\[\s*[0-9.]+ ms\] (?:US?ART\d|USB): (.*)$")]
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
            ["esp32c5"] = Esp32c5Chip.Load,
            ["ch32v003"] = Ch32v003Chip.Load,
        };

    /// <summary>What the command line takes, and what it prints.</summary>
    public const string Usage = """
        Usage: chipsim <pfs154|stm8s103|esp32c5|ch32v003> <image> [--ms <simulated milliseconds, default 1000>]
                                                     [--uart0|--uart1|--usart1|--usb <file to write that port's bytes to>]
                                                     [--input <pin>=<high|low>[@<simulated milliseconds>ms]]...
                                                     [--send [<port>=]<text>[@<simulated milliseconds>ms]]...
                                                     [--i2c 0:<address>=<device>]... [--spi 0=<device>[@<pin>]]...
                                                     [--adc <pin>=<volts>V|<reading>[@<simulated milliseconds>ms]]...
                                                     [--pll-running] [--idf-bootloader]

        Runs an image on a simulated chip and prints what the program does, each with the simulated time: an Intel HEX
        image (as SDCC writes it: sdcc -mpdk14, sdcc -mstm8) on a Padauk PFS154 or an STM8S103, an ELF file (as ld.lld
        writes it for riscv32) or an ESP image on an Espressif ESP32-C5, started as its ROM's loader leaves it (its pins
        GPIO0-GPIO28; UART0, the default serial port, and USB, the USB Serial/JTAG console), an Intel HEX file, a .bin
        (at 0x08000000) or an ELF file in the flash of a WCH CH32V003, started at 0 as reset leaves it (its pins PA1,
        PA2, PC0-PC7 and PD0-PD7; USART1 on PD5 and PD6, or where AFIO's remap puts it).

        --input drives a pin from outside, as a button or another chip would, e.g. --input PA3=high, or
        --input PA3=low@100ms (the pin changes then, and the change is printed). Give a pin several times to change it
        several times. A pin no --input drives reads high when the program turns its pull-up on, else low.

        --send sends text to a serial port's RX (the STM8S103's UART1 on PD6, the ESP32-C5's UART0, or USB=... its USB
        Serial/JTAG port, the CH32V003's USART1), byte after byte at the port's speed (10 bits a byte), from that time (or the start), e.g.
        --send "on\n@100ms" or --send "USB=on\n": its UTF-8 bytes, \r, \n, \t, \\ and \xHH standing for those bytes.
        Sends to a port follow each other. A byte that arrives while a UART's receiver is off, or its FIFO full, is lost,
        as on the chip; the USB host holds its bytes until the chip has room.

        --i2c and --spi put the Pico and Uno simulators' devices (tools/rp2040-sim/devices, in C#) on the STM8S103's
        buses, I2C0 (PB5, PB4) and SPI0 (PC5, PC6, PC7): --i2c 0:0x27=lcd1602 a 16x2 LCD on a PCF8574 backpack at 0x27
        (lcd2004: 20x4), --i2c 0:0x68=ds1307 a DS1307 real-time clock from the PC's time (ds1307@2026-11-27T18:00:00
        from a date, ds1307@halted a new one), --i2c 0:0x68=mpu6050, 0:0x76=bmp280, 0:0x50=stuck (holds the clock low);
        --spi 0=74hc595@PC4 a 74HC595 shift register with its latch on PC4, --spi 0=loopback RX wired to TX (@<pin>: only
        while that pin is low). An I2C address without a device does not answer.

        --adc sets the voltage on an analog input of the STM8S103 (AIN2-AIN6: PC4, PD2, PD3, PD5, PD6; 0 to 3.3 V against
        VDD, 1.65V reads 512 of 1023; or 512, a reading), from the start or from a time on, e.g. --adc PD3=1.65V@10ms.

        On the ESP32-C5 the same options reach I2C0 and SPI0 (GP-SPI2) on whatever pins the program gives them, a latch or
        chip select as GPIOn (--spi 0=74hc595@GPIO23), and the analog inputs GPIO1-GPIO6 (0 to 3.3 V, 12 bits: 1.65V reads
        2048); its PWM outputs (the LEDC) are measured on their pins like the STM8S103's and the PFS154's.

        Output lines:

            [    0.123 ms] PA3 high                  a pin changed: low or high when driven, else input or input pull-up
            [    0.200 ms] UART1 baud 9600           the program set UART1's speed
            [    1.234 ms] UART1: Hello              a line written to UART1 (without its line ending); USB: the same
                                                     for the ESP32-C5's USB Serial/JTAG port, USART1 the CH32V003's
            [    0.050 ms] CPU clock 240 MHz         the program changed the CPU's clock (the ESP32-C5, the CH32V003)
            [  100.000 ms] input: PA3 low            an --input with a time changed the pin from outside
            [  100.000 ms] input: UART1 "on\n"       a --send starts sending
            [   10.000 ms] input: PD3 1.650 V (512)  an --adc with a time changed an analog input
            [    2.100 ms] I2C0 100000 Hz            the clock the program set, when a transfer first uses it
            [    2.500 ms] I2C0 0x68 write 75, read 68
                                                     a transfer, its bytes each way in hex (": no answer" when nothing
                                                     answered, "(refused)" when the device refused a byte)
            [    3.000 ms] SPI0 mode 0, 1000000 Hz   the SPI mode and clock, when a transfer first uses them
            [    3.100 ms] SPI0 74HC595 Q3 high      a shift register's output changed (its latch went high)
            [   60.000 ms] LCD 0x27: |Hello           |
                                                     a row of an LCD, once what it shows has changed and settled
            [   12.000 ms] PB4 PWM 1000.00 Hz, duty 25.00% (250.00 us high)
                                                     a pin a timer drives, measured from its edges: printed when a period
                                                     differs from the one before, instead of every edge
            [   15.000 ms] PB4 PWM steady low        a timer's pin with no edge for 2.5 periods
            [ 1000.000 ms] stopped: <why>            the last line

        The run stops after the simulated time, or earlier: when the program has finished (it sleeps with nothing left
        to wake it: stopsys or stopexe on the PFS154, halt or wfi on the STM8S103, wfi on the ESP32-C5 with no interrupt
        enabled above the threshold to come, wfi on the CH32V003 with interrupts off or none the PFIC would take to
        come), or went wrong (the PFS154's reset instruction or its watchdog, the STM8S103's break or its IWDG, the
        ESP32-C5's ebreak, an exception without a trap handler, a ROM function the simulator does not have or a
        watchdog's reset (the ROM leaves MWDT0, RWDT and the super watchdog running), the CH32V003's ebreak, a trap with
        no handler, its IWDG or WWDG, a software reset, a peripheral used with its clock off or not simulated yet, 48 MHz
        without a flash wait state, an instruction or an address the chip does not have, memory the image does not
        fill, an interrupt with no handler, a pop past the top of the stack).

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

        if (options.IdfBootloader)
        {
            if (!options.Chip.Equals("esp32c5", StringComparison.OrdinalIgnoreCase))
            {
                throw new UsageException("--idf-bootloader is for the esp32c5.");
            }

            Esp32c5Image.CheckForIdfBootloader(options.Image);
        }

        var chip = load(options.Image);
        if (options.PllRunning && chip is Esp32c5Chip c5)
        {
            c5.PllRunning = true;
        }

        if (options.IdfBootloader && chip is Esp32c5Chip started)
        {
            started.StartLikeIdfBootloader();
        }

        var lines = new List<string>();
        void Log(string line)
        {
            lines.Add(line);
            echo?.Invoke(line);
        }

        var pins = options.Inputs.Select(input => (input, Pin(chip, input.Pin))).ToList();
        if ((options.Sends.Count > 0 || options.SerialFiles.Count > 0) && chip.SerialInputs.Count == 0)
        {
            throw new UsageException($"the {chip.Name} has no UART for --send or --uart1.");
        }

        var ports = chip.SerialInputs.ToDictionary(input => input.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var port in options.Sends.Select(send => send.Port).OfType<string>().Concat(options.SerialFiles.Keys))
        {
            if (!ports.ContainsKey(port))
            {
                throw new UsageException($"the {chip.Name} has no {port}; its serial ports: {string.Join(", ", ports.Keys)}.");
            }
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

        foreach (var group in options.Sends.GroupBy(send => send.Port ?? chip.SerialInputs[0].Name, StringComparer.OrdinalIgnoreCase))
        {
            ScheduleSends(chip, ports[group.Key], [.. group]);
        }

        DeviceCatalog.Attach(chip, options.Devices, options.Analog, name => Pin(chip, name));
        chip.Run(Tick(options.Milliseconds));
        foreach (var bus in chip.I2cBuses)
        {
            bus.Finish();
        }

        foreach (var output in chip.SerialOutputs)
        {
            output.Finish();
        }

        var sent = chip.SerialOutputs.ToDictionary(output => output.Name, output => output.Bytes.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (port, file) in options.SerialFiles)
        {
            File.WriteAllBytes(file, sent[port]);
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
        if (chip.FindPin(name) is { } pin)
        {
            return pin;
        }

        var all = chip.Ports.SelectMany(p => Enumerable.Range(0, 8).Where(b => (p.Pins & (1 << b)) != 0).Select(p.PinName));
        throw new UsageException($"the {chip.Name} has no pin {name}; it has {string.Join(", ", all)}.");
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
