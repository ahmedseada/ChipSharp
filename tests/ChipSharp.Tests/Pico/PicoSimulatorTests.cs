namespace ChipSharp.Tests.Pico;

/// <summary>
/// C# built for the Pico the whole way (C# to .uf2) and run in the rp2040js simulator: the blink example blinks,
/// and the PC samples print over UART0 exactly what .NET prints.
/// </summary>
public sealed class PicoSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> SampleNames =>
        [.. Directory.GetFiles(Repository.Samples, "*.cs").Select(path => Path.GetFileNameWithoutExtension(path)).Order()];

    [SimulatorFact]
    public async Task The_blink_example_toggles_GPIO_25_every_500_ms_of_simulated_time()
    {
        var program = PicoBuild.Build(PicoBuild.BlinkSource, "PicoBlink", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 2600);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.EndsWith("stopped: 2600 ms of simulated time have passed", run.Stopped);
        var led = run.Pins.Where(p => p.Pin == 25 && p.State is "low" or "high").ToList();
        // Pin.Output drives the pin low; the first Toggle makes it high at once, then one toggle per 500 ms
        Assert.True(led.Count > 0, run.ToString());
        Assert.Equal("low", led[0].State);
        Assert.InRange(led[0].Milliseconds, 0, 5); // boot2, clocks and the program start take well under 5 ms
        var toggles = led.Skip(1).ToList();
        Assert.Equal(["high", "low", "high", "low", "high", "low"], toggles.Select(t => t.State));
        Assert.Equal(led[0].Milliseconds, toggles[0].Milliseconds, precision: 1);
        for (var i = 1; i < toggles.Count; i++)
        {
            Assert.InRange(toggles[i].Milliseconds - toggles[i - 1].Milliseconds, 499.9, 500.1);
        }

        // nothing else is driven: no other pin becomes an output
        Assert.DoesNotContain(run.Pins, p => p.Pin != 25 && p.State is "low" or "high");
    }

    [SimulatorFact]
    public async Task Every_hardware_call_works_in_the_simulator()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                const uint SioGpioOut = 0xd0000010;

                static void Main()
                {
                    Pin.Output(2);
                    Pin.Write(2, true);
                    Timing.DelayMicroseconds(250);
                    Pin.Write(2, false);
                    Timing.DelayMilliseconds(3);
                    Pin.Toggle(2);
                    Console.WriteLine(Registers.Read(SioGpioOut) & (1u << 2));
                    Registers.Write(SioGpioOut, 0);
                    Console.WriteLine(Registers.Read(SioGpioOut));
                    Pin.Input(2);
                    Pin.Input(3);
                    Pin.Input(4);
                    Console.WriteLine(Pin.Read(3));
                    Console.WriteLine(Pin.Read(4));
                    Timing.DelayMilliseconds(0);
                    Timing.DelayMilliseconds(-5);
                    Pin.Toggle(99);
                    Console.WriteLine(Pin.Read(99));
                }
            }
            """, "Hardware", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000, "--input", "3=high", "--input", "4=low");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("the program has finished", run.Stopped);
        Assert.Equal("4\r\n0\r\nTrue\r\nFalse\r\nFalse\r\n", System.Text.Encoding.ASCII.GetString(run.Uart0));
        var pin = run.Pins.Where(p => p.Pin == 2).ToList();
        var levels = pin.Where(p => p.State is "low" or "high").ToList();
        // Output, Write(true), Write(false), Toggle, Registers.Write of GPIO_OUT
        Assert.Equal(["low", "high", "low", "high", "low"], levels.Select(p => p.State));
        // high for the 250 µs delay, low for the 3 ms delay (rp2040js schedules an alarm in whole microseconds from
        // the current fraction of one, so it may wake the core up to a microsecond early; the chip itself does not)
        Assert.InRange(levels[2].Milliseconds - levels[1].Milliseconds, 0.245, 0.3);
        Assert.InRange(levels[3].Milliseconds - levels[2].Milliseconds, 2.995, 3.05);
        Assert.StartsWith("input", pin[^1].State); // Input: the pin is no longer driven
    }

    /// <summary>
    /// Every sample, built for the Pico, prints over UART0 exactly what .NET prints (its .expected.txt): this
    /// runs the runtime's division, remainder and 64-bit helpers on the Cortex-M0+ the way programs use them.
    /// </summary>
    [SimulatorTheory]
    [MemberData(nameof(SampleNames))]
    public async Task Every_sample_prints_over_UART0_what_dotnet_prints(string name)
    {
        var source = await File.ReadAllTextAsync(Path.Combine(Repository.Samples, name + ".cs"));
        var program = PicoBuild.Build(source, name, Path.Combine(_scratch, name));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 60_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        var expected = await File.ReadAllBytesAsync(Path.Combine(Repository.Samples, name + ".expected.txt"));
        Assert.Equal(System.Text.Encoding.ASCII.GetString(expected), System.Text.Encoding.ASCII.GetString(run.Uart0));
        Assert.Equal(expected, run.Uart0);
        // 125 MHz clk_peri with pico-sdk's divisor rounding: 115207 baud, within 0.01 % of 115200
        Assert.InRange(run.Uart0BaudRates[^1], 115_200 * 0.999, 115_200 * 1.001);
    }

    [SimulatorFact]
    public async Task A_division_by_zero_says_so_on_UART0_and_stops_the_program_at_the_runtimes_breakpoint()
    {
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(1);
                    int divisor = (int)Registers.Read(0xd0000004); // SIO GPIO_IN: 0, as no pin is high
                    Console.WriteLine(100 / divisor);
                    Console.WriteLine(2);
                }
            }
            """, "DivideByZero", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
        Assert.Equal("1\r\nUnhandled exception. System.DivideByZeroException: Attempted to divide by zero.\r\n",
            System.Text.Encoding.ASCII.GetString(run.Uart0));
    }

    [SimulatorFact]
    public async Task An_image_whose_boot2_checksum_is_wrong_is_refused_as_the_boot_ROM_would()
    {
        var program = PicoBuild.Build(PicoBuild.BlinkSource, "PicoBlink", _scratch);
        var image = await File.ReadAllBytesAsync(program.Uf2);
        image[32 + 10] ^= 0xff; // a byte of boot2, in the first block's payload
        var broken = Path.Combine(_scratch, "Broken.uf2");
        await File.WriteAllBytesAsync(broken, image);

        var run = await Rp2040Simulator.RunAsync(broken, 100);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("the boot ROM would not start this image", run.Errors);
    }
}
