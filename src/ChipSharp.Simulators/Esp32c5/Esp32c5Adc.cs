using ChipSharp.Simulators.Devices;

namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// The ESP32-C5's SAR ADC1 in one-shot mode (APB_SARADC at 0x6000_E000; TRM chapter 28, behaviour as ESP-IDF's
/// adc_oneshot_hal_convert drives it) and its temperature sensor. APB_SARADC_ONETIME_SAMPLE chooses the channel (channel
/// n of ADC1 is GPIO n+1: GPIO1 to GPIO6) and the attenuation, and with SARADC1_ONETIME_SAMPLE set starts a conversion when
/// its START bit goes from 0 to 1: after a conversion's time (12 microseconds) INT_RAW's SARADC1_DONE (bit 31) is set and
/// SAR1DATA_STATUS holds the 12-bit result, the voltage on the pin (<see cref="AnalogInputs"/>, from --adc) over the
/// attenuation's full scale (0 dB 1.1 V, 2.5 dB 1.5 V, 6 dB 2.2 V, 12 dB 3.3 V, nominal) times 4096. The converter reads 0
/// until the program has powered it as ESP-IDF does (CTRL's XPD_SAR_FORCE 3: software power-on). The temperature sensor
/// (TSENS_CTRL's TSENS_PU) reads the chip at 25 degrees Celsius, 104 counts (celsius = 0.4386 * count - 20.52).
/// </summary>
internal sealed class Esp32c5Adc(SimulatedChip chip, AnalogInputs inputs)
{
    private const uint Ctrl = 0x00, OneTimeSample = 0x20, Sar1DataStatus = 0x2C, IntRaw = 0x44, IntClr = 0x4C, TsensCtrl = 0x58;
    private const uint Start = 1u << 29, Done = 1u << 31;
    private const long ConversionTicks = 12 * SimulatedChip.TicksPerSecond / 1_000_000;

    private static readonly double[] FullScaleVolts = [1.1, 1.5, 2.2, 3.3];

    private readonly Dictionary<uint, uint> _registers = [];
    private uint _result;
    private bool _done;
    private long _doneAt = long.MaxValue;

    /// <summary>The analog inputs' pins by name, in the order of ADC1's channels.</summary>
    public static IReadOnlyList<string> Pins { get; } = ["GPIO1", "GPIO2", "GPIO3", "GPIO4", "GPIO5", "GPIO6"];

    public uint Read(uint offset)
    {
        Settle();
        switch (offset)
        {
            case IntRaw:
                return _done ? Done : 0;
            case Sar1DataStatus:
                return _result;
            case TsensCtrl:
                var control = _registers.GetValueOrDefault(TsensCtrl);
                return (control & ~0xFFu) | ((control & (1u << 22)) != 0 ? 104u : 0u);
            default:
                return _registers.GetValueOrDefault(offset);
        }
    }

    public void Write(uint offset, uint value)
    {
        Settle();
        switch (offset)
        {
            case IntClr:
                if ((value & Done) != 0)
                {
                    _done = false;
                }

                return;
            case OneTimeSample:
                var rising = (value & Start) != 0 && (_registers.GetValueOrDefault(OneTimeSample) & Start) == 0;
                _registers[offset] = value;
                if (rising && (value & (1u << 31)) != 0)
                {
                    Convert(value);
                }

                return;
            default:
                _registers[offset] = value;
                return;
        }
    }

    private void Settle()
    {
        if (!_done && chip.Ticks >= _doneAt)
        {
            _done = true;
            _doneAt = long.MaxValue;
        }
    }

    private void Convert(uint sample)
    {
        var channel = (int)((sample >> 25) & 7);
        var attenuation = (int)((sample >> 23) & 3);
        var powered = ((_registers.GetValueOrDefault(Ctrl) >> 27) & 3) == 3;
        _result = 0;
        if (powered && channel < Pins.Count)
        {
            var volts = inputs.Volts(Pins[channel]);
            _result = (uint)Math.Clamp(Math.Floor(volts / FullScaleVolts[attenuation] * 4096), 0, 4095);
        }

        _done = false;
        _doneAt = chip.Ticks + ConversionTicks;
    }
}
