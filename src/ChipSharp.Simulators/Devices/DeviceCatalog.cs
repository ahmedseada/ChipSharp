using System.Globalization;
using System.Text.RegularExpressions;

namespace ChipSharp.Simulators.Devices;

/// <summary>A device an --i2c or --spi puts on a bus: which bus, the device, its address (I2C), its date (a DS1307) or its pin (SPI).</summary>
public sealed record BusDevice(string Kind, int Bus, string Device, int? Address = null, string? At = null, string? Pin = null);

/// <summary>An --adc: the voltage on an analog input from the start or from a time on; a reading when it gave one.</summary>
public sealed record AnalogInput(string Pin, double? Volts, int? Reading, double? AtMilliseconds);

/// <summary>
/// The simulated devices chipsim puts on a chip's buses, tools/rp2040-sim/devices' (catalog.mjs) in C#, with the same
/// options:
/// <code>
/// --i2c &lt;bus&gt;:&lt;address&gt;=&lt;device&gt;     a device at an address (0x68 or 104) on an I2C bus:
///                                      mpu6050   accelerometer and gyroscope
///                                      bmp280    pressure and temperature
///                                      lcd1602   a 16x2 character LCD on a PCF8574 backpack
///                                      lcd2004   the same, 20x4
///                                      stuck     a device that holds the clock low for ever once addressed
///                                      ds1307[@&lt;date&gt;]  a real-time clock, from the PC's time, or from a date
///                                                (2026-11-27T18:00:00), or halted (@halted)
/// --spi &lt;bus&gt;=&lt;device&gt;[@&lt;pin&gt;]       a device on an SPI bus:
///                                      74hc595@&lt;latch pin&gt;   a shift register
///                                      loopback[@&lt;chip select pin&gt;]   RX wired to TX; with a pin, only while the
///                                      program holds that pin low
/// --adc &lt;pin&gt;=&lt;volts&gt;V|&lt;reading&gt;[@&lt;ms&gt;ms]   the voltage on an analog input (or the reading it gives), from the
///                                      start or from a time on
/// </code>
/// </summary>
public static partial class DeviceCatalog
{
    /// <summary>The I2C devices by the names the option takes.</summary>
    public static IReadOnlyList<string> I2cDevices { get; } = ["mpu6050", "bmp280", "lcd1602", "lcd2004", "stuck", "ds1307"];

    /// <summary>The device an --i2c value names, e.g. "0:0x68=mpu6050"; a <see cref="UsageException"/> when it is not valid.</summary>
    public static BusDevice ParseI2c(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var match = I2cPattern().Match(value);
        var address = match.Success ? Number(match.Groups[2].Value) : -1;
        if (!match.Success || address is < 0 or > 0x7F || !I2cDevices.Contains(match.Groups[3].Value))
        {
            throw new UsageException($"--i2c takes a bus, an address from 0 to 0x7f and a device " +
                $"({string.Join(", ", I2cDevices)}), e.g. 0:0x68=mpu6050, not '{value}'.");
        }

        var at = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (at is not null && (match.Groups[3].Value != "ds1307" || Ds1307.ParseStart(at) is null))
        {
            throw new UsageException("--i2c takes a date after '@' only for a ds1307, from 2000 to 2099 " +
                $"(2026-11-27T18:00:00, or halted), not '{value}'.");
        }

        return new BusDevice("i2c", int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[3].Value,
            address, at);
    }

    /// <summary>The device an --spi value names, e.g. "0=74hc595@PC4"; a <see cref="UsageException"/> when it is not valid.</summary>
    public static BusDevice ParseSpi(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var match = SpiPattern().Match(value);
        if (!match.Success || (match.Groups[2].Value == "74hc595" && !match.Groups[3].Success))
        {
            throw new UsageException("--spi takes a bus and a device: 74hc595@<its latch pin> or loopback, maybe " +
                $"@<its chip select pin>, e.g. 0=74hc595@PC4, not '{value}'.");
        }

        return new BusDevice("spi", int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value,
            Pin: match.Groups[3].Success ? match.Groups[3].Value.ToUpperInvariant() : null);
    }

    /// <summary>An --adc value, e.g. "PD3=1.65V@10ms" or "PD3=512"; a <see cref="UsageException"/> when it is not valid.</summary>
    public static AnalogInput ParseAnalog(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var match = AnalogPattern().Match(value);
        if (!match.Success)
        {
            throw new UsageException("--adc takes an analog pin and volts or a reading, and maybe a time, e.g. PD3=1.65V, " +
                $"PD3=512 or PD3=1V@100ms, not '{value}'.");
        }

        var number = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var at = match.Groups[4].Success ? double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : (double?)null;
        if (match.Groups[3].Success)
        {
            return new AnalogInput(match.Groups[1].Value.ToUpperInvariant(), number, null, at);
        }

        if (number != Math.Floor(number))
        {
            throw new UsageException($"--adc: a reading is a whole number, or volts end with V, not '{value}'.");
        }

        return new AnalogInput(match.Groups[1].Value.ToUpperInvariant(), null, (int)number, at);
    }

    /// <summary>
    /// Puts the devices on the chip's buses, and the analog inputs on its timeline: a bus, pin or voltage the chip does
    /// not have is a <see cref="UsageException"/>. A shift register's latch, and a loopback's chip select, are pins of the
    /// chip.
    /// </summary>
    public static void Attach(SimulatedChip chip, IReadOnlyList<BusDevice> devices, IReadOnlyList<AnalogInput> analog,
        Func<string, (GpioPort Port, int Bit)> pin)
    {
        ArgumentNullException.ThrowIfNull(chip);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(analog);
        ArgumentNullException.ThrowIfNull(pin);
        foreach (var device in devices)
        {
            var buses = device.Kind == "i2c" ? chip.I2cBuses.Count : chip.SpiBuses.Count;
            if (device.Bus >= buses)
            {
                throw new UsageException(buses == 0
                    ? $"the {chip.Name} has no {device.Kind.ToUpperInvariant()} bus for --{device.Kind}."
                    : $"the {chip.Name} has {device.Kind.ToUpperInvariant()} bus{(buses == 1 ? " 0" : $"es 0 to {buses - 1}")}, " +
                      $"not {device.Bus}.");
            }

            if (device.Kind == "i2c")
            {
                AttachI2c(chip, chip.I2cBuses[device.Bus], device);
            }
            else
            {
                AttachSpi(chip, chip.SpiBuses[device.Bus], device, device.Pin is null ? null : pin(device.Pin));
            }
        }

        foreach (var input in analog)
        {
            var inputs = chip.Analog ?? throw new UsageException($"the {chip.Name} has no analog inputs for --adc.");
            if (!inputs.Pins.Contains(input.Pin))
            {
                throw new UsageException($"the {chip.Name} has no analog input on {input.Pin}; it has " +
                    $"{string.Join(", ", inputs.Pins)}.");
            }

            var volts = input.Volts ?? (input.Reading!.Value + 0.5) * inputs.ReferenceVolts / inputs.FullScale;
            if (input.Volts is { } given ? !(given >= 0 && given <= inputs.ReferenceVolts)
                : input.Reading is < 0 || input.Reading >= inputs.FullScale)
            {
                throw new UsageException(string.Create(CultureInfo.InvariantCulture,
                    $"--adc: {input.Pin} reads 0 to {inputs.ReferenceVolts} V, 0 to {inputs.FullScale - 1}."));
            }

            if (input.AtMilliseconds is { } at)
            {
                chip.At(Simulation.Tick(at), () =>
                {
                    inputs.Set(input.Pin, volts);
                    chip.Print(string.Create(CultureInfo.InvariantCulture,
                        $"input: {input.Pin} {volts:F3} V ({inputs.Reading(input.Pin)})"));
                });
            }
            else
            {
                inputs.Set(input.Pin, volts);
            }
        }
    }

    private static void AttachI2c(SimulatedChip chip, I2cBus bus, BusDevice device)
    {
        var address = device.Address!.Value;
        bus.Add(address, device.Device switch
        {
            "mpu6050" => new Mpu6050(),
            "bmp280" => new Bmp280(),
            "lcd1602" => new Lcd(address, 16, 2, chip, chip.Print),
            "lcd2004" => new Lcd(address, 20, 4, chip, chip.Print),
            "stuck" => new StuckDevice(),
            _ => new Ds1307(address, chip, chip.Print, device.At),
        });
    }

    private static void AttachSpi(SimulatedChip chip, SpiBus bus, BusDevice device, (GpioPort Port, int Bit)? pin)
    {
        if (device.Device == "loopback")
        {
            bus.Add(new Loopback(), pin);
            return;
        }

        // the shift register takes every byte; its latch pin going high shows them
        var register = new ShiftRegister(bus);
        bus.Add(register, null);
        var (port, bit) = pin!.Value;
        chip.PinLevelChanged += (changed, changedBit, high) =>
        {
            if (changed == port && changedBit == bit && high)
            {
                register.Latched();
            }
        };
    }

    private static int Number(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? int.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : int.Parse(text, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(\d+):(0x[0-9a-fA-F]+|\d+)=([a-z0-9]+)(?:@([0-9a-zA-Z:-]+))?$")]
    private static partial Regex I2cPattern();

    [GeneratedRegex(@"^(\d+)=(74hc595|loopback)(?:@(P[A-Fa-f][0-7]|GPIO\d{1,2}))?$")]
    private static partial Regex SpiPattern();

    [GeneratedRegex(@"^(P[A-Fa-f][0-7]|GPIO\d{1,2})=(\d+(?:\.\d+)?)(V)?(?:@(\d+(?:\.\d+)?)ms)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AnalogPattern();
}
