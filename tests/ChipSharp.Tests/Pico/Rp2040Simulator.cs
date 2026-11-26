using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ChipSharp.Tests.Pico;

/// <summary>A pin change the simulator saw: when (simulated milliseconds), which pin, and its new state (low, high, input, ...).</summary>
internal sealed record PinChange(double Milliseconds, int Pin, string State);

/// <summary>What one run of tools/rp2040-sim/run.mjs printed, and the bytes the program wrote to UART0.</summary>
internal sealed partial record SimulatorRun(int ExitCode, string Output, string Errors, byte[] Uart0)
{
    /// <summary>Every GPIO change, in order.</summary>
    public IReadOnlyList<PinChange> Pins =>
    [
        .. Output.Split('\n').Select(line => PinLine().Match(line.TrimEnd('\r'))).Where(m => m.Success).Select(m =>
            new PinChange(
                double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                m.Groups[3].Value)),
    ];

    /// <summary>The baud rates the program set on UART0, in order.</summary>
    public IReadOnlyList<int> Uart0BaudRates =>
    [
        .. Output.Split('\n').Select(line => BaudLine().Match(line.TrimEnd('\r'))).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)),
    ];

    /// <summary>The last line: why the run stopped.</summary>
    public string Stopped => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.TrimEnd('\r') ?? "";

    public override string ToString() => $"exit code {ExitCode}\n{Output}\n{Errors}";

    [GeneratedRegex(@"^\[\s*([0-9.]+) ms\] GPIO(\d+) (.+)$")]
    private static partial Regex PinLine();

    [GeneratedRegex(@"^\[\s*[0-9.]+ ms\] UART0 baud (\d+)$")]
    private static partial Regex BaudLine();
}

/// <summary>Runs a .uf2 in the rp2040js simulator through tools/rp2040-sim/run.mjs.</summary>
internal static class Rp2040Simulator
{
    /// <summary>
    /// Runs <paramref name="uf2"/> for at most <paramref name="milliseconds"/> of simulated time, with more of
    /// run.mjs's options after that (e.g. "--input", "3=high").
    /// </summary>
    public static async Task<SimulatorRun> RunAsync(string uf2, int milliseconds, params string[] options)
    {
        var uart = Path.ChangeExtension(uf2, ".uart0.bin");
        var start = new ProcessStartInfo(PicoTools.Node ?? "node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string[] arguments =
        [
            Path.Combine(Repository.Simulator, "run.mjs"), uf2,
            "--ms", milliseconds.ToString(CultureInfo.InvariantCulture), "--uart0", uart, .. options,
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
            throw new TimeoutException($"The simulator ran '{uf2}' for more than 3 minutes.");
        }

        return new SimulatorRun(process.ExitCode, await output, await errors,
            File.Exists(uart) ? await File.ReadAllBytesAsync(uart) : []);
    }
}
