using System.Globalization;

namespace ChipSharp.Simulators.RiscV;

/// <summary>
/// The QingKe V2's trap model (<see cref="RiscVTrapModel.QingKeV2"/>; QingKeV2 Processor Manual V1.1, chapters 2, 3 and
/// 7; CH32V003 Reference Manual V1.6, 6.5.3), for a core whose PFIC is <see cref="Pfic"/>.
/// <para>
/// The CSRs are chapter 7's: marchid (0xDC68D841, "WCH-V2A"), mimpid, mstatus (MIE, MPIE, MPOP, MPPOP; MPP reads 11,
/// machine mode, the only one), misa (0x40800014: RV32, C, E, X), mtvec, mscratch, mepc, mcause (the interrupt bit and
/// the code) and INTSYSCR (0x804: HWSTKEN, bit 0, the HPE; INESTEN, bit 1, nesting). Any other is illegal, the counters
/// (mcycle, minstret, time) and mtval among them, as is dcsr, dpc and dscratch outside debug mode.
/// </para>
/// <para>
/// A trap (an interrupt the chip's PFIC lets in, <see cref="TakeQingKeInterrupt"/>; an exception, as number 3, EXC):
/// mepc (the interrupted instruction's next one, or the faulting one), mcause; mstatus.MPIE takes MIE, and MIE is cleared
/// unless nesting is on (INESTEN) and the trap is the first level ("MIE will not be cleared before entering the last
/// level", 2.2): the PFIC lets a higher preemption priority in. With HWSTKEN, the HPE pushes x1, x5-x7 and x10-x15 to the
/// stack (Table 3-3: x1 at sp - 4 down to x15 at sp - 40, sp lowered by 48) for up to 2 levels, and MPOP says so
/// (MPPOP takes the MPOP before). The handler: a VTF channel's address (PFIC_VTFADDRRx) for its interrupt; else mtvec's:
/// MODE0 0, the base (bits 31:2) for every trap, 1, the base + 4 × the number (the vector table, 1 KB aligned); MODE1
/// 0, the instruction there runs (a jump), 1, the address there is jumped to (WCH's start-up sets mtvec to its table
/// at 0 with MODE 3). mret: back to mepc; MIE takes MPIE, MPIE 1; with MPOP the HPE pops what it pushed; MPOP takes
/// MPPOP; the PFIC's active trap ends, and the interrupted level's mepc and mcause come back (a nested trap would
/// otherwise lose them, and WCH's "WCH-Interrupt-fast" handlers do not save them).
/// </para>
/// <para>
/// The run stops (<see cref="ProgramFault"/>) at a trap the chip could not serve: with mtvec 0, as after reset (the chip
/// would run the reset code at 0 again), a vector table not 1 KB aligned, an entry of 0 (WCH's table has 0 for the
/// reserved and unused numbers), a stack the HPE cannot push to. Cycles: the C5's for each instruction (the V2's own are
/// not published; its 2-stage pipeline refetches after a jump); a trap 3, 4 with an address from the table, 10 more for
/// the HPE's pushes; mret 3, 10 more for its pops.
/// </para>
/// </summary>
public sealed partial class RiscVCpu
{
    /// <summary>mstatus's MPOP and MPPOP (QingKe V2): whether the active, and the previous, trap's HPE pops on mret.</summary>
    public const uint MstatusMpop = 1u << 23, MstatusMppop = 1u << 24;

    /// <summary>The QingKe V2A's misa, marchid and mimpid (QingKeV2 Processor Manual, Tables 7-2, 7-4 and 7-6).</summary>
    public const uint QingKeV2aMisa = 0x4080_0014, QingKeV2aMarchid = 0xDC68_D841, QingKeV2aMimpid = 0xDC68_8001;

    /// <summary>INTSYSCR's bits: HWSTKEN (the HPE), INESTEN (nesting).</summary>
    public const uint IntsyscrHpe = 1, IntsyscrNesting = 2;

    /// <summary>The trap number exceptions go through on the QingKe V2 (EXC: the vector table's HardFault entry).</summary>
    public const int QingKeExceptionNumber = 3;

    private const int Intsyscr = 0x804;
    private const int HpeCycles = 10;

    /// <summary>The registers the HPE saves, in the order of Table 3-3: x1 at sp - 4, x5 at sp - 8, ..., x15 at sp - 40.</summary>
    private static readonly int[] HpeRegisters = [1, 5, 6, 7, 10, 11, 12, 13, 14, 15];

    private readonly Stack<(uint Mepc, uint Mcause)> _interrupted = new();

    /// <summary>The PFIC (QingKe V2): which traps are active, the VTF channels; null for none.</summary>
    public IQingKeInterrupts? Pfic { get; set; }

    /// <summary>INTSYSCR (CSR 0x804): HWSTKEN and INESTEN.</summary>
    public uint InterruptSystemControl { get; set; }

    /// <summary>How many levels of registers the HPE holds on the stack now (0 to 2).</summary>
    public int HardwareStackDepth { get; private set; }

    /// <summary>
    /// Takes interrupt <paramref name="number"/> (the PFIC chose it: enabled, pending, above the threshold, preempting
    /// what runs), as the QingKe V2 does. Returns the cycles it took.
    /// </summary>
    public int TakeQingKeInterrupt(int number)
    {
        Waiting = false;
        return EnterQingKeTrap(number, 0x8000_0000u | (uint)number, Pc,
            string.Create(CultureInfo.InvariantCulture, $"interrupt {number}"));
    }

    private int TakeQingKeException(int cause, uint pc) =>
        EnterQingKeTrap(QingKeExceptionNumber, (uint)cause, pc, LastException!);

    private int EnterQingKeTrap(int number, uint cause, uint mepc, string what)
    {
        var cycles = 3;
        uint handler;
        if (number != QingKeExceptionNumber && Pfic?.FreeVector(number) is { } free)
        {
            handler = free & ~1u;
        }
        else
        {
            var mode = _mtvec & 3;
            var table = _mtvec & ~3u;
            if (_mtvec == 0)
            {
                throw new ProgramFault($"the program caused {what}, and has no trap handler (mtvec 0x00000000, as after " +
                    "reset: the chip would run the reset code at 0 again)");
            }

            if ((mode & 1) != 0 && (table & 0x3FF) != 0)
            {
                throw new ProgramFault(string.Create(CultureInfo.InvariantCulture,
                    $"the program caused {what}, but mtvec's vector table at 0x{table:x8} is not 1 KB aligned, as the QingKe V2 needs"));
            }

            var entry = (mode & 1) != 0 ? table + 4 * (uint)number : table;
            handler = entry;
            if ((mode & 2) != 0)
            {
                cycles = 4;
                try
                {
                    handler = _bus.Read(entry, 4) & ~1u;
                }
                catch (RiscVAccessFault)
                {
                    throw new ProgramFault(string.Create(CultureInfo.InvariantCulture,
                        $"the program caused {what}, but its vector table's entry at 0x{entry:x8} is not memory"));
                }

                if (handler == 0)
                {
                    throw new ProgramFault(string.Create(CultureInfo.InvariantCulture,
                        $"the program caused {what}, and its vector table's entry at 0x{entry:x8} (mtvec 0x{_mtvec:x8}) is 0: no handler"));
                }
            }
        }

        var level = Pfic?.Enter(number) ?? 1;
        _interrupted.Push((Mepc, _mcauseFields));
        Mepc = mepc;
        _mcauseFields = cause;
        var mie = _mstatus & MstatusMie;
        var keep = (InterruptSystemControl & IntsyscrNesting) != 0 && level < 2;
        var pop = 0u;
        if ((InterruptSystemControl & IntsyscrHpe) != 0 && HardwareStackDepth < 2)
        {
            PushHpe(what);
            pop = MstatusMpop;
            cycles += HpeCycles;
        }

        var mpop = (_mstatus & MstatusMpop) != 0 ? MstatusMppop : 0;
        _mstatus = (_mstatus & ~(MstatusMie | MstatusMpie | MstatusMpop | MstatusMppop)) | (keep ? mie : 0) |
            (mie != 0 ? MstatusMpie : 0) | pop | mpop;
        Pc = handler;
        return cycles;
    }

    private void PushHpe(string what)
    {
        var sp = X[2];
        try
        {
            for (var r = 0; r < HpeRegisters.Length; r++)
            {
                _bus.Write(sp - 4 * (uint)(r + 1), 4, X[HpeRegisters[r]]);
            }
        }
        catch (RiscVAccessFault fault)
        {
            throw new ProgramFault(string.Create(CultureInfo.InvariantCulture,
                $"the program caused {what}, and the HPE's push of the registers to the stack (sp 0x{sp:x8}) reached 0x{fault.Address:x8}, which is not memory"));
        }

        X[2] = sp - 48;
        HardwareStackDepth++;
    }

    private int QingKeMret()
    {
        var cycles = 3;
        Pc = Mepc;
        var mpie = (_mstatus & MstatusMpie) != 0;
        if ((_mstatus & MstatusMpop) != 0)
        {
            var sp = X[2] + 48;
            try
            {
                for (var r = 0; r < HpeRegisters.Length; r++)
                {
                    X[HpeRegisters[r]] = _bus.Read(sp - 4 * (uint)(r + 1), 4);
                }
            }
            catch (RiscVAccessFault fault)
            {
                throw new ProgramFault(string.Create(CultureInfo.InvariantCulture,
                    $"mret: the HPE's pop of the registers from the stack reached 0x{fault.Address:x8}, which is not memory"));
            }

            X[2] = sp;
            HardwareStackDepth = Math.Max(0, HardwareStackDepth - 1);
            cycles += HpeCycles;
        }

        var mppop = (_mstatus & MstatusMppop) != 0 ? MstatusMpop : 0;
        _mstatus = (_mstatus & ~(MstatusMie | MstatusMpop | MstatusMppop)) | (mpie ? MstatusMie : 0) | MstatusMpie | mppop;
        if (_interrupted.TryPop(out var outer))
        {
            (Mepc, _mcauseFields) = outer;
            Pfic?.Return();
        }

        InterruptStateChanged?.Invoke();
        return cycles;
    }

    /// <summary>A CSR of the QingKe V2 (chapter 7's list); null for one it does not have.</summary>
    private uint? QingKeCsr(int csr, bool write, uint value)
    {
        switch (csr)
        {
            case Number.Mstatus:
                if (write)
                {
                    Mstatus = value;
                    InterruptStateChanged?.Invoke();
                }

                return _mstatus;
            case Number.Misa:
                return QingKeV2aMisa; // MRW, but it keeps what the core is
            case Number.Mtvec:
                if (write)
                {
                    Mtvec = value;
                }

                return _mtvec;
            case Number.Mscratch:
                if (write)
                {
                    Mscratch = value;
                }

                return Mscratch;
            case Number.Mepc:
                if (write)
                {
                    Mepc = value & ~1u;
                }

                return Mepc;
            case Number.Mcause:
                if (write)
                {
                    Mcause = value;
                }

                return Mcause;
            case Intsyscr:
                if (write)
                {
                    InterruptSystemControl = value & (IntsyscrHpe | IntsyscrNesting);
                    InterruptStateChanged?.Invoke();
                }

                return InterruptSystemControl;
            case Number.Marchid:
                return QingKeV2aMarchid;
            case Number.Mimpid:
                return QingKeV2aMimpid;
            default:
                return null;
        }
    }
}
