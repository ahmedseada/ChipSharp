namespace ChipSharp.Simulators.Devices;

/// <summary>
/// An alarm of the simulated time for a device (an LCD that prints what it shows once it settles), as rp2040js's clock
/// has them: <see cref="Schedule"/> runs the callback once, that many nanoseconds from now, unless
/// <see cref="Cancel"/> or another Schedule comes first. It wakes no sleeping chip: it is the device's, not the program's.
/// </summary>
public sealed class DeviceAlarm(SimulatedChip chip, Action callback)
{
    private int _version;

    public void Schedule(double nanoseconds)
    {
        var version = ++_version;
        var ticks = (long)Math.Round(nanoseconds * SimulatedChip.TicksPerSecond / 1e9);
        chip.At(chip.Ticks + Math.Max(ticks, 0), () =>
        {
            if (version == _version)
            {
                callback();
            }
        });
    }

    public void Cancel() => _version++;
}

/// <summary>
/// A chip's analog inputs: the pins (by name: "PD3") an --adc may set, and the volts the full scale would read (the
/// reference). Each starts at 0 V; <see cref="Set"/> changes one, as the --adc timeline does.
/// </summary>
public sealed class AnalogInputs(IReadOnlyList<string> pins, double referenceVolts, int fullScale)
{
    private readonly Dictionary<string, double> _volts = pins.ToDictionary(pin => pin, _ => 0.0, StringComparer.Ordinal);

    /// <summary>The pins with an analog input.</summary>
    public IReadOnlyList<string> Pins { get; } = pins;

    /// <summary>The volts of the reference, which a reading of <see cref="FullScale"/> would be.</summary>
    public double ReferenceVolts { get; } = referenceVolts;

    /// <summary>One more than the highest reading: 1024 for 10 bits.</summary>
    public int FullScale { get; } = fullScale;

    public double Volts(string pin) => _volts.GetValueOrDefault(pin);

    public void Set(string pin, double volts) => _volts[pin] = volts;

    /// <summary>The reading of a pin's voltage, the full scale's fraction it is, rounded down, 0 to the highest.</summary>
    public int Reading(string pin) =>
        (int)Math.Clamp(Math.Floor(Volts(pin) / ReferenceVolts * FullScale), 0, FullScale - 1);
}
