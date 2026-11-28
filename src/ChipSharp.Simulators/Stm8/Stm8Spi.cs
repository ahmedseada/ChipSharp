using ChipSharp.Simulators.Devices;

namespace ChipSharp.Simulators.Stm8;

/// <summary>
/// The STM8S's SPI controller (RM0016 chapter 20) as the bus's controller, at the STM8S103's addresses, driving an
/// <see cref="SpiBus"/> and its devices: CR1 (SPE, MSTR, BR: fMASTER / 2^(BR + 1), CPOL and CPHA, the mode; LSBFIRST),
/// CR2, SR (TXE, RXNE, OVR, BSY) and DR. A byte written to DR goes to the shift register when it is empty (TXE again at
/// once) and out in eight bits of SCK; at its end the byte the devices sent back is in DR (RXNE; OVR when the one before
/// has not been read, and this one is lost), and the next waiting one goes out.
/// </summary>
internal sealed class Stm8Spi(SpiBus bus, Func<long> ticksPerMasterClock)
{
    private const int Cr1 = 0, Cr2 = 1, Sr = 3, Dr = 4;
    private const byte Spe = 0x40, Mstr = 0x04, LsbFirst = 0x80;
    private const byte Bsy = 0x80, Ovr = 0x40, Txe = 0x02, Rxne = 0x01;

    private readonly byte[] _registers = new byte[8];
    private byte _sr = Txe;
    private byte _received;
    private byte _shifting;
    private byte? _pending;
    private bool _busy;
    private long _ticksLeft;

    /// <summary>The bus the controller drives: SPI0.</summary>
    public SpiBus Bus { get; } = bus;

    private int Mode => ((_registers[Cr1] & 0x02) != 0 ? 2 : 0) + ((_registers[Cr1] & 0x01) != 0 ? 1 : 0);

    /// <summary>fMASTER clocks a bit takes.</summary>
    private long BitClocks => 2L << ((_registers[Cr1] >> 3) & 7);

    public byte Read(int offset)
    {
        switch (offset)
        {
            case Sr:
                return _sr;
            case Dr:
                _sr &= unchecked((byte)~Rxne);
                return _received;
            default:
                return offset < _registers.Length ? _registers[offset] : (byte)0;
        }
    }

    public void Write(int offset, byte value)
    {
        switch (offset)
        {
            case Sr:
                _sr &= (byte)(value | ~Ovr); // OVR, CRCERR: rc_w0
                return;
            case Dr:
                if ((_registers[Cr1] & (Spe | Mstr)) != (Spe | Mstr))
                {
                    return;
                }

                if (_busy)
                {
                    _pending = value;
                    _sr &= unchecked((byte)~Txe);
                }
                else
                {
                    Shift(value);
                }

                return;
        }

        if (offset < _registers.Length)
        {
            _registers[offset] = value;
            if (offset == Cr1 && (value & Spe) == 0)
            {
                _busy = false;
                _pending = null;
                _sr = Txe;
            }
        }
    }

    /// <summary>Lets <paramref name="ticks"/> pass for the byte going out.</summary>
    public void Advance(long ticks)
    {
        if (!_busy)
        {
            return;
        }

        _ticksLeft -= ticks;
        while (_busy && _ticksLeft <= 0)
        {
            var lsbFirst = (_registers[Cr1] & LsbFirst) != 0;
            var hertz = (int)Math.Round((double)SimulatedChip.TicksPerSecond / ticksPerMasterClock() / BitClocks);
            var answer = Bus.Transfer(lsbFirst ? Reverse(_shifting) : _shifting, Mode, hertz);
            if ((_sr & Rxne) != 0)
            {
                _sr |= Ovr;
            }
            else
            {
                _received = lsbFirst ? Reverse(answer) : answer;
                _sr |= Rxne;
            }

            if (_pending is { } next)
            {
                _pending = null;
                var left = _ticksLeft;
                Shift(next);
                _ticksLeft += left;
            }
            else
            {
                _busy = false;
                _sr &= unchecked((byte)~Bsy);
            }
        }
    }

    private void Shift(byte value)
    {
        _shifting = value;
        _busy = true;
        _sr = (byte)(_sr | Txe | Bsy);
        _ticksLeft = 8 * BitClocks * ticksPerMasterClock();
    }

    private static byte Reverse(byte value)
    {
        var reversed = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            reversed |= ((value >> bit) & 1) << (7 - bit);
        }

        return (byte)reversed;
    }
}

/// <summary>
/// ADC1 of the STM8S (RM0016 chapter 24), single conversions, at the STM8S103's addresses: CSR (CH, the channel; EOC and
/// its interrupt EOCIE, vector 22), CR1 (ADON: the first write powers it up, the next starts a conversion; SPSEL, the ADC
/// clock's divider of fMASTER; CONT), CR2 (ALIGN), DRH and DRL, TDRH and TDRL (the Schmitt triggers, kept). A conversion
/// takes 14 ADC clocks and reads the voltage of its channel's pin then (<see cref="AnalogInputs"/>: AIN2 on PC4, AIN3
/// PD2, AIN4 PD3, AIN5 PD5, AIN6 PD6), in 10 bits against VDD, right or left aligned.
/// </summary>
internal sealed class Stm8Adc(AnalogInputs inputs, Func<long> ticksPerMasterClock)
{
    private const int Csr = 0, Cr1 = 1, Cr2 = 2, Drh = 4, Drl = 5;
    private const byte Eoc = 0x80, Eocie = 0x20, Adon = 0x01, Cont = 0x02, Align = 0x08;

    private static readonly int[] Dividers = [2, 3, 4, 6, 8, 10, 12, 18];

    private readonly byte[] _registers = new byte[0x10];
    private bool _converting;
    private long _ticksLeft;

    /// <summary>The pins of the channels, by number: AIN2-AIN6 on this package.</summary>
    public static IReadOnlyDictionary<int, string> ChannelPins { get; } = new Dictionary<int, string>
    {
        [2] = "PC4", [3] = "PD2", [4] = "PD3", [5] = "PD5", [6] = "PD6",
    };

    /// <summary>Whether the end of conversion's interrupt is requested and enabled.</summary>
    public bool InterruptPending => (_registers[Csr] & (Eoc | Eocie)) == (Eoc | Eocie);

    public byte Read(int offset) => offset < _registers.Length ? _registers[offset] : (byte)0;

    public void Write(int offset, byte value)
    {
        switch (offset)
        {
            case Csr:
                _registers[Csr] = (byte)((value & ~Eoc) | (value & _registers[Csr] & Eoc)); // EOC: rc_w0
                return;
            case Cr1:
                var wasOn = (_registers[Cr1] & Adon) != 0;
                _registers[Cr1] = value;
                if ((value & Adon) == 0)
                {
                    _converting = false;
                }
                else if (wasOn)
                {
                    Convert();
                }

                return;
            case Drh or Drl:
                return; // read-only
        }

        if (offset < _registers.Length)
        {
            _registers[offset] = value;
        }
    }

    /// <summary>Lets <paramref name="ticks"/> pass for the conversion going on.</summary>
    public void Advance(long ticks)
    {
        if (!_converting)
        {
            return;
        }

        _ticksLeft -= ticks;
        if (_ticksLeft > 0)
        {
            return;
        }

        _converting = false;
        var reading = ChannelPins.TryGetValue(_registers[Csr] & 0x0F, out var pin) ? inputs.Reading(pin) : 0;
        if ((_registers[Cr2] & Align) != 0)
        {
            _registers[Drh] = (byte)(reading >> 8);
            _registers[Drl] = (byte)reading;
        }
        else
        {
            _registers[Drh] = (byte)(reading >> 2);
            _registers[Drl] = (byte)((reading & 3) << 6);
        }

        _registers[Csr] |= Eoc;
        if ((_registers[Cr1] & Cont) != 0)
        {
            Convert();
        }
    }

    private void Convert()
    {
        _converting = true;
        _ticksLeft = 14L * Dividers[(_registers[Cr1] >> 4) & 7] * ticksPerMasterClock();
    }
}
