using System.Text.RegularExpressions;
using ChipSharp.Simulators;
using ChipSharp.Simulators.Pdk14;
using ChipSharp.Tests.Sdcc;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// The PDK14 core, an instruction at a time: the flags (Z, C, AC, OV) of the additions and subtractions, the skips and
/// their cycles, the shifts, the stack (call, ret, ret k, pushaf, popaf), idxm, ldt16 / stt16, pcadd, the interrupt;
/// and every form of every instruction, as SDCC's assembler encodes it, decoded as one instruction.
/// </summary>
public sealed partial class Pdk14CpuTests
{
    private const byte Z = Pdk14Cpu.Z, C = Pdk14Cpu.C, AC = Pdk14Cpu.AC, OV = Pdk14Cpu.OV;

    /// <summary>An I/O space of plain registers, and a Timer16.</summary>
    private sealed class PlainIo : IPdk14Io
    {
        public readonly byte[] Registers = new byte[64];

        public ushort Timer16 { get; set; }

        public bool WatchdogReset { get; private set; }

        public byte Read(int address) => Registers[address];

        public void Write(int address, byte value) => Registers[address] = value;

        public void ResetWatchdog() => WatchdogReset = true;
    }

    private static (Pdk14Cpu Cpu, PlainIo Io) Core(params ushort[] words)
    {
        var rom = new ushort[2048];
        words.CopyTo(rom, 0);
        var io = new PlainIo();
        return (new Pdk14Cpu(rom, new byte[128], io) { Sp = 0x60 }, io);
    }

    [Theory]
    // add a, k (0x28kk): Z, C (carry out), AC (carry out of bit 3), OV (signed overflow)
    [InlineData(0x2801, 0x7F, false, 0x80, AC | OV)]
    [InlineData(0x2801, 0xFF, false, 0x00, Z | C | AC)]
    [InlineData(0x2880, 0x80, false, 0x00, Z | C | OV)]
    [InlineData(0x2810, 0x05, false, 0x15, 0)]
    // sub a, k (0x29kk): C and AC are borrows
    [InlineData(0x2901, 0x00, false, 0xFF, C | AC)]
    [InlineData(0x2901, 0x80, false, 0x7F, AC | OV)]
    [InlineData(0x2910, 0x10, false, 0x00, Z)]
    // addc a / subc a (0x0060 / 0x0061): the carry
    [InlineData(0x0060, 0xFF, true, 0x00, Z | C | AC)]
    [InlineData(0x0061, 0x00, true, 0xFF, C | AC)]
    [InlineData(0x0061, 0x05, false, 0x05, 0)]
    // and, or, xor a, k: Z only, C kept
    [InlineData(0x2C0F, 0xF0, true, 0x00, Z | C)]
    [InlineData(0x2D80, 0x01, false, 0x81, 0)]
    [InlineData(0x2EFF, 0xFF, false, 0x00, Z)]
    public void The_alu_sets_the_flags(int op, byte a, bool carry, byte result, int flags)
    {
        var (cpu, _) = Core((ushort)op);
        cpu.A = a;
        cpu.Flags = carry ? C : (byte)0;

        Assert.Equal(1, cpu.Step());

        Assert.Equal(result, cpu.A);
        Assert.Equal(flags, cpu.Flags);
    }

    [Fact]
    public void Memory_operations_read_and_write_RAM()
    {
        // mov a, #5; add 0x20, a; sub a, 0x20; addc 0x21; xch 0x22; mov 0x23, a; mov a, 0x23; clear 0x23; inc 0x24; dec 0x24
        var (cpu, _) = Core(0x2F05, 0x0820, 0x0CA0, 0x1021, 0x13A2, 0x0BA3, 0x0FA3, 0x1323, 0x1224, 0x12A4);
        cpu.Ram[0x20] = 10;
        cpu.Ram[0x21] = 0xFF;
        cpu.Ram[0x22] = 0x77;

        cpu.Step();
        cpu.Step(); // add 0x20, a: 15
        Assert.Equal(15, cpu.Ram[0x20]);
        cpu.Step(); // sub a, 0x20: 5 - 15
        Assert.Equal((0xF6, C | AC), (cpu.A, cpu.Flags));
        cpu.Step(); // addc 0x21: 0xff + C
        Assert.Equal((0x00, Z | C | AC), (cpu.Ram[0x21], cpu.Flags));
        cpu.Step(); // xch 0x22
        Assert.Equal((0x77, 0xF6), (cpu.A, cpu.Ram[0x22]));
        cpu.Step(); // mov 0x23, a
        cpu.Step(); // mov a, 0x23: sets Z
        Assert.Equal((0x77, 0x77), (cpu.Ram[0x23], cpu.A));
        Assert.Equal(0, cpu.Flags & Z);
        cpu.Step();
        Assert.Equal(0, cpu.Ram[0x23]);
        cpu.Step();
        cpu.Step();
        Assert.Equal(0, cpu.Ram[0x24]);
        Assert.Equal(Z, cpu.Flags & Z);
    }

    [Theory]
    [InlineData(0x2A05, 5, true)]       // ceqsn a, #5
    [InlineData(0x2A05, 6, false)]
    [InlineData(0x2B05, 6, true)]       // cneqsn a, #5
    [InlineData(0x0062, 0xFF, true)]    // izsn a
    [InlineData(0x0063, 0x02, false)]   // dzsn a
    [InlineData(0x0063, 0x01, true)]
    public void A_skip_skips_the_next_instruction_in_2_cycles(int op, byte a, bool skips)
    {
        var (cpu, _) = Core((ushort)op, 0x0000, 0x0000);
        cpu.A = a;

        var cycles = cpu.Step();

        Assert.Equal(skips ? 2 : 1, cpu.Pc);
        Assert.Equal(skips ? 2 : 1, cycles);
    }

    [Fact]
    public void Bit_tests_and_sets_of_memory_and_io()
    {
        // set1 0x10.3; t1sn 0x10.3 (skips); nop; set0 0x10.3; t0sn 0x10.3 (skips); nop; set1.io 0x05.7; t0sn.io 0x05.7 (no skip)
        var (cpu, io) = Core(0x26D0, 0x22D0, 0x0000, 0x24D0, 0x20D0, 0x0000, 0x1FC5, 0x19C5);

        cpu.Step();
        Assert.Equal(0x08, cpu.Ram[0x10]);
        cpu.Step();
        Assert.Equal(3, cpu.Pc);
        cpu.Step();
        Assert.Equal(0, cpu.Ram[0x10]);
        cpu.Step();
        Assert.Equal(6, cpu.Pc);
        cpu.Step();
        Assert.Equal(0x80, io.Registers[5]);
        cpu.Step();
        Assert.Equal(8, cpu.Pc);
    }

    [Theory]
    [InlineData(0x006A, 0x81, false, 0x40, true)]  // sr a
    [InlineData(0x006B, 0x81, false, 0x02, true)]  // sl a
    [InlineData(0x006C, 0x02, true, 0x81, false)]  // src a
    [InlineData(0x006D, 0x80, true, 0x01, true)]   // slc a
    [InlineData(0x006E, 0x12, false, 0x21, false)] // swap a
    [InlineData(0x0068, 0x0F, false, 0xF0, false)] // not a
    [InlineData(0x0069, 0x01, false, 0xFF, false)] // neg a
    public void Shifts_and_the_rest_of_A(int op, byte a, bool carry, byte result, bool carryAfter)
    {
        var (cpu, _) = Core((ushort)op);
        cpu.A = a;
        cpu.Flags = carry ? C : (byte)0;

        cpu.Step();

        Assert.Equal(result, cpu.A);
        Assert.Equal(carryAfter, (cpu.Flags & C) != 0);
    }

    [Fact]
    public void Call_ret_and_ret_k_use_the_stack_upwards()
    {
        // call 0x100; ...; 0x100: call 0x200; ret; 0x200: ret #0x42
        var (cpu, _) = Core(0x3900);
        cpu.Rom[0x100] = 0x3A00;
        cpu.Rom[0x101] = 0x007A;
        cpu.Rom[0x200] = 0x0242;

        Assert.Equal(2, cpu.Step());
        Assert.Equal((0x100, 0x62, 0x01, 0x00), (cpu.Pc, cpu.Sp, cpu.Ram[0x60], cpu.Ram[0x61]));
        cpu.Step();
        Assert.Equal((0x200, 0x64, 0x01, 0x01), (cpu.Pc, cpu.Sp, cpu.Ram[0x62], cpu.Ram[0x63]));
        Assert.Equal(2, cpu.Step()); // ret #0x42
        Assert.Equal((0x101, 0x62, 0x42), (cpu.Pc, cpu.Sp, cpu.A));
        cpu.Step(); // ret
        Assert.Equal((0x001, 0x60), (cpu.Pc, cpu.Sp));
    }

    [Fact]
    public void Pushaf_puts_A_then_FLAG_and_popaf_takes_them_back()
    {
        var (cpu, _) = Core(0x0072, 0x2F00, 0x0073);
        cpu.A = 0x5A;
        cpu.Flags = C | OV;

        cpu.Step();
        Assert.Equal((0x5A, C | OV, 0x62), (cpu.Ram[0x60], cpu.Ram[0x61], cpu.Sp));
        cpu.Step(); // mov a, #0
        cpu.Flags = 0;
        cpu.Step();
        Assert.Equal((0x5A, C | OV, 0x60), (cpu.A, cpu.Flags, cpu.Sp));
    }

    [Fact]
    public void Idxm_reads_and_writes_through_a_pointer_in_2_cycles()
    {
        // idxm 0x10, a: [0x10] <- a; idxm a, 0x12: a <- [0x12]
        var (cpu, _) = Core(0x0390, 0x0393);
        cpu.Ram[0x10] = 0x40;
        cpu.Ram[0x12] = 0x41;
        cpu.Ram[0x41] = 0x99;
        cpu.A = 0x77;

        Assert.Equal(2, cpu.Step());
        Assert.Equal(0x77, cpu.Ram[0x40]);
        Assert.Equal(2, cpu.Step());
        Assert.Equal(0x99, cpu.A);
    }

    [Fact]
    public void Ldt16_and_stt16_move_Timer16_to_and_from_a_word_of_RAM()
    {
        var (cpu, io) = Core(0x0320, 0x0323);
        cpu.Ram[0x20] = 0x34;
        cpu.Ram[0x21] = 0x12;

        cpu.Step(); // stt16 0x20
        Assert.Equal(0x1234, io.Timer16);
        io.Timer16 = 0xBEEF;
        cpu.Step(); // ldt16 0x22
        Assert.Equal((0xEF, 0xBE), (cpu.Ram[0x22], cpu.Ram[0x23]));
    }

    [Fact]
    public void Pcadd_jumps_from_its_own_address_by_A()
    {
        var (cpu, _) = Core(0x0000, 0x0067);
        cpu.Pc = 1;
        cpu.A = 3;

        Assert.Equal(2, cpu.Step());

        Assert.Equal(4, cpu.Pc);
    }

    [Fact]
    public void Swapc_exchanges_an_io_bit_with_C()
    {
        var (cpu, io) = Core(0x04C5); // swapc.io 0x05, #3
        io.Registers[5] = 0x08;
        cpu.Flags = 0;

        cpu.Step();

        Assert.Equal((0x00, C), (io.Registers[5], cpu.Flags));
    }

    [Fact]
    public void The_interrupt_pushes_the_pc_jumps_to_0x10_and_reti_turns_interrupts_back_on()
    {
        var (cpu, _) = Core(0x0078, 0x0000);
        cpu.Rom[0x10] = 0x007B;

        cpu.Step(); // engint
        Assert.True(cpu.InterruptsEnabled);
        Assert.Equal(2, cpu.Interrupt());
        Assert.Equal((0x10, false, 0x62), (cpu.Pc, cpu.InterruptsEnabled, cpu.Sp));
        cpu.Step();
        Assert.Equal((1, true, 0x60), (cpu.Pc, cpu.InterruptsEnabled, cpu.Sp));
    }

    [Fact]
    public void Flag_and_sp_are_io_registers_of_the_core()
    {
        // mov a, #0x0f; mov.io flag, a; mov a, #0x40; mov.io sp, a; mov.io a, sp
        var (cpu, _) = Core(0x2F0F, 0x0180, 0x2F40, 0x0182, 0x01C2);

        cpu.Step();
        cpu.Step();
        Assert.Equal(0x0F, cpu.Flags);
        cpu.Step();
        cpu.Step();
        cpu.Step();
        Assert.Equal((0x40, 0x40), (cpu.Sp, cpu.A));
    }

    [Theory]
    [InlineData(0x0076, Pdk14Sleep.StopSys)]
    [InlineData(0x0077, Pdk14Sleep.StopExe)]
    public void Stopsys_and_stopexe_sleep(int op, Pdk14Sleep sleep)
    {
        var (cpu, _) = Core((ushort)op);

        cpu.Step();

        Assert.Equal(sleep, cpu.Sleep);
    }

    [Fact]
    public void Reset_and_wdreset_reach_the_chip()
    {
        var (cpu, io) = Core(0x0070, 0x0075);

        cpu.Step();
        cpu.Step();

        Assert.True(io.WatchdogReset);
        Assert.True(cpu.ResetRequested);
    }

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x007C)] // mul: the PFS154 has no multiplier
    [InlineData(0x0100)]
    public void An_instruction_the_core_does_not_have_stops_the_run(int op)
    {
        var (cpu, _) = Core((ushort)op);

        var fault = Assert.Throws<ProgramFault>(() => cpu.Step());

        Assert.Contains("the PDK14 core does not have", fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stack_past_the_end_of_RAM_stops_the_run()
    {
        var (cpu, _) = Core(0x3800);
        cpu.Sp = 0x7F;

        var fault = Assert.Throws<ProgramFault>(() => cpu.Step());

        Assert.Contains("the stack has grown past the end of RAM", fault.Message, StringComparison.Ordinal);
    }

    /// <summary>Every form of every PDK14 instruction, assembled by sdaspdk14, runs as one instruction.</summary>
    [SdccFact]
    public void Every_instruction_form_sdaspdk14_assembles_decodes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "pdk14-forms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string[] lines =
            [
                "nop", "ldsptl", "ldspth", "addc a", "subc a", "izsn a", "dzsn a", "pcadd a", "not a", "neg a", "sr a", "sl a",
                "src a", "slc a", "swap a", "wdreset", "pushaf", "popaf", "reset", "stopsys", "stopexe", "engint", "disgint",
                "ret", "reti", "xor.io 0x05, a", "mov.io 0x05, a", "mov.io a, 0x05", "ret #0x42", "stt16 0x10", "ldt16 0x10",
                "idxm 0x10, a", "idxm a, 0x10", "swapc.io 0x05, #3", "comp a, 0x11", "comp 0x11, a", "nadd a, 0x11",
                "nadd 0x11, a", "t0sn.io 0x05, #3", "t1sn.io 0x05, #3", "set0.io 0x05, #3", "set1.io 0x05, #3",
                "t0sn 0x11, #3", "t1sn 0x11, #3", "set0 0x11, #3", "set1 0x11, #3", "t0sn 0x3f, #7", "goto 0x123",
                "call 0x123",
                .. new[] { "add", "sub", "addc", "subc", "and", "or", "xor", "mov" }.SelectMany(op => new[] { $"{op} 0x11, a", $"{op} a, 0x7f" }),
                .. new[] { "addc", "subc", "izsn", "dzsn", "inc", "dec", "clear", "xch", "not", "neg", "sr", "sl", "src", "slc" }.Select(op => $"{op} 0x11"),
                .. new[] { "add", "sub", "ceqsn", "cneqsn", "and", "or", "xor", "mov" }.Select(op => $"{op} a, #0x42"),
                "ceqsn a, 0x11", "cneqsn a, 0x11",
            ];
            var forms = Stm8CpuTests.Assemble("sdaspdk14", lines, folder);
            Assert.Equal(lines.Length, forms.Count);
            foreach (var (bytes, _, text) in forms)
            {
                var word = (ushort)(bytes[0] | (bytes[1] << 8));
                var (cpu, io) = Core(word);
                cpu.Ram[0x10] = 0x20; // idxm's pointer
                cpu.Sp = 0x40;
                cpu.Ram[0x3E] = 0x05; // what ret and popaf take
                try
                {
                    cpu.Step();
                }
                catch (ProgramFault e)
                {
                    throw new Xunit.Sdk.XunitException($"{text} (0x{word:x4}): {e.Message}");
                }

                if (!JumpOrSkip().IsMatch(text))
                {
                    Assert.True(cpu.Pc == 1, $"{text} (0x{word:x4}) went to {cpu.Pc}");
                }
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [GeneratedRegex(@"^(goto|call|ret|reti|pcadd|izsn|dzsn|ceqsn|cneqsn|t0sn|t1sn)\b")]
    private static partial Regex JumpOrSkip();
}
