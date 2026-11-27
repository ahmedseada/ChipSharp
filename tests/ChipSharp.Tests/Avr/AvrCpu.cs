namespace ChipSharp.Tests.Avr;

/// <summary>
/// An AVR core as the tests run the avr runtime on it: the ATmega328P's (AVR5: MUL, MOVW, JMP and CALL, LPM Rd, Z+;
/// a 2-byte program counter), its 32 registers, I/O space and RAM in one data space, and its flash, with every
/// instruction of that core but SPM, the ELPMs and EIJMP/EICALL. No peripherals and no interrupts: it runs code that
/// only computes, from reset or a function at a time (<see cref="Call"/>). It counts cycles as the datasheet does, and
/// stops at BREAK (<see cref="StopReason.Break"/>), at SLEEP with interrupts off (<see cref="StopReason.Sleep"/>) and at
/// what a program must not do (an address outside RAM or flash, an instruction it does not have), which throws.
/// </summary>
internal sealed class AvrCpu
{
    /// <summary>SREG, SPH and SPL in the data space (I/O 0x3F, 0x3E, 0x3D).</summary>
    public const int Sreg = 0x5F;

    public const int SpHigh = 0x5E;
    public const int SpLow = 0x5D;

    private const int C = 0;
    private const int Z = 1;
    private const int N = 2;
    private const int V = 3;
    private const int S = 4;
    private const int H = 5;
    private const int T = 6;
    private const int I = 7;

    /// <summary>A word no program has: where <see cref="Call"/> returns to.</summary>
    private readonly int _returnAddress;

    public AvrCpu(byte[] flash, int dataSize = 0x900)
    {
        ArgumentNullException.ThrowIfNull(flash);
        Program = new ushort[32 * 1024 / 2];
        if (flash.Length > Program.Length * 2)
        {
            throw new ArgumentException($"{flash.Length} bytes do not fit the ATmega328P's 32 KB of flash.", nameof(flash));
        }

        for (var i = 0; i < flash.Length; i++)
        {
            Program[i / 2] |= (ushort)(flash[i] << (8 * (i % 2)));
        }

        Data = new byte[dataSize];
        _returnAddress = Program.Length - 1;
        Sp = dataSize - 1;
    }

    /// <summary>Why <see cref="Run"/> stopped.</summary>
    public enum StopReason
    {
        None,

        /// <summary>BREAK: the program failed (chipsharp_halt_failure).</summary>
        Break,

        /// <summary>SLEEP with interrupts off: the program has ended (chipsharp_halt).</summary>
        Sleep,

        /// <summary>The function <see cref="Call"/> called has returned.</summary>
        Returned,
    }

    /// <summary>Flash, as 16-bit words.</summary>
    public ushort[] Program { get; }

    /// <summary>The data space: the registers (0x00-0x1F), I/O (0x20-0x5F, 0x60-0xFF) and RAM (from 0x100).</summary>
    public byte[] Data { get; }

    /// <summary>The program counter, a word address.</summary>
    public int Pc { get; set; }

    public long Cycles { get; private set; }

    public StopReason Stopped { get; private set; }

    public int Sp
    {
        get => Data[SpLow] | (Data[SpHigh] << 8);
        set
        {
            Data[SpLow] = (byte)value;
            Data[SpHigh] = (byte)(value >> 8);
        }
    }

    /// <summary>
    /// Runs from the current <see cref="Pc"/> until the program stops (BREAK, SLEEP with interrupts off) or
    /// <paramref name="maxCycles"/> have passed, which throws: the program is stuck.
    /// </summary>
    public StopReason Run(long maxCycles)
    {
        Stopped = StopReason.None;
        var end = Cycles + maxCycles;
        while (Stopped == StopReason.None)
        {
            if (Cycles > end)
            {
                throw new InvalidOperationException($"Still running after {maxCycles} cycles, at 0x{Pc * 2:X4}.");
            }

            Step();
        }

        return Stopped;
    }

    /// <summary>
    /// Calls the function at byte address <paramref name="address"/> with the registers as they are, as a CALL would,
    /// and runs until it returns. Throws when it stops another way or runs longer than <paramref name="maxCycles"/>.
    /// </summary>
    public void Call(int address, long maxCycles = 10_000_000)
    {
        Push((byte)_returnAddress);
        Push((byte)(_returnAddress >> 8));
        Pc = address / 2;
        var stopped = Run(maxCycles);
        if (stopped != StopReason.Returned)
        {
            throw new InvalidOperationException($"The function at 0x{address:X4} stopped ({stopped}) at 0x{Pc * 2:X4}.");
        }
    }

    /// <summary>Executes one instruction.</summary>
    public void Step()
    {
        if (Pc == _returnAddress)
        {
            Stopped = StopReason.Returned;
            return;
        }

        var op = Fetch(Pc);
        var next = Pc + 1;
        var cycles = 1;
        var d = (op >> 4) & 0x1F;
        var r = ((op >> 5) & 0x10) | (op & 0x0F);
        var immediateRegister = 16 + ((op >> 4) & 0x0F);
        var immediate = ((op >> 4) & 0xF0) | (op & 0x0F);
        switch (op >> 12)
        {
            case 0x0:
                switch ((op >> 10) & 3)
                {
                    case 0:
                        switch ((op >> 8) & 3)
                        {
                            case 0 when op == 0: // NOP
                                break;
                            case 1: // MOVW
                                var to = ((op >> 4) & 0x0F) * 2;
                                var from = (op & 0x0F) * 2;
                                Data[to] = Data[from];
                                Data[to + 1] = Data[from + 1];
                                break;
                            case 2: // MULS
                                Multiply((sbyte)Data[16 + ((op >> 4) & 0x0F)] * (sbyte)Data[16 + (op & 0x0F)], false);
                                cycles = 2;
                                break;
                            case 3: // MULSU, FMUL, FMULS, FMULSU
                                int a = Data[16 + ((op >> 4) & 7)];
                                int b = Data[16 + (op & 7)];
                                var product = (((op >> 6) & 2) | ((op >> 3) & 1)) switch
                                {
                                    0 => (sbyte)a * b,
                                    1 => a * b,
                                    2 => (sbyte)a * (sbyte)b,
                                    _ => (sbyte)a * b,
                                };
                                Multiply(product, (op & 0x88) != 0);
                                cycles = 2;
                                break;
                            default:
                                throw Unknown(op);
                        }

                        break;
                    case 1: // CPC
                        Subtract(Data[d], Data[r], Flag(C), keepZero: true);
                        break;
                    case 2: // SBC
                        Data[d] = Subtract(Data[d], Data[r], Flag(C), keepZero: true);
                        break;
                    default: // ADD
                        Data[d] = Add(Data[d], Data[r], 0);
                        break;
                }

                break;
            case 0x1:
                switch ((op >> 10) & 3)
                {
                    case 0: // CPSE
                        if (Data[d] == Data[r])
                        {
                            cycles += Skip(ref next);
                        }

                        break;
                    case 1: // CP
                        Subtract(Data[d], Data[r], 0, keepZero: false);
                        break;
                    case 2: // SUB
                        Data[d] = Subtract(Data[d], Data[r], 0, keepZero: false);
                        break;
                    default: // ADC
                        Data[d] = Add(Data[d], Data[r], Flag(C));
                        break;
                }

                break;
            case 0x2:
                switch ((op >> 10) & 3)
                {
                    case 0: // AND
                        Data[d] = Logic(Data[d] & Data[r]);
                        break;
                    case 1: // EOR
                        Data[d] = Logic(Data[d] ^ Data[r]);
                        break;
                    case 2: // OR
                        Data[d] = Logic(Data[d] | Data[r]);
                        break;
                    default: // MOV
                        Data[d] = Data[r];
                        break;
                }

                break;
            case 0x3: // CPI
                Subtract(Data[immediateRegister], immediate, 0, keepZero: false);
                break;
            case 0x4: // SBCI
                Data[immediateRegister] = Subtract(Data[immediateRegister], immediate, Flag(C), keepZero: true);
                break;
            case 0x5: // SUBI
                Data[immediateRegister] = Subtract(Data[immediateRegister], immediate, 0, keepZero: false);
                break;
            case 0x6: // ORI
                Data[immediateRegister] = Logic(Data[immediateRegister] | immediate);
                break;
            case 0x7: // ANDI
                Data[immediateRegister] = Logic(Data[immediateRegister] & immediate);
                break;
            case 0x8:
            case 0xA: // LDD and STD: Y or Z plus a displacement
                var displacement = ((op >> 8) & 0x20) | ((op >> 7) & 0x18) | (op & 7);
                var address = Word((op & 8) != 0 ? 28 : 30) + displacement;
                if ((op & 0x0200) != 0)
                {
                    Write(address, Data[d]);
                }
                else
                {
                    Data[d] = Read(address);
                }

                cycles = 2;
                break;
            case 0x9:
                cycles = Group9(op, d, r, ref next);
                break;
            case 0xB: // IN and OUT
                var io = 0x20 + (((op >> 5) & 0x30) | (op & 0x0F));
                if ((op & 0x0800) != 0)
                {
                    Write(io, Data[d]);
                }
                else
                {
                    Data[d] = Read(io);
                }

                break;
            case 0xC: // RJMP
                next = Pc + 1 + SignExtend(op & 0x0FFF, 12);
                cycles = 2;
                break;
            case 0xD: // RCALL
                PushReturn(next);
                next = Pc + 1 + SignExtend(op & 0x0FFF, 12);
                cycles = 3;
                break;
            case 0xE: // LDI
                Data[immediateRegister] = (byte)immediate;
                break;
            default:
                if ((op & 0x0800) == 0)
                {
                    // BRBS (bit 10 clear) and BRBC
                    var set = Flag(op & 7) == 1;
                    if (set == ((op & 0x0400) == 0))
                    {
                        next = Pc + 1 + SignExtend((op >> 3) & 0x7F, 7);
                        cycles = 2;
                    }
                }
                else if ((op & 8) != 0)
                {
                    throw Unknown(op);
                }
                else
                {
                    var bit = op & 7;
                    switch ((op >> 9) & 3)
                    {
                        case 0: // BLD
                            Data[d] = (byte)((Data[d] & ~(1 << bit)) | (Flag(T) << bit));
                            break;
                        case 1: // BST
                            SetFlag(T, (Data[d] >> bit) & 1);
                            break;
                        case 2: // SBRC
                            if (((Data[d] >> bit) & 1) == 0)
                            {
                                cycles += Skip(ref next);
                            }

                            break;
                        default: // SBRS
                            if (((Data[d] >> bit) & 1) == 1)
                            {
                                cycles += Skip(ref next);
                            }

                            break;
                    }
                }

                break;
        }

        Pc = next;
        Cycles += cycles;
    }

    private int Group9(int op, int d, int r, ref int next)
    {
        switch ((op >> 9) & 7)
        {
            case 0: // loads: LDS, LD, LPM, POP
                switch (op & 0x0F)
                {
                    case 0x0:
                        Data[d] = Read(Fetch(Pc + 1));
                        next = Pc + 2;
                        return 2;
                    case 0x1:
                        Data[d] = Read(PostIncrement(30));
                        return 2;
                    case 0x2:
                        Data[d] = Read(PreDecrement(30));
                        return 2;
                    case 0x4:
                        Data[d] = ReadProgram(Word(30));
                        return 3;
                    case 0x5:
                        Data[d] = ReadProgram(PostIncrement(30));
                        return 3;
                    case 0x9:
                        Data[d] = Read(PostIncrement(28));
                        return 2;
                    case 0xA:
                        Data[d] = Read(PreDecrement(28));
                        return 2;
                    case 0xC:
                        Data[d] = Read(Word(26));
                        return 2;
                    case 0xD:
                        Data[d] = Read(PostIncrement(26));
                        return 2;
                    case 0xE:
                        Data[d] = Read(PreDecrement(26));
                        return 2;
                    case 0xF:
                        Data[d] = Pop();
                        return 2;
                    default:
                        throw Unknown(op);
                }

            case 1: // stores: STS, ST, PUSH
                switch (op & 0x0F)
                {
                    case 0x0:
                        Write(Fetch(Pc + 1), Data[d]);
                        next = Pc + 2;
                        return 2;
                    case 0x1:
                        Write(PostIncrement(30), Data[d]);
                        return 2;
                    case 0x2:
                        Write(PreDecrement(30), Data[d]);
                        return 2;
                    case 0x9:
                        Write(PostIncrement(28), Data[d]);
                        return 2;
                    case 0xA:
                        Write(PreDecrement(28), Data[d]);
                        return 2;
                    case 0xC:
                        Write(Word(26), Data[d]);
                        return 2;
                    case 0xD:
                        Write(PostIncrement(26), Data[d]);
                        return 2;
                    case 0xE:
                        Write(PreDecrement(26), Data[d]);
                        return 2;
                    case 0xF:
                        Push(Data[d]);
                        return 2;
                    default:
                        throw Unknown(op);
                }

            case 2: // one register, and the instructions with none
                return OneRegister(op, d, ref next);
            case 3: // ADIW, SBIW
                var low = 24 + ((op >> 4) & 3) * 2;
                var constant = ((op >> 2) & 0x30) | (op & 0x0F);
                var value = Word(low);
                int result;
                if ((op & 0x0100) == 0)
                {
                    result = (value + constant) & 0xFFFF;
                    SetFlag(V, (~value & result & 0x8000) != 0);
                    SetFlag(C, (~result & value & 0x8000) != 0);
                }
                else
                {
                    result = (value - constant) & 0xFFFF;
                    SetFlag(V, (value & ~result & 0x8000) != 0);
                    SetFlag(C, (result & ~value & 0x8000) != 0);
                }

                SetFlag(N, (result & 0x8000) != 0);
                SetFlag(Z, result == 0);
                SetFlag(S, Flag(N) ^ Flag(V));
                Data[low] = (byte)result;
                Data[low + 1] = (byte)(result >> 8);
                return 2;
            case 4: // CBI, SBIC
            case 5: // SBI, SBIS
                var port = 0x20 + ((op >> 3) & 0x1F);
                var mask = 1 << (op & 7);
                switch ((op >> 8) & 3)
                {
                    case 0:
                        Write(port, (byte)(Read(port) & ~mask));
                        return 2;
                    case 1:
                        return (Read(port) & mask) == 0 ? 1 + Skip(ref next) : 1;
                    case 2:
                        Write(port, (byte)(Read(port) | mask));
                        return 2;
                    default:
                        return (Read(port) & mask) != 0 ? 1 + Skip(ref next) : 1;
                }

            default: // MUL
                Multiply(Data[d] * Data[r], false);
                return 2;
        }
    }

    private int OneRegister(int op, int d, ref int next)
    {
        switch (op & 0x0F)
        {
            case 0x0: // COM
                Data[d] = Logic(~Data[d] & 0xFF);
                SetFlag(C, 1);
                return 1;
            case 0x1: // NEG
                Data[d] = Subtract(0, Data[d], 0, keepZero: false);
                return 1;
            case 0x2: // SWAP
                Data[d] = (byte)((Data[d] << 4) | (Data[d] >> 4));
                return 1;
            case 0x3: // INC
                Data[d]++;
                SetFlag(V, Data[d] == 0x80);
                SetNegativeZero(Data[d]);
                return 1;
            case 0x5: // ASR
                Shift(d, (Data[d] >> 1) | (Data[d] & 0x80));
                return 1;
            case 0x6: // LSR
                Shift(d, Data[d] >> 1);
                return 1;
            case 0x7: // ROR
                Shift(d, (Data[d] >> 1) | (Flag(C) << 7));
                return 1;
            case 0xA: // DEC
                Data[d]--;
                SetFlag(V, Data[d] == 0x7F);
                SetNegativeZero(Data[d]);
                return 1;
            case 0x8:
                return NoRegister(op, ref next);
            case 0x9: // IJMP, ICALL
                if (op == 0x9509)
                {
                    PushReturn(next);
                    next = Word(30);
                    return 3;
                }

                if (op == 0x9409)
                {
                    next = Word(30);
                    return 2;
                }

                throw Unknown(op);
            case 0xC:
            case 0xD: // JMP
                next = (((op >> 3) & 0x3E) | (op & 1)) << 16 | Fetch(Pc + 1);
                return 3;
            case 0xE:
            case 0xF: // CALL
                PushReturn(Pc + 2);
                next = (((op >> 3) & 0x3E) | (op & 1)) << 16 | Fetch(Pc + 1);
                return 4;
            default:
                throw Unknown(op);
        }
    }

    private int NoRegister(int op, ref int next)
    {
        if ((op & 0xFF0F) == 0x9408)
        {
            // BSET (bit 7 clear) and BCLR
            SetFlag((op >> 4) & 7, (op & 0x80) == 0);
            return 1;
        }

        switch (op)
        {
            case 0x9508: // RET
            case 0x9518: // RETI
                var high = Pop();
                var target = (high << 8) | Pop();
                if (op == 0x9518)
                {
                    SetFlag(I, 1);
                }

                next = target;
                return 4;
            case 0x9588: // SLEEP
                if (Flag(I) == 0)
                {
                    Stopped = StopReason.Sleep;
                }

                return 1;
            case 0x9598: // BREAK
                Stopped = StopReason.Break;
                return 1;
            case 0x95A8: // WDR
                return 1;
            case 0x95C8: // LPM r0, Z
                Data[0] = ReadProgram(Word(30));
                return 3;
            default:
                throw Unknown(op);
        }
    }

    /// <summary>How many more cycles a skip takes: it skips one word or two (LDS, STS, JMP, CALL).</summary>
    private int Skip(ref int next)
    {
        var skipped = Fetch(next);
        var words = (skipped & 0xFE0F) is 0x9000 or 0x9200 || (skipped & 0xFE0C) == 0x940C ? 2 : 1;
        next += words;
        return words;
    }

    private byte Add(int a, int b, int carry)
    {
        var sum = a + b + carry;
        var result = (byte)sum;
        SetFlag(H, (a & 0x0F) + (b & 0x0F) + carry > 0x0F);
        SetFlag(C, sum > 0xFF);
        SetFlag(V, ((a ^ result) & (b ^ result) & 0x80) != 0);
        SetNegativeZero(result);
        return result;
    }

    /// <summary>a - b - carry; CPC, SBC and SBCI keep Z only when it was set and the result is zero.</summary>
    private byte Subtract(int a, int b, int carry, bool keepZero)
    {
        var difference = a - b - carry;
        var result = (byte)difference;
        SetFlag(H, (a & 0x0F) - (b & 0x0F) - carry < 0);
        SetFlag(C, difference < 0);
        SetFlag(V, ((a ^ b) & (a ^ result) & 0x80) != 0);
        var zero = result == 0 && (!keepZero || Flag(Z) == 1);
        SetFlag(N, (result & 0x80) != 0);
        SetFlag(Z, zero);
        SetFlag(S, Flag(N) ^ Flag(V));
        return result;
    }

    private byte Logic(int value)
    {
        var result = (byte)value;
        SetFlag(V, 0);
        SetNegativeZero(result);
        return result;
    }

    /// <summary>ASR, LSR, ROR: the low bit into C, V = N ^ C.</summary>
    private void Shift(int d, int value)
    {
        SetFlag(C, Data[d] & 1);
        Data[d] = (byte)value;
        SetFlag(N, (Data[d] & 0x80) != 0);
        SetFlag(V, Flag(N) ^ Flag(C));
        SetFlag(Z, Data[d] == 0);
        SetFlag(S, Flag(N) ^ Flag(V));
    }

    /// <summary>The product into r1:r0; FMUL's is shifted left once.</summary>
    private void Multiply(int product, bool fractional)
    {
        var bits = product & 0xFFFF;
        SetFlag(C, (bits & 0x8000) != 0);
        if (fractional)
        {
            bits = (bits << 1) & 0xFFFF;
        }

        SetFlag(Z, bits == 0);
        Data[0] = (byte)bits;
        Data[1] = (byte)(bits >> 8);
    }

    /// <summary>N, Z and S = N ^ V of a result.</summary>
    private void SetNegativeZero(byte result)
    {
        SetFlag(N, (result & 0x80) != 0);
        SetFlag(Z, result == 0);
        SetFlag(S, Flag(N) ^ Flag(V));
    }

    private int Flag(int bit) => (Data[Sreg] >> bit) & 1;

    private void SetFlag(int bit, bool value) => SetFlag(bit, value ? 1 : 0);

    private void SetFlag(int bit, int value) => Data[Sreg] = (byte)((Data[Sreg] & ~(1 << bit)) | (value << bit));

    private int Word(int low) => Data[low] | (Data[low + 1] << 8);

    private int PostIncrement(int low)
    {
        var address = Word(low);
        SetWord(low, address + 1);
        return address;
    }

    private int PreDecrement(int low)
    {
        var address = (Word(low) - 1) & 0xFFFF;
        SetWord(low, address);
        return address;
    }

    private void SetWord(int low, int value)
    {
        Data[low] = (byte)value;
        Data[low + 1] = (byte)(value >> 8);
    }

    private void Push(byte value)
    {
        Write(Sp, value);
        Sp = (Sp - 1) & 0xFFFF;
    }

    private byte Pop()
    {
        Sp = (Sp + 1) & 0xFFFF;
        return Read(Sp);
    }

    /// <summary>A return address on the stack, as CALL, RCALL and ICALL push it: the low byte first.</summary>
    private void PushReturn(int address)
    {
        Push((byte)address);
        Push((byte)(address >> 8));
    }

    private byte Read(int address) => address < Data.Length
        ? Data[address]
        : throw new InvalidOperationException($"A read of 0x{address:X4}, outside RAM, at 0x{Pc * 2:X4}.");

    private void Write(int address, byte value)
    {
        if (address >= Data.Length)
        {
            throw new InvalidOperationException($"A write to 0x{address:X4}, outside RAM, at 0x{Pc * 2:X4}.");
        }

        Data[address] = value;
    }

    private byte ReadProgram(int byteAddress) => byteAddress / 2 < Program.Length
        ? (byte)(Program[byteAddress / 2] >> (8 * (byteAddress & 1)))
        : throw new InvalidOperationException($"An LPM of 0x{byteAddress:X4}, outside flash, at 0x{Pc * 2:X4}.");

    private int Fetch(int word) => word < Program.Length
        ? Program[word]
        : throw new InvalidOperationException($"The program ran to 0x{word * 2:X}, outside flash.");

    private static int SignExtend(int value, int bits) => (value << (32 - bits)) >> (32 - bits);

    private InvalidOperationException Unknown(int op) =>
        new($"The instruction 0x{op:X4} at 0x{Pc * 2:X4} is not one this core runs.");
}
