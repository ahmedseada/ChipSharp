using System.Globalization;

namespace ChipSharp.Simulators.Pdk14;

/// <summary>The I/O space of a PDK14 chip, as its CPU reaches it: every register but FLAG (0x00) and SP (0x02).</summary>
public interface IPdk14Io
{
    /// <summary>Reads I/O register <paramref name="address"/> (0x00-0x3F).</summary>
    byte Read(int address);

    /// <summary>Writes I/O register <paramref name="address"/> (0x00-0x3F).</summary>
    void Write(int address, byte value);

    /// <summary>Timer16's counter, which ldt16 reads and stt16 writes.</summary>
    ushort Timer16 { get; set; }

    /// <summary>wdreset: the watchdog starts again.</summary>
    void ResetWatchdog();
}

/// <summary>How a PDK14 CPU sleeps.</summary>
public enum Pdk14Sleep
{
    /// <summary>It runs.</summary>
    None,

    /// <summary>stopsys: the clocks stop, until a pin changes.</summary>
    StopSys,

    /// <summary>stopexe: the CPU stops, the clocks and timers run, until an interrupt request or a pin change.</summary>
    StopExe,
}

/// <summary>
/// A Padauk PDK14 core (the PFS154's, and the PMS154's, PFS172's...): 14-bit instruction words, an 11-bit program counter
/// (2K words), the accumulator A, FLAG (Z, C, AC, OV in bits 0-3) and SP in the I/O space, the stack in RAM (it grows
/// up: call stores the return address at [SP], [SP+1] and adds 2), and every instruction of the Free-PDK PDK14 table
/// (https://free-pdk.github.io/instruction-sets/PDK14) but mul, which the PFS154 does not have. Cycles as the Padauk
/// datasheets give them: 1, 2 for goto, call, ret, reti, pcadd, idxm, ldsptl/ldspth, and a skip instruction that skips.
/// The interrupt (<see cref="Interrupt"/>) pushes the PC as a call does, jumps to word 0x10 and turns the global
/// interrupt enable off; reti turns it back on. An instruction the core does not have, or a RAM address past its end
/// (idxm, the stack), throws <see cref="ProgramFault"/>.
/// </summary>
public sealed class Pdk14Cpu
{
    /// <summary>FLAG's bits.</summary>
    public const byte Z = 1, C = 2, AC = 4, OV = 8;

    /// <summary>The I/O addresses of FLAG and SP, which the core keeps itself.</summary>
    public const int FlagAddress = 0x00, SpAddress = 0x02;

    /// <summary>The word address the interrupt jumps to.</summary>
    public const int InterruptVector = 0x10;

    private readonly IPdk14Io _io;
    private readonly int _pcMask;

    /// <summary>A core with <paramref name="rom"/> (2^n words) and <paramref name="ram"/>, its I/O space <paramref name="io"/>.</summary>
    public Pdk14Cpu(ushort[] rom, byte[] ram, IPdk14Io io)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentNullException.ThrowIfNull(ram);
        ArgumentNullException.ThrowIfNull(io);
        if (rom.Length == 0 || (rom.Length & (rom.Length - 1)) != 0 || rom.Length > 2048)
        {
            throw new ArgumentException("A PDK14 core has 2^n words of program memory, at most 2048.", nameof(rom));
        }

        Rom = rom;
        Ram = ram;
        _io = io;
        _pcMask = rom.Length - 1;
    }

    /// <summary>Program memory, 14-bit words.</summary>
    public ushort[] Rom { get; }

    /// <summary>The RAM.</summary>
    public byte[] Ram { get; }

    /// <summary>The accumulator.</summary>
    public byte A { get; set; }

    /// <summary>FLAG: Z, C, AC and OV in bits 0-3.</summary>
    public byte Flags { get; set; }

    /// <summary>The stack pointer: the RAM address the next call stores its return address at.</summary>
    public byte Sp { get; set; }

    /// <summary>The program counter, a word address.</summary>
    public int Pc { get; set; }

    /// <summary>The global interrupt enable (engint, disgint).</summary>
    public bool InterruptsEnabled { get; set; }

    /// <summary>How the CPU sleeps (stopsys, stopexe), or <see cref="Pdk14Sleep.None"/>.</summary>
    public Pdk14Sleep Sleep { get; set; }

    /// <summary>Whether the program ran reset (the whole chip resets).</summary>
    public bool ResetRequested { get; set; }

    private bool Carry => (Flags & C) != 0;

    /// <summary>Runs the instruction at <see cref="Pc"/>; returns its cycles.</summary>
    public int Step()
    {
        var at = Pc;
        int op = Rom[at];
        Pc = (at + 1) & _pcMask;
        switch (op >> 8)
        {
            case 0x00:
                return Miscellaneous(op, at);
            case 0x01 when (op & 0xC0) == 0x80: // mov io, a
                WriteIo(op & 0x3F, A);
                return 1;
            case 0x01 when (op & 0xC0) == 0xC0: // mov a, io
                A = ReadIo(op & 0x3F);
                SetZero(A);
                return 1;
            case 0x02: // ret k
                A = (byte)op;
                Return();
                return 2;
            case 0x03:
                var word = op & 0x7E;
                switch (op & 0x81)
                {
                    case 0x00: // stt16 m
                        _io.Timer16 = (ushort)(Load(word) | (Load(word + 1) << 8));
                        return 1;
                    case 0x01: // ldt16 m
                        var timer = _io.Timer16;
                        Store(word, (byte)timer);
                        Store(word + 1, (byte)(timer >> 8));
                        return 1;
                    case 0x80: // idxm m, a: [m] <- a
                        Store(Load(word), A);
                        return 2;
                    default: // idxm a, m
                        A = Load(Load(word));
                        return 2;
                }

            case 0x04 or 0x05: // swapc io.n
                var io = op & 0x3F;
                var mask = 1 << ((op >> 6) & 7);
                var value = ReadIo(io);
                var bitWasSet = (value & mask) != 0;
                WriteIo(io, (byte)(Carry ? value | mask : value & ~mask));
                SetFlag(C, bitWasSet);
                return 1;
            case >= 0x06 and <= 0x17:
                return Memory(op, at);
            case >= 0x18 and <= 0x1F: // t0sn, t1sn, set0, set1 io.n
                return BitOperation(op, ReadIo(op & 0x3F), value => WriteIo(op & 0x3F, value));
            case >= 0x20 and <= 0x27: // t0sn, t1sn, set0, set1 m.n (m 0-63)
                return BitOperation(op, Load(op & 0x3F), value => Store(op & 0x3F, value));
            case >= 0x28 and <= 0x2F:
                return Immediate(op);
            case >= 0x30 and <= 0x37: // goto k
                Pc = op & 0x7FF & _pcMask;
                return 2;
            default: // call k
                Push((byte)Pc, (byte)(Pc >> 8));
                Pc = op & 0x7FF & _pcMask;
                return 2;
        }
    }

    /// <summary>Takes the interrupt: pushes the PC as a call would, jumps to word 0x10, turns interrupts off.</summary>
    public int Interrupt()
    {
        Push((byte)Pc, (byte)(Pc >> 8));
        Pc = InterruptVector & _pcMask;
        InterruptsEnabled = false;
        Sleep = Pdk14Sleep.None;
        return 2;
    }

    private int Miscellaneous(int op, int at)
    {
        switch (op)
        {
            case 0x0000: // nop
                return 1;
            case 0x0006 or 0x0007: // ldsptl, ldspth: a byte of the program word whose address the word at [SP] holds
                var address = Load(Sp) | (Load(Sp + 1) << 8);
                var code = Rom[address & _pcMask];
                A = (byte)(op == 0x0006 ? code : code >> 8);
                return 2;
            case 0x0060: // addc a
                A = Add(A, 0, Carry);
                return 1;
            case 0x0061: // subc a
                A = Subtract(A, 0, Carry);
                return 1;
            case 0x0062: // izsn a
                A = Add(A, 1, false);
                return SkipIf(A == 0);
            case 0x0063: // dzsn a
                A = Subtract(A, 1, false);
                return SkipIf(A == 0);
            case 0x0067: // pcadd a: to the address of pcadd + A
                Pc = (at + A) & _pcMask;
                return 2;
            case 0x0068: // not a
                A = (byte)~A;
                SetZero(A);
                return 1;
            case 0x0069: // neg a
                A = (byte)-A;
                SetZero(A);
                return 1;
            case 0x006A: // sr a
                SetFlag(C, (A & 1) != 0);
                A >>= 1;
                return 1;
            case 0x006B: // sl a
                SetFlag(C, (A & 0x80) != 0);
                A <<= 1;
                return 1;
            case 0x006C: // src a
                var right = (byte)((A >> 1) | (Carry ? 0x80 : 0));
                SetFlag(C, (A & 1) != 0);
                A = right;
                return 1;
            case 0x006D: // slc a
                var left = (byte)((A << 1) | (Carry ? 1 : 0));
                SetFlag(C, (A & 0x80) != 0);
                A = left;
                return 1;
            case 0x006E: // swap a
                A = (byte)((A << 4) | (A >> 4));
                return 1;
            case 0x0070: // wdreset
                _io.ResetWatchdog();
                return 1;
            case 0x0072: // pushaf: [SP] <- A, [SP+1] <- FLAG
                Push(A, Flags);
                return 1;
            case 0x0073: // popaf
                Sp -= 2;
                A = Load(Sp);
                Flags = (byte)(Load(Sp + 1) & 0x0F);
                return 1;
            case 0x0075: // reset
                ResetRequested = true;
                return 1;
            case 0x0076: // stopsys
                Sleep = Pdk14Sleep.StopSys;
                return 1;
            case 0x0077: // stopexe
                Sleep = Pdk14Sleep.StopExe;
                return 1;
            case 0x0078: // engint
                InterruptsEnabled = true;
                return 1;
            case 0x0079: // disgint
                InterruptsEnabled = false;
                return 1;
            case 0x007A: // ret
                Return();
                return 2;
            case 0x007B: // reti
                Return();
                InterruptsEnabled = true;
                return 2;
            case >= 0x00C0 and <= 0x00FF: // xor io, a
                WriteIo(op & 0x3F, (byte)(ReadIo(op & 0x3F) ^ A));
                return 1;
            default:
                throw Unknown(op, at);
        }
    }

    private int Memory(int op, int at)
    {
        var m = op & 0x7F;
        switch (op >> 7)
        {
            case 0x0C: // comp a, m
                Subtract(A, Load(m), false);
                return 1;
            case 0x0D: // comp m, a
                Subtract(Load(m), A, false);
                return 1;
            case 0x0E: // nadd a, m: a <- m + -a
                A = Add(Load(m), (byte)-A, false);
                return 1;
            case 0x0F: // nadd m, a: m <- -m + a
                Store(m, Add((byte)-Load(m), A, false));
                return 1;
            case 0x10: // add m, a
                Store(m, Add(Load(m), A, false));
                return 1;
            case 0x11: // sub m, a: m <- m - a
                Store(m, Subtract(Load(m), A, false));
                return 1;
            case 0x12: // addc m, a
                Store(m, Add(Load(m), A, Carry));
                return 1;
            case 0x13: // subc m, a
                Store(m, Subtract(Load(m), A, Carry));
                return 1;
            case 0x14: // and m, a
                Store(m, Logic((byte)(Load(m) & A)));
                return 1;
            case 0x15: // or m, a
                Store(m, Logic((byte)(Load(m) | A)));
                return 1;
            case 0x16: // xor m, a
                Store(m, Logic((byte)(Load(m) ^ A)));
                return 1;
            case 0x17: // mov m, a
                Store(m, A);
                return 1;
            case 0x18: // add a, m
                A = Add(A, Load(m), false);
                return 1;
            case 0x19: // sub a, m
                A = Subtract(A, Load(m), false);
                return 1;
            case 0x1A: // addc a, m
                A = Add(A, Load(m), Carry);
                return 1;
            case 0x1B: // subc a, m
                A = Subtract(A, Load(m), Carry);
                return 1;
            case 0x1C: // and a, m
                A = Logic((byte)(A & Load(m)));
                return 1;
            case 0x1D: // or a, m
                A = Logic((byte)(A | Load(m)));
                return 1;
            case 0x1E: // xor a, m
                A = Logic((byte)(A ^ Load(m)));
                return 1;
            case 0x1F: // mov a, m
                A = Logic(Load(m));
                return 1;
            case 0x20: // addc m
                Store(m, Add(Load(m), 0, Carry));
                return 1;
            case 0x21: // subc m
                Store(m, Subtract(Load(m), 0, Carry));
                return 1;
            case 0x22: // izsn m
                var increased = Add(Load(m), 1, false);
                Store(m, increased);
                return SkipIf(increased == 0);
            case 0x23: // dzsn m
                var decreased = Subtract(Load(m), 1, false);
                Store(m, decreased);
                return SkipIf(decreased == 0);
            case 0x24: // inc m
                Store(m, Add(Load(m), 1, false));
                return 1;
            case 0x25: // dec m
                Store(m, Subtract(Load(m), 1, false));
                return 1;
            case 0x26: // clear m
                Store(m, 0);
                return 1;
            case 0x27: // xch m
                var old = Load(m);
                Store(m, A);
                A = old;
                return 1;
            case 0x28: // not m
                Store(m, Logic((byte)~Load(m)));
                return 1;
            case 0x29: // neg m
                Store(m, Logic((byte)-Load(m)));
                return 1;
            case 0x2A: // sr m
                var sr = Load(m);
                SetFlag(C, (sr & 1) != 0);
                Store(m, (byte)(sr >> 1));
                return 1;
            case 0x2B: // sl m
                var sl = Load(m);
                SetFlag(C, (sl & 0x80) != 0);
                Store(m, (byte)(sl << 1));
                return 1;
            case 0x2C: // src m
                var src = Load(m);
                Store(m, (byte)((src >> 1) | (Carry ? 0x80 : 0)));
                SetFlag(C, (src & 1) != 0);
                return 1;
            case 0x2D: // slc m
                var slc = Load(m);
                Store(m, (byte)((slc << 1) | (Carry ? 1 : 0)));
                SetFlag(C, (slc & 0x80) != 0);
                return 1;
            case 0x2E: // ceqsn a, m
                var equal = Load(m);
                Subtract(A, equal, false);
                return SkipIf(A == equal);
            case 0x2F: // cneqsn a, m
                var other = Load(m);
                Subtract(A, other, false);
                return SkipIf(A != other);
            default:
                throw Unknown(op, at);
        }
    }

    private int BitOperation(int op, byte value, Action<byte> store)
    {
        var mask = 1 << ((op >> 6) & 7);
        switch ((op >> 9) & 3)
        {
            case 0: // t0sn
                return SkipIf((value & mask) == 0);
            case 1: // t1sn
                return SkipIf((value & mask) != 0);
            case 2: // set0
                store((byte)(value & ~mask));
                return 1;
            default: // set1
                store((byte)(value | mask));
                return 1;
        }
    }

    private int Immediate(int op)
    {
        var k = (byte)op;
        switch (op >> 8)
        {
            case 0x28: // add a, k
                A = Add(A, k, false);
                return 1;
            case 0x29: // sub a, k
                A = Subtract(A, k, false);
                return 1;
            case 0x2A: // ceqsn a, k
                Subtract(A, k, false);
                return SkipIf(A == k);
            case 0x2B: // cneqsn a, k
                Subtract(A, k, false);
                return SkipIf(A != k);
            case 0x2C: // and a, k
                A = Logic((byte)(A & k));
                return 1;
            case 0x2D: // or a, k
                A = Logic((byte)(A | k));
                return 1;
            case 0x2E: // xor a, k
                A = Logic((byte)(A ^ k));
                return 1;
            default: // mov a, k
                A = k;
                return 1;
        }
    }

    /// <summary>a + b + carry, setting Z, C (carry out of bit 7), AC (out of bit 3) and OV.</summary>
    private byte Add(byte a, byte b, bool carry)
    {
        var c = carry ? 1 : 0;
        var result = a + b + c;
        var carryOut = result > 0xFF;
        var carryInto7 = (a & 0x7F) + (b & 0x7F) + c > 0x7F;
        Flags = (byte)(((result & 0xFF) == 0 ? Z : 0) | (carryOut ? C : 0) | ((a & 0xF) + (b & 0xF) + c > 0xF ? AC : 0) |
            (carryOut != carryInto7 ? OV : 0));
        return (byte)result;
    }

    /// <summary>a - b - borrow, setting Z, C (a borrow), AC (a borrow from bit 4) and OV.</summary>
    private byte Subtract(byte a, byte b, bool borrow)
    {
        var c = borrow ? 1 : 0;
        var result = a - b - c;
        var borrowOut = result < 0;
        var borrowInto7 = (a & 0x7F) - (b & 0x7F) - c < 0;
        Flags = (byte)(((result & 0xFF) == 0 ? Z : 0) | (borrowOut ? C : 0) | ((a & 0xF) - (b & 0xF) - c < 0 ? AC : 0) |
            (borrowOut != borrowInto7 ? OV : 0));
        return (byte)result;
    }

    private byte Logic(byte value)
    {
        SetZero(value);
        return value;
    }

    private void SetZero(byte value) => SetFlag(Z, value == 0);

    private void SetFlag(byte flag, bool set) => Flags = (byte)(set ? Flags | flag : Flags & ~flag);

    private int SkipIf(bool skip)
    {
        if (!skip)
        {
            return 1;
        }

        Pc = (Pc + 1) & _pcMask;
        return 2;
    }

    private void Push(byte low, byte high)
    {
        Store(Sp, low);
        Store(Sp + 1, high);
        Sp += 2;
    }

    private void Return()
    {
        Sp -= 2;
        Pc = (Load(Sp) | (Load(Sp + 1) << 8)) & _pcMask;
    }

    private byte ReadIo(int address) => address switch
    {
        FlagAddress => Flags,
        SpAddress => Sp,
        _ => _io.Read(address),
    };

    private void WriteIo(int address, byte value)
    {
        switch (address)
        {
            case FlagAddress:
                Flags = (byte)(value & 0x0F);
                break;
            case SpAddress:
                Sp = value;
                break;
            default:
                _io.Write(address, value);
                break;
        }
    }

    private byte Load(int address) => address < Ram.Length ? Ram[address] : throw OutsideRam(address);

    private void Store(int address, byte value)
    {
        if (address >= Ram.Length)
        {
            throw OutsideRam(address);
        }

        Ram[address] = value;
    }

    private ProgramFault OutsideRam(int address) => new(string.Create(CultureInfo.InvariantCulture,
        $"the program used RAM address 0x{address:x2}, past the end of the {Ram.Length} bytes of RAM, at pc 0x{Pc:x3} " +
        $"(SP 0x{Sp:x2}{(address >= Sp ? ": the stack has grown past the end of RAM" : "")})"));

    private static ProgramFault Unknown(int op, int at) => new(string.Create(CultureInfo.InvariantCulture,
        $"the program ran an instruction the PDK14 core does not have, 0x{op:x4}, at pc 0x{at:x3}"));
}
