using System.Globalization;

namespace ChipSharp.Simulators.Stm8;

/// <summary>The memory space of an STM8 chip, as its CPU reaches it: 24-bit addresses, a byte at a time.</summary>
public interface IStm8Bus
{
    /// <summary>Reads the byte at <paramref name="address"/>.</summary>
    byte Read(int address);

    /// <summary>Writes the byte at <paramref name="address"/>.</summary>
    void Write(int address, byte value);
}

/// <summary>How an STM8 CPU sleeps.</summary>
public enum Stm8Sleep
{
    /// <summary>It runs.</summary>
    None,

    /// <summary>halt: the CPU and the peripherals' clocks stop, until an external interrupt.</summary>
    Halt,

    /// <summary>wfi (or wfe): the CPU stops, the peripherals run, until an interrupt.</summary>
    Wait,
}

/// <summary>
/// The STM8 core, as ST's programming manual PM0044 describes it: A, X, Y, SP, a 24-bit PC and CC (V, I1, H, I0, N, Z,
/// C), every instruction with every addressing mode (the prefixes 0x72, 0x90, 0x91 and 0x92 included: the pointers
/// [shortptr.w], [longptr.w], [longptr.e], indexed or not), and the cycles PM0044 gives each (a conditional jump 1 more
/// when taken; DIV and DIVW by the quotient's size, 2 to 17), not the pipeline's stalls. The stack grows down: a push
/// stores at SP and then decrements it. An interrupt (<see cref="Interrupt"/>) saves PCL, PCH, PCE, YL, YH, XL, XH, A
/// and CC as PM0044 says, and sets I1:I0 to the interrupt's level; iret restores them. trap jumps through the vector at
/// 0x8004; break stops (<see cref="BreakHit"/>); halt and wfi sleep (<see cref="Sleep"/>) with interrupts on. An
/// opcode the core does not have throws <see cref="ProgramFault"/>.
/// </summary>
public sealed class Stm8Cpu
{
    /// <summary>CC's bits.</summary>
    public const byte V = 0x80, I1 = 0x20, H = 0x10, I0 = 0x08, N = 0x04, Z = 0x02, C = 0x01;

    /// <summary>The trap's vector.</summary>
    public const int TrapVector = 0x8004;

    private readonly IStm8Bus _bus;
    private int _at;

    /// <summary>A core on <paramref name="bus"/>, as after reset but for the PC and SP, which the chip sets.</summary>
    public Stm8Cpu(IStm8Bus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        CC = I1 | I0;
    }

    public byte A { get; set; }

    public ushort X { get; set; }

    public ushort Y { get; set; }

    public ushort SP { get; set; }

    /// <summary>The program counter, 24 bits.</summary>
    public int PC { get; set; }

    /// <summary>The condition code register.</summary>
    public byte CC { get; set; }

    /// <summary>How the CPU sleeps (halt, wfi), or <see cref="Stm8Sleep.None"/>.</summary>
    public Stm8Sleep Sleep { get; set; }

    /// <summary>Whether the program ran break.</summary>
    public bool BreakHit { get; set; }

    /// <summary>The CPU's interrupt level from I1:I0: 10 is 0 (the main program, every interrupt on), 01 is 1, 00 is 2, 11 is 3.</summary>
    public int Level => LevelOf((CC >> 4 & 2) | (CC >> 3 & 1));

    /// <summary>The level of an I1:I0 (or ITC_SPR) code: 10 is 0, 01 is 1, 00 is 2, 11 is 3.</summary>
    public static int LevelOf(int code) => code switch { 2 => 0, 1 => 1, 0 => 2, _ => 3 };

    /// <summary>Runs the instruction at <see cref="PC"/>; returns its cycles.</summary>
    public int Step()
    {
        _at = PC;
        var prefix = 0;
        int op = Fetch();
        if (op is 0x72 or 0x90 or 0x91 or 0x92)
        {
            prefix = op;
            op = Fetch();
        }

        return (op >> 4) switch
        {
            0x0 when prefix == 0x72 => BitTestJump(op),
            0x1 when prefix == 0x72 => BitSetReset(op),
            0x1 when prefix == 0x90 => BitComplementCopy(op),
            0x2 => JumpRelative(op, prefix),
            0x8 => Control(op, prefix),
            0x9 => Registers(op, prefix),
            0x0 or 0x3 or 0x4 or 0x5 or 0x6 or 0x7 => ReadModifyWrite(op, prefix),
            _ => Binary(op, prefix),
        };
    }

    /// <summary>
    /// Takes an interrupt: saves the context on the stack, sets I1:I0 to <paramref name="levelCode"/> (the code of the
    /// interrupt's level, as ITC_SPR holds it) and jumps to <paramref name="handler"/>. Returns its cycles.
    /// </summary>
    public int Interrupt(int handler, int levelCode)
    {
        PushContext();
        CC = (byte)((CC & ~(I1 | I0)) | ((levelCode & 2) != 0 ? I1 : 0) | ((levelCode & 1) != 0 ? I0 : 0));
        PC = handler;
        Sleep = Stm8Sleep.None;
        return 9;
    }

    private int ReadModifyWrite(int op, int prefix)
    {
        var hi = op >> 4;
        var lo = op & 0xF;
        switch (lo)
        {
            case 0x1 or 0x2 or 0x5 or 0xB:
                return Special(op, prefix);
            case 0x0 or 0x3 or 0x4 or 0x6 or 0x7 or 0x8 or 0x9 or 0xA or 0xC or 0xD or 0xE or 0xF:
                break;
            default:
                throw Illegal(prefix, op);
        }

        if (hi == 0x5 && prefix is 0 or 0x90)
        {
            // the word operations on X or Y
            var y = prefix == 0x90;
            var word = y ? Y : X;
            var (result, cycles) = WordOperation(lo, word);
            if (lo != 0xD)
            {
                if (y)
                {
                    Y = result;
                }
                else
                {
                    X = result;
                }
            }

            return cycles;
        }

        if (hi == 0x4 && prefix == 0)
        {
            var result = ByteOperation(lo, A);
            if (lo != 0xD)
            {
                A = result;
            }

            return 1;
        }

        var pointer = prefix is 0x72 or 0x91 or 0x92 && hi != 0x4 && hi != 0x5;
        var address = (hi, prefix) switch
        {
            (0x0, 0) => SP + Fetch(),
            (0x3, 0) => Fetch(),
            (0x3, 0x72) => Read16(Fetch16()),
            (0x3, 0x92) => Read16(Fetch()),
            (0x4, 0x72) => Fetch16() + X,
            (0x4, 0x90) => Fetch16() + Y,
            (0x5, 0x72) => Fetch16(),
            (0x6, 0) => Fetch() + X,
            (0x6, 0x90) => Fetch() + Y,
            (0x6, 0x72) => Read16(Fetch16()) + X,
            (0x6, 0x92) => Read16(Fetch()) + X,
            (0x6, 0x91) => Read16(Fetch()) + Y,
            (0x7, 0) => X,
            (0x7, 0x90) => Y,
            _ => throw Illegal(prefix, op),
        };
        if (lo == 0xF)
        {
            Write(address, 0); // clr writes without reading
            CC = (byte)((CC & ~N) | Z);
        }
        else
        {
            var result = ByteOperation(lo, Read(address));
            if (lo != 0xD)
            {
                Write(address, result);
            }
        }

        return pointer ? 4 : 1;
    }

    /// <summary>The operations of the rows of read-modify-write instructions that are not one (columns 1, 2, 5 and B).</summary>
    private int Special(int op, int prefix)
    {
        switch (op, prefix)
        {
            case (0x01, 0): // rrwa x, a: A -> XH -> XL -> A
                X = RotateWordRight(X);
                return 1;
            case (0x01, 0x90):
                Y = RotateWordRight(Y);
                return 1;
            case (0x02, 0): // rlwa x, a: A <- XH <- XL <- A
                X = RotateWordLeft(X);
                return 1;
            case (0x02, 0x90):
                Y = RotateWordLeft(Y);
                return 1;
            case (0x31, 0): // exg a, longmem
                var exchanged = Fetch16();
                var memory = Read(exchanged);
                Write(exchanged, A);
                A = memory;
                return 3;
            case (0x32, 0): // pop longmem
                var popped = Fetch16();
                Write(popped, Pop());
                return 1;
            case (0x35, 0): // mov longmem, #byte
                var value = Fetch();
                Write(Fetch16(), value);
                return 1;
            case (0x3B, 0): // push longmem
                Push(Read(Fetch16()));
                return 1;
            case (0x41, 0): // exg a, xl
                (A, X) = ((byte)X, (ushort)((X & 0xFF00) | A));
                return 1;
            case (0x42, 0): // mul x, a
                X = (ushort)((X & 0xFF) * A);
                CC &= unchecked((byte)~(H | C));
                return 4;
            case (0x42, 0x90): // mul y, a
                Y = (ushort)((Y & 0xFF) * A);
                CC &= unchecked((byte)~(H | C));
                return 4;
            case (0x45, 0): // mov shortmem, shortmem: 45 source destination
                var source = Fetch();
                Write(Fetch(), Read(source));
                return 1;
            case (0x4B, 0): // push #byte
                Push(Fetch());
                return 1;
            case (0x51, 0): // exgw x, y
                (X, Y) = (Y, X);
                return 1;
            case (0x52, 0): // sub sp, #byte
                SP -= Fetch();
                return 1;
            case (0x55, 0): // mov longmem, longmem: 55 source destination
                var from = Fetch16();
                Write(Fetch16(), Read(from));
                return 1;
            case (0x5B, 0): // addw sp, #byte
                SP += Fetch();
                return 2;
            case (0x61, 0): // exg a, yl
                (A, Y) = ((byte)Y, (ushort)((Y & 0xFF00) | A));
                return 1;
            case (0x62, 0): // div x, a
                X = Divide(X, A, out var remainder, out var cycles);
                A = (byte)remainder;
                return cycles;
            case (0x62, 0x90): // div y, a
                Y = Divide(Y, A, out var yRemainder, out var yCycles);
                A = (byte)yRemainder;
                return yCycles;
            case (0x65, 0): // divw x, y
                var quotient = Divide(X, Y, out var wordRemainder, out var wordCycles);
                Y = (ushort)wordRemainder;
                X = quotient;
                return wordCycles;
            case (0x6B, 0): // ld (shortoff, sp), a
                Write(SP + Fetch(), A);
                SetNz(A);
                return 1;
            case (0x7B, 0): // ld a, (shortoff, sp)
                A = Read(SP + Fetch());
                SetNz(A);
                return 1;
            default:
                throw Illegal(prefix, op);
        }
    }

    /// <summary>The instructions with A and an operand of columns 1 and A-F: the 8-bit ALU, LD, LDW, CPW, JP, CALL, ...</summary>
    private int Binary(int op, int prefix)
    {
        switch (op, prefix)
        {
            case (0xA9 or 0xB9 or 0xF9 or 0xBB or 0xFB or 0xA2 or 0xB2 or 0xF2 or 0xB0 or 0xF0, 0x72):
                return AddSubtractWord(op);
            case (0x1C, 0): // addw x, #word
                X = AddWord(X, Fetch16());
                return 2;
            case (0x1D, 0): // subw x, #word
                X = SubtractWord(X, Fetch16());
                return 2;
            case (0x16, 0): // ldw y, (shortoff, sp)
                Y = (ushort)Read16(SP + Fetch());
                SetNz16(Y);
                return 2;
            case (0x17, 0): // ldw (shortoff, sp), y
                Write16(SP + Fetch(), Y);
                SetNz16(Y);
                return 2;
            case (0xAC, 0): // jpf extmem
                PC = Fetch24();
                return 2;
            case (0xAC, 0x92): // jpf [longptr.e]
                PC = Read24(Fetch16());
                return 6;
            case (0xAD, 0): // callr shortoff
                var offset = (sbyte)Fetch();
                PushWord(PC);
                PC = (PC + offset) & 0xFFFFFF;
                return 4;
            case (0xBC or 0xBD or 0xA7 or 0xAF, _):
                return FarLoad(op, prefix);
        }

        var hi = op >> 4;
        var lo = op & 0xF;
        var pointer = prefix is 0x72 or 0x91 or 0x92;
        switch (lo)
        {
            case 0x3: // cpw x|y, src: X for the direct modes and those indexed by Y, Y for those indexed by X
                var cpwY = (hi is 0xA or 0xB or 0xC) ? (prefix is 0x90 or 0x91) : (hi != 0x1 && prefix is 0 or 0x72 or 0x92);
                var compared = hi == 0xA ? Fetch16() : Read16(Address(hi, prefix, op, wordRegister: true));
                CompareWord(cpwY ? Y : X, compared);
                return pointer ? 5 : 2;
            case 0xC: // jp
                if (hi is 0xA or 0xB or 0x1)
                {
                    throw Illegal(prefix, op);
                }

                PC = (PC & 0xFF0000) | (Address(hi, prefix, op, wordRegister: false) & 0xFFFF);
                return pointer ? 5 : 2;
            case 0xD: // call
                if (hi is 0xA or 0xB or 0x1)
                {
                    throw Illegal(prefix, op);
                }

                var target = Address(hi, prefix, op, wordRegister: false);
                PushWord(PC);
                PC = (PC & 0xFF0000) | (target & 0xFFFF);
                return pointer ? 6 : 4;
            case 0xE: // ldw x|y, src: Y with the prefixes 90 and 91 (the modes with Y)
                var loadY = prefix is 0x90 or 0x91;
                var loaded = hi == 0xA ? Fetch16() : Read16(Address(hi, prefix, op, wordRegister: true));
                if (loadY)
                {
                    Y = (ushort)loaded;
                }
                else
                {
                    X = (ushort)loaded;
                }

                SetNz16(loaded);
                return pointer ? 5 : 2;
            case 0xF: // ldw dst, x|y: the other register than the index, X or Y for the direct modes
                if (hi == 0xA)
                {
                    throw Illegal(prefix, op);
                }

                var storeY = (hi is 0xB or 0xC or 0x1) ? (prefix is 0x90 or 0x91) : (prefix is 0 or 0x72 or 0x92);
                var stored = storeY ? Y : X;
                Write16(Address(hi, prefix, op, wordRegister: true), stored);
                SetNz16(stored);
                return pointer ? 5 : 2;
            case 0x7: // ld dst, a
                if (hi == 0xA)
                {
                    throw Illegal(prefix, op);
                }

                Write(Address(hi, prefix, op, wordRegister: false), A);
                SetNz(A);
                return pointer ? 4 : 1;
        }

        var operand = hi == 0xA ? Fetch() : Read(Address(hi, prefix, op, wordRegister: false));
        switch (lo)
        {
            case 0x0: // sub
                A = Subtract(A, operand, 0);
                break;
            case 0x1: // cp
                Subtract(A, operand, 0);
                break;
            case 0x2: // sbc
                A = Subtract(A, operand, CC & C);
                break;
            case 0x4: // and
                A &= operand;
                SetNz(A);
                break;
            case 0x5: // bcp
                SetNz((byte)(A & operand));
                break;
            case 0x6: // ld a, src
                A = operand;
                SetNz(A);
                break;
            case 0x8: // xor
                A ^= operand;
                SetNz(A);
                break;
            case 0x9: // adc
                A = Add(A, operand, CC & C);
                break;
            case 0xA: // or
                A |= operand;
                SetNz(A);
                break;
            case 0xB: // add
                A = Add(A, operand, 0);
                break;
        }

        return pointer ? 4 : 1;
    }

    /// <summary>
    /// The address of the operand of a column 1, B-F instruction, by its prefix: (shortoff,SP); shortmem; longmem,
    /// [longptr.w] (72), [shortptr.w] (92, 91); (longoff,X), (longoff,Y) (90), ([longptr.w],X) (72), ([shortptr.w],X)
    /// (92), ([shortptr.w],Y) (91); (shortoff,X), (shortoff,Y) (90); (X), (Y) (90). <paramref name="wordRegister"/>: an
    /// LDW or CPW, whose Y forms of the direct modes take the prefix 90.
    /// </summary>
    private int Address(int hi, int prefix, int op, bool wordRegister) => (hi, prefix) switch
    {
        (0x1, 0) => SP + Fetch(),
        (0xB, 0) => Fetch(),
        (0xB, 0x90) when wordRegister => Fetch(),
        (0xC, 0) => Fetch16(),
        (0xC, 0x90) when wordRegister => Fetch16(),
        (0xC, 0x72) => Read16(Fetch16()),
        (0xC, 0x92) => Read16(Fetch()),
        (0xC, 0x91) when wordRegister => Read16(Fetch()),
        (0xD, 0) => Fetch16() + X,
        (0xD, 0x90) => Fetch16() + Y,
        (0xD, 0x72) => Read16(Fetch16()) + X,
        (0xD, 0x92) => Read16(Fetch()) + X,
        (0xD, 0x91) => Read16(Fetch()) + Y,
        (0xE, 0) => Fetch() + X,
        (0xE, 0x90) => Fetch() + Y,
        (0xF, 0) => X,
        (0xF, 0x90) => Y,
        _ => throw Illegal(prefix, op),
    };

    /// <summary>LDF: A to or from a 24-bit address, (extoff,X), (extoff,Y), [longptr.e], ([longptr.e],X) or ([longptr.e],Y).</summary>
    private int FarLoad(int op, int prefix)
    {
        var (address, cycles) = (op, prefix) switch
        {
            (0xBC or 0xBD, 0) => (Fetch24(), 1),
            (0xBC or 0xBD, 0x92) => (Read24(Fetch16()), 5),
            (0xA7 or 0xAF, 0) => (Fetch24() + X, 1),
            (0xA7 or 0xAF, 0x90) => (Fetch24() + Y, 1),
            (0xA7 or 0xAF, 0x92) => (Read24(Fetch16()) + X, 5),
            (0xA7 or 0xAF, 0x91) => (Read24(Fetch16()) + Y, 5),
            _ => throw Illegal(prefix, op),
        };
        if (op is 0xBC or 0xAF)
        {
            A = Read(address);
        }
        else
        {
            Write(address, A);
        }

        SetNz(A);
        return cycles;
    }

    /// <summary>ADDW and SUBW of X or Y with a word, a longmem or (shortoff,SP) (prefix 72).</summary>
    private int AddSubtractWord(int op)
    {
        var operand = (op >> 4) switch
        {
            0xA => Fetch16(),
            0xB => Read16(Fetch16()),
            _ => Read16(SP + Fetch()),
        };
        switch (op & 0xF)
        {
            case 0x9: // addw y
                Y = AddWord(Y, operand);
                break;
            case 0xB: // addw x
                X = AddWord(X, operand);
                break;
            case 0x2: // subw y
                Y = SubtractWord(Y, operand);
                break;
            default: // subw x
                X = SubtractWord(X, operand);
                break;
        }

        return 2;
    }

    private int JumpRelative(int op, int prefix)
    {
        var offset = (sbyte)Fetch();
        var cc = CC;
        bool Flag(byte flag) => (cc & flag) != 0;
        var taken = (op, prefix) switch
        {
            (0x20, 0) => true, // jra
            (0x21, 0) => false, // jrf
            (0x22, 0) => !Flag(C) && !Flag(Z), // jrugt
            (0x23, 0) => Flag(C) || Flag(Z), // jrule
            (0x24, 0) => !Flag(C), // jrnc
            (0x25, 0) => Flag(C), // jrc
            (0x26, 0) => !Flag(Z), // jrne
            (0x27, 0) => Flag(Z), // jreq
            (0x28, 0) => !Flag(V), // jrnv
            (0x29, 0) => Flag(V), // jrv
            (0x2A, 0) => !Flag(N), // jrpl
            (0x2B, 0) => Flag(N), // jrmi
            (0x2C, 0) => !Flag(Z) && Flag(N) == Flag(V), // jrsgt
            (0x2D, 0) => Flag(Z) || Flag(N) != Flag(V), // jrsle
            (0x2E, 0) => Flag(N) == Flag(V), // jrsge
            (0x2F, 0) => Flag(N) != Flag(V), // jrslt
            (0x28, 0x90) => !Flag(H), // jrnh
            (0x29, 0x90) => Flag(H), // jrh
            (0x2C, 0x90) => !(Flag(I1) && Flag(I0)), // jrnm
            (0x2D, 0x90) => Flag(I1) && Flag(I0), // jrm
            (0x2E, 0x90) => false, // jril: the interrupt line is high
            (0x2F, 0x90) => true, // jrih
            _ => throw Illegal(prefix, op),
        };
        if (!taken)
        {
            return 1;
        }

        PC = (PC + offset) & 0xFFFFFF;
        return 2;
    }

    /// <summary>72 00-0F: btjt / btjf longmem, #pos, rel; the bit goes to C.</summary>
    private int BitTestJump(int op)
    {
        var address = Fetch16();
        var offset = (sbyte)Fetch();
        var set = (Read(address) & (1 << ((op >> 1) & 7))) != 0;
        CC = (byte)((CC & ~C) | (set ? C : 0));
        if (set != ((op & 1) == 0))
        {
            return 2;
        }

        PC = (PC + offset) & 0xFFFFFF;
        return 3;
    }

    /// <summary>72 10-1F: bset / bres longmem, #pos.</summary>
    private int BitSetReset(int op)
    {
        var address = Fetch16();
        var mask = 1 << ((op >> 1) & 7);
        var value = Read(address);
        Write(address, (byte)((op & 1) == 0 ? value | mask : value & ~mask));
        return 1;
    }

    /// <summary>90 10-1F: bcpl / bccm longmem, #pos.</summary>
    private int BitComplementCopy(int op)
    {
        var address = Fetch16();
        var mask = 1 << ((op >> 1) & 7);
        var value = Read(address);
        Write(address, (byte)((op & 1) == 0 ? value ^ mask : (CC & C) != 0 ? value | mask : value & ~mask));
        return 1;
    }

    /// <summary>Row 8: iret, ret, int, trap, pop, push, retf, break, ccf, callf, halt, wfi.</summary>
    private int Control(int op, int prefix)
    {
        switch (op, prefix)
        {
            case (0x80, 0): // iret
                CC = Pop();
                A = Pop();
                X = (ushort)((Pop() << 8) | Pop());
                Y = (ushort)((Pop() << 8) | Pop());
                PC = (Pop() << 16) | (Pop() << 8) | Pop();
                return 11;
            case (0x81, 0): // ret
                PC = (PC & 0xFF0000) | (Pop() << 8) | Pop();
                return 4;
            case (0x82, 0): // int extmem (the reset vector's jump)
                PC = Fetch24();
                return 2;
            case (0x83, 0): // trap
                PushContext();
                CC |= I1 | I0;
                PC = Read24(TrapVector + 1);
                return 9;
            case (0x84, 0): // pop a
                A = Pop();
                return 1;
            case (0x85, 0): // popw x
                X = (ushort)((Pop() << 8) | Pop());
                return 2;
            case (0x85, 0x90): // popw y
                Y = (ushort)((Pop() << 8) | Pop());
                return 2;
            case (0x86, 0): // pop cc
                CC = Pop();
                return 1;
            case (0x87, 0): // retf
                PC = (Pop() << 16) | (Pop() << 8) | Pop();
                return 5;
            case (0x88, 0): // push a
                Push(A);
                return 1;
            case (0x89, 0): // pushw x
                PushWord(X);
                return 2;
            case (0x89, 0x90): // pushw y
                PushWord(Y);
                return 2;
            case (0x8A, 0): // push cc
                Push(CC);
                return 1;
            case (0x8B, 0): // break
                BreakHit = true;
                PC = _at;
                return 1;
            case (0x8C, 0): // ccf
                CC ^= C;
                return 1;
            case (0x8D, 0): // callf extmem
                var target = Fetch24();
                PushFar(PC);
                PC = target;
                return 5;
            case (0x8D, 0x92): // callf [longptr.e]
                var far = Read24(Fetch16());
                PushFar(PC);
                PC = far;
                return 8;
            case (0x8E, 0): // halt
                CC = (byte)((CC & ~I0) | I1);
                Sleep = Stm8Sleep.Halt;
                return 10;
            case (0x8F, 0): // wfi
                CC = (byte)((CC & ~I0) | I1);
                Sleep = Stm8Sleep.Wait;
                return 10;
            case (0x8F, 0x72): // wfe
                Sleep = Stm8Sleep.Wait;
                return 1;
            default:
                throw Illegal(prefix, op);
        }
    }

    /// <summary>Row 9: the moves between registers, the flags' instructions, nop.</summary>
    private int Registers(int op, int prefix)
    {
        var y = prefix == 0x90;
        if (prefix != 0 && !(y && op is 0x93 or 0x94 or 0x95 or 0x96 or 0x97 or 0x9E or 0x9F))
        {
            throw Illegal(prefix, op);
        }

        switch (op)
        {
            case 0x93: // ldw x, y / ldw y, x
                if (y)
                {
                    Y = X;
                }
                else
                {
                    X = Y;
                }

                break;
            case 0x94: // ldw sp, x|y
                SP = y ? Y : X;
                break;
            case 0x95: // ld xh|yh, a
                SetIndex(y, (ushort)((A << 8) | ((y ? Y : X) & 0xFF)));
                break;
            case 0x96: // ldw x|y, sp
                SetIndex(y, SP);
                break;
            case 0x97: // ld xl|yl, a
                SetIndex(y, (ushort)(((y ? Y : X) & 0xFF00) | A));
                break;
            case 0x98: // rcf
                CC &= unchecked((byte)~C);
                break;
            case 0x99: // scf
                CC |= C;
                break;
            case 0x9A: // rim: I1:I0 = 10, level 0
                CC = (byte)((CC & ~I0) | I1);
                break;
            case 0x9B: // sim: I1:I0 = 11, level 3
                CC |= I1 | I0;
                break;
            case 0x9C: // rvf
                CC &= unchecked((byte)~V);
                break;
            case 0x9D: // nop
                break;
            case 0x9E: // ld a, xh|yh
                A = (byte)((y ? Y : X) >> 8);
                break;
            case 0x9F: // ld a, xl|yl
                A = (byte)(y ? Y : X);
                break;
            default:
                throw Illegal(prefix, op);
        }

        return 1;
    }

    private void SetIndex(bool y, ushort value)
    {
        if (y)
        {
            Y = value;
        }
        else
        {
            X = value;
        }
    }

    /// <summary>An 8-bit read-modify-write operation (column <paramref name="lo"/>) of <paramref name="value"/>, setting the flags.</summary>
    private byte ByteOperation(int lo, byte value)
    {
        int result;
        switch (lo)
        {
            case 0x0: // neg
                result = (byte)-value;
                SetFlags(V | C, (result == 0x80 ? V : 0) | (result != 0 ? C : 0));
                break;
            case 0x3: // cpl
                result = (byte)~value;
                CC |= C;
                break;
            case 0x4: // srl
                result = value >> 1;
                SetFlags(C, value & 1);
                break;
            case 0x6: // rrc
                result = (value >> 1) | ((CC & C) << 7);
                SetFlags(C, value & 1);
                break;
            case 0x7: // sra
                result = (value >> 1) | (value & 0x80);
                SetFlags(C, value & 1);
                break;
            case 0x8: // sll
                result = (byte)(value << 1);
                SetFlags(C, value >> 7);
                break;
            case 0x9: // rlc
                result = (byte)((value << 1) | (CC & C));
                SetFlags(C, value >> 7);
                break;
            case 0xA: // dec
                result = (byte)(value - 1);
                SetFlags(V, value == 0x80 ? V : 0);
                break;
            case 0xC: // inc
                result = (byte)(value + 1);
                SetFlags(V, value == 0x7F ? V : 0);
                break;
            case 0xD: // tnz
                result = value;
                break;
            case 0xE: // swap
                result = (byte)((value << 4) | (value >> 4));
                break;
            default: // clr
                result = 0;
                break;
        }

        SetNz((byte)result);
        return (byte)result;
    }

    /// <summary>A 16-bit operation on X or Y (row 5): negw, cplw, srlw, rrcw, sraw, sllw, rlcw, decw, incw, tnzw, swapw, clrw.</summary>
    private (ushort Result, int Cycles) WordOperation(int lo, ushort value)
    {
        int result;
        var cycles = 2;
        switch (lo)
        {
            case 0x0: // negw
                result = (ushort)-value;
                SetFlags(V | C, (result == 0x8000 ? V : 0) | (result != 0 ? C : 0));
                break;
            case 0x3: // cplw
                result = (ushort)~value;
                CC |= C;
                break;
            case 0x4: // srlw
                result = value >> 1;
                SetFlags(C, value & 1);
                break;
            case 0x6: // rrcw
                result = (value >> 1) | ((CC & C) << 15);
                SetFlags(C, value & 1);
                break;
            case 0x7: // sraw
                result = (value >> 1) | (value & 0x8000);
                SetFlags(C, value & 1);
                break;
            case 0x8: // sllw
                result = (ushort)(value << 1);
                SetFlags(C, value >> 15);
                break;
            case 0x9: // rlcw
                result = (ushort)((value << 1) | (CC & C));
                SetFlags(C, value >> 15);
                break;
            case 0xA: // decw
                result = (ushort)(value - 1);
                SetFlags(V, value == 0x8000 ? V : 0);
                break;
            case 0xC: // incw
                result = (ushort)(value + 1);
                SetFlags(V, value == 0x7FFF ? V : 0);
                cycles = 1;
                break;
            case 0xD: // tnzw
                result = value;
                break;
            case 0xE: // swapw
                result = (ushort)((value << 8) | (value >> 8));
                cycles = 1;
                break;
            default: // clrw
                result = 0;
                cycles = 1;
                break;
        }

        SetNz16(result);
        return ((ushort)result, cycles);
    }

    /// <summary>a + b + carry: V, H (carry from bit 3), N, Z, C.</summary>
    private byte Add(byte a, byte b, int carry)
    {
        var sum = a + b + carry;
        var result = (byte)sum;
        SetFlags(V | H | N | Z | C, ((~(a ^ b) & (a ^ result) & 0x80) != 0 ? V : 0) |
            ((a & 0xF) + (b & 0xF) + carry > 0xF ? H : 0) | NzOf(result) | (sum > 0xFF ? C : 0));
        return result;
    }

    /// <summary>a - b - borrow: V, N, Z, C (a borrow); H stays.</summary>
    private byte Subtract(byte a, byte b, int borrow)
    {
        var difference = a - b - borrow;
        var result = (byte)difference;
        SetFlags(V | N | Z | C, (((a ^ b) & (a ^ result) & 0x80) != 0 ? V : 0) | NzOf(result) | (difference < 0 ? C : 0));
        return result;
    }

    /// <summary>a + b, 16 bits: V, H (carry from bit 7), N, Z, C.</summary>
    private ushort AddWord(int a, int b)
    {
        var sum = a + b;
        var result = (ushort)sum;
        SetFlags(V | H | N | Z | C, ((~(a ^ b) & (a ^ result) & 0x8000) != 0 ? V : 0) |
            ((a & 0xFF) + (b & 0xFF) > 0xFF ? H : 0) | Nz16Of(result) | (sum > 0xFFFF ? C : 0));
        return result;
    }

    /// <summary>a - b, 16 bits: V, H (a borrow from bit 8), N, Z, C.</summary>
    private ushort SubtractWord(int a, int b)
    {
        var difference = a - b;
        var result = (ushort)difference;
        SetFlags(V | H | N | Z | C, (((a ^ b) & (a ^ result) & 0x8000) != 0 ? V : 0) |
            ((a & 0xFF) < (b & 0xFF) ? H : 0) | Nz16Of(result) | (difference < 0 ? C : 0));
        return result;
    }

    /// <summary>cpw: the flags of a - b (V, N, Z, C).</summary>
    private void CompareWord(int a, int b)
    {
        var difference = a - b;
        var result = (ushort)difference;
        SetFlags(V | N | Z | C, (((a ^ b) & (a ^ result) & 0x8000) != 0 ? V : 0) | Nz16Of(result) | (difference < 0 ? C : 0));
    }

    /// <summary>
    /// div and divw: the quotient, the remainder; V, H and N cleared, Z for a quotient of 0, C for a division by 0 (which
    /// leaves the registers as they were). 2 cycles and one more for every bit of the quotient, 17 at most.
    /// </summary>
    private ushort Divide(ushort dividend, int divisor, out int remainder, out int cycles)
    {
        CC &= unchecked((byte)~(V | H | N));
        if (divisor == 0)
        {
            CC |= C;
            remainder = 0; // the divisor: A (div) or Y (divw) stays 0
            cycles = 2;
            return dividend;
        }

        var quotient = dividend / divisor;
        remainder = dividend % divisor;
        CC = (byte)((CC & ~(Z | C)) | (quotient == 0 ? Z : 0));
        cycles = Math.Min(17, 2 + (32 - int.LeadingZeroCount(quotient)));
        return (ushort)quotient;
    }

    /// <summary>rrwa: A -> XH -> XL -> A (rotates the 24 bits of A:X right by a byte). N, Z of the result.</summary>
    private ushort RotateWordRight(ushort word)
    {
        var result = (ushort)((A << 8) | (word >> 8));
        A = (byte)word;
        SetNz16(result);
        return result;
    }

    /// <summary>rlwa: A &lt;- XH &lt;- XL &lt;- A. N, Z of the result.</summary>
    private ushort RotateWordLeft(ushort word)
    {
        var result = (ushort)((word << 8) | A);
        A = (byte)(word >> 8);
        SetNz16(result);
        return result;
    }

    private static int NzOf(byte value) => (value == 0 ? Z : 0) | ((value & 0x80) != 0 ? N : 0);

    private static int Nz16Of(int value) => ((ushort)value == 0 ? Z : 0) | ((value & 0x8000) != 0 ? N : 0);

    private void SetNz(byte value) => SetFlags(N | Z, NzOf(value));

    private void SetNz16(int value) => SetFlags(N | Z, Nz16Of(value));

    private void SetFlags(int mask, int value) => CC = (byte)((CC & ~mask) | (value & mask));

    private void PushContext()
    {
        PushFar(PC);
        PushWord(Y);
        PushWord(X);
        Push(A);
        Push(CC);
    }

    private void Push(byte value)
    {
        Write(SP, value);
        SP--;
    }

    /// <summary>Pushes the low byte, then the high byte: the word is at SP+1 in memory, high byte first.</summary>
    private void PushWord(int value)
    {
        Push((byte)value);
        Push((byte)(value >> 8));
    }

    private void PushFar(int value)
    {
        Push((byte)value);
        Push((byte)(value >> 8));
        Push((byte)(value >> 16));
    }

    private byte Pop()
    {
        SP++;
        return Read(SP);
    }

    private byte Fetch()
    {
        var value = _bus.Read(PC);
        PC = (PC + 1) & 0xFFFFFF;
        return value;
    }

    private int Fetch16() => (Fetch() << 8) | Fetch();

    private int Fetch24() => (Fetch() << 16) | (Fetch() << 8) | Fetch();

    private byte Read(int address) => _bus.Read(address & 0xFFFFFF);

    private int Read16(int address) => (Read(address) << 8) | Read(address + 1);

    private int Read24(int address) => (Read(address) << 16) | (Read(address + 1) << 8) | Read(address + 2);

    private void Write(int address, byte value) => _bus.Write(address & 0xFFFFFF, value);

    private void Write16(int address, int value)
    {
        Write(address, (byte)(value >> 8));
        Write(address + 1, (byte)value);
    }

    private ProgramFault Illegal(int prefix, int op) => new(string.Create(CultureInfo.InvariantCulture,
        $"the program ran an opcode the STM8 does not have, {(prefix != 0 ? $"{prefix:x2} " : "")}{op:x2}, at pc 0x{_at:x4}"));
}
