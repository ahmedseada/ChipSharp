using System.Text;

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// What the conformance suite's tests share: each builds a small C# program for a chip, with its adapter's pins as
/// constants, and runs it in the chip's simulator. A program says what it found with <c>Check.That(bool)</c>, which
/// toggles the pass pin when its condition holds and the fail pin when not: that needs nothing but pins, so the same
/// program runs on every chip, a 128-byte Padauk's too.
/// </summary>
public abstract class ConformanceTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// <paramref name="statements"/> (top-level statements) and <paramref name="members"/> (of <c>partial class
    /// Program</c>), with the pins of <paramref name="chip"/> as Program's constants (Output, Second, Pass, Fail,
    /// Input, and AdcPin, PwmPin, UartTx, UartRx, Sda, Scl, Sck, Mosi, Miso when the chip has them) and the class Check, built
    /// for the chip and run with <paramref name="stimulus"/>.
    /// </summary>
    internal async Task<ChipRun> RunAsync(ChipSimulator chip, string name, string statements, ChipStimulus stimulus,
        string members = "")
    {
        var pins = chip.Pins;
        var constants = new StringBuilder()
            .Append($"    internal const int Output = {pins.Output}, Second = {pins.Second}, Pass = {pins.Pass}, ")
            .Append($"Fail = {pins.Fail}, Input = {pins.Input};\n");
        if (pins.Adc is { } adc)
        {
            constants.Append($"    internal const int AdcPin = {adc};\n");
        }

        if (pins.Pwm is { } pwm)
        {
            constants.Append($"    internal const int PwmPin = {pwm};\n");
        }

        if (pins.Uart0 is { } uart)
        {
            constants.Append($"    internal const int UartTx = {uart.Tx}, UartRx = {uart.Rx};\n");
        }

        if (pins.I2c0 is { } i2c)
        {
            constants.Append($"    internal const int Sda = {i2c.Sda}, Scl = {i2c.Scl};\n");
        }

        if (pins.Spi0 is { } spi)
        {
            constants.Append($"    internal const int Sck = {spi.Sck}, Mosi = {spi.Tx}, Miso = {spi.Rx};\n");
        }

        var source = $$"""
            using System;
            using System.Diagnostics;
            using ChipSharp.Hardware;

            {{statements}}

            partial class Program
            {
            {{constants}}
            {{members}}
            }

            static class Check
            {
                public static void Start()
                {
                    Pin.Output(Program.Pass);
                    Pin.Output(Program.Fail);
                }

                public static void That(bool holds) => Pin.Toggle(holds ? Program.Pass : Program.Fail);
            }
            """;
        var image = chip.Build(source, name, Path.Combine(_scratch, name));
        return await chip.RunAsync(image, stimulus);
    }

    /// <summary>
    /// The program made <paramref name="count"/> checks, and each held: the pass pin toggled that often and the fail pin
    /// never. Else which check did not hold, counted from 1.
    /// </summary>
    internal static void AssertChecks(ChipSimulator chip, ChipRun run, int count)
    {
        var passed = run.Levels(chip.Pins.Pass).Skip(1).Select(change => (change.Milliseconds, Held: true));
        var failed = run.Levels(chip.Pins.Fail).Skip(1).Select(change => (change.Milliseconds, Held: false));
        var checks = passed.Concat(failed).OrderBy(check => check.Milliseconds).ToList();
        var failing = checks.FindIndex(check => !check.Held);
        Assert.True(failing < 0, $"{chip}: check {failing + 1} of {count} did not hold\n{run}");
        Assert.True(checks.Count == count, $"{chip}: {checks.Count} checks of {count} ran\n{run}");
    }

    /// <summary>
    /// The times of <paramref name="pin"/>'s changes after its first level (Pin.Output's low), in milliseconds since that
    /// first one.
    /// </summary>
    internal static IReadOnlyList<double> Toggles(ChipRun run, int pin)
    {
        var levels = run.Levels(pin);
        return levels.Count == 0 ? [] : [.. levels.Skip(1).Select(change => change.Milliseconds - levels[0].Milliseconds)];
    }

    /// <summary>
    /// How far apart two times the simulators print can be from the times themselves: they print a thousandth of a
    /// millisecond, rounded.
    /// </summary>
    internal const double Printed = 0.002;

    /// <summary>Asserts <paramref name="value"/> is in [<paramref name="low"/>, <paramref name="high"/>], saying what.</summary>
    internal static void AssertIn(double value, double low, double high, string what, ChipRun run) =>
        Assert.True(value >= low && value <= high, $"{what}: {value}, not between {low} and {high}\n{run}");
}
