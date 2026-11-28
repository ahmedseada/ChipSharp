using ChipSharp.Simulators.RiscV;

namespace ChipSharp.Simulators.Ch32v003;

/// <summary>
/// The QingKe V2's PFIC (Programmable Fast Interrupt Controller; QingKeV2 Processor Manual V1.1, chapter 3; CH32V003
/// RM V1.6, 6.5.2), at 0xE000_E000: PFIC_ISRx (the enables; NMI and EXC always on), PFIC_IPRx (pending), PFIC_ITHRESDR,
/// PFIC_CFGR (KEY3 0xBEEF with SYSRESET: a system reset), PFIC_GISR (NESTSTA 1 or 3, GACTSTA, GPENDSTA), PFIC_VTFIDR and
/// PFIC_VTFADDRR0-1 (the two VTF channels), PFIC_IENRx and PFIC_IRERx (enable, disable), PFIC_IPSRx and PFIC_IPRRx (set,
/// clear pending), PFIC_IACTRx (active), PFIC_IPRIORx (a byte each: bits 7:6, bit 7 the preemption bit when nesting is
/// on), PFIC_SCTLR (SLEEPONEXIT, SLEEPDEEP, WFITOWFE, SEVONPEND kept; SYSRESET).
/// <para>
/// An interrupt is pending while its peripheral asserts it (<see cref="SetLine"/>: SysTick's CNTIF with STIE, EXTI's
/// flags, USART1's) or its pending bit was set (IPSR; cleared by IPRR or when the interrupt is taken). <see cref="Best"/>
/// picks the one to take: enabled, pending, not active, below the threshold (ITHRESDR, when not 0: a priority value at or
/// above it waits); with none active any of them, with one active (and INTSYSCR's nesting on) only one of a higher
/// preemption priority (bit 7 0 against 1), with two none; the lowest priority value first, then the lowest number.
/// </para>
/// </summary>
internal sealed class Ch32v003Pfic(Func<bool> nesting) : IQingKeInterrupts
{
    private const uint Key3 = 0xBEEF_0000;

    private readonly bool[] _enabled = new bool[Ch32v003Map.InterruptCount];
    private readonly bool[] _pendingSet = new bool[Ch32v003Map.InterruptCount];
    private readonly bool[] _line = new bool[Ch32v003Map.InterruptCount];
    private readonly byte[] _priority = new byte[256];
    private readonly List<int> _active = [];
    private uint _threshold;
    private uint _vtfIds;
    private readonly uint[] _vtfAddress = new uint[2];
    private uint _sctlr;

    /// <summary>Called when what is pending, enabled or active changed.</summary>
    public Action? Changed { get; set; }

    /// <summary>Called after an mret ended the active trap.</summary>
    public Action? Returned { get; set; }

    /// <summary>Called with why when the program resets the chip (PFIC_CFGR or PFIC_SCTLR's SYSRESET).</summary>
    public Action<string>? Reset { get; set; }

    /// <summary>SCTLR.SLEEPONEXIT: the last mret puts the core to sleep.</summary>
    public bool SleepOnExit => (_sctlr & 2) != 0;

    /// <summary>How many traps are active: 0, 1 or 2.</summary>
    public int Depth => _active.Count;

    /// <summary>A peripheral asserts interrupt <paramref name="number"/>'s line, or lets go of it.</summary>
    public void SetLine(int number, bool on)
    {
        if (_line[number] != on)
        {
            _line[number] = on;
            Changed?.Invoke();
        }
    }

    /// <summary>Whether interrupt <paramref name="number"/> is pending (its line, or its pending bit).</summary>
    public bool Pending(int number) => _line[number] || _pendingSet[number];

    /// <summary>Whether interrupt <paramref name="number"/> is enabled (NMI and EXC always).</summary>
    public bool Enabled(int number) => number is 2 or 3 || _enabled[number];

    /// <summary>The interrupt to take now, or null: see the class's summary. NMI's is taken whatever MIE says.</summary>
    public int? Best()
    {
        int? best = null;
        var bestPriority = int.MaxValue;
        for (var n = 2; n < Ch32v003Map.InterruptCount; n++)
        {
            if (!Pending(n) || !Enabled(n) || n == RiscVCpu.QingKeExceptionNumber || _active.Contains(n) || !Admitted(n))
            {
                continue;
            }

            var priority = n == Ch32v003Map.NmiNumber ? -1 : _priority[n] & 0xC0;
            if (priority < bestPriority)
            {
                (best, bestPriority) = (n, priority);
            }
        }

        return best;
    }

    /// <summary>Whether interrupt <paramref name="number"/>, when pending and enabled, could be taken now (not counting MIE).</summary>
    public bool CouldWake(int number) => Enabled(number) && !_active.Contains(number) && Admitted(number);

    private bool Admitted(int number)
    {
        var priority = _priority[number] & 0xC0;
        if (number != Ch32v003Map.NmiNumber && (_threshold & 0xC0) != 0 && priority >= (_threshold & 0xC0))
        {
            return false;
        }

        switch (_active.Count)
        {
            case 0:
                return true;
            case 1:
                if (!nesting())
                {
                    return false;
                }

                var running = _active[0];
                return number == Ch32v003Map.NmiNumber ||
                    (running != Ch32v003Map.NmiNumber && running != RiscVCpu.QingKeExceptionNumber &&
                    (priority & 0x80) == 0 && (_priority[running] & 0x80) != 0);
            default:
                return false;
        }
    }

    public int Enter(int number)
    {
        _active.Add(number);
        _pendingSet[number] = false;
        Changed?.Invoke();
        return _active.Count;
    }

    public void Return()
    {
        if (_active.Count > 0)
        {
            _active.RemoveAt(_active.Count - 1);
        }

        Changed?.Invoke();
        Returned?.Invoke();
    }

    public uint? FreeVector(int number)
    {
        for (var channel = 0; channel < 2; channel++)
        {
            if ((_vtfAddress[channel] & 1) != 0 && ((_vtfIds >> (8 * channel)) & 0xFF) == number)
            {
                return _vtfAddress[channel] & ~1u;
            }
        }

        return null;
    }

    /// <summary>Reads <paramref name="size"/> bytes at <paramref name="offset"/> (IPRIORx and VTFIDR are bytes).</summary>
    public uint Read(uint offset, int size)
    {
        if (offset is >= 0x400 and < 0x500)
        {
            uint value = 0;
            for (var b = 0; b < size && offset + b < 0x500; b++)
            {
                value |= (uint)_priority[offset - 0x400 + b] << (8 * b);
            }

            return value;
        }

        var word = ReadWord(offset & ~3u);
        var shift = (int)(offset & 3) * 8;
        return size == 4 ? word : (word >> shift) & (size == 1 ? 0xFFu : 0xFFFFu);
    }

    private uint ReadWord(uint offset)
    {
        switch (offset)
        {
            case < 0x20:
                return Bits(_enabled, offset / 4) | (offset == 0 ? 0xCu : 0);
            case < 0x40:
                return PendingBits((offset - 0x20) / 4);
            case 0x40:
                return _threshold;
            case 0x4C:
                var nest = _active.Count switch { 0 => 0u, 1 => 1u, _ => 3u };
                var pending = Enumerable.Range(0, Ch32v003Map.InterruptCount).Any(Pending);
                return nest | (_active.Count > 0 ? 1u << 8 : 0) | (pending ? 1u << 9 : 0);
            case 0x50:
                return _vtfIds;
            case 0x60 or 0x64:
                return _vtfAddress[(offset - 0x60) / 4];
            case >= 0x300 and < 0x320:
                uint active = 0;
                foreach (var number in _active)
                {
                    if (number / 32 == (offset - 0x300) / 4)
                    {
                        active |= 1u << (number % 32);
                    }
                }

                return active;
            case 0xD10:
                return _sctlr;
            default:
                return 0; // CFGR reads 0; IENR, IRER, IPSR, IPRR are write-only
        }
    }

    /// <summary>Writes the low <paramref name="size"/> bytes of <paramref name="value"/> at <paramref name="offset"/>.</summary>
    public void Write(uint offset, int size, uint value)
    {
        if (offset is >= 0x400 and < 0x500)
        {
            for (var b = 0; b < size && offset + b < 0x500; b++)
            {
                _priority[offset - 0x400 + b] = (byte)((value >> (8 * b)) & 0xC0);
            }

            Changed?.Invoke();
            return;
        }

        if (offset is >= 0x50 and < 0x54)
        {
            for (var b = 0; b < size && offset + b < 0x54; b++)
            {
                var shift = 8 * (int)(offset - 0x50 + b);
                _vtfIds = (_vtfIds & ~(0xFFu << shift)) | (((value >> (8 * b)) & 0xFF) << shift);
            }

            return;
        }

        var word = size == 4 ? value : value << (int)(offset & 3) * 8;
        switch (offset & ~3u)
        {
            case 0x40:
                _threshold = word & 0xC0;
                break;
            case 0x48:
                if ((word & 0xFFFF_0000) == Key3 && (word & 0x80) != 0)
                {
                    Reset?.Invoke("the program reset the chip (PFIC_CFGR's SYSRESET, with KEY3)");
                }

                break;
            case 0x60 or 0x64:
                _vtfAddress[((offset & ~3u) - 0x60) / 4] = word;
                break;
            case >= 0x100 and < 0x120:
                SetBits(_enabled, ((offset & ~3u) - 0x100) / 4, word, true);
                break;
            case >= 0x180 and < 0x1A0:
                SetBits(_enabled, ((offset & ~3u) - 0x180) / 4, word, false);
                break;
            case >= 0x200 and < 0x220:
                SetBits(_pendingSet, ((offset & ~3u) - 0x200) / 4, word, true);
                break;
            case >= 0x280 and < 0x2A0:
                SetBits(_pendingSet, ((offset & ~3u) - 0x280) / 4, word, false);
                break;
            case 0xD10:
                if ((word & 0x8000_0000) != 0)
                {
                    Reset?.Invoke("the program reset the chip (PFIC_SCTLR's SYSRESET)");
                }

                _sctlr = word & 0x1E;
                break;
        }

        Changed?.Invoke();
    }

    private static uint Bits(bool[] bits, uint register)
    {
        uint value = 0;
        for (var b = 0; b < 32; b++)
        {
            var n = (int)register * 32 + b;
            if (n < bits.Length && bits[n])
            {
                value |= 1u << b;
            }
        }

        return value;
    }

    private uint PendingBits(uint register)
    {
        uint value = 0;
        for (var b = 0; b < 32; b++)
        {
            var n = (int)register * 32 + b;
            if (n < Ch32v003Map.InterruptCount && Pending(n))
            {
                value |= 1u << b;
            }
        }

        return value;
    }

    /// <summary>The bits of <paramref name="mask"/> set (or cleared) in register <paramref name="register"/>'s 32 interrupts.</summary>
    private static void SetBits(bool[] bits, uint register, uint mask, bool on)
    {
        for (var b = 0; b < 32; b++)
        {
            var n = (int)register * 32 + b;
            if ((mask & (1u << b)) != 0 && n < bits.Length)
            {
                bits[n] = on; // NMI's and EXC's enables are on whatever is written (Enabled)
            }
        }
    }
}
