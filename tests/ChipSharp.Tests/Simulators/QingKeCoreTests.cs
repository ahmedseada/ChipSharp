using System.Buffers.Binary;
using System.Text.RegularExpressions;
using ChipSharp.Simulators;
using ChipSharp.Simulators.RiscV;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// The RISC-V core configured as WCH's QingKe V2A (<see cref="RiscVCore.QingKeV2A"/>, the CH32V003's), an instruction at
/// a time on a flat memory: every instruction form of RV32EC, Zicsr, Zifencei and XW clang assembles decodes to its
/// length; XW's loads and stores do what their 32-bit expansions do; what the V2A does not have (x16-x31, M, A, Zcb's
/// operations, the counters and other CSRs) is illegal; its CSRs are the manual's; a trap goes through each of mtvec's
/// modes and a VTF channel; the HPE pushes and pops the caller-saved registers (Table 3-3) for 2 levels; nesting keeps MIE
/// at the first level and mret gives the interrupted level its mepc back; misaligned accesses trap.
/// </summary>
public sealed partial class QingKeCoreTests : IDisposable
{
    private const uint Code = 0x1000, Data = 0x8000, Table = 0x4000, Stack = 0xC000;

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

    /// <summary>A PFIC that says which level a trap is, and has a VTF channel for one interrupt.</summary>
    private sealed class FakePfic(int vtfNumber = -1, uint vtfAddress = 0) : IQingKeInterrupts
    {
        public List<int> Active { get; } = [];

        public int Enter(int number)
        {
            Active.Add(number);
            return Active.Count;
        }

        public void Return() => Active.RemoveAt(Active.Count - 1);

        public uint? FreeVector(int number) => number == vtfNumber ? vtfAddress : null;
    }

    /// <summary>A V2A with mtvec's unified entry at <see cref="Table"/>.</summary>
    private static (RiscVCpu Cpu, FlatBus Bus) Core(params uint[] code)
    {
        var bus = new FlatBus();
        bus.Load(Code, code);
        var cpu = new RiscVCpu(bus, RiscVCore.QingKeV2A) { Pc = Code, Mtvec = Table };
        cpu.X[2] = Stack;
        return (cpu, bus);
    }

    private static uint Csr(uint funct3, int rd, int rs1, int csr) =>
        ((uint)csr << 20) | ((uint)rs1 << 15) | (funct3 << 12) | ((uint)rd << 7) | 0x73;

    private const uint Mret = 0x30200073, Ecall = 0x00000073;

    [Ch32v003Fact]
    public async Task Every_RV32EC_Zicsr_Zifencei_and_XW_instruction_form_clang_assembles_decodes_to_its_length()
    {
        var forms = await RiscVCpuTests.AssembleAsync(Forms(), "rv32ec_zicsr_zifencei_xwchc", _scratch, "ilp32e");
        Assert.True(forms.Count > 120, $"Only {forms.Count} instructions in the listing.");
        var compressed = 0;
        foreach (var (bytes, text) in forms)
        {
            var (cpu, bus) = Core();
            bus.LoadBytes(Code, bytes);
            cpu.X[1] = Code + 0x200;
            for (var r = 8; r < 16; r++)
            {
                cpu.X[r] = Data + (uint)(16 * r);
            }

            cpu.X[2] = Data + 0x400;
            cpu.Step();
            Assert.False(cpu.Pc == Table && (cpu.Mcause & 0xFF) == RiscVCpu.IllegalInstruction,
                $"{text} ({Convert.ToHexString(bytes)}): illegal to the core");
            if (!Jumps().IsMatch(text) && !cpu.Waiting && !cpu.BreakHit)
            {
                Assert.True(cpu.Pc == Code + bytes.Length, $"{text} ({Convert.ToHexString(bytes)}) took {cpu.Pc - Code} bytes, not {bytes.Length}");
            }

            compressed += bytes.Length == 2 ? 1 : 0;
        }

        Assert.True(compressed > 40, $"Only {compressed} compressed forms.");
    }

    [GeneratedRegex(@"^(c\.)?(j|jal|jr|jalr|beq|bne|blt|bge|bltu|bgeu|beqz|bnez|ecall|mret|ret)\b")]
    private static partial Regex Jumps();

    /// <summary>Every form of RV32EC's instructions, Zicsr's on the V2A's CSRs, Zifencei's and XW's, in clang's syntax.</summary>
    private static IEnumerable<string> Forms()
    {
        foreach (var op in new[] { "add", "sub", "sll", "slt", "sltu", "xor", "srl", "sra", "or", "and" })
        {
            yield return $"{op} a0, a1, a2";
            yield return $"{op} t2, s1, a5";
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

        yield return "lui a5, 0x12345";
        yield return "auipc t0, 0x12";
        yield return "jal ra, .+256";
        yield return "jalr ra, 4(a1)";
        yield return "fence rw, rw";
        yield return "fence.i";
        yield return "ecall";
        yield return "ebreak";
        yield return "mret";
        yield return "wfi";
        foreach (var csr in new[] { "mstatus", "misa", "mtvec", "mscratch", "mepc", "mcause", "0x804" })
        {
            yield return $"csrrw a0, {csr}, a1";
            yield return $"csrrs a0, {csr}, zero";
            yield return $"csrrci a0, {csr}, 0";
            yield return $"csrrsi a0, {csr}, 31";
        }

        yield return "csrr a0, marchid";
        yield return "csrr a0, mimpid";

        foreach (var form in new[]
        {
            "c.addi4spn a0, sp, 16", "c.lw a0, 4(a1)", "c.sw a0, 8(a1)", "c.nop", "c.addi a0, -3", "c.addi t2, 7", "c.jal .+32",
            "c.li a0, -5", "c.li a5, 31", "c.addi16sp sp, -64", "c.lui a0, 0x1f", "c.srli a0, 3", "c.srai a0, 3", "c.andi a0, -2",
            "c.sub a0, a1", "c.xor a0, a1", "c.or a0, a1", "c.and a0, a1", "c.j .+20", "c.beqz a0, .+12", "c.bnez a0, .+12",
            "c.slli a0, 5", "c.slli t0, 31", "c.lwsp a0, 12(sp)", "c.lwsp a5, 252(sp)", "c.jr a1", "c.mv a0, a1", "c.mv a5, t2",
            "c.ebreak", "c.jalr a1", "c.add a0, a1", "c.add t1, a5", "c.swsp a0, 16(sp)", "c.swsp a5, 252(sp)",
            "qk.c.lbu a0, 0(a1)", "qk.c.lbu a5, 31(s0)", "qk.c.sb a0, 17(a1)", "qk.c.sb s1, 31(a5)", "qk.c.lhu a0, 2(a1)",
            "qk.c.lhu a2, 62(a3)", "qk.c.sh a0, 34(a1)", "qk.c.sh s0, 62(a4)", "qk.c.lbusp a0, 0(sp)", "qk.c.lbusp a5, 15(sp)",
            "qk.c.sbsp a1, 9(sp)", "qk.c.lhusp a0, 30(sp)", "qk.c.lhusp s1, 16(sp)", "qk.c.shsp a3, 6(sp)", "qk.c.shsp a4, 30(sp)",
        })
        {
            yield return form;
        }
    }

    /// <summary>
    /// XW's loads and stores do what their 32-bit expansions do: run from the same registers (random, but for the base
    /// addresses) and memory, the two leave the same registers and memory.
    /// </summary>
    [Ch32v003Fact]
    public async Task Every_XW_instruction_does_what_its_expansion_does()
    {
        (string Compressed, string Expanded)[] pairs =
        [
            ("qk.c.lbu a0, 0(a1)", "lbu a0, 0(a1)"), ("qk.c.lbu a5, 31(s0)", "lbu a5, 31(s0)"), ("qk.c.lbu s1, 13(a4)", "lbu s1, 13(a4)"),
            ("qk.c.sb a0, 17(a1)", "sb a0, 17(a1)"), ("qk.c.sb s1, 31(a5)", "sb s1, 31(a5)"), ("qk.c.sb a3, 6(s0)", "sb a3, 6(s0)"),
            ("qk.c.lhu a0, 2(a1)", "lhu a0, 2(a1)"), ("qk.c.lhu a2, 62(a4)", "lhu a2, 62(a4)"), ("qk.c.lhu s0, 40(a5)", "lhu s0, 40(a5)"),
            ("qk.c.sh a0, 34(a1)", "sh a0, 34(a1)"), ("qk.c.sh s0, 62(a4)", "sh s0, 62(a4)"), ("qk.c.sh a5, 10(s1)", "sh a5, 10(s1)"),
            ("qk.c.lbusp a0, 0(sp)", "lbu a0, 0(sp)"), ("qk.c.lbusp a5, 15(sp)", "lbu a5, 15(sp)"), ("qk.c.lbusp s1, 6(sp)", "lbu s1, 6(sp)"),
            ("qk.c.sbsp a1, 9(sp)", "sb a1, 9(sp)"), ("qk.c.sbsp s0, 15(sp)", "sb s0, 15(sp)"),
            ("qk.c.lhusp a0, 30(sp)", "lhu a0, 30(sp)"), ("qk.c.lhusp s1, 16(sp)", "lhu s1, 16(sp)"), ("qk.c.lhusp a2, 2(sp)", "lhu a2, 2(sp)"),
            ("qk.c.shsp a3, 6(sp)", "sh a3, 6(sp)"), ("qk.c.shsp a4, 30(sp)", "sh a4, 30(sp)"), ("qk.c.shsp a5, 16(sp)", "sh a5, 16(sp)"),
        ];
        var compressed = await RiscVCpuTests.AssembleAsync(pairs.Select(pair => pair.Compressed), "rv32ec_xwchc",
            Path.Combine(_scratch, "c"), "ilp32e");
        var expanded = await RiscVCpuTests.AssembleAsync(pairs.Select(pair => pair.Expanded), "rv32e", Path.Combine(_scratch, "e"),
            "ilp32e");
        Assert.Equal(pairs.Length, compressed.Count);
        Assert.All(compressed, form => Assert.Equal(2, form.Bytes.Length));
        var random = new Random(7);
        for (var i = 0; i < pairs.Length; i++)
        {
            for (var round = 0; round < 20; round++)
            {
                var registers = new uint[16];
                for (var r = 1; r < 16; r++)
                {
                    registers[r] = (uint)random.NextInt64() >> random.Next(32);
                }

                foreach (var r in new[] { 2, 8, 9, 11, 14, 15 }) // sp, s0, s1, a1, a4, a5: where the loads and stores go
                {
                    registers[r] = Data + 0x200 + 2 * (uint)random.Next(64);
                }

                var memory = new byte[0x800];
                random.NextBytes(memory);
                var (a, aBus) = Prepared(registers, memory, compressed[i].Bytes);
                var (b, bBus) = Prepared(registers, memory, expanded[i].Bytes);
                a.Step();
                b.Step();

                Assert.True(a.Pc == Code + 2 && b.Pc == Code + 4, $"{pairs[i].Compressed}: pc 0x{a.Pc:x}, its expansion's 0x{b.Pc:x}");
                var differs = Enumerable.Range(0, 16).Where(r => a.X[r] != b.X[r]).ToList();
                Assert.True(differs.Count == 0, $"{pairs[i].Compressed}: x{differs.FirstOrDefault()} differs");
                Assert.True(aBus.Memory.AsSpan((int)Data, 0x800).SequenceEqual(bBus.Memory.AsSpan((int)Data, 0x800)),
                    $"{pairs[i].Compressed}: the memory differs");
            }
        }

        static (RiscVCpu Cpu, FlatBus Bus) Prepared(uint[] registers, byte[] memory, byte[] code)
        {
            var (cpu, bus) = Core();
            bus.LoadBytes(Code, code);
            bus.LoadBytes(Data, memory);
            registers.CopyTo(cpu.X, 0);
            return (cpu, bus);
        }
    }

    /// <summary>
    /// What the V2A does not have is illegal: an instruction that names x16-x31 (RV32E), M's and A's, Zcb's c.zext.b,
    /// c.not and c.mul, the CSRs the manual does not list (the counters, mtval, mvendorid, mhartid, the CLIC's).
    /// (Zcb's loads and stores and Zcmp's and Zcmt's encodings are XW's on the V2A.)
    /// </summary>
    [Ch32v003Fact]
    public async Task What_the_V2A_does_not_have_is_illegal()
    {
        string[] forms =
        [
            "add a6, a0, a1", "add a0, a6, a1", "add a0, a1, s2", "addi t3, a0, 1", "lw a0, 0(s2)", "sw t4, 0(a0)", "beq a0, x16, .+8",
            "lui s3, 1", "auipc t5, 1", "jal x17, .+8", "jalr ra, 0(x20)", "csrrw a6, mscratch, a0", "csrrwi x31, mscratch, 1",
            "c.mv a0, a6", "c.mv a6, a0", "c.add s2, a0", "c.li x16, 1", "c.addi t3, 1", "c.lui x20, 1", "c.slli t6, 1",
            "c.lwsp x18, 0(sp)", "c.swsp t5, 0(sp)", "c.jr x16",
            "mul a0, a1, a2", "mulh a0, a1, a2", "mulhsu a0, a1, a2", "mulhu a0, a1, a2", "div a0, a1, a2", "divu a0, a1, a2",
            "rem a0, a1, a2", "remu a0, a1, a2", "lr.w a0, (a1)", "sc.w a0, a1, (a2)", "amoadd.w a0, a1, (a2)", "amoswap.w a0, a1, (a2)",
            "c.zext.b a0", "c.not a0", "c.mul a0, a1",
            "csrr a0, mcycle", "csrr a0, minstret", "csrr a0, cycle", "csrr a0, time", "csrr a0, mtval", "csrr a0, mvendorid",
            "csrr a0, mhartid", "csrr a0, mie", "csrr a0, mip", "csrr a0, 0x307", "csrr a0, 0x345", "csrr a0, dcsr", "csrr a0, dpc",
        ];
        var assembled = await RiscVCpuTests.AssembleAsync(forms, "rv32imac_zicsr_zcb", _scratch);
        Assert.Equal(forms.Length, assembled.Count);
        foreach (var (bytes, text) in assembled)
        {
            var (cpu, bus) = Core();
            bus.LoadBytes(Code, bytes);
            cpu.X[11] = Data;
            cpu.X[12] = Data;
            cpu.Step();
            Assert.True(cpu.Pc == Table && cpu.Mcause == RiscVCpu.IllegalInstruction,
                $"{text} ({Convert.ToHexString(bytes)}) is not illegal to the V2A: pc 0x{cpu.Pc:x}, mcause {cpu.Mcause}");
            Assert.Equal(Code, cpu.Mepc);
        }
    }

    [Fact]
    public void The_CSRs_are_chapter_7_s_and_mstatus_keeps_machine_mode()
    {
        var (cpu, _) = Core(Csr(2, 10, 0, 0x301), Csr(2, 11, 0, 0xF12), Csr(2, 12, 0, 0xF13), Csr(1, 0, 0, 0x300),
            Csr(2, 13, 0, 0x300), Csr(1, 0, 5, 0x804), Csr(2, 14, 0, 0x804), Csr(1, 0, 6, 0x305), Csr(2, 15, 0, 0x305));
        cpu.X[5] = 0xFFFF_FFFF;
        cpu.X[6] = 0x0000_0403;
        for (var i = 0; i < 9; i++)
        {
            cpu.Step();
        }

        Assert.Equal(0x4080_0014u, cpu.X[10]); // misa: RV32, C, E, X
        Assert.Equal(0xDC68_D841u, cpu.X[11]); // marchid: WCH-V2A
        Assert.Equal(RiscVCpu.QingKeV2aMimpid, cpu.X[12]);
        Assert.Equal(0x1800u, cpu.X[13]); // MPP reads machine mode whatever is written
        Assert.Equal(3u, cpu.X[14]); // INTSYSCR: HWSTKEN and INESTEN
        Assert.Equal(0x0000_0403u, cpu.X[15]); // mtvec as written: the base and both modes
        Assert.Equal(Code + 36, cpu.Pc);
    }

    /// <summary>An exception and interrupt 12 through each of mtvec's modes, and a VTF channel's address.</summary>
    [Theory]
    [InlineData(0u, false, Table, Table)] // MODE0 0: the unified entry for both
    [InlineData(1u, false, Table + 12, Table + 48)] // MODE0 1, MODE1 0: the table's instruction, base + 4 x the number
    [InlineData(3u, false, 0x2222u, 0x3330u)] // MODE 3: the addresses the table holds (its bit 0 cleared)
    [InlineData(2u, false, 0x5550u, 0x5550u)] // MODE0 0, MODE1 1: the address at the base, for both
    [InlineData(3u, true, 0x2222u, 0x7770u)] // a VTF channel for 12: its address, not the table's
    public void A_trap_goes_where_mtvec_s_modes_or_a_VTF_channel_say(uint mode, bool vtf, uint exception, uint interrupt)
    {
        var (cpu, bus) = Core(Ecall);
        bus.Load(Table, 0x5551);
        bus.Load(Table + 12, 0x2222);
        bus.Load(Table + 48, 0x3331);
        cpu.Mtvec = Table | mode;
        cpu.Pfic = new FakePfic(vtf ? 12 : -1, 0x7771);

        cpu.Step(); // ecall: exception 11, through number 3
        Assert.Equal(exception, cpu.Pc);
        Assert.Equal(11u, cpu.Mcause);
        Assert.Equal(Code, cpu.Mepc);

        (cpu, bus) = Core();
        bus.Load(Table, 0x5551);
        bus.Load(Table + 12, 0x2222);
        bus.Load(Table + 48, 0x3331);
        cpu.Mtvec = Table | mode;
        cpu.Pfic = new FakePfic(vtf ? 12 : -1, 0x7771);
        cpu.Mstatus = RiscVCpu.MstatusMie;
        cpu.TakeQingKeInterrupt(12);
        Assert.Equal(interrupt, cpu.Pc);
        Assert.Equal(0x8000_000Cu, cpu.Mcause);
        Assert.Equal(Code, cpu.Mepc); // the next instruction to run
    }

    [Theory]
    [InlineData(0u, "the program caused an ecall at pc 0x00001000 (mcause 11), and has no trap handler (mtvec 0x00000000")]
    [InlineData(0x4101u, "but mtvec's vector table at 0x00004100 is not 1 KB aligned, as the QingKe V2 needs")]
    [InlineData(0x4003u, "and its vector table's entry at 0x0000400c (mtvec 0x00004003) is 0: no handler")]
    public void A_trap_the_chip_could_not_serve_stops_the_run_saying_why(uint mtvec, string why)
    {
        var (cpu, _) = Core(Ecall);
        cpu.Mtvec = mtvec;

        var fault = Assert.Throws<ProgramFault>(() => cpu.Step());

        Assert.Contains(why, fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_HPE_pushes_the_caller_saved_registers_as_Table_3_3_lays_them_out_and_mret_pops_them()
    {
        var (cpu, bus) = Core();
        bus.Load(Table, Mret);
        cpu.Pfic = new FakePfic();
        cpu.InterruptSystemControl = RiscVCpu.IntsyscrHpe;
        int[] saved = [1, 5, 6, 7, 10, 11, 12, 13, 14, 15];
        foreach (var r in saved.Concat([8, 9, 3, 4]))
        {
            cpu.X[r] = 0x100u + (uint)r;
        }

        cpu.Mstatus = RiscVCpu.MstatusMie;
        Assert.Equal(13, cpu.TakeQingKeInterrupt(12)); // 3, and 10 for the pushes

        Assert.Equal(Stack - 48, cpu.X[2]);
        for (var i = 0; i < saved.Length; i++)
        {
            Assert.Equal(0x100u + (uint)saved[i], bus.Read(Stack - 4 * (uint)(i + 1), 4)); // x1 at sp - 4, x5 at sp - 8, ...
        }

        Assert.Equal(RiscVCpu.MstatusMpop, cpu.Mstatus & (RiscVCpu.MstatusMpop | RiscVCpu.MstatusMppop));
        Assert.Equal(1, cpu.HardwareStackDepth);
        foreach (var r in saved)
        {
            cpu.X[r] = 0xDEAD;
        }

        Assert.Equal(13, cpu.Step()); // mret: 3, and 10 for the pops
        Assert.Equal(Code, cpu.Pc);
        Assert.Equal(Stack, cpu.X[2]);
        Assert.All(saved, r => Assert.Equal(0x100u + (uint)r, cpu.X[r]));
        Assert.Equal(0u, cpu.Mstatus & RiscVCpu.MstatusMpop);
        Assert.Equal(0, cpu.HardwareStackDepth);
        Assert.True(cpu.InterruptsEnabled);
    }

    [Fact]
    public void Nesting_keeps_MIE_at_the_first_level_the_HPE_holds_2_levels_and_mret_gives_each_level_its_mepc_back()
    {
        var (cpu, bus) = Core();
        bus.Load(Table, Mret);
        var pfic = new FakePfic();
        cpu.Pfic = pfic;
        cpu.InterruptSystemControl = RiscVCpu.IntsyscrHpe | RiscVCpu.IntsyscrNesting;
        cpu.Mstatus = RiscVCpu.MstatusMie;

        cpu.TakeQingKeInterrupt(20); // level 1: MIE stays, the PFIC lets a higher preemption priority in
        Assert.True(cpu.InterruptsEnabled);
        Assert.Equal(Code, cpu.Mepc);
        cpu.Pc = 0x2000; // somewhere in its handler
        cpu.TakeQingKeInterrupt(14); // level 2: MIE cleared
        Assert.False(cpu.InterruptsEnabled);
        Assert.Equal(0x2000u, cpu.Mepc);
        Assert.Equal(2, cpu.HardwareStackDepth);
        Assert.Equal(RiscVCpu.MstatusMpop | RiscVCpu.MstatusMppop, cpu.Mstatus & (RiscVCpu.MstatusMpop | RiscVCpu.MstatusMppop));
        Assert.Equal(Stack - 96, cpu.X[2]);
        cpu.Pc = 0x2100;
        bus.Load(0x2100, Ecall);
        cpu.Step(); // an exception at level 3: no third push
        Assert.Equal(2, cpu.HardwareStackDepth);
        Assert.Equal(Stack - 96, cpu.X[2]);
        Assert.Equal([20, 14, 3], pfic.Active);

        cpu.Pc = Table;
        cpu.Step(); // the exception's mret (mepc 0x2100): no pop
        Assert.Equal(0x2100u, cpu.Pc);
        Assert.Equal(Stack - 96, cpu.X[2]);
        cpu.Pc = Table;
        cpu.Step(); // interrupt 14's mret: back to 0x2000, the HPE pops a level
        Assert.Equal(0x2000u, cpu.Pc);
        Assert.Equal(Stack - 48, cpu.X[2]);
        Assert.Equal(Code, cpu.Mepc); // interrupt 20's, back for its mret
        Assert.True(cpu.InterruptsEnabled);
        cpu.Pc = Table;
        cpu.Step();
        Assert.Equal(Code, cpu.Pc);
        Assert.Equal(Stack, cpu.X[2]);
        Assert.Empty(pfic.Active);
    }

    [Fact]
    public void Without_nesting_a_trap_clears_MIE_and_mret_sets_it_back()
    {
        var (cpu, _) = Core();
        cpu.Mstatus = RiscVCpu.MstatusMie;

        cpu.TakeQingKeInterrupt(12);

        Assert.False(cpu.InterruptsEnabled);
        Assert.Equal(RiscVCpu.MstatusMpie, cpu.Mstatus & RiscVCpu.MstatusMpie);
        Assert.Equal(0, cpu.HardwareStackDepth); // HWSTKEN 0: no pushes
    }

    [Theory]
    [InlineData(0x00052503u, 2u, 4u)] // lw a0, 0(a0) at a halfword
    [InlineData(0x00051503u, 1u, 4u)] // lh a0, 0(a0) at an odd address
    [InlineData(0x00b52023u, 2u, 6u)] // sw a1, 0(a0)
    [InlineData(0x00b51023u, 3u, 6u)] // sh a1, 0(a0)
    public void A_misaligned_load_or_store_traps_with_4_or_6(uint instruction, uint offset, uint cause)
    {
        var (cpu, _) = Core(instruction);
        cpu.X[10] = Data + offset;

        cpu.Step();

        Assert.Equal(Table, cpu.Pc);
        Assert.Equal(cause, cpu.Mcause);
        Assert.Contains($"a misaligned {(cause == 4 ? "load" : "store")} of 0x{Data + offset:x8}", cpu.LastException,
            StringComparison.Ordinal);
    }
}
