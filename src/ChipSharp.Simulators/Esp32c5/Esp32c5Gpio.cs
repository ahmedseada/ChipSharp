namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// The HP GPIO matrix and IO MUX (TRM v1.1, chapter 8; registers by the SVD) for plain I/O on GPIO0-GPIO28. A pin is a
/// GPIO output when its IO MUX function (IO_MUX_GPIOn's MCU_SEL) is 1, GPIO, its GPIO_FUNCn_OUT_SEL_CFG selects 256
/// (simple GPIO output: GPIO_OUT's bit, inverted by OUT_INV_SEL) and GPIO_ENABLE's bit is set; GPIO_PINn's PAD_DRIVER
/// makes it open-drain. GPIO_OUT, GPIO_ENABLE (and their W1TS, W1TC), GPIO_IN (the pins' levels where IO MUX's FUN_IE is
/// on, else 0); the pad's pulls, IO MUX's FUN_WPU and FUN_WPD: an input nothing drives reads high with its pull-up,
/// low with its pull-down (or with neither), and an --input's level whatever its pulls. GPIO_PINn's INT_TYPE (1 rising, 2 falling, 3
/// both edges, 4 low level, 5 high level) sets the pin's bit of GPIO_STATUS (and W1TS, W1TC), and INT_ENA's bit 13 lets
/// it into GPIO_PROCPU_INT and the interrupt source GPIO (31). The input selections (FUNCn_IN_SEL_CFG) and the other
/// registers only keep what is written. After reset (as the ROM leaves them) every pin is an input with FUN_IE on, the
/// pull-ups of GPIO4, 12, 15-22, 27 and 28 on (the datasheet's Table 2-1), GPIO11 and 12 on UART0's function and GPIO15-22
/// on the flash's (MCU_SEL 0).
/// </summary>
internal sealed class Esp32c5Gpio
{
    private const uint ValidPins = (1u << Esp32c5Map.GpioCount) - 1;

    private readonly GpioPort[] _ports;
    private readonly uint[] _pin = new uint[Esp32c5Map.GpioCount];
    private readonly uint[] _outSel = new uint[Esp32c5Map.GpioCount];
    private readonly uint[] _ioMux = new uint[Esp32c5Map.GpioCount];
    private readonly Dictionary<uint, uint> _other = [];
    private uint _out;
    private uint _enable;
    private uint _status;
    private uint _levels;

    public Esp32c5Gpio(SimulatedChip chip)
    {
        _ports =
        [
            new GpioPort(chip, 'A', 0xFF, 0), new GpioPort(chip, 'B', 0xFF, 8), new GpioPort(chip, 'C', 0xFF, 16),
            new GpioPort(chip, 'D', 0x1F, 24),
        ];
        for (var p = 0; p < _ports.Length; p++)
        {
            var first = p * 8;
            _ports[p].LevelChanged = (bit, high) => LevelChanged(first + bit, high);
        }

        for (var n = 0; n < Esp32c5Map.GpioCount; n++)
        {
            _outSel[n] = 0x100;
            var function = n is 11 or 12 or (>= 15 and <= 22) ? 0u : 1u;
            var pullUp = n is 4 or 12 or (>= 15 and <= 22) or 27 or 28;
            _ioMux[n] = (function << 12) | (2u << 10) | (1u << 9) | (pullUp ? 1u << 8 : 0);
        }

        Apply();
    }

    public IReadOnlyList<GpioPort> Ports => _ports;

    /// <summary>Called when GPIO_STATUS or what lets it in may have changed.</summary>
    public Action? Changed { get; set; }

    /// <summary>The pins whose interrupt reaches the CPU: INT_ENA's bit 13.</summary>
    private uint CpuEnabled
    {
        get
        {
            uint mask = 0;
            for (var n = 0; n < Esp32c5Map.GpioCount; n++)
            {
                mask |= ((_pin[n] >> 13) & 1) << n;
            }

            return mask;
        }
    }

    /// <summary>GPIO_IN: the levels of the pins whose input is on.</summary>
    public uint In
    {
        get
        {
            uint enabled = 0;
            for (var n = 0; n < Esp32c5Map.GpioCount; n++)
            {
                enabled |= ((_ioMux[n] >> 9) & 1) << n;
            }

            return _levels & enabled;
        }
    }

    /// <summary>The interrupt source GPIO: a pin's status bit with its CPU interrupt on.</summary>
    public bool InterruptPending => (_status & CpuEnabled) != 0;

    /// <summary>Whether a pin's interrupt is on, so that an input to come could raise it.</summary>
    public bool InterruptsOn
    {
        get
        {
            for (var n = 0; n < Esp32c5Map.GpioCount; n++)
            {
                if ((_pin[n] & (1u << 13)) != 0 && ((_pin[n] >> 7) & 7) is >= 1 and <= 5)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public uint ReadGpio(uint offset)
    {
        switch (offset)
        {
            case 0x04:
                return _out;
            case 0x34:
                return _enable;
            case 0x64:
                return In;
            case 0x74:
                return _status;
            case 0xA4:
                return _status & CpuEnabled;
            case 0xC4:
                return _status;
            case >= 0xD4 and < 0xD4 + 4 * Esp32c5Map.GpioCount:
                return _pin[(offset - 0xD4) / 4];
            case >= 0xAD4 and < 0xAD4 + 4 * Esp32c5Map.GpioCount:
                return _outSel[(offset - 0xAD4) / 4];
            case >= 0x2D4 and < 0x2D4 + 4 * 116:
                return _other.GetValueOrDefault(offset, 0x60u);
            case 0xDF8:
                return _other.GetValueOrDefault(offset, 1u);
            case 0xDFC:
                return 0x0241_0300;
            default:
                return _other.GetValueOrDefault(offset);
        }
    }

    public void WriteGpio(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x04:
                _out = value & ValidPins;
                break;
            case 0x08:
                _out |= value & ValidPins;
                break;
            case 0x0C:
                _out &= ~value;
                break;
            case 0x34:
                _enable = value & ValidPins;
                break;
            case 0x38:
                _enable |= value & ValidPins;
                break;
            case 0x3C:
                _enable &= ~value;
                break;
            case 0x74:
                _status = value & ValidPins;
                break;
            case 0x78:
                _status |= value & ValidPins;
                break;
            case 0x7C:
                _status &= ~value;
                break;
            case >= 0xD4 and < 0xD4 + 4 * Esp32c5Map.GpioCount:
                _pin[(offset - 0xD4) / 4] = value & 0x3_E79F;
                break;
            case >= 0xAD4 and < 0xAD4 + 4 * Esp32c5Map.GpioCount:
                _outSel[(offset - 0xAD4) / 4] = value & 0xFFF;
                break;
            default:
                _other[offset] = value;
                return;
        }

        Apply();
        SetLevelStatus();
        Changed?.Invoke();
    }

    public uint ReadIoMux(uint offset) => offset switch
    {
        < 4 * Esp32c5Map.GpioCount => _ioMux[offset / 4],
        0x1FC => 0x0231_1270,
        _ => _other.GetValueOrDefault(0x1_0000 + offset),
    };

    public void WriteIoMux(uint offset, uint value)
    {
        if (offset < 4 * Esp32c5Map.GpioCount)
        {
            _ioMux[offset / 4] = value & 0x3_FFFF;
            Apply();
            SetLevelStatus();
            Changed?.Invoke();
        }
        else
        {
            _other[0x1_0000 + offset] = value;
        }
    }

    /// <summary>Gives the ports what the registers say each pin does; they print what changed.</summary>
    private void Apply()
    {
        for (var p = 0; p < _ports.Length; p++)
        {
            byte output = 0, direction = 0, pushPull = 0, pullUp = 0, pullDown = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                var n = p * 8 + bit;
                if (n >= Esp32c5Map.GpioCount)
                {
                    break;
                }

                var mask = (byte)(1 << bit);
                var gpio = ((_ioMux[n] >> 12) & 7) == 1 && (_outSel[n] & 0x1FF) == 256;
                if (gpio && (_enable & (1u << n)) != 0)
                {
                    direction |= mask;
                }

                if (((_out >> n) & 1) != ((_outSel[n] >> 9) & 1))
                {
                    output |= mask;
                }

                if ((_pin[n] & 4) == 0)
                {
                    pushPull |= mask;
                }

                if ((_ioMux[n] & (1u << 8)) != 0)
                {
                    pullUp |= mask;
                }

                if ((_ioMux[n] & (1u << 7)) != 0)
                {
                    pullDown |= mask;
                }
            }

            var port = _ports[p];
            port.Output = output;
            port.Direction = direction;
            port.PushPull = pushPull;
            port.PullUp = pullUp;
            port.PullDown = pullDown;
            port.Update();
        }

        _levels = 0;
        for (var p = 0; p < _ports.Length; p++)
        {
            _levels |= (uint)_ports[p].Levels << (8 * p);
        }
    }

    private void LevelChanged(int n, bool high)
    {
        _levels = high ? _levels | (1u << n) : _levels & ~(1u << n);
        if ((_ioMux[n] & (1u << 9)) != 0)
        {
            var type = (_pin[n] >> 7) & 7;
            if ((type == 1 && high) || (type == 2 && !high) || type == 3)
            {
                _status |= 1u << n;
            }
        }

        SetLevelStatus();
        Changed?.Invoke();
    }

    /// <summary>The level-triggered pins set their status bit while their level holds.</summary>
    private void SetLevelStatus()
    {
        var levels = In;
        for (var n = 0; n < Esp32c5Map.GpioCount; n++)
        {
            var type = (_pin[n] >> 7) & 7;
            if ((type == 4 && (levels & (1u << n)) == 0) || (type == 5 && (levels & (1u << n)) != 0))
            {
                _status |= 1u << n;
            }
        }
    }
}
