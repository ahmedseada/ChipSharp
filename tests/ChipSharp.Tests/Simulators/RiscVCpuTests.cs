using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using ChipSharp.Simulators;
using ChipSharp.Simulators.RiscV;
using ChipSharp.Tests.Llvm;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// The RISC-V core of the ESP32-C5 simulator, an instruction at a time, on a flat memory: every RV32I and M operation
/// against C#'s for edge and random operands, loads and stores (sign and zero extension, misaligned ones split), branches
/// and jumps, LR/SC and the AMOs, the CSRs, the exceptions and the CLIC's interrupt entry, mret and mnxti; and, assembled
/// by clang (llvm-mc), every instruction form of RV32IMAC, Zicsr, Zifencei, Zcb, Zcmp and Zcmt decoded to its length,
/// every compressed instruction doing what its 32-bit expansion does, Zcmp's pushes and pops and Zcmt's table jumps.
/// </summary>
public sealed partial class RiscVCpuTests : IDisposable
{
    private const uint Code = 0x1000, Data = 0x8000, Handler = 0x4000, Table = 0x6000;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => Scratch.Delete(_scratch);

    /// <summary>64 KB of memory from 0; an access beyond it is a bus error.</summary>
    private sealed class FlatBus : IRiscVBus
    {
        public byte[] Memory { get; } = new byte[0x10000];

        public uint Fetch(uint address) => address + 4 <= Memory.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(Memory.AsSpan((int)address))
            : throw new RiscVAccessFault(address);

        public uint Read(uint address, int size)
        {
            if (address + (uint)size > Memory.Length)
            {
                throw new RiscVAccessFault(address);
            }

            uint value = 0;
            for (var b = 0; b < size; b++)
            {
                value |= (uint)Memory[address + b] << (8 * b);
            }

            return value;
        }

        public void Write(uint address, int size, uint value)
        {
            if (address + (uint)size > Memory.Length)
            {
                throw new RiscVAccessFault(address);
            }

            for (var b = 0; b < size; b++)
            {
                Memory[address + b] = (byte)(value >> (8 * b));
            }
        }

        public int AccessCycles(uint address) => 0;

        public void Load(uint address, params uint[] words)
        {
            for (var i = 0; i < words.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(Memory.AsSpan((int)address + 4 * i), words[i]);
            }
        }

        public void LoadBytes(uint address, byte[] bytes) => bytes.CopyTo(Memory, address);
    }

    private static (RiscVCpu Cpu, FlatBus Bus) Core(params uint[] code)
    {
        var bus = new FlatBus();
        bus.Load(Code, code);
        var cpu = new RiscVCpu(bus) { Pc = Code, Mtvec = Handler };
        return (cpu, bus);
    }

    // the encodings of the base instruction set (the ISA manual's formats)
    private static uint R(uint funct7, uint funct3, int rd, int rs1, int rs2, uint opcode = 0x33) =>
        (funct7 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (funct3 << 12) | ((uint)rd << 7) | opcode;

    private static uint I(uint funct3, int rd, int rs1, int immediate, uint opcode = 0x13) =>
        ((uint)(immediate & 0xFFF) << 20) | ((uint)rs1 << 15) | (funct3 << 12) | ((uint)rd << 7) | opcode;

    private static uint S(uint funct3, int rs1, int rs2, int immediate) =>
        ((uint)((immediate >> 5) & 0x7F) << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (funct3 << 12) |
        ((uint)(immediate & 0x1F) << 7) | 0x23;

    private static uint B(uint funct3, int rs1, int rs2, int offset) =>
        ((uint)((offset >> 12) & 1) << 31) | ((uint)((offset >> 5) & 0x3F) << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) |
        (funct3 << 12) | ((uint)((offset >> 1) & 0xF) << 8) | ((uint)((offset >> 11) & 1) << 7) | 0x63;

    private static uint J(int rd, int offset) =>
        ((uint)((offset >> 20) & 1) << 31) | ((uint)((offset >> 1) & 0x3FF) << 21) | ((uint)((offset >> 11) & 1) << 20) |
        ((uint)((offset >> 12) & 0xFF) << 12) | ((uint)rd << 7) | 0x6F;

    private static uint Csr(uint funct3, int rd, int rs1, int csr) =>
        ((uint)csr << 20) | ((uint)rs1 << 15) | (funct3 << 12) | ((uint)rd << 7) | 0x73;

    private static uint Amo(uint funct5, int rd, int rs1, int rs2) =>
        (funct5 << 27) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (2u << 12) | ((uint)rd << 7) | 0x2F;

    /// <summary>Edge values, then random ones (the same every run).</summary>
    private static uint[] Operands(int seed, int count = 400)
    {
        uint[] edges = [0, 1, 2, 3, 5, 7, 31, 32, 0x7FFF_FFFF, 0x8000_0000, 0x8000_0001, 0xFFFF_FFFF, 0xFFFF_FFFE, 0xFFFF_FFF9,
            0x0000_FFFF, 0x0001_0000, 0x55555555, 0xAAAAAAAA, 100, 1000];
        var random = new Random(seed);
        return [.. Enumerable.Range(0, count).Select(i => i < edges.Length ? edges[i] : (uint)random.NextInt64() >> random.Next(32))];
    }

    public static TheoryData<string, uint, uint> RegisterOperations => new()
    {
        { "add", 0, 0 }, { "sub", 0x20, 0 }, { "sll", 0, 1 }, { "slt", 0, 2 }, { "sltu", 0, 3 }, { "xor", 0, 4 },
        { "srl", 0, 5 }, { "sra", 0x20, 5 }, { "or", 0, 6 }, { "and", 0, 7 }, { "mul", 1, 0 }, { "mulh", 1, 1 },
        { "mulhsu", 1, 2 }, { "mulhu", 1, 3 }, { "div", 1, 4 }, { "divu", 1, 5 }, { "rem", 1, 6 }, { "remu", 1, 7 },
    };

    [Theory]
    [MemberData(nameof(RegisterOperations))]
    public void Every_register_operation_gives_what_the_ISA_manual_says(string name, uint funct7, uint funct3)
    {
        var a = Operands(1);
        var b = Operands(2);
        var (cpu, _) = Core(R(funct7, funct3, 10, 11, 12));
        foreach (var x in a)
        {
            foreach (var y in b.Take(60))
            {
                cpu.Pc = Code;
                cpu.X[11] = x;
                cpu.X[12] = y;

                var cycles = cpu.Step();

                var expected = Reference(name, x, y);
                Assert.True(cpu.X[10] == expected, $"{name} 0x{x:x8}, 0x{y:x8}: expected 0x{expected:x8}, the core gave 0x{cpu.X[10]:x8}");
                Assert.Equal(Code + 4, cpu.Pc);
                Assert.InRange(cycles, 1, 18);
            }
        }
    }

    private static uint Reference(string name, uint a, uint b) => name switch
    {
        "add" => a + b,
        "sub" => a - b,
        "sll" => a << (int)(b & 31),
        "slt" => (int)a < (int)b ? 1u : 0,
        "sltu" => a < b ? 1u : 0,
        "xor" => a ^ b,
        "srl" => a >> (int)(b & 31),
        "sra" => (uint)((int)a >> (int)(b & 31)),
        "or" => a | b,
        "and" => a & b,
        "mul" => unchecked(a * b),
        "mulh" => (uint)(((Int128)(int)a * (int)b) >> 32),
        "mulhsu" => (uint)(((Int128)(int)a * (Int128)b) >> 32),
        "mulhu" => (uint)(((UInt128)a * b) >> 32),
        "div" => b == 0 ? uint.MaxValue : (a == 0x8000_0000 && b == uint.MaxValue) ? a : (uint)((int)a / (int)b),
        "divu" => b == 0 ? uint.MaxValue : a / b,
        "rem" => b == 0 ? a : (a == 0x8000_0000 && b == uint.MaxValue) ? 0 : (uint)((int)a % (int)b),
        _ => b == 0 ? a : a % b,
    };

    [Fact]
    public void Every_immediate_operation_gives_what_the_ISA_manual_says()
    {
        int[] immediates = [0, 1, -1, 2047, -2048, 5, -7, 0x555, -0x556];
        var (cpu, bus) = Core();
        foreach (var x in Operands(3, 120))
        {
            foreach (var immediate in immediates)
            {
                var signed = (uint)immediate;
                foreach (var (funct3, expected) in new[]
                {
                    (0u, x + signed), (2u, (int)x < immediate ? 1u : 0), (3u, x < signed ? 1u : 0), (4u, x ^ signed),
                    (6u, x | signed), (7u, x & signed),
                })
                {
                    bus.Load(Code, I(funct3, 10, 11, immediate));
                    cpu.Pc = Code;
                    cpu.X[11] = x;
                    cpu.Step();
                    Assert.True(cpu.X[10] == expected, $"funct3 {funct3} 0x{x:x8}, {immediate}");
                }
            }

            for (var shamt = 0; shamt < 32; shamt++)
            {
                foreach (var (instruction, expected) in new[]
                {
                    (I(1, 10, 11, shamt), x << shamt), (I(5, 10, 11, shamt), x >> shamt),
                    (I(5, 10, 11, shamt | 0x400), (uint)((int)x >> shamt)),
                })
                {
                    bus.Load(Code, instruction);
                    cpu.Pc = Code;
                    cpu.X[11] = x;
                    cpu.Step();
                    Assert.Equal(expected, cpu.X[10]);
                }
            }
        }
    }

    [Fact]
    public void Lui_auipc_and_x0_give_the_manual_s_values()
    {
        var (cpu, _) = Core(0xABCDE537, 0x00001597, I(0, 0, 11, 5), 0xFFFFF637); // lui a0, 0xabcde; auipc a1, 1; addi x0, a1, 5; lui a2, 0xfffff
        cpu.Step();
        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(0xABCDE000, cpu.X[10]);
        Assert.Equal(Code + 4 + 0x1000, cpu.X[11]);
        Assert.Equal(0u, cpu.X[0]);
        Assert.Equal(0xFFFFF000, cpu.X[12]);
    }

    [Fact]
    public void Loads_extend_their_sign_or_zeros_and_stores_write_their_width_misaligned_ones_too()
    {
        var (cpu, bus) = Core(
            I(0, 10, 11, 0, 0x03), I(4, 12, 11, 0, 0x03), I(1, 13, 11, 0, 0x03), I(5, 14, 11, 0, 0x03), I(2, 15, 11, 0, 0x03),
            I(2, 16, 11, 1, 0x03), I(1, 17, 11, 3, 0x03), S(0, 11, 15, 16), S(1, 11, 15, 21), S(2, 11, 15, 25), S(2, 11, 15, 30));
        bus.Load(Data, 0x8382_81F0, 0x0000_0084);
        cpu.X[11] = Data;
        for (var i = 0; i < 11; i++)
        {
            cpu.Step();
        }

        Assert.Equal(0xFFFF_FFF0, cpu.X[10]); // lb
        Assert.Equal(0xF0u, cpu.X[12]); // lbu
        Assert.Equal(0xFFFF_81F0, cpu.X[13]); // lh
        Assert.Equal(0x81F0u, cpu.X[14]); // lhu
        Assert.Equal(0x8382_81F0, cpu.X[15]); // lw
        Assert.Equal(0x8483_8281, cpu.X[16]); // lw at Data + 1, misaligned
        Assert.Equal(0xFFFF_8483u, cpu.X[17]); // lh at Data + 3, across the word
        Assert.Equal(0xF0, bus.Memory[Data + 16]);
        Assert.Equal(0, bus.Memory[Data + 17]);
        Assert.Equal((byte[])[0xF0, 0x81, 0, 0], bus.Memory[(int)(Data + 21)..(int)(Data + 25)]);
        Assert.Equal((byte[])[0xF0, 0x81, 0x82, 0x83, 0, 0xF0, 0x81, 0x82, 0x83], bus.Memory[(int)(Data + 25)..(int)(Data + 34)]);
    }

    [Theory]
    [InlineData(0u, 5u, 5u, true)] // beq
    [InlineData(1u, 5u, 6u, true)] // bne
    [InlineData(4u, 0xFFFF_FFFFu, 0u, true)] // blt: -1 < 0
    [InlineData(5u, 0u, 0xFFFF_FFFFu, true)] // bge: 0 >= -1
    [InlineData(6u, 0u, 0xFFFF_FFFFu, true)] // bltu
    [InlineData(7u, 0xFFFF_FFFFu, 0u, true)] // bgeu
    [InlineData(0u, 5u, 6u, false)]
    [InlineData(4u, 0u, 0xFFFF_FFFFu, false)]
    [InlineData(6u, 0xFFFF_FFFFu, 0u, false)]
    public void Branches_compare_as_the_manual_says_and_a_taken_one_costs_a_cycle_more(uint funct3, uint a, uint b, bool taken)
    {
        var (cpu, _) = Core(B(funct3, 10, 11, -16));
        cpu.X[10] = a;
        cpu.X[11] = b;

        var cycles = cpu.Step();

        Assert.Equal(taken ? Code - 16 : Code + 4, cpu.Pc);
        Assert.Equal(taken ? 2 : 1, cycles);
    }

    [Fact]
    public void Jal_and_jalr_link_and_jalr_clears_the_target_s_lowest_bit()
    {
        var (cpu, _) = Core(J(1, 0x800));
        cpu.Step();
        Assert.Equal(Code + 0x800, cpu.Pc);
        Assert.Equal(Code + 4, cpu.X[1]);

        (cpu, _) = Core(I(0, 11, 11, 3, 0x67)); // jalr a1, 3(a1): the link is written after the target is read
        cpu.X[11] = 0x2000;
        Assert.Equal(3, cpu.Step());
        Assert.Equal(0x2002u, cpu.Pc);
        Assert.Equal(Code + 4, cpu.X[11]);
    }

    [Fact]
    public void Lr_and_sc_succeed_together_and_fail_after_a_store_or_without_lr()
    {
        var (cpu, bus) = Core(Amo(0x02, 10, 11, 0), Amo(0x03, 12, 11, 13), Amo(0x03, 14, 11, 13), Amo(0x02, 10, 11, 0),
            S(2, 11, 0, 4), Amo(0x03, 15, 11, 13));
        bus.Load(Data, 1234);
        cpu.X[11] = Data;
        cpu.X[13] = 99;
        for (var i = 0; i < 6; i++)
        {
            cpu.Step();
        }

        Assert.Equal(99u, bus.Read(Data, 4));
        Assert.Equal(0u, cpu.X[12]); // the first sc stored
        Assert.Equal(1u, cpu.X[14]); // a second sc has no reservation
        Assert.Equal(1u, cpu.X[15]); // a store between lr and sc takes it away
        Assert.Equal(99u, cpu.X[10]);
    }

    [Theory]
    [InlineData(0x01u, 7u, 0xFFFF_FFFEu, 0xFFFF_FFFEu)] // amoswap
    [InlineData(0x00u, 7u, 0xFFFF_FFFEu, 5u)] // amoadd
    [InlineData(0x04u, 6u, 3u, 5u)] // amoxor
    [InlineData(0x0Cu, 6u, 3u, 2u)] // amoand
    [InlineData(0x08u, 6u, 3u, 7u)] // amoor
    [InlineData(0x10u, 7u, 0xFFFF_FFFEu, 0xFFFF_FFFEu)] // amomin
    [InlineData(0x14u, 7u, 0xFFFF_FFFEu, 7u)] // amomax
    [InlineData(0x18u, 7u, 0xFFFF_FFFEu, 7u)] // amominu
    [InlineData(0x1Cu, 7u, 0xFFFF_FFFEu, 0xFFFF_FFFEu)] // amomaxu
    public void Amos_return_the_old_value_and_store_the_operation_s(uint funct5, uint memory, uint operand, uint stored)
    {
        var (cpu, bus) = Core(Amo(funct5, 10, 11, 12) | (3u << 25)); // aq and rl set: ignored
        bus.Load(Data, memory);
        cpu.X[11] = Data;
        cpu.X[12] = operand;

        Assert.Equal(2, cpu.Step());

        Assert.Equal(memory, cpu.X[10]);
        Assert.Equal(stored, bus.Read(Data, 4));
    }

    [Fact]
    public void A_misaligned_amo_raises_exception_6_with_its_address()
    {
        var (cpu, _) = Core(Amo(0x00, 10, 11, 12));
        cpu.X[11] = Data + 2;

        cpu.Step();

        Assert.Equal(Handler, cpu.Pc);
        Assert.Equal(6u, cpu.Mcause & 0x3F);
        Assert.Equal(Data + 2, cpu.Mtval);
        Assert.Equal(Code, cpu.Mepc);
    }

    [Fact]
    public void An_illegal_instruction_traps_to_mtvec_with_its_bits_and_mret_comes_back()
    {
        var (cpu, bus) = Core(0xFFFF_FFFF);
        bus.Load(Handler, Csr(2, 10, 0, 0x342), Csr(2, 11, 0, 0x343), Csr(2, 12, 0, 0x341), I(0, 12, 12, 4),
            Csr(1, 0, 12, 0x341), 0x30200073);
        cpu.Mstatus = RiscVCpu.MstatusMie;

        cpu.Step();
        Assert.Equal(Handler, cpu.Pc);
        Assert.Equal(0u, cpu.Mstatus & RiscVCpu.MstatusMie);
        Assert.Equal(RiscVCpu.MstatusMpie | RiscVCpu.MstatusMpp, cpu.Mstatus & (RiscVCpu.MstatusMpie | RiscVCpu.MstatusMpp));
        for (var i = 0; i < 6; i++)
        {
            cpu.Step();
        }

        Assert.Equal(2u | (3u << 28) | (1u << 27), cpu.X[10]); // the code, with mstatus's MPP and MPIE mirrored
        Assert.Equal(0xFFFF_FFFF, cpu.X[11]);
        Assert.Equal(Code, cpu.X[12] - 4);
        Assert.Equal(Code + 4, cpu.Pc);
        Assert.Equal(RiscVCpu.MstatusMie, cpu.Mstatus & RiscVCpu.MstatusMie);
        Assert.Contains("an illegal instruction 0xffffffff at pc 0x00001000 (mcause 2)", cpu.LastException, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_without_a_trap_handler_stops_the_run_saying_what_happened()
    {
        var (cpu, _) = Core(0x00000073); // ecall
        cpu.Mtvec = 0;

        var fault = Assert.Throws<ProgramFault>(() => cpu.Step());

        Assert.Equal("the program caused an ecall at pc 0x00001000 (mcause 11), and has no trap handler (mtvec 0x00000003)",
            fault.Message);
    }

    [Fact]
    public void Ebreak_and_c_ebreak_are_the_failure_stop_and_wfi_waits()
    {
        var (cpu, _) = Core(0x00100073);
        cpu.Step();
        Assert.True(cpu.BreakHit);
        Assert.Equal(Code, cpu.Pc);

        (cpu, _) = Core(0x9002);
        cpu.Step();
        Assert.True(cpu.BreakHit);

        (cpu, _) = Core(0x10500073);
        cpu.Step();
        Assert.True(cpu.Waiting);
        Assert.Equal(Code + 4, cpu.Pc);
    }

    [Fact]
    public void Zcb_s_forms_that_need_Zbb_are_illegal_on_the_C5_and_its_others_run()
    {
        var (cpu, bus) = Core();
        bus.LoadBytes(Code, [0x61, 0x9D, 0x75, 0x9D, 0x65, 0x9D]); // c.zext.b a0; c.not a0; c.sext.b a0
        cpu.X[10] = 0x1234;

        cpu.Step();
        Assert.Equal(0x34u, cpu.X[10]);
        cpu.Step();
        Assert.Equal(~0x34u, cpu.X[10]);
        cpu.Step();
        Assert.Equal(Handler, cpu.Pc);
        Assert.Equal((uint)RiscVCpu.IllegalInstruction, cpu.Mcause & 0x3F);
        Assert.Equal(0x9D65u, cpu.Mtval);
    }

    [Fact]
    public void The_csr_instructions_read_write_set_and_clear_and_read_only_csrs_refuse_writes()
    {
        var (cpu, _) = Core(Csr(1, 10, 11, 0x340), Csr(2, 12, 13, 0x340), Csr(3, 14, 15, 0x340), Csr(5, 16, 0x1F, 0x340),
            Csr(6, 17, 0x2, 0x340), Csr(7, 18, 0x10, 0x340), Csr(2, 19, 0, 0x301), Csr(2, 20, 0, 0xF11), Csr(1, 0, 11, 0xF11));
        cpu.Mscratch = 0x100;
        cpu.X[11] = 0xF0F0;
        cpu.X[13] = 0x000F;
        cpu.X[15] = 0x00F0;
        for (var i = 0; i < 8; i++)
        {
            cpu.Step();
        }

        Assert.Equal(0x100u, cpu.X[10]); // csrrw
        Assert.Equal(0xF0F0u, cpu.X[12]); // csrrs
        Assert.Equal(0xF0FFu, cpu.X[14]); // csrrc
        Assert.Equal(0xF00Fu, cpu.X[16]); // csrrwi
        Assert.Equal(0x1Fu, cpu.X[17]); // csrrsi
        Assert.Equal(0x1Fu, cpu.X[18]); // csrrci
        Assert.Equal(0xFu, cpu.Mscratch);
        Assert.Equal(RiscVCpu.MisaValue, cpu.X[19]);
        Assert.Equal(0x612u, cpu.X[20]); // mvendorid
        cpu.Step(); // a write of mvendorid
        Assert.Equal(Handler, cpu.Pc);
        Assert.Equal(2u, cpu.Mcause & 0x3F);
    }

    [Fact]
    public void Mtvec_is_hardwired_to_clic_mode_and_mie_mip_read_0()
    {
        var (cpu, _) = Core(Csr(1, 0, 11, 0x305), Csr(2, 10, 0, 0x305), Csr(1, 0, 11, 0x304), Csr(2, 12, 0, 0x304),
            Csr(2, 13, 0, 0x344), Csr(2, 14, 0, 0x350));
        cpu.X[11] = 0x4080_0101;
        for (var i = 0; i < 6; i++)
        {
            cpu.Step();
        }

        Assert.Equal(0x4080_0103u, cpu.X[10]); // the base 64-byte aligned (0x40800100), the mode 3
        Assert.Equal(0u, cpu.X[12]);
        Assert.Equal(0u, cpu.X[13]);
        Assert.Equal(0x2080_0000u, cpu.X[14]); // mclicbase
    }

    [Fact]
    public void An_interrupt_saves_the_level_takes_the_vector_and_mret_restores_it()
    {
        var (cpu, bus) = Core(I(0, 10, 10, 1), I(0, 10, 10, 1));
        bus.Load(Table + 4 * 20, Handler + 0x100);
        bus.Load(Handler + 0x100, 0x30200073);
        cpu.Mtvt = Table;
        cpu.Mstatus = RiscVCpu.MstatusMie;
        cpu.Step();
        cpu.InterruptLevel = 63; // as if serving a level-63 interrupt already

        Assert.Equal(4, cpu.TakeInterrupt(20, 127, vectored: true));

        Assert.Equal(Handler + 0x100, cpu.Pc);
        Assert.Equal(Code + 4, cpu.Mepc);
        Assert.Equal(0x8000_0000u | (3u << 28) | (1u << 27) | (63u << 16) | 20, cpu.Mcause);
        Assert.Equal(127, cpu.InterruptLevel);
        Assert.False(cpu.InterruptsEnabled);
        Assert.Equal(127u << 24, cpu.ReadCsr(0xFB1)); // mintstatus

        cpu.Step(); // mret
        Assert.Equal(Code + 4, cpu.Pc);
        Assert.Equal(63, cpu.InterruptLevel);
        Assert.True(cpu.InterruptsEnabled);

        Assert.Equal(3, cpu.TakeInterrupt(7, 255, vectored: false));
        Assert.Equal(Handler, cpu.Pc);
        Assert.Equal(7u, cpu.Mcause & 0x3F);
    }

    private sealed class OneInterrupt(int id, int level) : IRiscVInterrupts
    {
        public bool Pending { get; set; } = true;

        public (int Id, int Level)? Next(int above) => Pending && level > above ? (id, level) : null;

        public void Taken(int taken) => Pending = false;
    }

    [Fact]
    public void Mnxti_gives_the_next_interrupt_s_vector_entry_and_takes_it_and_0_without_one()
    {
        var (cpu, _) = Core(Csr(6, 10, 8, 0x345), Csr(6, 11, 8, 0x345));
        var clic = new OneInterrupt(21, 95);
        cpu.Interrupts = clic;
        cpu.Mtvt = Table;

        cpu.Step(); // csrrsi a0, mnxti, MIE
        Assert.Equal(Table + 4 * 21, cpu.X[10]);
        Assert.Equal(95, cpu.InterruptLevel);
        Assert.Equal(21u, cpu.Mcause & 0x3F);
        Assert.False(clic.Pending);
        Assert.True(cpu.InterruptsEnabled);

        cpu.Step();
        Assert.Equal(0u, cpu.X[11]);
    }

    [Fact]
    public void The_counters_count_cycles_and_instructions_unless_inhibited()
    {
        var (cpu, _) = Core(I(0, 0, 0, 0), I(0, 0, 0, 0), Csr(2, 10, 0, 0xB00), Csr(2, 11, 0, 0xB02), Csr(5, 0, 5, 0x320),
            I(0, 0, 0, 0), Csr(2, 12, 0, 0xC02));
        for (var i = 0; i < 7; i++)
        {
            cpu.Step();
        }

        Assert.Equal(2u, cpu.X[10]);
        Assert.Equal(3u, cpu.X[11]);
        Assert.Equal(4u, cpu.X[12]); // instret stopped at mcountinhibit's write, which it does not count
    }

    /// <summary>
    /// Every instruction form clang assembles for the C5's ISA, as llvm-objdump shows it: none is illegal to the core,
    /// and each that does not jump moves the PC by its length (2 or 4 bytes, as the assembler encoded it).
    /// </summary>
    [Esp32c5Fact]
    public async Task Every_instruction_form_clang_assembles_decodes_to_its_length()
    {
        var forms = await AssembleAsync(Forms(), "rv32imac_zicsr_zifencei_zcb_zcmp_zcmt", _scratch);
        Assert.True(forms.Count > 160, $"Only {forms.Count} instructions in the listing.");
        var compressed = 0;
        foreach (var (bytes, text) in forms)
        {
            var (cpu, bus) = Core();
            bus.LoadBytes(Code, bytes);
            bus.Load(Table, Enumerable.Repeat(Code + 0x100, 64).Select(a => (uint)a).ToArray());
            cpu.Jvt = Table;
            cpu.Mtvt = Table;
            cpu.X[2] = Data + 0x400; // sp
            for (var r = 8; r < 16; r++)
            {
                cpu.X[r] = Data + (uint)(16 * r);
            }

            cpu.X[1] = Code + 0x200;
            cpu.Step();
            Assert.False(cpu.Pc == Handler && (cpu.Mcause & 0x3F) == RiscVCpu.IllegalInstruction,
                $"{text} ({Convert.ToHexString(bytes)}): illegal to the core");
            if (!Jumps().IsMatch(text) && !cpu.Waiting && !cpu.BreakHit)
            {
                Assert.True(cpu.Pc == Code + bytes.Length,
                    $"{text} ({Convert.ToHexString(bytes)}) took {cpu.Pc - Code} bytes, not {bytes.Length}");
            }

            compressed += bytes.Length == 2 ? 1 : 0;
        }

        Assert.True(compressed > 50, $"Only {compressed} compressed forms.");
    }

    [GeneratedRegex(@"^(c\.)?(j|jal|jr|jalr|beq|bne|blt|bge|bltu|bgeu|beqz|bnez|ecall|mret|cm\.jt|cm\.jalt|cm\.popret|cm\.popretz|ret)\b")]
    private static partial Regex Jumps();

    /// <summary>Every form of the instructions, in clang's syntax.</summary>
    private static IEnumerable<string> Forms()
    {
        foreach (var op in new[] { "add", "sub", "sll", "slt", "sltu", "xor", "srl", "sra", "or", "and", "mul", "mulh", "mulhsu",
            "mulhu", "div", "divu", "rem", "remu" })
        {
            yield return $"{op} a0, a1, a2";
        }

        foreach (var op in new[] { "addi", "slti", "sltiu", "xori", "ori", "andi" })
        {
            yield return $"{op} a0, a1, -5";
        }

        foreach (var op in new[] { "slli", "srli", "srai" })
        {
            yield return $"{op} a0, a1, 31";
        }

        foreach (var op in new[] { "lb", "lh", "lw", "lbu", "lhu" })
        {
            yield return $"{op} a0, 8(s0)";
        }

        foreach (var op in new[] { "sb", "sh", "sw" })
        {
            yield return $"{op} a0, 12(s0)";
        }

        foreach (var op in new[] { "beq", "bne", "blt", "bge", "bltu", "bgeu" })
        {
            yield return $"{op} a0, a1, .+64";
        }

        yield return "lui a0, 0x12345";
        yield return "auipc a0, 0x12";
        yield return "jal ra, .+256";
        yield return "jalr ra, 4(a1)";
        yield return "fence rw, rw";
        yield return "fence.i";
        yield return "ecall";
        yield return "ebreak";
        yield return "mret";
        yield return "wfi";
        foreach (var op in new[] { "lr.w a0, (s0)", "sc.w a0, a1, (s0)", "amoswap.w a0, a1, (s0)", "amoadd.w a0, a1, (s0)",
            "amoxor.w a0, a1, (s0)", "amoand.w a0, a1, (s0)", "amoor.w a0, a1, (s0)", "amomin.w a0, a1, (s0)",
            "amomax.w a0, a1, (s0)", "amominu.w a0, a1, (s0)", "amomaxu.w.aqrl a0, a1, (s0)" })
        {
            yield return op;
        }

        foreach (var csr in new[] { "mstatus", "misa", "mtvec", "0x307", "mscratch", "mepc", "mcause", "mtval", "0x347",
            "mcycle", "minstret", "mcycleh", "minstreth", "0x320", "0x7c1", "0x017" })
        {
            yield return $"csrrw a0, {csr}, a1";
            yield return $"csrrs a0, {csr}, zero";
            yield return $"csrrci a0, {csr}, 0";
        }

        foreach (var csr in new[] { "mvendorid", "marchid", "mimpid", "mhartid", "cycle", "time", "instret", "cycleh", "0xfb1" })
        {
            yield return $"csrr a0, {csr}";
        }

        yield return "csrrsi a0, 0x345, 8";
        yield return "csrrw a0, 0x348, a1";
        yield return "csrrw a0, 0x349, a1";

        // the compressed ones: C (Zca), then Zcb, Zcmp, Zcmt
        foreach (var form in new[]
        {
            "c.addi4spn a0, sp, 16", "c.lw a0, 4(a1)", "c.sw a0, 8(a1)", "c.nop", "c.addi a0, -3", "c.jal .+32", "c.li a0, -5",
            "c.addi16sp sp, -64", "c.lui a0, 0x1f", "c.srli a0, 3", "c.srai a0, 3", "c.andi a0, -2", "c.sub a0, a1",
            "c.xor a0, a1", "c.or a0, a1", "c.and a0, a1", "c.j .+20", "c.beqz a0, .+12", "c.bnez a0, .+12", "c.slli a0, 5",
            "c.lwsp a0, 12(sp)", "c.jr a1", "c.mv a0, a1", "c.ebreak", "c.jalr a1", "c.add a0, a1", "c.swsp a0, 16(sp)",
            "c.lbu a0, 2(a1)", "c.lhu a0, 2(a1)", "c.lh a0, 2(a1)", "c.sb a0, 3(a1)", "c.sh a0, 2(a1)", "c.zext.b a0",
            "c.not a0", "c.mul a0, a1",
            "cm.push {ra}, -16", "cm.push {ra, s0}, -32", "cm.push {ra, s0-s1}, -16", "cm.push {ra, s0-s2}, -16",
            "cm.push {ra, s0-s3}, -32", "cm.push {ra, s0-s7}, -48", "cm.push {ra, s0-s9}, -64", "cm.push {ra, s0-s11}, -112",
            "cm.pop {ra, s0-s1}, 16", "cm.pop {ra, s0-s11}, 64", "cm.popret {ra, s0-s2}, 16", "cm.popretz {ra}, 32",
            "cm.mvsa01 s0, s1", "cm.mva01s s2, s7", "cm.jt 3", "cm.jalt 40",
        })
        {
            yield return form;
        }
    }

    /// <summary>
    /// Every compressed instruction (C, and Zcb's) does what its 32-bit expansion does: run from the same registers
    /// (random, but for the base addresses) and memory, the two leave the same registers and memory, and jump by the same.
    /// </summary>
    [Esp32c5Fact]
    public async Task Every_compressed_instruction_does_what_its_expansion_does()
    {
        (string Compressed, string Expanded)[] pairs =
        [
            ("c.addi4spn a0, sp, 16", "addi a0, sp, 16"), ("c.addi4spn s1, sp, 1020", "addi s1, sp, 1020"),
            ("c.lw a0, 4(a1)", "lw a0, 4(a1)"), ("c.lw s1, 124(a5)", "lw s1, 124(a5)"), ("c.sw a0, 8(a1)", "sw a0, 8(a1)"),
            ("c.sw a3, 120(s0)", "sw a3, 120(s0)"), ("c.addi a0, -3", "addi a0, a0, -3"), ("c.addi t6, 31", "addi t6, t6, 31"),
            ("c.li a0, -5", "addi a0, zero, -5"), ("c.li s11, 31", "addi s11, zero, 31"), ("c.addi16sp sp, -64", "addi sp, sp, -64"),
            ("c.addi16sp sp, 496", "addi sp, sp, 496"), ("c.lui a0, 0x1f", "lui a0, 0x1f"), ("c.lui t3, 0xfffe0", "lui t3, 0xfffe0"),
            ("c.srli a0, 3", "srli a0, a0, 3"), ("c.srli a5, 31", "srli a5, a5, 31"), ("c.srai a0, 3", "srai a0, a0, 3"),
            ("c.srai s0, 30", "srai s0, s0, 30"), ("c.andi a0, -2", "andi a0, a0, -2"), ("c.andi a4, 21", "andi a4, a4, 21"),
            ("c.sub a0, a1", "sub a0, a0, a1"), ("c.xor a0, a1", "xor a0, a0, a1"), ("c.or s1, a5", "or s1, s1, a5"),
            ("c.and a0, a1", "and a0, a0, a1"), ("c.slli a0, 5", "slli a0, a0, 5"), ("c.slli t5, 31", "slli t5, t5, 31"),
            ("c.lwsp a0, 12(sp)", "lw a0, 12(sp)"), ("c.lwsp ra, 252(sp)", "lw ra, 252(sp)"), ("c.swsp a0, 16(sp)", "sw a0, 16(sp)"),
            ("c.swsp t6, 252(sp)", "sw t6, 252(sp)"), ("c.mv a0, a1", "add a0, zero, a1"), ("c.add a0, a1", "add a0, a0, a1"),
            ("c.add t0, s11", "add t0, t0, s11"), ("c.j .+20", "jal zero, .+20"), ("c.j .-2048", "jal zero, .-2048"),
            ("c.jal .+32", "jal ra, .+32"), ("c.jal .+2046", "jal ra, .+2046"), ("c.beqz a0, .+12", "beq a0, zero, .+12"),
            ("c.beqz s1, .-256", "beq s1, zero, .-256"), ("c.bnez a0, .+12", "bne a0, zero, .+12"),
            ("c.bnez a5, .+254", "bne a5, zero, .+254"), ("c.jr a1", "jalr zero, 0(a1)"), ("c.jalr a1", "jalr ra, 0(a1)"),
            ("c.lbu a0, 2(a1)", "lbu a0, 2(a1)"), ("c.lbu s1, 3(a5)", "lbu s1, 3(a5)"), ("c.lhu a0, 2(a1)", "lhu a0, 2(a1)"),
            ("c.lh a0, 2(a1)", "lh a0, 2(a1)"), ("c.lh a2, 0(s0)", "lh a2, 0(s0)"), ("c.sb a0, 3(a1)", "sb a0, 3(a1)"),
            ("c.sh a0, 2(a1)", "sh a0, 2(a1)"), ("c.zext.b a0", "andi a0, a0, 255"), ("c.not a0", "xori a0, a0, -1"),
            ("c.not s1", "xori s1, s1, -1\n addi zero, zero, 0"), ("c.mul a0, a1", "mul a0, a0, a1"),
        ];
        var compressed = await AssembleAsync(pairs.Select(pair => pair.Compressed), "rv32imac_zcb", Path.Combine(_scratch, "c"));
        var expanded = new List<List<byte[]>>();
        foreach (var (pair, index) in pairs.Select((pair, index) => (pair, index)))
        {
            var instructions = await AssembleAsync(pair.Expanded.Split('\n').Select(line => line.Trim()), "rv32ima",
                Path.Combine(_scratch, "e" + index.ToString(CultureInfo.InvariantCulture)));
            expanded.Add([.. instructions.Select(instruction => instruction.Bytes)]);
        }

        Assert.Equal(pairs.Length, compressed.Count);
        var random = new Random(5);
        for (var i = 0; i < pairs.Length; i++)
        {
            for (var round = 0; round < 20; round++)
            {
                var registers = new uint[32];
                for (var r = 1; r < 32; r++)
                {
                    registers[r] = (uint)random.NextInt64() >> random.Next(32);
                }

                foreach (var r in new[] { 2, 8, 11, 15 }) // sp, s0, a1, a5: where the loads and stores go
                {
                    registers[r] = Data + 0x200 + 4 * (uint)random.Next(32);
                }

                var memory = new byte[0x800];
                random.NextBytes(memory);
                var (a, aBus) = Prepared(registers, memory, [compressed[i].Bytes]);
                var (b, bBus) = Prepared(registers, memory, expanded[i]);
                a.Step();
                foreach (var _ in expanded[i])
                {
                    b.Step();
                }

                var jumps = Jumps().IsMatch(pairs[i].Compressed);
                var links = pairs[i].Compressed is "c.jal .+32" or "c.jal .+2046" or "c.jalr a1";
                var differs = Enumerable.Range(0, 32).Where(r => a.X[r] != b.X[r] && !(links && r == 1 && a.X[1] == b.X[1] - 2));
                Assert.True(!differs.Any(), $"{pairs[i].Compressed}: x{differs.FirstOrDefault()} is 0x{a.X[differs.FirstOrDefault()]:x8}, " +
                    $"its expansion's 0x{b.X[differs.FirstOrDefault()]:x8}");
                Assert.True(aBus.Memory.AsSpan((int)Data, 0x800).SequenceEqual(bBus.Memory.AsSpan((int)Data, 0x800)),
                    $"{pairs[i].Compressed}: the memory differs");
                var samePlace = jumps ? a.Pc == b.Pc || (a.Pc == Code + 2 && b.Pc == Code + 4) : a.Pc == Code + 2;
                Assert.True(samePlace, $"{pairs[i].Compressed}: pc 0x{a.Pc:x}, its expansion's 0x{b.Pc:x}");
            }
        }

        static (RiscVCpu Cpu, FlatBus Bus) Prepared(uint[] registers, byte[] memory, IEnumerable<byte[]> code)
        {
            var (cpu, bus) = Core();
            var at = Code;
            foreach (var instruction in code)
            {
                bus.LoadBytes(at, instruction);
                at += (uint)instruction.Length;
            }

            bus.LoadBytes(Data, memory);
            registers.CopyTo(cpu.X, 0);
            return (cpu, bus);
        }
    }

    [Esp32c5Fact]
    public async Task Zcmp_pushes_and_pops_ra_and_the_s_registers_as_the_spec_lays_them_out()
    {
        var forms = await AssembleAsync(["cm.push {ra, s0-s1}, -32", "cm.popret {ra, s0-s1}, 32", "cm.push {ra, s0-s11}, -80",
            "cm.popretz {ra, s0-s11}, 80", "cm.mvsa01 s1, s3", "cm.mva01s s5, s0"], "rv32imac_zcmp", _scratch);

        // {ra, s0-s1}: s1 at sp - 4, s0 at sp - 8, ra at sp - 12; the stack pointer down by 32 (16 for the 3 registers, 16 more)
        var (cpu, bus) = Core();
        bus.LoadBytes(Code, [.. forms[0].Bytes, .. forms[1].Bytes]);
        cpu.X[2] = Data + 0x100;
        (cpu.X[1], cpu.X[8], cpu.X[9]) = (0x2468, 0x1111, 0x2222);
        cpu.Step();
        Assert.Equal(Data + 0x100 - 32, cpu.X[2]);
        Assert.Equal((0x2222u, 0x1111u, 0x2468u), (bus.Read(Data + 0x100 - 4, 4), bus.Read(Data + 0x100 - 8, 4), bus.Read(Data + 0x100 - 12, 4)));
        (cpu.X[1], cpu.X[8], cpu.X[9]) = (0, 0, 0);
        cpu.Step(); // cm.popret
        Assert.Equal(Data + 0x100, cpu.X[2]);
        Assert.Equal((0x2468u, 0x1111u, 0x2222u), (cpu.X[1], cpu.X[8], cpu.X[9]));
        Assert.Equal(0x2468u, cpu.Pc);

        // {ra, s0-s11}: 13 registers, 52 bytes, so 64, and 16 more; s11 (x27) highest, then s10 ... s2 (x18), s1, s0, ra
        (cpu, bus) = Core();
        bus.LoadBytes(Code, [.. forms[2].Bytes, .. forms[3].Bytes]);
        cpu.X[2] = Data + 0x200;
        int[] saved = [27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 9, 8, 1];
        foreach (var r in saved)
        {
            cpu.X[r] = 0x100u + (uint)r;
        }

        cpu.X[10] = 77;
        cpu.Step();
        Assert.Equal(Data + 0x200 - 80, cpu.X[2]);
        for (var i = 0; i < saved.Length; i++)
        {
            Assert.Equal(0x100u + (uint)saved[i], bus.Read(Data + 0x200 - 4 * (uint)(i + 1), 4));
        }

        foreach (var r in saved)
        {
            cpu.X[r] = 0;
        }

        cpu.Step(); // cm.popretz: a0 = 0 too
        Assert.All(saved, r => Assert.Equal(0x100u + (uint)r, cpu.X[r]));
        Assert.Equal(0u, cpu.X[10]);
        Assert.Equal(Data + 0x200, cpu.X[2]);
        Assert.Equal(0x101u & ~1u, cpu.Pc);

        (cpu, bus) = Core();
        bus.LoadBytes(Code, [.. forms[4].Bytes, .. forms[5].Bytes]);
        (cpu.X[10], cpu.X[11], cpu.X[21], cpu.X[8]) = (5, 6, 7, 8);
        cpu.Step();
        Assert.Equal((5u, 6u), (cpu.X[9], cpu.X[19])); // cm.mvsa01 s1, s3: s1 = a0, s3 = a1
        cpu.Step();
        Assert.Equal((7u, 8u), (cpu.X[10], cpu.X[11])); // cm.mva01s s5, s0: a0 = s5, a1 = s0
    }

    [Esp32c5Fact]
    public async Task Zcmt_jumps_through_jvt_s_table_and_jalt_links()
    {
        var forms = await AssembleAsync(["cm.jt 5", "cm.jalt 33"], "rv32imac_zcmt", _scratch);
        var (cpu, bus) = Core();
        bus.LoadBytes(Code, [.. forms[0].Bytes]);
        bus.LoadBytes(Code + 0x40, [.. forms[1].Bytes]);
        bus.Load(Table + 4 * 5, Code + 0x40);
        bus.Load(Table + 4 * 33, 0x3001);
        cpu.Jvt = Table;
        cpu.X[1] = 0x99;

        Assert.Equal(3, cpu.Step());
        Assert.Equal(Code + 0x40, cpu.Pc);
        Assert.Equal(0x99u, cpu.X[1]);
        cpu.Step();
        Assert.Equal(0x3000u, cpu.Pc);
        Assert.Equal(Code + 0x42, cpu.X[1]);
    }

    /// <summary>
    /// Assembles <paramref name="lines"/> with clang (-march, -mabi) and reads each instruction back from llvm-objdump.
    /// </summary>
    internal static async Task<List<(byte[] Bytes, string Text)>> AssembleAsync(IEnumerable<string> lines, string march, string folder,
        string abi = "ilp32")
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllLinesAsync(Path.Combine(folder, "forms.S"), [".text", .. lines.Select(line => "\t" + line)]);
        var assembled = await LlvmTools.RunAsync(LlvmTools.Clang,
            ["--target=riscv32-unknown-elf", $"-march={march}", $"-mabi={abi}", "-mno-relax", "-c", "forms.S", "-o", "forms.o"], folder);
        Assert.True(assembled.ExitCode == 0, $"clang failed:\n{assembled}");
        var listing = await LlvmTools.RunAsync(PicoTools.LlvmTool("llvm-objdump"), ["-d", "-M", "no-aliases", "forms.o"], folder);
        Assert.True(listing.ExitCode == 0, $"llvm-objdump failed:\n{listing}");
        var forms = new List<(byte[], string)>();
        foreach (var line in listing.Output.Split('\n'))
        {
            var match = ListingLine().Match(line.TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            var word = uint.Parse(match.Groups["word"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var length = match.Groups["word"].Value.Length / 2;
            forms.Add(([.. Enumerable.Range(0, length).Select(b => (byte)(word >> (8 * b)))], match.Groups["text"].Value.Trim()));
        }

        return forms;
    }

    // "       0: 00b50533     	add	a0, a0, a1", "      4: 0505         	c.addi	a0, 0x1"
    [GeneratedRegex(@"^\s*[0-9a-f]+:\s+(?<word>[0-9a-f]{8}|[0-9a-f]{4})\s+(?<text>\S.*)$")]
    private static partial Regex ListingLine();
}
