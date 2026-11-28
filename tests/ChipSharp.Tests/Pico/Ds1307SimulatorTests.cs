using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// ChipSharp.Components' Ds1307 on the simulated Pico, with run.mjs's simulated DS1307 (tools/rp2040-sim/devices/
/// ds1307.mjs): the time it keeps, read in one transfer of seven registers and counting with the simulation, set and
/// started by Write, its 12-hour form, a halted clock, and no clock at all; on a Pico without a heap. And the model
/// itself: its register pointer, its RAM, its start-up date from the PC or the option.
/// </summary>
public sealed class Ds1307SimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    private TargetDescription NoHeap => new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
        ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");

    [SimulatorFact]
    public async Task Read_gives_the_time_the_DS1307_keeps_and_Write_sets_and_starts_it()
    {
        var program = Build("""
            using System;
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            I2c.Open(1, sda: 26, scl: 27, frequency: 100_000);
            Console.WriteLine($"{Ds1307.Open(1)} {Ds1307.Running}");
            Console.WriteLine($"{Ds1307.Read():yyyy-MM-dd HH:mm:ss dddd}");
            Timing.DelayMilliseconds(2_100);
            Console.WriteLine($"{Ds1307.Read():yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine(Ds1307.Write(new DateTime(2030, 12, 31, 23, 59, 58, 750)));
            Timing.DelayMilliseconds(2_500);
            var now = Ds1307.Read();
            Console.WriteLine($"{now:yyyy-MM-dd HH:mm:ss dddd} {now.Kind}");
            """, "Ds1307Time");

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 6_000, "--i2c", "1:0x68=ds1307@2026-11-27T18:00:00");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(
            "True True\r\n2026-11-27 18:00:00 Friday\r\n2026-11-27 18:00:02\r\nTrue\r\n2031-01-01 00:00:00 Wednesday Unspecified\r\n",
            Encoding.UTF8.GetString(run.Uart0));

        // a read is the register pointer, then seven registers after a repeated start; a write the pointer and seven
        Assert.Contains("I2C1 0x68 write 00, read 00 00 18 06 27 11 26", run.Output);
        Assert.Contains("I2C1 0x68 write 00 58 59 23 03 31 12 30", run.Output);
        Assert.Contains("DS1307 0x68 set to 2030-12-31 23:59:58 (Tuesday, day 3), running", run.Output);
    }

    [SimulatorFact]
    public async Task A_halted_DS1307_is_not_running_a_missing_one_gives_the_default_and_12_hours_read_as_24()
    {
        var program = Build("""
            using System;
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            I2c.Open(1, sda: 26, scl: 27, frequency: 100_000);
            Console.WriteLine($"{Ds1307.Read().Ticks} {Ds1307.Write(DateTime.MinValue)} {Ds1307.Running}");
            Console.WriteLine($"{Ds1307.Open(1)} {Ds1307.Running} {Ds1307.Read():yyyy-MM-dd HH:mm:ss}");
            Timing.DelayMilliseconds(1_500);
            Console.WriteLine($"{Ds1307.Read():HH:mm:ss}");

            // the hours in 12-hour form, as another device may have set them: 11 PM
            Span<byte> hours = stackalloc byte[2];
            hours[0] = 0x02;
            hours[1] = 0x40 | 0x20 | 0x11;
            I2c.Write(1, 0x68, hours);
            Console.WriteLine($"{Ds1307.Read():HH:mm}");
            Console.WriteLine($"{Ds1307.Open(1, 0x50)} {Ds1307.Read().Ticks}");
            """, "Ds1307Halted");

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 3_000, "--i2c", "1:0x68=ds1307@halted");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("0 False False\r\nTrue False 2000-01-01 00:00:00\r\n00:00:00\r\n23:00\r\nFalse 0\r\n",
            Encoding.UTF8.GetString(run.Uart0));
    }

    [SimulatorFact]
    public async Task The_model_counts_from_the_PCs_local_time_without_a_date_and_keeps_its_RAM()
    {
        var program = Build("""
            using System;
            using ChipSharp.Components;
            using ChipSharp.Hardware;

            I2c.Open(1, sda: 26, scl: 27, frequency: 100_000);
            Ds1307.Open(1);
            Console.WriteLine($"{Ds1307.Read():yyyy-MM-dd HH:mm}");

            // RAM from 0x08 on, then the pointer goes round from 0x3F to 0x00
            Span<byte> ram = stackalloc byte[3];
            ram[0] = 0x3F;
            ram[1] = 0xAB;
            ram[2] = 0x12;
            Span<byte> at = stackalloc byte[1];
            at[0] = 0x3F;
            Span<byte> back = stackalloc byte[2];
            Console.WriteLine($"{I2c.Write(1, 0x68, ram)} {I2c.WriteRead(1, 0x68, at, back)} {back[0]:X2} {back[1]:X2}");
            """, "Ds1307Model");
        var before = DateTime.Now;

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 500, "--i2c", "1:0x68=ds1307");

        Assert.True(run.ExitCode == 0, run.ToString());
        var lines = Encoding.UTF8.GetString(run.Uart0).Split("\r\n");
        var read = DateTime.ParseExact(lines[0], "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(read, before.AddMinutes(-1), DateTime.Now.AddMinutes(1));
        Assert.Equal("True True AB 12", lines[1]);
    }

    private PicoProgram Build(string source, string name) =>
        PicoBuild.Build(source, name, _scratch, NoHeap, [PicoBuild.ComponentsAssembly]);
}
