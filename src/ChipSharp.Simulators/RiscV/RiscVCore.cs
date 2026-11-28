namespace ChipSharp.Simulators.RiscV;

/// <summary>How a <see cref="RiscVCpu"/> takes interrupts and exceptions, and which CSRs it has.</summary>
public enum RiscVTrapModel
{
    /// <summary>
    /// The ESP32-C5's: mtvec hardwired to CLIC mode (3), the CLIC's levels, mnxti, mtvt and the scratch swaps, mcause
    /// with MPIL, the counters (<see cref="RiscVCpu"/>).
    /// </summary>
    Clic,

    /// <summary>
    /// WCH's QingKe V2 (QingKeV2 Processor Manual V1.1): its PFIC (<see cref="IQingKeInterrupts"/>), mtvec's two modes
    /// (a unified entry or a vector table, of jump instructions or of addresses), mstatus's MPOP and MPPOP, INTSYSCR's
    /// HPE (hardware prologue/epilogue) and nesting, and only the CSRs chapter 7 lists: no counters, no mtval, no user mode.
    /// </summary>
    QingKeV2,
}

/// <summary>
/// What a <see cref="RiscVCpu"/> is: its base ISA (RV32I, or RV32E with x0-x15), its extensions and its trap model.
/// <see cref="Esp32c5"/> is the ESP32-C5's HP CPU, <see cref="QingKeV2A"/> the WCH CH32V003's core.
/// </summary>
public sealed record RiscVCore
{
    /// <summary>
    /// The ESP32-C5's HP CPU: RV32IMAC, Zicsr, Zifencei, Zcb, Zcmp and Zcmt; misaligned loads and stores split in two
    /// accesses; the CLIC.
    /// </summary>
    public static RiscVCore Esp32c5 { get; } = new();

    /// <summary>
    /// WCH's QingKe V2A (the CH32V003's; QingKeV2 Processor Manual V1.1, chapter 1): RV32EC with Zicsr and Zifencei
    /// (fence.i, which WCH's __disable_irq runs), and WCH's XW (LLVM's Xwchc: qk.c.lbu, qk.c.lhu, qk.c.sb, qk.c.sh and
    /// their sp forms); no M (mul and div are illegal: the compiler calls __mulsi3 and its kin), no A; a misaligned
    /// load or store raises its exception (4, 6), as the manual's Table 2-1 has them; the QingKe V2 trap model.
    /// </summary>
    public static RiscVCore QingKeV2A { get; } = new()
    {
        Embedded = true,
        Multiply = false,
        Atomic = false,
        Zcb = false,
        Zcmp = false,
        Zcmt = false,
        Xwchc = true,
        MisalignedAccessTraps = true,
        Traps = RiscVTrapModel.QingKeV2,
    };

    /// <summary>RV32E: 16 registers, x0-x15; an instruction that names x16-x31 is illegal.</summary>
    public bool Embedded { get; init; }

    /// <summary>M: mul, mulh, mulhsu, mulhu, div, divu, rem, remu.</summary>
    public bool Multiply { get; init; } = true;

    /// <summary>A: lr.w, sc.w and the AMOs.</summary>
    public bool Atomic { get; init; } = true;

    /// <summary>Zcb: c.lbu, c.lhu, c.lh, c.sb, c.sh, c.zext.b, c.not, c.mul (with M).</summary>
    public bool Zcb { get; init; } = true;

    /// <summary>Zcmp: cm.push, cm.pop, cm.popret, cm.popretz, cm.mvsa01, cm.mva01s.</summary>
    public bool Zcmp { get; init; } = true;

    /// <summary>Zcmt: cm.jt, cm.jalt.</summary>
    public bool Zcmt { get; init; } = true;

    /// <summary>
    /// WCH's XW (Xwchc), in the slots of c.fld, c.fsd, c.fldsp, c.fsdsp and Zcb's: qk.c.lbu and qk.c.sb (quadrant 0,
    /// funct3 001 and 101), qk.c.lhu and qk.c.sh (quadrant 2, 001 and 101), qk.c.lbusp, qk.c.sbsp, qk.c.lhusp and
    /// qk.c.shsp (quadrant 0, funct3 100), with LLVM's encodings; the loads zero-extend.
    /// </summary>
    public bool Xwchc { get; init; }

    /// <summary>Whether a misaligned load or store raises its exception, instead of being done in byte accesses.</summary>
    public bool MisalignedAccessTraps { get; init; }

    /// <summary>How the core takes traps, and its CSRs.</summary>
    public RiscVTrapModel Traps { get; init; }
}

/// <summary>
/// What a QingKe V2 core asks its PFIC (Programmable Fast Interrupt Controller) when it takes a trap and returns from
/// one (QingKeV2 Processor Manual, chapter 3): which traps are active (PFIC_IACTR, PFIC_GISR's nesting) and the VTF
/// channels.
/// </summary>
public interface IQingKeInterrupts
{
    /// <summary>
    /// The core takes trap <paramref name="number"/> (an interrupt's, or 3, EXC, for an exception): it is active from
    /// now. Returns how many traps are active with it: the nesting level, 1 or 2.
    /// </summary>
    int Enter(int number);

    /// <summary>mret: the trap entered last is active no more.</summary>
    void Return();

    /// <summary>The address a VTF channel (PFIC_VTFIDR, PFIC_VTFADDRRx) gives interrupt <paramref name="number"/>, or null.</summary>
    uint? FreeVector(int number);
}
