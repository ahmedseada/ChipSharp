using ChipSharp.Simulators.Devices;

namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// The ESP32-C5's general-purpose SPI controller GP-SPI2 (TRM chapter 29; registers by the SVD, behaviour as ESP-IDF's
/// spi_ll.h drives it) as a controller at 0x6008_1000, driving an <see cref="SpiBus"/> and its devices: a "user"
/// transaction (CMD's USR) of MS_DLEN + 1 bits (whole bytes) in full duplex, from the data registers W0-W15 (byte 0 the
/// lowest byte of W0), most significant bit first, the answers coming back into them, as SPI_DMA_INT_RAW's TRANS_DONE
/// (bit 12) says once the bits' time has passed (the clock from the crystal over SPI_CLOCK's CLKDIV_PRE and CLKCNT_N, or
/// the crystal itself with CLK_EQU_SYSCLK, from PCR_SPI2_CLKM_CONF). CMD's UPDATE takes the settings over and clears at
/// once. The mode is SPI_MISC's CK_IDLE_EDGE and SPI_USER's CK_OUT_EDGE. A MISO that is not connected to a pin (the input
/// signal FSPIQ without a pin selected) reads 0.
/// </summary>
internal sealed class Esp32c5Spi(SimulatedChip chip, SpiBus bus, Func<uint> clockConfiguration, Func<bool> misoConnected)
{
    private const uint Cmd = 0x00, Clock = 0x0C, User = 0x10, MsDlen = 0x1C, Misc = 0x20, DmaIntClr = 0x38, DmaIntRaw = 0x3C,
        W0 = 0x98;
    private const uint Update = 1u << 23, Usr = 1u << 24, TransDone = 1u << 12;

    private readonly Dictionary<uint, uint> _registers = [];
    private readonly uint[] _data = new uint[16];
    private uint _raw;
    private uint _pending;
    private long _doneAt;

    /// <summary>The bus the controller drives: SPI0.</summary>
    public SpiBus Bus { get; } = bus;

    public uint Read(uint offset)
    {
        Settle();
        switch (offset)
        {
            case Cmd:
                return chip.Ticks < _doneAt ? Usr : 0;
            case DmaIntRaw:
                return _raw;
            case >= W0 and < W0 + 4 * 16:
                return _data[(offset - W0) / 4];
            default:
                return _registers.GetValueOrDefault(offset);
        }
    }

    public void Write(uint offset, uint value)
    {
        Settle();
        switch (offset)
        {
            case Cmd:
                if ((value & Usr) != 0)
                {
                    Start();
                }

                return;
            case DmaIntClr:
                _raw &= ~value;
                return;
            case >= W0 and < W0 + 4 * 16:
                _data[(offset - W0) / 4] = value;
                return;
            default:
                _registers[offset] = value;
                return;
        }
    }

    private void Settle()
    {
        if (_pending != 0 && chip.Ticks >= _doneAt)
        {
            _raw |= _pending;
            _pending = 0;
        }
    }

    /// <summary>The SPI clock in hertz.</summary>
    private double Hertz
    {
        get
        {
            var configuration = clockConfiguration();
            var source = ((configuration >> 20) & 3) switch
            {
                0 => Esp32c5Map.XtalHz,
                1 => Esp32c5Map.Pll160Hz,
                2 => Esp32c5Map.RcFastHz,
                _ => 120_000_000L,
            };
            var clock = _registers.GetValueOrDefault(Clock);
            double hertz = (double)source / (((configuration >> 12) & 0xFF) + 1);
            return (clock & 0x8000_0000) != 0 ? hertz : hertz / (((clock >> 18) & 0xF) + 1) / (((clock >> 12) & 0x3F) + 1);
        }
    }

    private void Start()
    {
        var bytes = (int)(((_registers.GetValueOrDefault(MsDlen) & 0x3FFFF) + 1) / 8);
        var idleHigh = (_registers.GetValueOrDefault(Misc) & (1u << 29)) != 0;
        var outEdge = (_registers.GetValueOrDefault(User) & (1u << 9)) != 0;
        var mode = idleHigh ? (outEdge ? 2 : 3) : (outEdge ? 1 : 0);
        var hertz = Hertz;
        var connected = misoConnected();
        for (var i = 0; i < Math.Min(bytes, 64); i++)
        {
            var word = i / 4;
            var shift = 8 * (i % 4);
            var answer = Bus.Transfer((byte)(_data[word] >> shift), mode, (int)Math.Round(hertz));
            _data[word] = (_data[word] & ~(0xFFu << shift)) | ((connected ? (uint)answer : 0u) << shift);
        }

        _doneAt = chip.Ticks + (long)(bytes * 8 * SimulatedChip.TicksPerSecond / hertz);
        _pending = TransDone;
    }
}
