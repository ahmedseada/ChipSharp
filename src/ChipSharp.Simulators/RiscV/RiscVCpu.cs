using System.Globalization;
using System.Numerics;

namespace ChipSharp.Simulators.RiscV;

/// <summary>
/// The memory space of a RISC-V core: 32-bit addresses, little-endian. A read, write or fetch of an address with nothing
/// there throws <see cref="RiscVAccessFault"/>, which the core turns into the access-fault exception.
/// </summary>
public interface IRiscVBus
{
    /// <summary>The 32 bits at <paramref name="address"/> (2-byte aligned): an instruction, or a compressed one in the low half.</summary>
    uint Fetch(uint address);

    /// <summary>The <paramref name="size"/> bytes (1, 2 or 4) at <paramref name="address"/>, zero-extended.</summary>
    uint Read(uint address, int size);

    /// <summary>Writes the low <paramref name="size"/> bytes (1, 2 or 4) of <paramref name="value"/> at <paramref name="address"/>.</summary>
    void Write(uint address, int size, uint value);

    /// <summary>The cycles an access to <paramref name="address"/> takes beyond the instruction's own (a peripheral's bus).</summary>
    int AccessCycles(uint address);
}

/// <summary>What the core asks the interrupt controller (a CLIC) for mnxti: the interrupt to take next, and taking it.</summary>
public interface IRiscVInterrupts
{
    /// <summary>The pending and enabled non-vectored interrupt of the highest level above <paramref name="level"/>, or null.</summary>
    (int Id, int Level)? Next(int level);

    /// <summary>The core takes <paramref name="id"/>: an edge-triggered interrupt's pending bit is cleared.</summary>
    void Taken(int id);
}

/// <summary>An access to an address with nothing there (no memory, no register), at <see cref="Address"/>.</summary>
public sealed class RiscVAccessFault(uint address) : Exception(string.Create(CultureInfo.InvariantCulture,
    $"nothing at 0x{address:x8}"))
{
    public uint Address { get; } = address;
}

/// <summary>A privilege mode of the core.</summary>
public enum RiscVPrivilege
{
    User = 0,
    Machine = 3,
}

/// <summary>
/// A 32-bit RISC-V core in machine mode (and user mode, as mret's MPP gives it), as Espressif's ESP32-C5 HP CPU is
/// (ESP32-C5 Technical Reference Manual v1.1, chapter 2 High-Performance CPU): RV32I, M, A (LR/SC and the AMOs; aq and rl
/// ignored, as the TRM says the core always orders memory), C, Zicsr and Zifencei, and the Zc extensions the datasheet
/// lists: Zcb (not its c.sext.b, c.zext.h and c.sext.h, which need Zbb), Zcmp as its specification has it (not the C5's
/// faulty cm.push and cm.popret, ESP-IDF's SOC_CPU_ZCMP_PUSH_REVERSED and SOC_CPU_ZCMP_POPRET_ISSUE: ChipSharp does not
/// use Zcmp), Zcmt. Not the custom hardware loops (Xhwlp): their opcodes, like every one the core does not have, raise
/// the illegal-instruction exception. Interrupts are the CLIC's (Core-Local Interrupt Controller, version 0.9): the
/// chip asks <see cref="TakeInterrupt"/> to take one; mtvec's mode is hardwired to 3 (CLIC mode), so exceptions and
/// non-vectored interrupts go to mtvec's base (64-byte aligned) and a vectored one (clicintattr.shv) through the table at
/// mtvt; mcause holds the interrupt's or exception's code, the previous level (MPIL) and mirrors mstatus's MPP and MPIE;
/// mintstatus holds the level being served and mintthresh the threshold; mret restores them. mnxti, mscratchcsw and
/// mscratchcswl are the CLIC's. mie, mip and mideleg read 0, as on the chip.
/// <para>
/// The stops ChipSharp's runtimes use: <c>ebreak</c> (and <c>c.ebreak</c>) sets <see cref="BreakHit"/> instead of
/// trapping, the failure stop, as a debugger would see it; <c>wfi</c> sets <see cref="Waiting"/>, and the chip wakes the
/// core when an interrupt is pending. An exception (illegal instruction, access fault, ecall, a misaligned AMO; a
/// misaligned load or store is done in two accesses, as the C5 splits them) traps to mtvec, unless mtvec has no
/// handler (0): then the run stops with what happened (<see cref="ProgramFault"/>).
/// </para>
/// <para>
/// Cycles (the chip turns them into time at its CPU clock): 1 an instruction, plus a taken branch, jal, c.j or c.jal 1;
/// jalr, c.jr, c.jalr, mret, cm.jt, cm.jalt 2; mul, mulh, mulhsu, mulhu 1 (the TRM's 2 cycles); div, divu, rem, remu
/// 1 to 18, two bits of the quotient a cycle (the TRM's radix-4 divider, 1 to 19 cycles); an AMO 2, lr.w and sc.w 1;
/// cm.push and cm.pop 1 a register; a load or store what the bus adds (<see cref="IRiscVBus.AccessCycles"/>); a trap
/// 3 (4 through the vector table). The pipeline's hazards and the branch predictor's misses are not counted.
/// </para>
/// </summary>
public sealed class RiscVCpu
{
    /// <summary>mstatus's bits.</summary>
    public const uint MstatusMie = 1u << 3, MstatusMpie = 1u << 7, MstatusMpp = 3u << 11, MstatusMprv = 1u << 17,
        MstatusTw = 1u << 21;

    /// <summary>Exception codes (mcause), as the TRM lists them.</summary>
    public const int InstructionAccessFault = 1, IllegalInstruction = 2, Breakpoint = 3, LoadAddressMisaligned = 4,
        LoadAccessFault = 5, StoreAddressMisaligned = 6, StoreAccessFault = 7, EcallFromUser = 8, EcallFromMachine = 11;

    /// <summary>The CSRs' numbers.</summary>
    private static class Number
    {
            public const int Jvt = 0x017, Mstatus = 0x300, Misa = 0x301, Mideleg = 0x303, Mie = 0x304, Mtvec = 0x305,
            Mcounteren = 0x306, Mtvt = 0x307, Mcountinhibit = 0x320, Mscratch = 0x340, Mepc = 0x341, Mcause = 0x342,
            Mtval = 0x343, Mip = 0x344, Mnxti = 0x345, Mintthresh = 0x347, Mscratchcsw = 0x348, Mscratchcswl = 0x349,
            Mclicbase = 0x350, Mcycle = 0xB00, Minstret = 0xB02, Mcycleh = 0xB80, Minstreth = 0xB82, Cycle = 0xC00,
            Time = 0xC01, Instret = 0xC02, Cycleh = 0xC80, Timeh = 0xC81, Instreth = 0xC82, Mvendorid = 0xF11,
            Marchid = 0xF12, Mimpid = 0xF13, Mhartid = 0xF14, Mintstatus = 0xFB1;
    }

    /// <summary>misa: RV32 (MXL 1) with A, C, I, M, N and U, as the TRM gives it.</summary>
    public const uint MisaValue = (1u << 30) | (1u << 0) | (1u << 2) | (1u << 8) | (1u << 12) | (1u << 13) | (1u << 20);

    private static readonly int[] SRegisters = [8, 9, 18, 19, 20, 21, 22, 23];

    /// <summary>The registers of Zcmp's rlist 4 to 15: ra, then s0 to s(rlist - 5), or s0 to s11 for 15 (s10 alone has none).</summary>
    private static readonly int[][] PushPopRegisters = [.. Enumerable.Range(0, 16).Select(rlist => rlist < 4
        ? Array.Empty<int>()
        : (int[])[1, .. Enumerable.Range(0, rlist == 15 ? 12 : rlist - 4).Select(s => s < 2 ? 8 + s : 16 + s)])];

    private readonly IRiscVBus _bus;
    private readonly Dictionary<int, uint> _otherCsrs = [];
    private uint _mstatus;
    private uint _mtvec = 3;
    private uint _mcauseFields;
    private bool _reserved;
    private uint _reservation;

    /// <summary>A core on <paramref name="bus"/>, as after reset: in machine mode, interrupts off, its PC at 0.</summary>
    public RiscVCpu(IRiscVBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
    }

    /// <summary>The registers x0 to x31 (x0 reads 0 whatever is written).</summary>
    public uint[] X { get; } = new uint[32];

    /// <summary>The address of the next instruction.</summary>
    public uint Pc { get; set; }

    /// <summary>The privilege mode it runs in.</summary>
    public RiscVPrivilege Privilege { get; private set; } = RiscVPrivilege.Machine;

    /// <summary>The CLIC, for mnxti; null for none (mnxti reads 0).</summary>
    public IRiscVInterrupts? Interrupts { get; set; }

    /// <summary>What the time CSR reads (the CLINT's counter); null reads 0.</summary>
    public Func<ulong>? TimeSource { get; set; }

    /// <summary>Called after a write to a CSR that can change which interrupt is taken (mstatus, mintthresh, mnxti).</summary>
    public Action? InterruptStateChanged { get; set; }

    /// <summary>The cycles counted (mcycle), unless mcountinhibit's CY stops it.</summary>
    public ulong Cycles { get; set; }

    /// <summary>The instructions retired (minstret), unless mcountinhibit's IR stops it.</summary>
    public ulong InstructionsRetired { get; set; }

    /// <summary>Whether the core waits for an interrupt (wfi); the chip clears it when one is pending.</summary>
    public bool Waiting { get; set; }

    /// <summary>Whether the program ran ebreak (the failure stop of ChipSharp's runtimes).</summary>
    public bool BreakHit { get; set; }

    /// <summary>The last exception taken, as a sentence ("illegal instruction 0x00000000 at pc 0x40800010"), or null.</summary>
    public string? LastException { get; private set; }

    public uint Mstatus
    {
        get => _mstatus;
        set
        {
            // MPP is one bit wide (user or machine), its two bits tied; UIE and UPIE are hardwired to 0
            var mpp = (value & (1u << 11)) != 0 ? MstatusMpp : 0;
            _mstatus = (value & (MstatusMie | MstatusMpie | MstatusMprv | MstatusTw)) | mpp;
        }
    }

    /// <summary>Whether interrupts are on: mstatus.MIE in machine mode; always in user mode.</summary>
    public bool InterruptsEnabled => Privilege == RiscVPrivilege.User || (_mstatus & MstatusMie) != 0;

    /// <summary>mtvec: the base of the trap handler (64-byte aligned) and the mode, hardwired to 3 (CLIC).</summary>
    public uint Mtvec
    {
        get => _mtvec;
        set => _mtvec = (value & ~0x3Fu) | 3;
    }

    /// <summary>mtvt: the base of the CLIC's vector table, 64-byte aligned.</summary>
    public uint Mtvt { get; set; }

    public uint Mepc { get; set; }

    /// <summary>mcause: INT, MINHV, MPP and MPIE (mstatus's), MPIL, the code.</summary>
    public uint Mcause
    {
        get => _mcauseFields | ((_mstatus & MstatusMpp) << 17) | ((_mstatus & MstatusMpie) << 20);
        set
        {
            _mcauseFields = value & 0xC0FF003Fu;
            Mstatus = (_mstatus & ~(MstatusMpp | MstatusMpie)) | ((value >> 17) & MstatusMpp) | ((value >> 20) & MstatusMpie);
        }
    }

    public uint Mtval { get; set; }

    public uint Mscratch { get; set; }

    /// <summary>mintthresh: the level an interrupt must be above to be taken.</summary>
    public int Mintthresh { get; set; }

    /// <summary>mintstatus.MIL: the level of the interrupt being served (0 in the program).</summary>
    public int InterruptLevel { get; set; }

    /// <summary>The level an interrupt must be above to be taken now: the higher of mintthresh and mintstatus.MIL.</summary>
    public int Threshold => Math.Max(Mintthresh, InterruptLevel);

    /// <summary>jvt: the table cm.jt and cm.jalt jump through (Zcmt), 64-byte aligned.</summary>
    public uint Jvt { get; set; }

    /// <summary>mcountinhibit: CY (bit 0) stops mcycle, IR (bit 2) minstret.</summary>
    public uint CountInhibit { get; set; }

    /// <summary>
    /// Runs one instruction, an exception it raises included (it is taken: the next instruction is the handler's).
    /// Returns its cycles.
    /// </summary>
    public int Step()
    {
        var pc = Pc;
        int cycles;
        try
        {
            uint instruction;
            try
            {
                instruction = _bus.Fetch(pc);
            }
            catch (RiscVAccessFault)
            {
                throw new Trap(InstructionAccessFault, pc);
            }

            cycles = (instruction & 3) == 3 ? Execute(pc, instruction) : ExecuteCompressed(pc, instruction & 0xFFFF);
            X[0] = 0;
            if ((CountInhibit & 4) == 0)
            {
                InstructionsRetired++;
            }
        }
        catch (Trap trap)
        {
            X[0] = 0;
            cycles = TakeException(trap.Cause, trap.Value, pc);
        }

        if ((CountInhibit & 1) == 0)
        {
            Cycles += (ulong)cycles;
        }

        return cycles;
    }

    /// <summary>
    /// Takes interrupt <paramref name="id"/> of <paramref name="level"/> (the chip's CLIC chose it): mepc, mcause (with the
    /// level before, MPIL), mstatus (MPIE, MPP; MIE off), mintstatus.MIL; the next instruction is mtvec's handler, or
    /// the one the vector table at mtvt gives (<paramref name="vectored"/>). Returns the cycles it took.
    /// </summary>
    public int TakeInterrupt(int id, int level, bool vectored)
    {
        Waiting = false;
        _reserved = false;
        Mepc = Pc;
        _mcauseFields = 0x80000000u | ((uint)(InterruptLevel & 0xFF) << 16) | (uint)(id & 0x3F);
        EnterMachineMode();
        InterruptLevel = level;
        if (!vectored)
        {
            Pc = HandlerBase(string.Create(CultureInfo.InvariantCulture, $"interrupt {id}"));
            return 3;
        }

        var entry = Mtvt + 4 * (uint)id;
        uint handler;
        try
        {
            handler = _bus.Read(entry, 4);
        }
        catch (RiscVAccessFault)
        {
            throw new ProgramFault(string.Create(CultureInfo.InvariantCulture,
                $"interrupt {id} is vectored, but its entry of the vector table (mtvt 0x{Mtvt:x8}) at 0x{entry:x8} is not memory"));
        }

        Pc = handler & ~1u;
        return 4;
    }

    /// <summary>Reads a CSR as csrr does (machine mode), e.g. for a test; throws for one the core does not have.</summary>
    public uint ReadCsr(int csr) => Csr(csr, write: false, 0) ?? throw new InvalidOperationException($"no CSR 0x{csr:x3}");

    private int Execute(uint pc, uint i)
    {
        var rd = (int)(i >> 7) & 31;
        var rs1 = (int)(i >> 15) & 31;
        var rs2 = (int)(i >> 20) & 31;
        var funct3 = (i >> 12) & 7;
        var next = pc + 4;
        var cycles = 1;
        switch (i & 0x7F)
        {
            case 0x37: // lui
                X[rd] = i & 0xFFFFF000;
                break;
            case 0x17: // auipc
                X[rd] = pc + (i & 0xFFFFF000);
                break;
            case 0x6F: // jal
                X[rd] = next;
                next = pc + (uint)JImmediate(i);
                cycles = 2;
                break;
            case 0x67: // jalr
                if (funct3 != 0)
                {
                    throw Illegal(i);
                }

                var target = (X[rs1] + (uint)((int)i >> 20)) & ~1u;
                X[rd] = next;
                next = target;
                _reserved = false;
                cycles = 3;
                break;
            case 0x63:
                if (Branch(funct3, X[rs1], X[rs2], i))
                {
                    var offset = BImmediate(i);
                    next = pc + (uint)offset;
                    cycles = 2;
                    if (offset < 0)
                    {
                        _reserved = false;
                    }
                }

                break;
            case 0x03:
                {
                    var address = X[rs1] + (uint)((int)i >> 20);
                    X[rd] = funct3 switch
                    {
                        0 => (uint)(sbyte)Load(address, 1, ref cycles),
                        1 => (uint)(short)Load(address, 2, ref cycles),
                        2 => Load(address, 4, ref cycles),
                        4 => Load(address, 1, ref cycles),
                        5 => Load(address, 2, ref cycles),
                        _ => throw Illegal(i),
                    };
                    break;
                }

            case 0x23:
                {
                    var address = X[rs1] + (uint)(((int)i >> 25 << 5) | (int)((i >> 7) & 31));
                    var size = funct3 switch { 0 => 1, 1 => 2, 2 => 4, _ => throw Illegal(i) };
                    Store(address, size, X[rs2], ref cycles);
                    break;
                }

            case 0x13:
                X[rd] = OpImmediate(funct3, X[rs1], i);
                break;
            case 0x33:
                X[rd] = Op(i, X[rs1], X[rs2], ref cycles);
                break;
            case 0x0F: // fence, fence.i: the core keeps memory in order, and has no cache of its instructions to flush
                if (funct3 > 1)
                {
                    throw Illegal(i);
                }

                _reserved = false;
                break;
            case 0x2F:
                cycles = Atomic(i, rd, rs1, rs2);
                break;
            case 0x73:
                return System(pc, i, rd, rs1, funct3);
            default:
                throw Illegal(i);
        }

        Pc = next;
        return cycles;
    }

    private static bool Branch(uint funct3, uint a, uint b, uint i) => funct3 switch
    {
        0 => a == b,
        1 => a != b,
        4 => (int)a < (int)b,
        5 => (int)a >= (int)b,
        6 => a < b,
        7 => a >= b,
        _ => throw Illegal(i),
    };

    private static uint OpImmediate(uint funct3, uint a, uint i)
    {
        var immediate = (uint)((int)i >> 20);
        var shamt = (int)(immediate & 31);
        return funct3 switch
        {
            0 => a + immediate,
            1 when (i >> 25) == 0 => a << shamt,
            2 => (int)a < (int)immediate ? 1u : 0,
            3 => a < immediate ? 1u : 0,
            4 => a ^ immediate,
            5 when (i >> 25) == 0 => a >> shamt,
            5 when (i >> 25) == 0x20 => (uint)((int)a >> shamt),
            6 => a | immediate,
            7 => a & immediate,
            _ => throw Illegal(i),
        };
    }

    private static uint Op(uint i, uint a, uint b, ref int cycles)
    {
        var funct3 = (i >> 12) & 7;
        switch (i >> 25)
        {
            case 0:
                return funct3 switch
                {
                    0 => a + b,
                    1 => a << (int)(b & 31),
                    2 => (int)a < (int)b ? 1u : 0,
                    3 => a < b ? 1u : 0,
                    4 => a ^ b,
                    5 => a >> (int)(b & 31),
                    6 => a | b,
                    _ => a & b,
                };
            case 0x20 when funct3 == 0:
                return a - b;
            case 0x20 when funct3 == 5:
                return (uint)((int)a >> (int)(b & 31));
            case 1:
                if (funct3 < 4)
                {
                    cycles = 2;
                }
                else
                {
                    cycles = DivideCycles(funct3 is 4 or 6, a, b);
                }

                return funct3 switch
                {
                    0 => a * b,
                    1 => (uint)(((long)(int)a * (int)b) >> 32),
                    2 => (uint)(((long)(int)a * (long)b) >> 32),
                    3 => (uint)(((ulong)a * b) >> 32),
                    4 => b == 0 ? uint.MaxValue : (a == 0x80000000 && b == uint.MaxValue) ? a : (uint)((int)a / (int)b),
                    5 => b == 0 ? uint.MaxValue : a / b,
                    6 => b == 0 ? a : (a == 0x80000000 && b == uint.MaxValue) ? 0 : (uint)((int)a % (int)b),
                    _ => b == 0 ? a : a % b,
                };
            default:
                throw Illegal(i);
        }
    }

    /// <summary>A division's cycles: 1 for a divisor of 0 or a quotient of 0, else 2 and a cycle for every 2 bits of the quotient.</summary>
    private static int DivideCycles(bool signed, uint a, uint b)
    {
        var dividend = signed && (int)a < 0 ? (uint)-(int)a : a;
        var divisor = signed && (int)b < 0 ? (uint)-(int)b : b;
        if (divisor == 0 || dividend < divisor)
        {
            return 1;
        }

        var bits = BitOperations.LeadingZeroCount(divisor) - BitOperations.LeadingZeroCount(dividend) + 1;
        return Math.Min(2 + (bits + 1) / 2, 18);
    }

    private int Atomic(uint i, int rd, int rs1, int rs2)
    {
        if (((i >> 12) & 7) != 2)
        {
            throw Illegal(i);
        }

        var address = X[rs1];
        var funct5 = i >> 27;
        if ((address & 3) != 0)
        {
            throw new Trap(StoreAddressMisaligned, address);
        }

        var cycles = 1;
        switch (funct5)
        {
            case 0x02: // lr.w
                if (rs2 != 0)
                {
                    throw Illegal(i);
                }

                X[rd] = Load(address, 4, ref cycles);
                _reserved = true;
                _reservation = address;
                Pc += 4;
                return cycles;
            case 0x03: // sc.w
                if (_reserved && _reservation == address)
                {
                    Store(address, 4, X[rs2], ref cycles);
                    X[rd] = 0;
                }
                else
                {
                    X[rd] = 1;
                }

                _reserved = false;
                Pc += 4;
                return cycles;
        }

        cycles = 2;
        var value = Load(address, 4, ref cycles);
        var operand = X[rs2];
        var result = funct5 switch
        {
            0x01 => operand,
            0x00 => value + operand,
            0x04 => value ^ operand,
            0x0C => value & operand,
            0x08 => value | operand,
            0x10 => (int)value < (int)operand ? value : operand,
            0x14 => (int)value > (int)operand ? value : operand,
            0x18 => value < operand ? value : operand,
            0x1C => value > operand ? value : operand,
            _ => throw Illegal(i),
        };
        Store(address, 4, result, ref cycles);
        X[rd] = value;
        Pc += 4;
        return cycles;
    }

    private int System(uint pc, uint i, int rd, int rs1, uint funct3)
    {
        if (funct3 == 0)
        {
            switch (i)
            {
                case 0x00000073: // ecall
                    throw new Trap(Privilege == RiscVPrivilege.Machine ? EcallFromMachine : EcallFromUser, 0);
                case 0x00100073: // ebreak
                    BreakHit = true;
                    return 1;
                case 0x30200073: // mret
                    if (Privilege != RiscVPrivilege.Machine)
                    {
                        throw Illegal(i);
                    }

                    Mret();
                    return 3;
                case 0x10500073: // wfi
                    if (Privilege != RiscVPrivilege.Machine && (_mstatus & MstatusTw) != 0)
                    {
                        throw Illegal(i);
                    }

                    Waiting = true;
                    Pc = pc + 4;
                    return 1;
                default:
                    throw Illegal(i);
            }
        }

        if (funct3 == 4)
        {
            throw Illegal(i);
        }

        var csr = (int)(i >> 20);
        var operand = funct3 >= 5 ? (uint)rs1 : X[rs1];
        var op = funct3 & 3; // 1 write, 2 set, 3 clear
        var writes = op == 1 || rs1 != 0;
        if (Privilege == RiscVPrivilege.User && ((csr >> 8) & 3) != 0)
        {
            throw Illegal(i);
        }

        if (writes && (csr >> 10) == 3)
        {
            throw Illegal(i); // a read-only CSR
        }

        if (csr == Number.Mnxti)
        {
            X[rd] = Mnxt(op, operand, writes);
        }
        else if (csr is Number.Mscratchcsw or Number.Mscratchcswl && op == 1)
        {
            X[rd] = ScratchSwap(csr, operand);
        }
        else
        {
            // csrrw with rd = x0 does not read; the others do
            var old = Csr(csr, write: false, 0) ?? throw Illegal(i);
            if (writes)
            {
                var value = op switch { 1 => operand, 2 => old | operand, _ => old & ~operand };
                Csr(csr, write: true, value);
            }

            X[rd] = old;
        }

        Pc = pc + 4;
        return 1;
    }

    /// <summary>Reads (and writes, when <paramref name="write"/>) a CSR; null for one the core does not have.</summary>
    private uint? Csr(int csr, bool write, uint value)
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
                return MisaValue;
            case Number.Mideleg or Number.Mie or Number.Mip:
                return 0; // hardwired: only the CLIC mode is there
            case Number.Mtvec:
                if (write)
                {
                    Mtvec = value;
                }

                return _mtvec;
            case Number.Mtvt:
                if (write)
                {
                    Mtvt = value & ~0x3Fu;
                }

                return Mtvt;
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
            case Number.Mtval:
                if (write)
                {
                    Mtval = value;
                }

                return Mtval;
            case Number.Mintthresh:
                if (write)
                {
                    Mintthresh = (int)(value & 0xFF);
                    InterruptStateChanged?.Invoke();
                }

                return (uint)Mintthresh;
            case Number.Mintstatus:
                return (uint)InterruptLevel << 24;
            case Number.Mscratchcsw or Number.Mscratchcswl:
                return Mscratch; // read without the swap (csrrs, csrrc)
            case Number.Mclicbase:
                return 0x20800000;
            case Number.Jvt:
                if (write)
                {
                    Jvt = value & ~0x3Fu; // jump table mode (0) only
                }

                return Jvt;
            case Number.Mcountinhibit:
                if (write)
                {
                    CountInhibit = value & 5;
                }

                return CountInhibit;
            case Number.Mcycle or Number.Cycle:
                if (write)
                {
                    Cycles = (Cycles & 0xFFFFFFFF00000000) | value;
                }

                return (uint)Cycles;
            case Number.Mcycleh or Number.Cycleh:
                if (write)
                {
                    Cycles = (Cycles & 0xFFFFFFFF) | ((ulong)value << 32);
                }

                return (uint)(Cycles >> 32);
            case Number.Minstret or Number.Instret:
                if (write)
                {
                    InstructionsRetired = (InstructionsRetired & 0xFFFFFFFF00000000) | value;
                }

                return (uint)InstructionsRetired;
            case Number.Minstreth or Number.Instreth:
                if (write)
                {
                    InstructionsRetired = (InstructionsRetired & 0xFFFFFFFF) | ((ulong)value << 32);
                }

                return (uint)(InstructionsRetired >> 32);
            case Number.Time:
                return (uint)(TimeSource?.Invoke() ?? 0);
            case Number.Timeh:
                return (uint)((TimeSource?.Invoke() ?? 0) >> 32);
            case Number.Mvendorid:
                return 0x612;
            case Number.Marchid:
                return 0x80000003;
            case Number.Mimpid:
                return 1;
            case Number.Mhartid:
                return 0;
        }

        // the registers that only keep what is written: mcounteren, the performance counters and their events, PMP,
        // the triggers, the custom ones (dedicated GPIO, mhcr, mexstatus, mhint, the PMA checker, the load/store PCs)
        if (csr is Number.Mcounteren or (>= 0x323 and <= 0x33F) or (>= 0xB03 and <= 0xB1F) or (>= 0xB83 and <= 0xB9F)
            or (>= 0xC03 and <= 0xC1F) or (>= 0xC83 and <= 0xC9F) or (>= 0x3A0 and <= 0x3A3) or (>= 0x3B0 and <= 0x3BF)
            or (>= 0x7A0 and <= 0x7A8) or (>= 0x803 and <= 0x805) or 0x7C1 or 0x7C5 or 0x7E1 or (>= 0xBC0 and <= 0xBFF))
        {
            if (write && (csr >> 10) != 3)
            {
                _otherCsrs[csr] = value;
            }

            return _otherCsrs.GetValueOrDefault(csr);
        }

        return null;
    }

    /// <summary>mnxti (CLIC): the vector table's entry of the next interrupt to serve, taking it, or 0; mstatus is the write's.</summary>
    private uint Mnxt(uint op, uint operand, bool writes)
    {
        if (writes)
        {
            var value = op switch { 1 => operand, 2 => _mstatus | operand, _ => _mstatus & ~operand };
            Mstatus = value;
        }

        var previous = (int)(_mcauseFields >> 16) & 0xFF;
        if (Interrupts?.Next(Math.Max(previous, Mintthresh)) is not { } next)
        {
            if (writes)
            {
                InterruptStateChanged?.Invoke();
            }

            return 0;
        }

        if (writes)
        {
            InterruptLevel = next.Level;
            _mcauseFields = (_mcauseFields & ~0x3Fu) | (uint)(next.Id & 0x3F);
            Interrupts.Taken(next.Id);
            InterruptStateChanged?.Invoke();
        }

        return Mtvt + 4 * (uint)next.Id;
    }

    /// <summary>mscratchcsw and mscratchcswl: swaps mscratch with the value written when the mode, or the level, changed.</summary>
    private uint ScratchSwap(int csr, uint value)
    {
        var swap = csr == Number.Mscratchcsw
            ? ((_mstatus >> 11) & 3) != (uint)Privilege
            : (((_mcauseFields >> 16) & 0xFF) == 0) != (InterruptLevel == 0);
        if (!swap)
        {
            return value;
        }

        var old = Mscratch;
        Mscratch = value;
        return old;
    }

    private void Mret()
    {
        Pc = Mepc;
        var mpie = (_mstatus & MstatusMpie) != 0;
        Privilege = (_mstatus & MstatusMpp) != 0 ? RiscVPrivilege.Machine : RiscVPrivilege.User;
        _mstatus = (_mstatus & ~(MstatusMie | MstatusMpp)) | (mpie ? MstatusMie : 0) | MstatusMpie;
        InterruptLevel = (int)(_mcauseFields >> 16) & 0xFF;
        _reserved = false;
        InterruptStateChanged?.Invoke();
    }

    private void EnterMachineMode()
    {
        var mie = (_mstatus & MstatusMie) != 0;
        _mstatus = (_mstatus & ~(MstatusMie | MstatusMpie | MstatusMpp)) | (mie ? MstatusMpie : 0) |
            (Privilege == RiscVPrivilege.Machine ? MstatusMpp : 0);
        Privilege = RiscVPrivilege.Machine;
    }

    private int TakeException(int cause, uint value, uint pc)
    {
        LastException = Describe(cause, value, pc);
        Waiting = false;
        _reserved = false;
        Mepc = pc;
        Mtval = value;
        _mcauseFields = ((uint)(InterruptLevel & 0xFF) << 16) | (uint)cause; // MPIL: the level, which stays
        EnterMachineMode();
        Pc = HandlerBase(LastException);
        return 3;
    }

    /// <summary>mtvec's base, where a trap goes; with none set up (0), the run stops with <paramref name="what"/>.</summary>
    private uint HandlerBase(string what)
    {
        var handler = _mtvec & ~0x3Fu;
        return handler != 0
            ? handler
            : throw new ProgramFault($"the program caused {what}, and has no trap handler (mtvec 0x{_mtvec:x8})");
    }

    private static string Describe(int cause, uint value, uint pc)
    {
        var at = string.Create(CultureInfo.InvariantCulture, $"at pc 0x{pc:x8}");
        return cause switch
        {
            InstructionAccessFault => string.Create(CultureInfo.InvariantCulture,
                $"an instruction access fault: nothing to run at 0x{value:x8} (mcause 1)"),
            IllegalInstruction => string.Create(CultureInfo.InvariantCulture,
                $"an illegal instruction 0x{value:x8} {at} (mcause 2)"),
            LoadAccessFault => string.Create(CultureInfo.InvariantCulture,
                $"a load access fault: nothing at 0x{value:x8}, read {at} (mcause 5)"),
            StoreAddressMisaligned => string.Create(CultureInfo.InvariantCulture,
                $"a misaligned atomic access of 0x{value:x8} {at} (mcause 6)"),
            StoreAccessFault => string.Create(CultureInfo.InvariantCulture,
                $"a store access fault: nothing at 0x{value:x8}, written {at} (mcause 7)"),
            EcallFromUser or EcallFromMachine => string.Create(CultureInfo.InvariantCulture,
                $"an ecall {at} (mcause {cause})"),
            _ => string.Create(CultureInfo.InvariantCulture, $"exception {cause} {at}"),
        };
    }

    private uint Load(uint address, int size, ref int cycles)
    {
        _reserved = false;
        try
        {
            cycles += _bus.AccessCycles(address);
            if ((address & (uint)(size - 1)) == 0)
            {
                return _bus.Read(address, size);
            }

            // misaligned: the core splits it into byte accesses
            cycles++;
            uint value = 0;
            for (var b = 0; b < size; b++)
            {
                value |= _bus.Read(address + (uint)b, 1) << (8 * b);
            }

            return value;
        }
        catch (RiscVAccessFault fault)
        {
            throw new Trap(LoadAccessFault, fault.Address);
        }
    }

    private void Store(uint address, int size, uint value, ref int cycles)
    {
        _reserved = false;
        try
        {
            cycles += _bus.AccessCycles(address);
            if ((address & (uint)(size - 1)) == 0)
            {
                _bus.Write(address, size, value);
                return;
            }

            cycles++;
            for (var b = 0; b < size; b++)
            {
                _bus.Write(address + (uint)b, 1, (value >> (8 * b)) & 0xFF);
            }
        }
        catch (RiscVAccessFault fault)
        {
            throw new Trap(StoreAccessFault, fault.Address);
        }
    }

    private int ExecuteCompressed(uint pc, uint i)
    {
        var next = pc + 2;
        var cycles = 1;
        var rdPrime = 8 + (int)((i >> 2) & 7);
        var rs1Prime = 8 + (int)((i >> 7) & 7);
        var rd = (int)(i >> 7) & 31;
        var rs2 = (int)(i >> 2) & 31;
        var funct3 = i >> 13;
        switch (i & 3)
        {
            case 0:
                switch (funct3)
                {
                    case 0: // c.addi4spn
                        {
                            var immediate = ((i >> 7) & 0x30) | ((i >> 1) & 0x3C0) | ((i >> 4) & 0x4) | ((i >> 2) & 0x8);
                            if (immediate == 0)
                            {
                                throw Illegal(i); // the all-zero instruction, and the reserved nzuimm 0
                            }

                            X[rdPrime] = X[2] + immediate;
                            break;
                        }

                    case 2: // c.lw
                        X[rdPrime] = Load(X[rs1Prime] + CompressedWordOffset(i), 4, ref cycles);
                        break;
                    case 6: // c.sw
                        Store(X[rs1Prime] + CompressedWordOffset(i), 4, X[rdPrime], ref cycles);
                        break;
                    case 4: // Zcb: c.lbu, c.lhu, c.lh, c.sb, c.sh
                        {
                            var byteOffset = ((i >> 4) & 2) | ((i >> 6) & 1);
                            var halfOffset = (i >> 4) & 2;
                            var address = X[rs1Prime];
                            switch ((i >> 10) & 7)
                            {
                                case 0:
                                    X[rdPrime] = Load(address + byteOffset, 1, ref cycles);
                                    break;
                                case 1:
                                    X[rdPrime] = (i & 0x40) == 0
                                        ? Load(address + halfOffset, 2, ref cycles)
                                        : (uint)(short)Load(address + halfOffset, 2, ref cycles);
                                    break;
                                case 2:
                                    Store(address + byteOffset, 1, X[rdPrime], ref cycles);
                                    break;
                                case 3 when (i & 0x40) == 0:
                                    Store(address + halfOffset, 2, X[rdPrime], ref cycles);
                                    break;
                                default:
                                    throw Illegal(i);
                            }

                            break;
                        }

                    default:
                        throw Illegal(i); // c.fld, c.flw, c.fsd, c.fsw: the core has no floating point
                }

                break;
            case 1:
                switch (funct3)
                {
                    case 0: // c.addi, c.nop
                        X[rd] += (uint)CompressedImmediate(i);
                        break;
                    case 1: // c.jal
                        X[1] = next;
                        next = pc + (uint)CompressedJumpOffset(i);
                        cycles = 2;
                        break;
                    case 2: // c.li
                        X[rd] = (uint)CompressedImmediate(i);
                        break;
                    case 3:
                        if (rd == 2)
                        {
                            // c.addi16sp
                            var immediate = ((i >> 3) & 0x200) | ((i >> 2) & 0x10) | ((i << 1) & 0x40) | ((i << 4) & 0x180) |
                                ((i << 3) & 0x20);
                            if (immediate == 0)
                            {
                                throw Illegal(i);
                            }

                            X[2] += (uint)((int)(immediate << 22) >> 22);
                        }
                        else
                        {
                            // c.lui
                            var immediate = CompressedImmediate(i);
                            if (immediate == 0)
                            {
                                throw Illegal(i);
                            }

                            X[rd] = (uint)(immediate << 12);
                        }

                        break;
                    case 4:
                        CompressedArithmetic(i, rs1Prime, rdPrime);
                        break;
                    case 5: // c.j
                        next = pc + (uint)CompressedJumpOffset(i);
                        cycles = 2;
                        break;
                    default: // c.beqz, c.bnez
                        if ((X[rs1Prime] == 0) == (funct3 == 6))
                        {
                            var offset = ((i >> 4) & 0x100) | ((i >> 7) & 0x18) | ((i << 1) & 0xC0) | ((i >> 2) & 0x6) |
                                ((i << 3) & 0x20);
                            var signed = (int)(offset << 23) >> 23;
                            next = pc + (uint)signed;
                            cycles = 2;
                            if (signed < 0)
                            {
                                _reserved = false;
                            }
                        }

                        break;
                }

                break;
            default:
                switch (funct3)
                {
                    case 0: // c.slli
                        if ((i & 0x1000) != 0)
                        {
                            throw Illegal(i); // shamt[5] is reserved on RV32
                        }

                        X[rd] <<= (int)((i >> 2) & 31);
                        break;
                    case 2: // c.lwsp
                        if (rd == 0)
                        {
                            throw Illegal(i);
                        }

                        X[rd] = Load(X[2] + (((i >> 7) & 0x20) | ((i >> 2) & 0x1C) | ((i << 4) & 0xC0)), 4, ref cycles);
                        break;
                    case 4:
                        if ((i & 0x1000) == 0)
                        {
                            if (rs2 == 0)
                            {
                                // c.jr
                                if (rd == 0)
                                {
                                    throw Illegal(i);
                                }

                                next = X[rd] & ~1u;
                                cycles = 3;
                                _reserved = false;
                            }
                            else
                            {
                                X[rd] = X[rs2]; // c.mv
                            }
                        }
                        else if (rs2 == 0)
                        {
                            if (rd == 0)
                            {
                                BreakHit = true; // c.ebreak
                                return 1;
                            }

                            // c.jalr
                            var target = X[rd] & ~1u;
                            X[1] = next;
                            next = target;
                            cycles = 3;
                            _reserved = false;
                        }
                        else
                        {
                            X[rd] += X[rs2]; // c.add
                        }

                        break;
                    case 5:
                        return CompressedMacro(pc, i);
                    case 6: // c.swsp
                        Store(X[2] + (((i >> 7) & 0x3C) | ((i >> 1) & 0xC0)), 4, X[rs2], ref cycles);
                        break;
                    default:
                        throw Illegal(i); // c.fldsp, c.flwsp, c.fswsp
                }

                break;
        }

        Pc = next;
        return cycles;
    }

    /// <summary>c.srli, c.srai, c.andi, c.sub, c.xor, c.or, c.and; Zcb's c.mul, c.zext.b and c.not.</summary>
    private void CompressedArithmetic(uint i, int rd, int rs2)
    {
        switch ((i >> 10) & 3)
        {
            case 0:
            case 1:
                if ((i & 0x1000) != 0)
                {
                    throw Illegal(i);
                }

                var shamt = (int)((i >> 2) & 31);
                X[rd] = (i & 0x400) == 0 ? X[rd] >> shamt : (uint)((int)X[rd] >> shamt);
                return;
            case 2:
                X[rd] &= (uint)CompressedImmediate(i);
                return;
        }

        var funct2 = (i >> 5) & 3;
        if ((i & 0x1000) == 0)
        {
            X[rd] = funct2 switch
            {
                0 => X[rd] - X[rs2],
                1 => X[rd] ^ X[rs2],
                2 => X[rd] | X[rs2],
                _ => X[rd] & X[rs2],
            };
            return;
        }

        switch (funct2)
        {
            case 2: // c.mul
                X[rd] *= X[rs2];
                return;
            case 3:
                // c.sext.b, c.zext.h and c.sext.h need Zbb, which the C5 does not have; c.zext.w is RV64's
                X[rd] = ((i >> 2) & 7) switch
                {
                    0 => X[rd] & 0xFF,
                    5 => ~X[rd],
                    _ => throw Illegal(i),
                };
                return;
            default:
                throw Illegal(i); // c.subw, c.addw: RV64 only
        }
    }

    /// <summary>Zcmp's cm.push, cm.pop, cm.popret, cm.popretz, cm.mvsa01, cm.mva01s; Zcmt's cm.jt and cm.jalt.</summary>
    private int CompressedMacro(uint pc, uint i)
    {
        switch ((i >> 10) & 7)
        {
            case 0: // cm.jt, cm.jalt
                {
                    var index = (i >> 2) & 0xFF;
                    uint target;
                    try
                    {
                        target = _bus.Read(Jvt + 4 * index, 4);
                    }
                    catch (RiscVAccessFault fault)
                    {
                        throw new Trap(InstructionAccessFault, fault.Address);
                    }

                    if (index >= 32)
                    {
                        X[1] = pc + 2;
                    }

                    _reserved = false;
                    Pc = target & ~1u;
                    return 3;
                }

            case 3:
                {
                    var r1 = SRegisters[(i >> 7) & 7];
                    var r2 = SRegisters[(i >> 2) & 7];
                    switch ((i >> 5) & 3)
                    {
                        case 1: // cm.mvsa01
                            if (r1 == r2)
                            {
                                throw Illegal(i);
                            }

                            X[r1] = X[10];
                            X[r2] = X[11];
                            break;
                        case 3: // cm.mva01s
                            X[10] = X[r1];
                            X[11] = X[r2];
                            break;
                        default:
                            throw Illegal(i);
                    }

                    Pc = pc + 2;
                    return 1;
                }

            case 6 or 7:
                return PushPop(pc, i);
            default:
                throw Illegal(i);
        }
    }

    /// <summary>
    /// cm.push, cm.pop, cm.popretz, cm.popret (Zcmp 1.0): ra and s0 to sN, the highest register at the highest address
    /// just below the stack pointer, which moves by the stack adjustment (16 to 64 for the registers, and 16 for each
    /// spimm).
    /// </summary>
    private int PushPop(uint pc, uint i)
    {
        var kind = (i >> 8) & 0x1F;
        var rlist = (int)(i >> 4) & 0xF;
        if (rlist < 4 || kind is not (0x18 or 0x1A or 0x1C or 0x1E))
        {
            throw Illegal(i);
        }

        var registers = PushPopRegisters[rlist];
        var adjustment = (uint)(((registers.Length * 4 + 15) & ~15) + 16 * (int)((i >> 2) & 3));
        var cycles = 1;
        var sp = X[2];
        if (kind == 0x18)
        {
            var address = sp;
            for (var r = registers.Length - 1; r >= 0; r--)
            {
                address -= 4;
                Store(address, 4, X[registers[r]], ref cycles);
                cycles++;
            }

            X[2] = sp - adjustment;
            Pc = pc + 2;
            return cycles;
        }

        var from = sp + adjustment;
        for (var r = registers.Length - 1; r >= 0; r--)
        {
            from -= 4;
            X[registers[r]] = Load(from, 4, ref cycles);
            cycles++;
        }

        X[2] = sp + adjustment;
        if (kind == 0x1C)
        {
            X[10] = 0;
        }

        if (kind is 0x1C or 0x1E)
        {
            Pc = X[1] & ~1u;
            _reserved = false;
            return cycles + 2;
        }

        Pc = pc + 2;
        return cycles;
    }

    private static uint CompressedWordOffset(uint i) => ((i >> 7) & 0x38) | ((i >> 4) & 0x4) | ((i << 1) & 0x40);

    private static int CompressedImmediate(uint i) => (int)((((i >> 7) & 0x20) | ((i >> 2) & 0x1F)) << 26) >> 26;

    private static int CompressedJumpOffset(uint i)
    {
        var offset = ((i >> 1) & 0x800) | ((i >> 7) & 0x10) | ((i >> 1) & 0x300) | ((i << 2) & 0x400) | ((i >> 1) & 0x40) |
            ((i << 1) & 0x80) | ((i >> 2) & 0xE) | ((i << 3) & 0x20);
        return (int)(offset << 20) >> 20;
    }

    private static int BImmediate(uint i) =>
        ((int)i >> 31 << 12) | (int)(((i >> 7) & 1) << 11) | (int)(((i >> 25) & 0x3F) << 5) | (int)(((i >> 8) & 0xF) << 1);

    private static int JImmediate(uint i) =>
        ((int)i >> 31 << 20) | (int)(i & 0xFF000) | (int)(((i >> 20) & 1) << 11) | (int)(((i >> 21) & 0x3FF) << 1);

    private static Trap Illegal(uint instruction) => new(IllegalInstruction, instruction);

    /// <summary>An exception an instruction raises: its cause and mtval.</summary>
    private sealed class Trap(int cause, uint value) : Exception
    {
        public int Cause { get; } = cause;

        public uint Value { get; } = value;
    }
}
