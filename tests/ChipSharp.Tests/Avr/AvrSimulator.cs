using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Avr;

/// <summary>A pin change the simulator saw: when (simulated milliseconds), which Uno pin (D13, A0, ...), its new state.</summary>
internal sealed record AvrPinChange(double Milliseconds, string Pin, string State);

/// <summary>
/// A PWM output's period as the simulator measured it from its edges: when, which Uno pin, its frequency, its duty
/// cycle in percent, and how long it was high, in microseconds.
/// </summary>
internal sealed record AvrPwmPeriod(double Milliseconds, string Pin, double Hertz, double Duty, double HighMicroseconds);

/// <summary>What one run of tools/avr-sim/run.mjs printed, and the bytes the program wrote to USART0.</summary>
internal sealed partial record AvrSimulatorRun(int ExitCode, string Output, string Errors, byte[] Usart0)
{
    /// <summary>Every PWM period printed, in order (each one that differs from the one before).</summary>
    public IReadOnlyList<AvrPwmPeriod> Pwm =>
    [
        .. Lines.Select(line => PwmLine().Match(line)).Where(m => m.Success).Select(m => new AvrPwmPeriod(
            Number(m.Groups[1].Value), m.Groups[2].Value, Number(m.Groups[3].Value), Number(m.Groups[4].Value),
            Number(m.Groups[5].Value))),
    ];

    /// <summary>The lines printed that start with <paramref name="prefix"/> after their time, without the time.</summary>
    public IReadOnlyList<string> After(string prefix) =>
    [
        .. Lines.Where(line => line.Length > 16 && line[0] == '[' && line[16..].StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[16..]),
    ];

    /// <summary>The times, in simulated milliseconds, of the lines that start with <paramref name="prefix"/> after their time.</summary>
    public IReadOnlyList<double> Times(string prefix) =>
    [
        .. Lines.Where(line => line.Length > 16 && line[0] == '[' && line[16..].StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => Number(line[1..11])),
    ];

    /// <summary>The time, in simulated milliseconds, of the first line that starts with <paramref name="prefix"/> after its time.</summary>
    public double Time(string prefix) =>
        Number(Lines.First(line => line.Length > 16 && line[16..].StartsWith(prefix, StringComparison.Ordinal))[1..11]);

    /// <summary>Every pin change, in order (not those an --input made, nor a PWM output's edges).</summary>
    public IReadOnlyList<AvrPinChange> Pins =>
    [
        .. Lines.Select(line => PinLine().Match(line)).Where(m => m.Success).Select(m => new AvrPinChange(
            double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value, m.Groups[3].Value)),
    ];

    /// <summary>The lines written to USART0, without their line endings.</summary>
    public IReadOnlyList<string> Usart0Lines =>
        [.. Lines.Select(line => SerialLine().Match(line)).Where(m => m.Success).Select(m => m.Groups[1].Value)];

    /// <summary>The baud rates the program set on USART0, in order.</summary>
    public IReadOnlyList<int> Usart0BaudRates =>
    [
        .. Lines.Select(line => BaudLine().Match(line)).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)),
    ];

    /// <summary>The last line: why the run stopped.</summary>
    public string Stopped => Lines.LastOrDefault() ?? "";

    private IEnumerable<string> Lines =>
        Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'));

    public override string ToString() => $"exit code {ExitCode}\n{Output}\n{Errors}";

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\[\s*([0-9.]+) ms\] ([DA]\d+|P[BCD]\d) (?!PWM )(.+)$")]
    private static partial Regex PinLine();

    [GeneratedRegex(@"^\[\s*([0-9.]+) ms\] (D\d+) PWM ([0-9.]+) Hz, duty ([0-9.]+)% \(([0-9.]+) us high\)$")]
    private static partial Regex PwmLine();

    [GeneratedRegex(@"^\[\s*[0-9.]+ ms\] USART0: (.*)$")]
    private static partial Regex SerialLine();

    [GeneratedRegex(@"^\[\s*[0-9.]+ ms\] USART0 baud (\d+)$")]
    private static partial Regex BaudLine();
}

/// <summary>Runs a .hex in the avr8js simulator (an Arduino Uno's ATmega328P) through tools/avr-sim/run.mjs.</summary>
internal static class AvrSimulator
{
    /// <summary>
    /// Runs <paramref name="hex"/> for at most <paramref name="milliseconds"/> of simulated time, with more of run.mjs's
    /// options after that (e.g. "--input", "D2=high").
    /// </summary>
    public static async Task<AvrSimulatorRun> RunAsync(string hex, int milliseconds, params string[] options)
    {
        var usart = Path.ChangeExtension(hex, ".usart0.bin");
        var start = new ProcessStartInfo(PicoTools.Node ?? "node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string[] arguments =
        [
            Path.Combine(AvrTools.Simulator, "run.mjs"), hex,
            "--ms", milliseconds.ToString(CultureInfo.InvariantCulture), "--usart0", usart, .. options,
        ];
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("node did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The simulator ran '{hex}' for more than 3 minutes.");
        }

        return new AvrSimulatorRun(process.ExitCode, await output, await errors,
            File.Exists(usart) ? await File.ReadAllBytesAsync(usart) : []);
    }
}
