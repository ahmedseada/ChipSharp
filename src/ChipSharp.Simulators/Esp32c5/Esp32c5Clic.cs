using ChipSharp.Simulators.RiscV;

namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// The HP CPU's CLIC (TRM v1.1, section 2.8.2): 48 interrupt IDs, of which 3 (the CLINT's software interrupt), 7 (its
/// timer) and 16 to 47 (the interrupt matrix's lines) are there. mcliccfg (MNLBITS: how many of clicintctl's top bits
/// are the level), clicinfo (48 interrupts, CLICINTCTLBITS 3) and, a byte each, clicintip (the pending bit: a level
/// interrupt's follows its line and is read-only, an edge interrupt's is set by the edge and cleared by writing 0, or
/// by hardware when it is taken vectored or through mnxti), clicintie, clicintattr (SHV, TRIG: positive or negative,
/// level or edge; MODE reads 3, machine) and clicintctl (3 bits, the low 5 read 1). The interrupt to take is the
/// pending and enabled one of the highest level, then priority, then ID, when its level is above the CPU's threshold
/// (mintthresh, and the level being served) and interrupts are on.
/// </summary>
internal sealed class Esp32c5Clic : IRiscVInterrupts
{
    public const int Count = 48;

    private readonly byte[] _ie = new byte[Count];
    private readonly byte[] _attr = new byte[Count];
    private readonly byte[] _ctl = new byte[Count];
    private readonly bool[] _line = new bool[Count];
    private readonly bool[] _edge = new bool[Count];
    private int _mnlbits;
    private uint _thresholdRegister;

    public Esp32c5Clic()
    {
        for (var id = 0; id < Count; id++)
        {
            _attr[id] = 0xC0;
            _ctl[id] = 0x1F;
        }
    }

    /// <summary>Called when what the CLIC would let in may have changed (a register written, a line changed).</summary>
    public Action? Changed { get; set; }

    /// <summary>Whether <paramref name="id"/> is one the chip has.</summary>
    public static bool Exists(int id) => id is 3 or 7 or (>= 16 and < Count);

    /// <summary>Whether interrupt <paramref name="id"/> is vectored (clicintattr.SHV).</summary>
    public bool Vectored(int id) => (_attr[id] & 1) != 0;

    /// <summary>Whether <paramref name="id"/> is enabled (clicintie).</summary>
    public bool Enabled(int id) => (_ie[id] & 1) != 0;

    /// <summary>Whether <paramref name="id"/> is pending (clicintip).</summary>
    public bool Pending(int id) => (_attr[id] & 2) != 0 ? _edge[id] : _line[id] != ((_attr[id] & 4) != 0);

    /// <summary>An interrupt's level, from its clicintctl and MNLBITS (the bits below the level read 1).</summary>
    public int Level(int id)
    {
        var bits = Math.Min(_mnlbits, 8);
        return bits == 0 ? 255 : (_ctl[id] & ~(0xFF >> bits) & 0xFF) | (0xFF >> bits);
    }

    private int Priority(int id) => _ctl[id] & (0xFF >> Math.Min(_mnlbits, 8));

    /// <summary>Sets the level of interrupt <paramref name="id"/>'s line: an edge interrupt's edge sets its pending bit.</summary>
    public void SetLine(int id, bool high)
    {
        var old = _line[id];
        if (old == high)
        {
            return;
        }

        _line[id] = high;
        if ((_attr[id] & 2) != 0 && high == ((_attr[id] & 4) == 0))
        {
            _edge[id] = true; // a rising edge (TRIG 01) or a falling one (TRIG 11)
        }

        Changed?.Invoke();
    }

    /// <summary>The interrupt to take now, if one is pending, enabled and above <paramref name="threshold"/>.</summary>
    public (int Id, int Level)? Best(int threshold, bool vectoredToo = true)
    {
        var best = -1;
        var bestLevel = threshold;
        var bestPriority = -1;
        for (var id = 3; id < Count; id++)
        {
            if ((_ie[id] & 1) == 0 || !Exists(id) || !Pending(id) || (!vectoredToo && Vectored(id)))
            {
                continue;
            }

            var level = Level(id);
            var priority = Priority(id);
            if (level > bestLevel || (level == bestLevel && best >= 0 && priority >= bestPriority))
            {
                best = id;
                bestLevel = level;
                bestPriority = priority;
            }
        }

        return best < 0 ? null : (best, bestLevel);
    }

    /// <summary>Whether interrupt <paramref name="id"/>, once pending, would wake a core waiting with <paramref name="threshold"/>.</summary>
    public bool CouldWake(int id, int threshold) => Exists(id) && Enabled(id) && Level(id) > threshold;

    public (int Id, int Level)? Next(int level) => Best(level, vectoredToo: false);

    public void Taken(int id)
    {
        if ((_attr[id] & 2) != 0)
        {
            _edge[id] = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Reads <paramref name="size"/> bytes at <paramref name="offset"/> from the CLIC's base (0x2080_0000).</summary>
    public uint Read(uint offset, int size)
    {
        uint value = 0;
        for (var b = 0; b < size; b++)
        {
            value |= (uint)ReadByte(offset + (uint)b) << (8 * b);
        }

        return value;
    }

    public void Write(uint offset, int size, uint value)
    {
        for (var b = 0; b < size; b++)
        {
            WriteByte(offset + (uint)b, (byte)(value >> (8 * b)));
        }

        Changed?.Invoke();
    }

    private byte ReadByte(uint offset)
    {
        switch (offset)
        {
            case 0:
                return (byte)_mnlbits;
            case >= 4 and < 8: // clicinfo: CLICINTCTLBITS 3 (bits 24:21), NUM_INTERRUPTS 48
                return (byte)(((3u << 21) | Count) >> (int)(8 * (offset - 4)));
            case >= 8 and < 12:
                return (byte)(_thresholdRegister >> (int)(8 * (offset - 8)));
            case >= 0x1000 and < 0x1000 + 4 * Count:
                var id = (int)(offset - 0x1000) / 4;
                if (!Exists(id))
                {
                    return 0;
                }

                return (offset & 3) switch
                {
                    0 => Pending(id) ? (byte)1 : (byte)0,
                    1 => _ie[id],
                    2 => _attr[id],
                    _ => _ctl[id],
                };
            default:
                return 0;
        }
    }

    private void WriteByte(uint offset, byte value)
    {
        switch (offset)
        {
            case 0:
                _mnlbits = Math.Min(value & 0xF, 8); // NMBITS reads 0: machine mode only
                return;
            case >= 8 and < 12:
                var shift = (int)(8 * (offset - 8));
                _thresholdRegister = (_thresholdRegister & ~(0xFFu << shift)) | ((uint)value << shift);
                return;
            case >= 0x1000 and < 0x1000 + 4 * Count:
                var id = (int)(offset - 0x1000) / 4;
                if (!Exists(id))
                {
                    return;
                }

                switch (offset & 3)
                {
                    case 0:
                        if ((_attr[id] & 2) != 0)
                        {
                            _edge[id] = (value & 1) != 0; // a level interrupt's is read-only
                        }

                        break;
                    case 1:
                        _ie[id] = (byte)(value & 1);
                        break;
                    case 2:
                        _attr[id] = (byte)(0xC0 | (value & 7));
                        break;
                    default:
                        _ctl[id] = (byte)((value & 0xE0) | 0x1F);
                        break;
                }

                return;
        }
    }
}

/// <summary>
/// The HP CPU's CLINT (TRM v1.1, section 2.8.3), at 0x2000_0000: MSIP (0x0000, the software interrupt, CLIC ID 3) and the
/// 64-bit timer: MTIMECMP (0x4000, 0x4004; all ones after reset), MTIMELOAD (0x4008, 0x400C), MTIMECTL (0x4010: MTIME_EN,
/// on after reset; MTIME_OVF; MTIME_SAM, which half a read of the other samples) and MTIME (0xBFF8, 0xBFFC). The timer
/// interrupt (CLIC ID 7) is pending while MTIME is at or past MTIMECMP. The TRM does not say what clocks MTIME: here it
/// counts at 16 MHz, as SYSTIMER does; nor how MTIMELOAD is loaded: it only keeps what is written.
/// </summary>
internal sealed class Esp32c5Clint(SimulatedChip chip)
{
    private ulong _base;
    private long _since;
    private bool _enabled = true;
    private bool _overflow;
    private uint _sampling;
    private uint? _sampledHigh;
    private uint? _sampledLow;
    private uint _loadLow;
    private uint _loadHigh;

    public bool SoftwarePending { get; private set; }

    public ulong Compare { get; private set; } = ulong.MaxValue;

    /// <summary>MTIME now.</summary>
    public ulong Time => _enabled ? _base + (ulong)(chip.Ticks - _since) : _base;

    /// <summary>Whether the timer interrupt is pending.</summary>
    public bool TimerPending => Time >= Compare;

    /// <summary>The tick at which the timer interrupt becomes pending, or long.MaxValue.</summary>
    public long NextEvent
    {
        get
        {
            if (!_enabled || TimerPending || Compare == ulong.MaxValue)
            {
                return long.MaxValue;
            }

            var ticks = Compare - Time;
            return ticks > long.MaxValue / 2 ? long.MaxValue : chip.Ticks + (long)ticks;
        }
    }

    public uint Read(uint offset) => offset switch
    {
        0x0000 => SoftwarePending ? 1u : 0,
        0x4000 => (uint)Compare,
        0x4004 => (uint)(Compare >> 32),
        0x4008 => _loadLow,
        0x400C => _loadHigh,
        0x4010 => (_enabled ? 1u : 0) | (_overflow ? 8u : 0) | (_sampling << 4),
        0xBFF8 => ReadHalf(low: true),
        0xBFFC => ReadHalf(low: false),
        _ => 0,
    };

    public void Write(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x0000:
                SoftwarePending = (value & 1) != 0;
                break;
            case 0x4000:
                Compare = (Compare & 0xFFFF_FFFF_0000_0000) | value;
                break;
            case 0x4004:
                Compare = (Compare & 0xFFFF_FFFF) | ((ulong)value << 32);
                break;
            case 0x4008:
                _loadLow = value;
                break;
            case 0x400C:
                _loadHigh = value;
                break;
            case 0x4010:
                var time = Time;
                _enabled = (value & 1) != 0;
                _base = time;
                _since = chip.Ticks;
                _overflow &= (value & 8) != 0; // cleared by writing 0
                _sampling = (value >> 4) & 3;
                break;
        }
    }

    private uint ReadHalf(bool low)
    {
        var time = Time;
        if (low)
        {
            if (_sampledLow is { } sampledLow)
            {
                _sampledLow = null; // the half a read of MTIMEHI sampled
                return sampledLow;
            }

            if ((_sampling & 1) != 0)
            {
                _sampledHigh = (uint)(time >> 32);
            }

            return (uint)time;
        }

        if (_sampledHigh is { } sampledHigh)
        {
            _sampledHigh = null;
            return sampledHigh;
        }

        if ((_sampling & 2) != 0)
        {
            _sampledLow = (uint)time;
        }

        return (uint)(time >> 32);
    }
}
