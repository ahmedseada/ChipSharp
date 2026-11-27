using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Image.Uf2;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;
using ChipSharp.Tests.Library;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// DateTime, DateOnly and TimeOnly on the Pico, in the rp2040js simulator: every member and format prints over UART0
/// what .NET prints, also on a Pico without a heap (printed straight away and with TryFormat); where .NET throws the
/// program stops with its message; the clock that Clock.Start sets counts the timer's microseconds, and Stopwatch
/// measures a pulse on a pin; what a program does not use of them is not in its .elf.
/// </summary>
public sealed class DateTimeSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>The Pico with "heap": false: what needs no heap compiles for it, and nothing else does.</summary>
    private TargetDescription NoHeap => new TargetLoader(TargetFiles.WithShipped(Path.Combine(_scratch, "targets"),
        ("pico-no-heap", """{ "inherits": "pico", "capabilities": { "heap": false } }"""))).Load("pico-no-heap");

    [SimulatorFact]
    [RunsLong]
    public async Task Every_member_prints_over_UART0_what_dotnet_prints_on_a_Pico_without_a_heap()
    {
        await CompareAsync(DateTimePrograms.Members(random: 6), "Members", NoHeap, 60_000);
    }

    [SimulatorFact]
    [RunsLong]
    public async Task Every_format_printed_and_written_into_a_span_is_dotnets_on_a_Pico_without_a_heap()
    {
        await CompareAsync(DateTimePrograms.Text(DateTimeProgramTests.TextTicks(random: 1).Take(4), kept: false), "Text",
            NoHeap, 60_000);
    }

    [SimulatorFact]
    [RunsLong]
    public async Task Text_kept_in_strings_is_dotnets_on_a_Pico_with_a_heap()
    {
        await CompareAsync(DateTimePrograms.Text(DateTimeProgramTests.TextTicks(random: 2).Skip(10).Take(3), kept: true),
            "Kept", PicoBuild.Pico, 60_000);
    }

    [SimulatorFact]
    public async Task Where_dotnet_throws_the_message_goes_to_UART0_and_the_program_stops_at_a_breakpoint()
    {
        var index = 0;
        foreach (var (statement, dotnet) in DateTimePrograms.Failures.Where((_, i) => i is 0 or 9 or 14))
        {
            var program = PicoBuild.Build(DateTimePrograms.Failing(statement), "Failing", Path.Combine(_scratch, $"case{index++}"),
                NoHeap);

            var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

            Assert.True(run.ExitCode == 3, run.ToString());
            Assert.Contains("stopped: the program hit a breakpoint (bkpt #0)", run.Stopped);
            Assert.Equal("1\r\n" + TimeSpanPrograms.Unhandled(dotnet), Encoding.ASCII.GetString(run.Uart0));
        }
    }

    [SimulatorFact]
    public async Task Clock_Start_sets_the_clock_and_DateTime_Now_counts_the_timers_microseconds_from_it()
    {
        // 25 hours of sleep (the simulator skips the time the core sleeps): the date turns, the timer's high word too
        var program = PicoBuild.Build("""
            using System;
            using ChipSharp.Hardware;

            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {DateTime.Now.Kind} {DateTime.UtcNow.Kind}");
            Clock.Start(new DateTime(2026, 11, 27, 18, 0, 0));
            Timing.DelayMilliseconds(1500);
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.ff} {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssK} {DateTime.Today:o}");
            Timing.Delay(TimeSpan.FromHours(25));
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(DateTime.UtcNow - DateTime.Now).Ticks < 10_000} {DateTime.Now:zzz}");
            Console.WriteLine(Environment.TickCount64 / 1000);
            """, "Clock", _scratch, NoHeap);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 25 * 3_600_000 + 3_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        var lines = Encoding.UTF8.GetString(run.Uart0).Split("\r\n");
        Assert.Equal("2000-01-01 00:00:00 Local Utc", lines[0]);
        Assert.Equal("2026-11-27 18:00:01.50 2026-11-27T18:00:01Z 2026-11-27T00:00:00.0000000+00:00", lines[1]);
        Assert.Equal("2026-11-28 19:00:01 True +00:00", lines[2]);
        Assert.Equal("90001", lines[3]);
    }

    [SimulatorFact]
    public async Task Stopwatch_GetTimestamp_measures_a_pulse_on_a_pin_with_no_heap()
    {
        // an HC-SR04's echo: the pin high for as long as the sound took, here 580 us (10 cm)
        var program = PicoBuild.Build("""
            using System;
            using System.Diagnostics;
            using ChipSharp.Hardware;

            Pin.Input(15);
            long started = Stopwatch.GetTimestamp();
            while (!Pin.Read(15))
            {
            }

            long rise = Stopwatch.GetTimestamp();
            while (Pin.Read(15))
            {
            }

            TimeSpan pulse = Stopwatch.GetElapsedTime(rise);
            Console.WriteLine($"{Stopwatch.Frequency} {Stopwatch.IsHighResolution}");
            Console.WriteLine(Stopwatch.GetElapsedTime(started, rise).TotalMilliseconds > 49);
            Console.WriteLine(pulse.Ticks / 10);
            Console.WriteLine($"{(int)(pulse.TotalMicroseconds / 58 + 0.5)} cm");
            """, "Pulse", _scratch, NoHeap);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 200, "--input", "15=high@50ms", "--input", "15=low@50.58ms");

        Assert.True(run.ExitCode == 0, run.ToString());
        var lines = Encoding.UTF8.GetString(run.Uart0).Split("\r\n");
        Assert.Equal("1000000 True", lines[0]);
        Assert.Equal("True", lines[1]);
        Assert.InRange(int.Parse(lines[2], System.Globalization.CultureInfo.InvariantCulture), 578, 582);
        Assert.Equal("10 cm", lines[3]);
    }

    [SimulatorFact]
    public async Task A_Stopwatch_object_needs_a_heap_and_measures_like_dotnets()
    {
        var program = PicoBuild.Build("""
            using System;
            using System.Diagnostics;
            using ChipSharp.Hardware;

            var watch = Stopwatch.StartNew();
            Timing.DelayMilliseconds(20);
            watch.Stop();
            Timing.DelayMilliseconds(20);
            Console.WriteLine($"{watch.ElapsedMilliseconds} {watch.IsRunning} {watch.Elapsed.Ticks / 10_000}");
            watch.Restart();
            Timing.DelayMicroseconds(300);
            Console.WriteLine(watch.ElapsedTicks / 100);
            watch.Reset();
            Console.WriteLine($"{watch.ElapsedTicks} {watch.IsRunning}");
            """, "Watch", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 500);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("20 False 20\r\n3\r\n0 False\r\n", Encoding.UTF8.GetString(run.Uart0));
    }

    [PicoFact]
    public void What_a_program_does_not_use_of_dates_and_the_clock_is_not_in_its_elf()
    {
        var plain = Symbols("System.Console.WriteLine(42);", "Plain");
        var parts = Symbols("System.Console.WriteLine(new System.DateTime(2026, 11, 27).AddDays(1.5).DayOfYear);", "Parts");
        var printed = Symbols("System.Console.WriteLine($\"{new System.DateTime(2026, 11, 27):HH:mm:ss}\");", "Printed");
        var clock = Symbols("System.Console.WriteLine(System.Diagnostics.Stopwatch.GetTimestamp());", "Timestamp");

        Assert.DoesNotContain(plain, symbol => symbol.Contains("Date", StringComparison.Ordinal));
        Assert.DoesNotContain(parts, symbol => symbol.Contains("DateTimeFormat", StringComparison.Ordinal)
            || symbol.Contains("DateTimeText", StringComparison.Ordinal) || symbol.Contains("Print", StringComparison.Ordinal));
        Assert.Contains(printed, symbol => symbol.Contains("DateTimeFormat_WriteCustom", StringComparison.Ordinal));
        Assert.DoesNotContain(printed, symbol => symbol.Contains("_Text", StringComparison.Ordinal)
            || symbol.Contains("DateOnly", StringComparison.Ordinal) || symbol.Contains("chipsharp_new", StringComparison.Ordinal));
        Assert.All([plain, parts, printed], symbols =>
            Assert.DoesNotContain(symbols, symbol => symbol.StartsWith("ChipSharp_Runtime_Clock", StringComparison.Ordinal)));
        Assert.Contains("ChipSharp_Runtime_ClockTimestamp", clock);
        Assert.DoesNotContain(clock, symbol => symbol.Contains("ClockUtcTicks", StringComparison.Ordinal)
            || symbol.Contains("Date", StringComparison.Ordinal));
    }

    private IReadOnlyCollection<string> Symbols(string source, string name) =>
        ElfFile.Read(PicoBuild.Build(source, name, Path.Combine(_scratch, name), NoHeap).Elf).Symbols.Keys.ToList();

    private async Task CompareAsync(string source, string name, TargetDescription target, int milliseconds)
    {
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name, Path.Combine(_scratch, "dotnet"));
        Assert.True(dotnet.Text.Split('\n').Length > 300, "The program prints too little to test much.");
        var program = PicoBuild.Build(source, name, Path.Combine(_scratch, "pico"), target);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, milliseconds);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text, Encoding.UTF8.GetString(run.Uart0));
        Assert.Equal(dotnet.Output, run.Uart0);
    }
}
