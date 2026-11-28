using System.Globalization;

namespace ChipSharp.Simulators.Ch32v003;

/// <summary>
/// The CH32V003's GPIO ports A (PA1, PA2), C (PC0-PC7) and D (PD0-PD7), AFIO and EXTI (CH32V003 RM V1.6, chapters 7
/// and 6.4; the F4P6's 18 I/O pins, the datasheet's Table 2-1).
/// <para>
/// GPIOx_CFGLR has 4 bits a pin, MODE (bits 1:0) and CNF (3:2): MODE 00 an input, CNF 00 analog (INDR reads 0), 01
/// floating, 10 with a pull-up or a pull-down, as the pin's OUTDR bit says (1 up, 0 down); MODE 01-11 an output (10, 2
/// and 50 MHz alike), CNF 00 push-pull, 01 open-drain (released at 1), 10 and 11 the same driven by a peripheral (its
/// alternate function): USART1's TX, idle high while the USART sends, is the one modeled; another alternate-function
/// pin is left floating. INDR (the levels), OUTDR, BSHR (bits 0-7 set, 16-23 clear; set wins), BCR (clear) and LCKR (the
/// key's sequence 1, 0, 1 with LCKK locks the LCK pins' CFGLR bits until reset). After reset every pin is a floating input
/// (0x4444_4444). PD7 is NRST while the option bytes' RST_MODE is not 11, and PD1 SWIO while AFIO_PCFR1.SWCFG is not
/// 100: a program that makes either an output stops the run, as the pin would not follow it.
/// </para>
/// <para>
/// AFIO_PCFR1 (0x04) keeps the remaps (USART1_RM, bits 21 and 2, gives USART1's pins; SWCFG, bits 26:24); AFIO_EXTICR
/// (0x08) maps EXTI line n to PAn (00), PCn (10) or PDn (11). EXTI: INTENR, EVENR, RTENR, FTENR, SWIEVR (sets INTFR's
/// bits), INTFR (write 1 to clear); a rising (falling) edge on the pin a line maps sets its flag with RTENR (FTENR); the
/// flags of lines 0-7 with INTENR are the interrupt EXTI7_0 (20).
/// </para>
/// </summary>
internal sealed class Ch32v003Gpio
{
    private static readonly char[] Letters = ['A', 'C', 'D'];

    private readonly SimulatedChip _chip;
    private readonly GpioPort[] _ports;
    private readonly uint[] _cfglr = new uint[3];
    private readonly uint[] _outdr = new uint[3];
    private readonly uint[] _locked = new uint[3];
    private readonly int[] _lockStep = new int[3];
    private uint _pcfr1;
    private uint _exticr;
    private uint _intenr, _evenr, _rtenr, _ftenr, _swievr, _intfr;

    public Ch32v003Gpio(SimulatedChip chip, Func<bool> pd7IsReset, Func<(int Port, int Bit, bool Drives)> usartTx)
    {
        _chip = chip;
        PinIsReset = pd7IsReset;
        UsartTx = usartTx;
        _ports = [new GpioPort(chip, 'A', 0x06), new GpioPort(chip, 'C', 0xFF), new GpioPort(chip, 'D', 0xFF)];
        for (var p = 0; p < 3; p++)
        {
            var port = p;
            _ports[p].LevelChanged = (bit, high) => LevelChanged(port, bit, high);
            Reset(p);
        }
    }

    public IReadOnlyList<GpioPort> Ports => _ports;

    /// <summary>Called when an EXTI flag or what lets it in may have changed.</summary>
    public Action? Changed { get; set; }

    /// <summary>Called with why when the program uses a pin as the chip would not let it.</summary>
    public Action<string>? Fault { get; set; }

    /// <summary>Whether PD7 is NRST (the option bytes' RST_MODE).</summary>
    private Func<bool> PinIsReset { get; }

    /// <summary>USART1's TX pin (port 0-2, bit) and whether the USART drives it (UE and TE).</summary>
    private Func<(int Port, int Bit, bool Drives)> UsartTx { get; }

    /// <summary>The interrupt EXTI7_0: a flag of lines 0-7 with its INTENR bit.</summary>
    public bool InterruptPending => (_intfr & _intenr & 0xFF) != 0;

    /// <summary>Whether an input to come could raise EXTI7_0: a line of 0-7 enabled with an edge.</summary>
    public bool InterruptsOn => (_intenr & (_rtenr | _ftenr) & 0xFF) != 0;

    /// <summary>AFIO_PCFR1's USART1_RM (bits 21 and 2): 0 default (TX PD5, RX PD6), 1 (PD0, PD1), 2 (PD6, PD5), 3 (PC0, PC1).</summary>
    public int UsartRemap => (int)(((_pcfr1 >> 20) & 2) | ((_pcfr1 >> 2) & 1));

    /// <summary>Whether pin <paramref name="bit"/> of port <paramref name="port"/> (0 A, 1 C, 2 D) is an alternate-function output.</summary>
    public bool IsAlternateOutput(int port, int bit) => Mode(port, bit) is var (mode, cnf) && mode != 0 && cnf >= 2;

    /// <summary>Whether pin <paramref name="bit"/> of port <paramref name="port"/> is a digital input (not analog, not an output).</summary>
    public bool IsDigitalInput(int port, int bit) => Mode(port, bit) is var (mode, cnf) && mode == 0 && cnf != 0;

    private (uint Mode, uint Cnf) Mode(int port, int bit)
    {
        var field = (_cfglr[port] >> (4 * bit)) & 0xF;
        return (field & 3, field >> 2);
    }

    /// <summary>The port's index (0 A, 1 C, 2 D) and name, from its base address.</summary>
    public static int PortIndex(uint address) => address switch
    {
        Ch32v003Map.GpioA => 0,
        Ch32v003Map.GpioC => 1,
        _ => 2,
    };

    /// <summary>A port's registers to their reset values (RCC_APB2PRSTR's IOPxRST, or the chip's reset).</summary>
    public void Reset(int port)
    {
        _cfglr[port] = 0x4444_4444;
        _outdr[port] = 0;
        _locked[port] = 0;
        _lockStep[port] = 0;
        Apply(port);
    }

    /// <summary>AFIO's registers to their reset values.</summary>
    public void ResetAfio()
    {
        _pcfr1 = 0;
        _exticr = 0;
    }

    public uint ReadPort(int port, uint offset)
    {
        switch (offset)
        {
            case 0x00:
                return _cfglr[port];
            case 0x08:
                uint levels = _ports[port].Levels;
                for (var bit = 0; bit < 8; bit++)
                {
                    if (Mode(port, bit) == (0, 0))
                    {
                        levels &= ~(1u << bit); // an analog input reads 0
                    }
                }

                return levels & _ports[port].Pins;
            case 0x0C:
                return _outdr[port];
            case 0x18:
                return _locked[port] | (_lockStep[port] == 3 ? 0x100u : 0);
            default:
                return 0;
        }
    }

    public void WritePort(int port, uint offset, uint value)
    {
        switch (offset)
        {
            case 0x00:
                _cfglr[port] = (_cfglr[port] & Mask(_locked[port])) | (value & ~Mask(_locked[port]));
                CheckPins(port);
                break;
            case 0x0C:
                _outdr[port] = value & 0xFF;
                break;
            case 0x10:
                _outdr[port] = ((_outdr[port] & ~(value >> 16)) | value) & 0xFF;
                break;
            case 0x14:
                _outdr[port] &= ~value & 0xFF;
                break;
            case 0x18:
                Lock(port, value);
                return;
            default:
                return;
        }

        Apply(port);
    }

    /// <summary>LCKR's sequence: LCKK written 1, 0, 1 with the same LCK bits locks them (until reset).</summary>
    private void Lock(int port, uint value)
    {
        if (_lockStep[port] == 3)
        {
            return;
        }

        var key = (value & 0x100) != 0;
        _lockStep[port] = (_lockStep[port], key) switch
        {
            (0, true) => 1,
            (1, false) => 2,
            (2, true) => 3,
            _ => key ? 1 : 0,
        };
        _locked[port] = value & 0xFF;
    }

    /// <summary>The CFGLR bits of the locked pins.</summary>
    private uint Mask(uint locked)
    {
        if (locked == 0)
        {
            return 0;
        }

        uint mask = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            if ((locked & (1u << bit)) != 0)
            {
                mask |= 0xFu << (4 * bit);
            }
        }

        return mask;
    }

    /// <summary>PD7 (NRST) and PD1 (SWIO) as outputs: the run stops, saying why.</summary>
    private void CheckPins(int port)
    {
        if (port != 2)
        {
            return;
        }

        if (PinIsReset() && Mode(2, 7).Mode != 0)
        {
            Fault?.Invoke("the program made PD7 an output, but PD7 is NRST, the reset pin, while the option bytes' RST_MODE " +
                "is not 11 (WCH ships it as 10): the pin does not follow GPIOD");
        }

        if (((_pcfr1 >> 24) & 7) != 4 && Mode(2, 1).Mode != 0)
        {
            Fault?.Invoke("the program made PD1 an output, but PD1 is SWIO, the debug interface's pin, while AFIO_PCFR1's " +
                "SWCFG is not 100 (as after reset): the pin does not follow GPIOD");
        }
    }

    public uint ReadAfio(uint offset) => offset switch
    {
        0x04 => _pcfr1,
        0x08 => _exticr,
        _ => 0,
    };

    public void WriteAfio(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x04:
                _pcfr1 = value & 0x07E6_83C7;
                CheckPins(2);
                ApplyAll();
                break;
            case 0x08:
                _exticr = value & 0xFFFF;
                break;
        }
    }

    public uint ReadExti(uint offset) => offset switch
    {
        0x00 => _intenr,
        0x04 => _evenr,
        0x08 => _rtenr,
        0x0C => _ftenr,
        0x10 => _swievr,
        0x14 => _intfr,
        _ => 0,
    };

    public void WriteExti(uint offset, uint value)
    {
        value &= 0x3FF;
        switch (offset)
        {
            case 0x00:
                _intenr = value;
                break;
            case 0x04:
                _evenr = value;
                break;
            case 0x08:
                _rtenr = value;
                break;
            case 0x0C:
                _ftenr = value;
                break;
            case 0x10:
                _intfr |= value & ~_swievr; // a software trigger on a line sets its flag
                _swievr = value;
                break;
            case 0x14:
                _intfr &= ~value;
                _swievr &= ~value;
                break;
        }

        Changed?.Invoke();
    }

    /// <summary>USART1's TX went on or off: the pin it drives follows.</summary>
    public void ApplyAll()
    {
        for (var p = 0; p < 3; p++)
        {
            Apply(p);
        }
    }

    /// <summary>Gives the port what the registers say each pin does; it prints what changed.</summary>
    private void Apply(int port)
    {
        byte output = 0, direction = 0, pushPull = 0, pullUp = 0, pullDown = 0;
        var tx = UsartTx();
        for (var bit = 0; bit < 8; bit++)
        {
            var mask = (byte)(1 << bit);
            var (mode, cnf) = Mode(port, bit);
            var high = (_outdr[port] & mask) != 0;
            if (mode != 0 && cnf < 2)
            {
                direction |= mask;
                output |= high ? mask : (byte)0;
                pushPull |= cnf == 0 ? mask : (byte)0;
            }
            else if (mode != 0 && tx.Port == port && tx.Bit == bit && tx.Drives)
            {
                direction |= mask; // USART1's TX, idle high
                output |= mask;
                pushPull |= cnf == 2 ? mask : (byte)0;
            }
            else if (mode == 0 && cnf == 2)
            {
                pullUp |= high ? mask : (byte)0;
                pullDown |= high ? (byte)0 : mask;
            }
        }

        var gpio = _ports[port];
        gpio.Output = output;
        gpio.Direction = direction;
        gpio.PushPull = pushPull;
        gpio.PullUp = pullUp;
        gpio.PullDown = pullDown;
        gpio.Update();
    }

    private void LevelChanged(int port, int bit, bool high)
    {
        var line = 1u << bit;
        var mapped = ((_exticr >> (2 * bit)) & 3) switch { 0 => 0, 2 => 1, 3 => 2, _ => -1 };
        if (mapped == port && Mode(port, bit) != (0, 0) && ((high ? _rtenr : _ftenr) & line) != 0)
        {
            _intfr |= line;
            Changed?.Invoke();
        }
    }

    /// <summary>A pin's name: "PD5".</summary>
    public static string PinName(int port, int bit) =>
        string.Create(CultureInfo.InvariantCulture, $"P{Letters[port]}{bit}");
}
