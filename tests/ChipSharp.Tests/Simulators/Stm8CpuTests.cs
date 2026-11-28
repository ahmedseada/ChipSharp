using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Simulators;
using ChipSharp.Simulators.Stm8;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Toolchain.Sdcc;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// The STM8 core, an instruction at a time, on a flat memory: the flags of the ALU (PM0044's V, H, N, Z, C), every
/// addressing mode, which register the word instructions take with which prefix, the stack, interrupts, jumps and the
/// bit instructions; and every form of every instruction, as SDCC's assembler encodes it, decoded to its length.
/// </summary>
public sealed partial class Stm8CpuTests
{
    private const byte V = Stm8Cpu.V, H = Stm8Cpu.H, N = Stm8Cpu.N, Z = Stm8Cpu.Z, C = Stm8Cpu.C;

    /// <summary>A flat 24-bit memory: what is not written reads a byte of its address.</summary>
    private sealed class FlatBus : IStm8Bus
    {
        private readonly Dictionary<int, byte> _bytes = [];

        public byte Read(int address) => _bytes.TryGetValue(address, out var value) ? value : (byte)(address ^ (address >> 8) ^ 0x5A);

        public void Write(int address, byte value) => _bytes[address] = value;

        public void Load(int address, params byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                Write(address + i, bytes[i]);
            }
        }
    }

    /// <summary>A core with <paramref name="code"/> at 0x8000, SP at 0x03FF, interrupts off.</summary>
    private static (Stm8Cpu Cpu, FlatBus Bus) Core(string code)
    {
        var bus = new FlatBus();
        bus.Load(0x8000, Convert.FromHexString(code.Replace(" ", "", StringComparison.Ordinal)));
        return (new Stm8Cpu(bus) { PC = 0x8000, SP = 0x03FF }, bus);
    }

    private static byte Flags(Stm8Cpu cpu, byte mask = V | H | N | Z | C) => (byte)(cpu.CC & mask);

    [Theory]
    // add a, #byte (AB): V, H, N, Z, C
    [InlineData("AB01", 0x7F, false, 0x80, V | H | N)]
    [InlineData("AB01", 0xFF, false, 0x00, H | Z | C)]
    [InlineData("AB80", 0x80, false, 0x00, V | Z | C)]
    [InlineData("AB10", 0x05, false, 0x15, 0)]
    // adc (A9): the carry in, and its half carry
    [InlineData("A900", 0x0F, true, 0x10, H)]
    [InlineData("A9FF", 0x00, true, 0x00, H | Z | C)]
    // sub (A0): a borrow sets C; V for the signed overflow; H stays as it was (clear here)
    [InlineData("A001", 0x00, false, 0xFF, N | C)]
    [InlineData("A001", 0x80, false, 0x7F, V)]
    [InlineData("A07F", 0x7F, false, 0x00, Z)]
    // sbc (A2)
    [InlineData("A200", 0x00, true, 0xFF, N | C)]
    [InlineData("A2FF", 0x7F, true, 0x7F, C)]
    // and, or, xor, ld: N and Z only (C stays set when it was)
    [InlineData("A40F", 0xF0, true, 0x00, Z | C)]
    [InlineData("AA80", 0x01, false, 0x81, N)]
    [InlineData("A8FF", 0xFF, false, 0x00, Z)]
    [InlineData("A680", 0x00, false, 0x80, N)]
    public void The_8_bit_alu_sets_the_flags_PM0044_gives(string code, byte a, bool carry, byte result, int flags)
    {
        var (cpu, _) = Core(code);
        cpu.A = a;
        cpu.CC = (byte)(cpu.CC | (carry ? C : 0));

        Assert.Equal(1, cpu.Step());

        Assert.Equal(result, cpu.A);
        Assert.Equal(flags, Flags(cpu));
        Assert.Equal(0x8002, cpu.PC);
    }

    [Theory]
    [InlineData("A101", 0x00, N | C)] // cp a, #1
    [InlineData("A180", 0x7F, V | N | C)]
    [InlineData("A542", 0x02, 0)]     // bcp a, #0x42: A & 0x42
    [InlineData("A580", 0x7F, Z)]
    public void Compares_set_the_flags_and_leave_A(string code, byte a, int flags)
    {
        var (cpu, _) = Core(code);
        cpu.A = a;

        cpu.Step();

        Assert.Equal(a, cpu.A);
        Assert.Equal(flags, Flags(cpu));
    }

    [Theory]
    // addw x, #word (1C): H is the carry from bit 7
    [InlineData("1C0001", 0x00FF, 0x0100, H)]
    [InlineData("1C0001", 0x7FFF, 0x8000, V | N | H)]
    [InlineData("1C0001", 0xFFFF, 0x0000, H | Z | C)]
    [InlineData("1C8000", 0x8000, 0x0000, V | Z | C)]
    // subw x, #word (1D): H is the borrow into bit 8
    [InlineData("1D0001", 0x0100, 0x00FF, H)]
    [InlineData("1D0001", 0x0000, 0xFFFF, H | N | C)]
    [InlineData("1D0001", 0x8000, 0x7FFF, V | H)]
    public void Word_additions_set_the_flags_PM0044_gives(string code, int x, int result, int flags)
    {
        var (cpu, _) = Core(code);
        cpu.X = (ushort)x;

        Assert.Equal(2, cpu.Step());

        Assert.Equal(result, cpu.X);
        Assert.Equal(flags, Flags(cpu));
    }

    [Fact]
    public void Addw_and_subw_of_Y_and_of_memory_take_the_prefix_72()
    {
        var (cpu, bus) = Core("72A90102 72B20010 72BB0010 72F003");
        bus.Load(0x0010, 0x00, 0x05);
        bus.Load(0x03FF + 3, 0x00, 0x02);
        cpu.Y = 0x1000;
        cpu.X = 0x0001;

        cpu.Step(); // addw y, #0x0102
        Assert.Equal(0x1102, cpu.Y);
        cpu.Step(); // subw y, 0x0010
        Assert.Equal(0x10FD, cpu.Y);
        cpu.Step(); // addw x, 0x0010
        Assert.Equal(0x0006, cpu.X);
        cpu.Step(); // subw x, (3, sp)
        Assert.Equal(0x0004, cpu.X);
    }

    [Theory]
    [InlineData("A31234", 0x1234, Z)]          // cpw x, #0x1234
    [InlineData("A31235", 0x1234, N | C)]
    [InlineData("A30001", 0x8000, V)]
    public void Cpw_compares_words(string code, int x, int flags)
    {
        var (cpu, _) = Core(code);
        cpu.X = (ushort)x;

        cpu.Step();

        Assert.Equal(x, cpu.X);
        Assert.Equal(flags, Flags(cpu));
    }

    [Theory]
    // read-modify-write on A: neg, cpl, srl, rrc, sra, sll, rlc, dec, inc, tnz, swap, clr
    [InlineData("40", 0x80, false, 0x80, V | N | C)]
    [InlineData("40", 0x00, false, 0x00, Z)]
    [InlineData("40", 0x01, false, 0xFF, N | C)]
    [InlineData("43", 0x0F, false, 0xF0, N | C)]
    [InlineData("44", 0x81, false, 0x40, C)]
    [InlineData("46", 0x81, true, 0xC0, N | C)]
    [InlineData("47", 0x81, false, 0xC0, N | C)]
    [InlineData("48", 0x81, false, 0x02, C)]
    [InlineData("49", 0x81, true, 0x03, C)]
    [InlineData("4A", 0x80, false, 0x7F, V)]
    [InlineData("4A", 0x01, true, 0x00, Z | C)]
    [InlineData("4C", 0x7F, false, 0x80, V | N)]
    [InlineData("4C", 0xFF, false, 0x00, Z)]
    [InlineData("4D", 0x80, true, 0x80, N | C)]
    [InlineData("4E", 0x12, false, 0x21, 0)]
    [InlineData("4F", 0x80, true, 0x00, Z | C)]
    public void Read_modify_write_operations_on_A(string code, byte a, bool carry, byte result, int flags)
    {
        var (cpu, _) = Core(code);
        cpu.A = a;
        cpu.CC = (byte)(cpu.CC | (carry ? C : 0));

        cpu.Step();

        Assert.Equal(result, cpu.A);
        Assert.Equal(flags, Flags(cpu));
    }

    [Theory]
    [InlineData("50", 0x8000, false, 0x8000, V | N | C)]       // negw x
    [InlineData("50", 0x0001, false, 0xFFFF, N | C)]
    [InlineData("53", 0x00FF, false, 0xFF00, N | C)]           // cplw
    [InlineData("54", 0x8001, false, 0x4000, C)]               // srlw
    [InlineData("56", 0x0001, true, 0x8000, N | C)]            // rrcw
    [InlineData("57", 0x8001, false, 0xC000, N | C)]           // sraw
    [InlineData("58", 0x8001, false, 0x0002, C)]               // sllw
    [InlineData("59", 0x8000, true, 0x0001, C)]                // rlcw
    [InlineData("5A", 0x8000, false, 0x7FFF, V)]               // decw
    [InlineData("5C", 0x7FFF, false, 0x8000, V | N)]           // incw
    [InlineData("5D", 0x0000, false, 0x0000, Z)]               // tnzw
    [InlineData("5E", 0x1234, false, 0x3412, 0)]               // swapw
    [InlineData("5F", 0x1234, false, 0x0000, Z)]               // clrw
    public void Word_operations_on_X(string code, int x, bool carry, int result, int flags)
    {
        var (cpu, _) = Core(code);
        cpu.X = (ushort)x;
        cpu.CC = (byte)(cpu.CC | (carry ? C : 0));

        cpu.Step();

        Assert.Equal(result, cpu.X);
        Assert.Equal(flags, Flags(cpu));
    }

    [Fact]
    public void Word_operations_on_Y_take_the_prefix_90()
    {
        var (cpu, _) = Core("905C 9050");
        cpu.Y = 0x00FF;

        cpu.Step();
        Assert.Equal(0x0100, cpu.Y);
        cpu.Step();
        Assert.Equal(0xFF00, cpu.Y);
    }

    [Fact]
    public void Mul_multiplies_XL_by_A_and_clears_H_and_C()
    {
        var (cpu, _) = Core("42 9042");
        cpu.X = 0x12FF;
        cpu.Y = 0x0003;
        cpu.A = 0xFF;
        cpu.CC |= H | C;

        Assert.Equal(4, cpu.Step());
        Assert.Equal(0xFE01, cpu.X);
        Assert.Equal(0, Flags(cpu, H | C));
        cpu.Step();
        Assert.Equal(0x02FD, cpu.Y);
    }

    [Fact]
    public void Div_and_divw_give_the_quotient_and_remainder_and_C_for_a_division_by_zero()
    {
        var (cpu, _) = Core("62 65 62 65");
        cpu.X = 1000;
        cpu.A = 7;

        cpu.Step(); // div x, a
        Assert.Equal((142, 6), (cpu.X, cpu.A));
        Assert.Equal(0, Flags(cpu));

        cpu.X = 50000;
        cpu.Y = 7;
        cpu.Step(); // divw x, y
        Assert.Equal((7142, 6), (cpu.X, cpu.Y));

        cpu.X = 1234;
        cpu.A = 0;
        cpu.Step(); // div x, 0: C, X unchanged
        Assert.Equal((1234, 0), (cpu.X, cpu.A));
        Assert.Equal(C, Flags(cpu));

        cpu.X = 5;
        cpu.Y = 9;
        cpu.Step(); // divw: a quotient of 0 sets Z
        Assert.Equal((0, 5), (cpu.X, cpu.Y));
        Assert.Equal(Z, Flags(cpu));
    }

    [Fact]
    public void Rlwa_and_rrwa_rotate_A_through_X_by_a_byte()
    {
        var (cpu, _) = Core("02 01");
        cpu.X = 0x1234;
        cpu.A = 0x56;

        cpu.Step(); // rlwa x: A <- XH <- XL <- A
        Assert.Equal((0x3456, 0x12), (cpu.X, cpu.A));
        cpu.Step(); // rrwa x: A -> XH -> XL -> A
        Assert.Equal((0x1234, 0x56), (cpu.X, cpu.A));
    }

    [Fact]
    public void Exchanges_and_moves_leave_the_flags()
    {
        var (cpu, bus) = Core("41 61 51 311000 35AA1001 451011 5510011002");
        bus.Load(0x1000, 0x99);
        cpu.A = 0x01;
        cpu.X = 0x1122;
        cpu.Y = 0x3344;
        cpu.CC |= Z | N;

        cpu.Step(); // exg a, xl
        Assert.Equal((0x22, 0x1101), (cpu.A, cpu.X));
        cpu.Step(); // exg a, yl
        Assert.Equal((0x44, 0x3322), (cpu.A, cpu.Y));
        cpu.Step(); // exgw x, y
        Assert.Equal((0x3322, 0x1101), (cpu.X, cpu.Y));
        cpu.Step(); // exg a, 0x1000
        Assert.Equal((0x99, 0x44), (cpu.A, bus.Read(0x1000)));
        cpu.Step(); // mov 0x1001, #0xaa
        Assert.Equal(0xAA, bus.Read(0x1001));
        cpu.Step(); // mov 0x11, 0x10: source first
        Assert.Equal(bus.Read(0x10), bus.Read(0x11));
        cpu.Step(); // mov 0x1002, 0x1001
        Assert.Equal(0xAA, bus.Read(0x1002));
        Assert.Equal(Z | N, Flags(cpu));
    }

    public static TheoryData<string, string> AddressingModes => new()
    {
        { "A6", "#" }, { "B6", "short" }, { "C6", "long" }, { "F6", "(x)" }, { "E6", "(short,x)" }, { "D6", "(long,x)" },
        { "90F6", "(y)" }, { "90E6", "(short,y)" }, { "90D6", "(long,y)" }, { "7B", "(short,sp)" },
        { "92C6", "[short]" }, { "72C6", "[long]" }, { "92D6", "([short],x)" }, { "72D6", "([long],x)" },
        { "91D6", "([short],y)" },
    };

    [Theory]
    [MemberData(nameof(AddressingModes))]
    public void Ld_a_reads_where_each_addressing_mode_points(string opcode, string mode)
    {
        // short operands 0x40 (a pointer to 0x2345 there), long ones 0x1C00 (a pointer to 0x3456)
        var operand = mode.Contains("long", StringComparison.Ordinal) ? "1C00" : "40";
        var (cpu, bus) = Core(opcode + operand);
        bus.Load(0x0040, 0x23, 0x45);
        bus.Load(0x1C00, 0x34, 0x56);
        cpu.X = 0x0100;
        cpu.Y = 0x0200;
        cpu.SP = 0x0300;
        var address = mode switch
        {
            "#" => 0x8000 + opcode.Length / 2,
            "short" => 0x40,
            "long" => 0x1C00,
            "(x)" => 0x0100,
            "(short,x)" => 0x0140,
            "(long,x)" => 0x1D00,
            "(y)" => 0x0200,
            "(short,y)" => 0x0240,
            "(long,y)" => 0x1E00,
            "(short,sp)" => 0x0340,
            "[short]" => 0x2345,
            "[long]" => 0x3456,
            "([short],x)" => 0x2445,
            "([long],x)" => 0x3556,
            _ => 0x2545,
        };
        var length = opcode.Length / 2 + (mode is "(x)" or "(y)" ? 0 : operand.Length / 2);

        cpu.Step();

        Assert.Equal(bus.Read(address), cpu.A);
        Assert.Equal(0x8000 + length, cpu.PC);
    }

    [Theory]
    // ldw loads the register of the index (or X, Y with 90 / 91), and stores the other one
    [InlineData("FE", "x", "x", 0x0100)]          // ldw x, (x)
    [InlineData("90FE", "y", "y", 0x0200)]        // ldw y, (y)
    [InlineData("1E40", "x", "sp", 0x0340)]       // ldw x, (0x40, sp)
    [InlineData("1640", "y", "sp", 0x0340)]       // ldw y, (0x40, sp)
    [InlineData("92CE40", "x", "-", 0x2345)]      // ldw x, [0x40]
    [InlineData("91CE40", "y", "-", 0x2345)]      // ldw y, [0x40]
    [InlineData("91DE40", "y", "y", 0x2545)]      // ldw y, ([0x40], y)
    [InlineData("72DE1C00", "x", "x", 0x3556)]    // ldw x, ([0x1c00], x)
    public void Ldw_loads_the_register_PM0044_gives(string code, string register, string index, int address)
    {
        var (cpu, bus) = Core(code);
        bus.Load(0x0040, 0x23, 0x45);
        bus.Load(0x1C00, 0x34, 0x56);
        cpu.X = 0x0100;
        cpu.Y = 0x0200;
        cpu.SP = 0x0300;
        _ = index;

        cpu.Step();

        var expected = (bus.Read(address) << 8) | bus.Read(address + 1);
        Assert.Equal(expected, register == "x" ? cpu.X : cpu.Y);
    }

    [Theory]
    [InlineData("FF", "y", 0x0100)]               // ldw (x), y
    [InlineData("90FF", "x", 0x0200)]             // ldw (y), x
    [InlineData("1F40", "x", 0x0340)]             // ldw (0x40, sp), x
    [InlineData("1740", "y", 0x0340)]             // ldw (0x40, sp), y
    [InlineData("BF40", "x", 0x0040)]             // ldw 0x40, x
    [InlineData("90BF40", "y", 0x0040)]           // ldw 0x40, y
    [InlineData("92DF40", "y", 0x2445)]           // ldw ([0x40], x), y
    [InlineData("91DF40", "x", 0x2545)]           // ldw ([0x40], y), x
    public void Ldw_stores_the_register_PM0044_gives(string code, string register, int address)
    {
        var (cpu, bus) = Core(code);
        bus.Load(0x0040, 0x23, 0x45);
        cpu.X = 0x0100;
        cpu.Y = 0x0200;
        cpu.SP = 0x0300;
        var value = register == "x" ? cpu.X : cpu.Y;

        cpu.Step();

        Assert.Equal(value, (bus.Read(address) << 8) | bus.Read(address + 1));
    }

    [Theory]
    [InlineData("F3", "y", 0x0100)]       // cpw y, (x)
    [InlineData("90F3", "x", 0x0200)]     // cpw x, (y)
    [InlineData("1340", "x", 0x0340)]     // cpw x, (0x40, sp)
    [InlineData("90A31000", "y", -1)]     // cpw y, #0x1000
    [InlineData("92D340", "y", 0x2445)]   // cpw y, ([0x40], x)
    [InlineData("91C340", "y", 0x2345)]   // cpw y, [0x40]
    public void Cpw_compares_the_register_PM0044_gives(string code, string register, int address)
    {
        var (cpu, bus) = Core(code);
        bus.Load(0x0040, 0x23, 0x45);
        if (address >= 0)
        {
            bus.Load(address, 0x10, 0x00);
        }

        // the register compared is 0x1001, one more than the word: no flags; the other one, an index, is below it: C
        cpu.X = register == "x" ? (ushort)0x1001 : (ushort)0x0100;
        cpu.Y = register == "y" ? (ushort)0x1001 : (ushort)0x0200;
        cpu.SP = 0x0300;

        cpu.Step();

        Assert.Equal(0, Flags(cpu, Z | C | N));
    }

    [Fact]
    public void Push_pop_call_and_ret_use_the_stack_downwards()
    {
        var (cpu, bus) = Core("88 89 CD9000");
        bus.Load(0x9000, 0x84, 0x85, 0x81);
        cpu.A = 0xAB;
        cpu.X = 0x1234;

        cpu.Step(); // push a
        Assert.Equal((0x03FE, 0xAB), (cpu.SP, bus.Read(0x03FF)));
        cpu.Step(); // pushw x: XL, then XH, so the word is at SP+1, high byte first
        Assert.Equal((0x03FC, 0x12, 0x34), (cpu.SP, bus.Read(0x03FD), bus.Read(0x03FE)));
        Assert.Equal(4, cpu.Step()); // call 0x9000: PCL, then PCH
        Assert.Equal((0x9000, 0x03FA, 0x80, 0x05), (cpu.PC, cpu.SP, bus.Read(0x03FB), bus.Read(0x03FC)));
        cpu.SP = 0x03FC; // as if the call had not pushed: pop the word and the byte
        cpu.Step(); // pop a: 0x12
        cpu.Step(); // popw x: 0x34AB
        Assert.Equal((0x12, 0x34AB, 0x03FF), (cpu.A, cpu.X, cpu.SP));
    }

    [Fact]
    public void Callf_and_retf_keep_24_bits_callr_is_relative()
    {
        var (cpu, bus) = Core("8D012345 AD10");
        bus.Load(0x012345, 0x87); // retf

        Assert.Equal(5, cpu.Step());
        Assert.Equal((0x012345, 0x03FC), (cpu.PC, cpu.SP));
        Assert.Equal((0x00, 0x80, 0x04), (bus.Read(0x03FD), bus.Read(0x03FE), bus.Read(0x03FF)));
        cpu.Step();
        Assert.Equal((0x8004, 0x03FF), (cpu.PC, cpu.SP));
        cpu.Step(); // callr .+0x12
        Assert.Equal(0x8006 + 0x10, cpu.PC);
    }

    [Fact]
    public void An_interrupt_saves_the_context_as_PM0044_says_and_iret_restores_it()
    {
        var (cpu, bus) = Core("9D");
        bus.Load(0x9000, 0x80); // iret
        cpu.A = 0x11;
        cpu.X = 0x2233;
        cpu.Y = 0x4455;
        cpu.CC = (byte)(Stm8Cpu.I1 | C); // level 0

        Assert.Equal(9, cpu.Interrupt(0x9000, 1)); // level 1 (I1:I0 = 01)

        Assert.Equal(0x9000, cpu.PC);
        Assert.Equal(1, cpu.Level);
        Assert.Equal(0x03FF - 9, cpu.SP);
        // from SP+1 up: CC, A, XH, XL, YH, YL, PCE, PCH, PCL
        byte[] saved = [(byte)(Stm8Cpu.I1 | C), 0x11, 0x22, 0x33, 0x44, 0x55, 0x00, 0x80, 0x00];
        Assert.Equal(saved, Enumerable.Range(0x03F7, 9).Select(bus.Read));
        cpu.A = 0;
        cpu.X = 0;
        cpu.Step();
        Assert.Equal((0x8000, 0x11, 0x2233, 0x4455, 0x03FF, 0), (cpu.PC, cpu.A, cpu.X, cpu.Y, cpu.SP, cpu.Level));
    }

    [Fact]
    public void Trap_jumps_through_the_vector_at_0x8004_with_interrupts_masked()
    {
        var (cpu, bus) = Core("83");
        bus.Load(0x8004, 0x82, 0x00, 0x91, 0x23);
        cpu.CC = Stm8Cpu.I1;

        Assert.Equal(9, cpu.Step());

        Assert.Equal((0x9123, 3), (cpu.PC, cpu.Level));
    }

    [Theory]
    [InlineData("20", 0, true)]                 // jra
    [InlineData("21", 0, false)]                // jrf
    [InlineData("22", 0, true)]                 // jrugt
    [InlineData("22", Z, false)]
    [InlineData("23", C, true)]                 // jrule
    [InlineData("24", C, false)]                // jrnc
    [InlineData("25", C, true)]                 // jrc
    [InlineData("26", Z, false)]                // jrne
    [InlineData("27", Z, true)]                 // jreq
    [InlineData("28", V, false)]                // jrnv
    [InlineData("29", V, true)]                 // jrv
    [InlineData("2A", N, false)]                // jrpl
    [InlineData("2B", N, true)]                 // jrmi
    [InlineData("2C", N | V, true)]             // jrsgt: Z = 0, N = V
    [InlineData("2C", N, false)]
    [InlineData("2D", Z, true)]                 // jrsle
    [InlineData("2E", 0, true)]                 // jrsge
    [InlineData("2F", V, true)]                 // jrslt: N != V
    [InlineData("9028", H, false)]              // jrnh
    [InlineData("9029", H, true)]               // jrh
    [InlineData("902D", Stm8Cpu.I1 | Stm8Cpu.I0, true)] // jrm
    [InlineData("902C", Stm8Cpu.I1, true)]      // jrnm
    public void Conditional_jumps_take_1_cycle_and_2_when_taken(string opcode, int cc, bool taken)
    {
        var (cpu, _) = Core(opcode + "10");
        cpu.CC = (byte)cc;

        var cycles = cpu.Step();

        var next = 0x8000 + opcode.Length / 2 + 1;
        Assert.Equal(taken ? next + 0x10 : next, cpu.PC);
        Assert.Equal(taken ? 2 : 1, cycles);
    }

    [Fact]
    public void Bit_instructions_set_reset_complement_copy_and_test()
    {
        var (cpu, bus) = Core("721E1000 72111000 90101000 90131000 72001000F0 72031000F0");
        bus.Load(0x1000, 0x01);
        cpu.CC = C;

        cpu.Step(); // bset 0x1000, #7
        Assert.Equal(0x81, bus.Read(0x1000));
        cpu.Step(); // bres 0x1000, #0
        Assert.Equal(0x80, bus.Read(0x1000));
        cpu.Step(); // bcpl 0x1000, #0
        Assert.Equal(0x81, bus.Read(0x1000));
        cpu.Step(); // bccm 0x1000, #1: C
        Assert.Equal(0x83, bus.Read(0x1000));
        cpu.CC = 0;
        Assert.Equal(3, cpu.Step()); // btjt 0x1000, #0, .-16: set, so it jumps, C = 1
        Assert.Equal((0x8015 - 16, C), (cpu.PC, Flags(cpu)));
        cpu.PC = 0x8015;
        Assert.Equal(2, cpu.Step()); // btjf 0x1000, #1: set, so no jump
        Assert.Equal((0x801A, C), (cpu.PC, Flags(cpu)));
    }

    [Fact]
    public void Ldf_reaches_24_bit_addresses()
    {
        var (cpu, bus) = Core("BC012345 BD012346 AF012300");
        bus.Load(0x012345, 0x77);
        cpu.X = 0x45;

        cpu.Step();
        Assert.Equal(0x77, cpu.A);
        cpu.Step();
        Assert.Equal(0x77, bus.Read(0x012346));
        cpu.Step(); // ldf a, (0x012300, x)
        Assert.Equal(0x77, cpu.A);
    }

    [Fact]
    public void Rim_sim_halt_and_wfi_set_the_interrupt_level()
    {
        var (cpu, _) = Core("9A 9B 8E 9B 8F");

        cpu.Step();
        Assert.Equal(0, cpu.Level);
        cpu.Step();
        Assert.Equal(3, cpu.Level);
        cpu.Step(); // halt: interrupts on
        Assert.Equal((Stm8Sleep.Halt, 0), (cpu.Sleep, cpu.Level));
        cpu.Sleep = Stm8Sleep.None;
        cpu.Step();
        cpu.Step(); // wfi
        Assert.Equal((Stm8Sleep.Wait, 0), (cpu.Sleep, cpu.Level));
    }

    [Fact]
    public void Break_stops_where_it_is()
    {
        var (cpu, _) = Core("9D 8B");

        cpu.Step();
        cpu.Step();

        Assert.True(cpu.BreakHit);
        Assert.Equal(0x8001, cpu.PC);
    }

    [Theory]
    [InlineData("71")]
    [InlineData("75")]
    [InlineData("05")]
    [InlineData("0B")]
    [InlineData("9030")]
    [InlineData("7241")]
    [InlineData("9098")]
    [InlineData("72A6")]
    public void An_opcode_the_STM8_does_not_have_stops_the_run(string code)
    {
        var (cpu, _) = Core(code + "000000");

        var fault = Assert.Throws<ProgramFault>(() => cpu.Step());

        Assert.Contains("an opcode the STM8 does not have", fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Popping_past_the_top_of_the_stack_stops_the_run()
    {
        var (cpu, _) = Core("81");
        cpu.StackTop = 0x03FF;

        var fault = Assert.Throws<ProgramFault>(() => cpu.Step());

        Assert.Contains("popped more than it pushed", fault.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every form of every instruction (each addressing mode, each prefix), assembled by sdasstm8, runs as one
    /// instruction of the length the assembler gave it, and in the cycles its listing gives, but where PM0044 or the
    /// pipeline-free model says otherwise (below).
    /// </summary>
    [SdccFact]
    public void Every_instruction_form_sdasstm8_assembles_decodes_to_its_length()
    {
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "stm8-forms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var forms = Assemble("sdasstm8", Stm8Forms(), folder);
            Assert.True(forms.Count > 550, $"Only {forms.Count} instructions in the listing.");
            var wrongCycles = new List<string>();
            foreach (var (bytes, cycles, text) in forms)
            {
                var (cpu, bus) = Core(Convert.ToHexString(bytes));
                bus.Load(0x8004, 0x82, 0x00, 0x90, 0x00); // trap's vector
                cpu.X = 0x0100;
                cpu.Y = 0x0200;
                cpu.SP = 0x0300;
                cpu.A = 7;
                int took;
                try
                {
                    took = cpu.Step();
                }
                catch (ProgramFault e)
                {
                    throw new Xunit.Sdk.XunitException($"{text} ({Convert.ToHexString(bytes)}): {e.Message}");
                }

                if (!Regex.IsMatch(text, @"^(jp|jpf|call|callf|callr|ret|retf|iret|int|trap|jr\w+|btj[tf]|break)\b"))
                {
                    Assert.True(cpu.PC == 0x8000 + bytes.Length, $"{text} ({Convert.ToHexString(bytes)}) took " +
                        $"{cpu.PC - 0x8000} bytes, not {bytes.Length}");
                }

                if (cycles is { } listed && took != listed && !CyclesDiffer.IsMatch(text))
                {
                    wrongCycles.Add($"{text}: {took}, the listing says {listed}");
                }
            }

            Assert.True(wrongCycles.Count == 0, string.Join('\n', wrongCycles));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Where the cycles differ from sdasstm8's listing, on purpose: a conditional jump (1, and 2 taken; the listing gives
    /// one of them) and btjt / btjf (2, 3 taken), div and divw (2 and one more for every bit of the quotient), halt, wfi,
    /// wfe, int and break (none or one in the listing); and where the listing is at odds with itself: bcp with a pointer
    /// (5, the other operations' 4), ldf with ([longptr.e], y) (1, its X form's 4 and 5), cpw x with Y's modes (1, cpw
    /// y with X's 2), cpw with [longptr.w] (4, with [shortptr.w] 5, as ldw's).
    /// </summary>
    [GeneratedRegex(@"^(jr\w+|btj[tf]|div|divw|halt|wfi|wfe|int|break|bcp a, \(?\[|ldf (a, )?\(\[0x1234\], y\)|cpw x, \(|cpw x, \[0x1234\]|cpw y, \(\[0x1234\])")]
    private static partial Regex CyclesDiffer { get; }

    /// <summary>Assembles <paramref name="lines"/> with <paramref name="assembler"/> (-l) and reads back each instruction's bytes and cycles.</summary>
    internal static List<(byte[] Bytes, int? Cycles, string Text)> Assemble(string assembler, IEnumerable<string> lines, string folder)
    {
        var bin = Path.GetDirectoryName(new SdccToolchain().FindSdcc())!;
        File.WriteAllLines(Path.Combine(folder, "forms.asm"), ["\t.module forms", "\t.area CODE", .. lines.Select(line => "\t" + line)]);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(bin, assembler + ".exe"), "-l -o forms.rel forms.asm")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"{assembler} failed:\n{output}");
        }

        var forms = new List<(byte[], int?, string)>();
        foreach (var line in File.ReadAllLines(Path.Combine(folder, "forms.lst")))
        {
            var match = ListingLine().Match(line);
            if (match.Success)
            {
                var cycles = match.Groups["cycles"].Success ? int.Parse(match.Groups["cycles"].Value, CultureInfo.InvariantCulture) : (int?)null;
                forms.Add((Convert.FromHexString(match.Groups["bytes"].Value.Replace(" ", "", StringComparison.Ordinal)), cycles,
                    match.Groups["text"].Value.Trim()));
            }
        }

        return forms;
    }

    // "      008000 A9 12            [ 1]    3 	adc a, #0x12"
    [GeneratedRegex(@"^\s+[0-9A-F]{6} (?<bytes>(?:[0-9A-F]{2} )+)\s*(?:\[\s*(?<cycles>\d+)\])?\s+\d+\s+(?<text>\S.*)$")]
    private static partial Regex ListingLine();

    /// <summary>Every form of every STM8 instruction, in sdasstm8's syntax.</summary>
    private static IEnumerable<string> Stm8Forms()
    {
        string[] source = ["#0x12", "0x12", "0x1234", "(x)", "(0x12, x)", "(0x1234, x)", "(y)", "(0x12, y)", "(0x1234, y)",
            "(0x12, sp)", "[0x12]", "[0x1234]", "([0x12], x)", "([0x1234], x)", "([0x12], y)"];
        foreach (var op in new[] { "adc", "add", "and", "bcp", "cp", "or", "sbc", "sub", "xor", "ld" })
        {
            foreach (var s in source)
            {
                yield return $"{op} a, {s}";
            }
        }

        foreach (var s in source.Skip(1))
        {
            yield return $"ld {s}, a";
        }

        foreach (var r in new[] { "xl", "yl", "xh", "yh" })
        {
            yield return $"ld {r}, a";
            yield return $"ld a, {r}";
        }

        foreach (var s in new[] { "0x123456", "(0x123456, x)", "(0x123456, y)", "[0x1234]", "([0x1234], x)", "([0x1234], y)" })
        {
            yield return $"ldf a, {s}";
            yield return $"ldf {s}, a";
        }

        foreach (var s in new[] { "#0x1234", "0x12", "0x1234", "(x)", "(0x12, x)", "(0x1234, x)", "(0x12, sp)", "[0x12]", "[0x1234]", "([0x12], x)", "([0x1234], x)" })
        {
            yield return $"ldw x, {s}";
        }

        foreach (var s in new[] { "#0x1234", "0x12", "0x1234", "(y)", "(0x12, y)", "(0x1234, y)", "(0x12, sp)", "[0x12]", "([0x12], y)" })
        {
            yield return $"ldw y, {s}";
        }

        foreach (var s in new[] { "0x12", "0x1234", "(0x12, sp)", "[0x12]", "[0x1234]" })
        {
            yield return $"ldw {s}, x";
        }

        foreach (var s in new[] { "(x)", "(0x12, x)", "(0x1234, x)", "([0x12], x)", "([0x1234], x)", "0x12", "0x1234", "(0x12, sp)", "[0x12]" })
        {
            yield return $"ldw {s}, y";
        }

        foreach (var s in new[] { "(y)", "(0x12, y)", "(0x1234, y)", "([0x12], y)" })
        {
            yield return $"ldw {s}, x";
        }

        foreach (var s in new[] { "ldw x, y", "ldw y, x", "ldw x, sp", "ldw sp, x", "ldw y, sp", "ldw sp, y" })
        {
            yield return s;
        }

        foreach (var s in new[] { "#0x1234", "0x12", "0x1234", "(y)", "(0x12, y)", "(0x1234, y)", "(0x12, sp)", "[0x12]", "[0x1234]", "([0x12], y)" })
        {
            yield return $"cpw x, {s}";
        }

        foreach (var s in new[] { "#0x1234", "0x12", "0x1234", "(x)", "(0x12, x)", "(0x1234, x)", "[0x12]", "([0x12], x)", "([0x1234], x)" })
        {
            yield return $"cpw y, {s}";
        }

        foreach (var op in new[] { "addw", "subw" })
        {
            foreach (var r in new[] { "x", "y" })
            {
                foreach (var s in new[] { "#0x1234", "0x1234", "(0x12, sp)" })
                {
                    yield return $"{op} {r}, {s}";
                }
            }
        }

        yield return "addw sp, #0x12";
        yield return "sub sp, #0x12";
        string[] target = ["a", "0x12", "0x1234", "(x)", "(0x12, x)", "(0x1234, x)", "(y)", "(0x12, y)", "(0x1234, y)", "(0x12, sp)",
            "[0x12]", "[0x1234]", "([0x12], x)", "([0x1234], x)", "([0x12], y)"];
        foreach (var op in new[] { "neg", "cpl", "srl", "rrc", "sra", "sll", "rlc", "dec", "inc", "tnz", "swap", "clr" })
        {
            foreach (var t in target)
            {
                yield return $"{op} {t}";
            }
        }

        foreach (var op in new[] { "negw", "cplw", "srlw", "rrcw", "sraw", "sllw", "rlcw", "decw", "incw", "tnzw", "swapw", "clrw" })
        {
            yield return $"{op} x";
            yield return $"{op} y";
        }

        foreach (var s in new[]
                 {
                     "exg a, xl", "exg a, yl", "exg a, 0x1234", "exgw x, y", "mov 0x1234, #0x12", "mov 0x12, 0x34",
                     "mov 0x1234, 0x5678", "mul x, a", "mul y, a", "div x, a", "div y, a", "divw x, y", "rlwa x", "rlwa y",
                     "rrwa x", "rrwa y", "push a", "push cc", "push #0x12", "push 0x1234", "pop a", "pop cc", "pop 0x1234",
                     "pushw x", "pushw y", "popw x", "popw y",
                 })
        {
            yield return s;
        }

        foreach (var op in new[] { "jp", "call" })
        {
            foreach (var s in new[] { "0x1234", "(x)", "(0x12, x)", "(0x1234, x)", "(y)", "(0x12, y)", "(0x1234, y)", "[0x12]", "[0x1234]", "([0x12], x)", "([0x1234], x)", "([0x12], y)" })
            {
                yield return $"{op} {s}";
            }
        }

        foreach (var s in new[]
                 {
                     "jpf 0x123456", "jpf [0x1234]", "callf 0x123456", "callf [0x1234]", "callr .+0x10", "ret", "retf", "iret",
                     "trap", "break", "halt", "wfi", "wfe", "nop", "rim", "sim", "scf", "rcf", "ccf", "rvf", "int 0x123456",
                 })
        {
            yield return s;
        }

        foreach (var j in new[] { "jra", "jrt", "jrf", "jrugt", "jrule", "jrnc", "jruge", "jrc", "jrult", "jrne", "jreq", "jrnv", "jrv", "jrpl", "jrmi", "jrsgt", "jrsle", "jrsge", "jrslt", "jrnh", "jrh", "jrnm", "jrm", "jril", "jrih" })
        {
            yield return $"{j} .+0x10";
        }

        foreach (var op in new[] { "bset", "bres", "bcpl", "bccm" })
        {
            foreach (var n in new[] { 0, 3, 7 })
            {
                yield return $"{op} 0x1234, #{n}";
            }
        }

        foreach (var op in new[] { "btjt", "btjf" })
        {
            foreach (var n in new[] { 0, 3, 7 })
            {
                yield return $"{op} 0x1234, #{n}, .+0x10";
            }
        }
    }
}
