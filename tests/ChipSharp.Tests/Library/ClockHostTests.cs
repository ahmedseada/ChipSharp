using System.Diagnostics;
using System.Globalization;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Library;

/// <summary>
/// The clock of a PC program: DateTime.UtcNow is Windows' clock, DateTime.Now its local time with the time zone's
/// offset now, Stopwatch the performance counter and Environment.TickCount64 the milliseconds since Windows started,
/// as .NET's are; Clock.Start makes it a software clock as on a chip, with no time zone.
/// </summary>
public sealed class ClockHostTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [HostFact]
    public async Task Now_UtcNow_Stopwatch_and_TickCount_are_Windows_as_dotnets_and_Clock_Start_sets_a_clock_of_its_own()
    {
        var bytes = CSharpCompiler.Compile(["""
            using System;
            using System.Diagnostics;
            using ChipSharp.Hardware;

            Console.WriteLine(DateTime.UtcNow.Ticks);
            Console.WriteLine((long)Math.Round((DateTime.Now - DateTime.UtcNow).TotalMinutes));
            Console.WriteLine($"{DateTime.Now:zzz} {DateTime.Now.Kind} {DateTime.UtcNow:zzz} {DateTime.Today.Ticks % TimeSpan.TicksPerDay}");
            Console.WriteLine($"{Stopwatch.Frequency} {Stopwatch.IsHighResolution}");
            Console.WriteLine(Environment.TickCount64);
            Console.WriteLine(Stopwatch.GetTimestamp());

            Clock.Start(new DateTime(2026, 11, 27, 18, 0, 0));
            long start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 150)
            {
            }

            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.f} {DateTime.UtcNow:HH:mm:ss.f} {DateTime.Now:zzz}");
            """], [PicoBuild.HardwareAssembly], "Clock");
        var module = Lowered.Lower(WholeProgramPass.Analyze(IlAssembly.Load(bytes, "Clock.dll")));
        var (utc, ticks, timestamp) = (DateTime.UtcNow, Environment.TickCount64, Stopwatch.GetTimestamp());

        var run = await HostRun.BuildAndRunAsync(module, _scratch);

        Assert.Equal(0, run.ExitCode);
        var lines = run.Text.Split("\r\n");
        Assert.InRange(new DateTime(long.Parse(lines[0], CultureInfo.InvariantCulture)), utc, utc.AddMinutes(1));
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        Assert.Equal((long)offset.TotalMinutes, long.Parse(lines[1], CultureInfo.InvariantCulture));
        Assert.Equal($"{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm} Local +00:00 0", lines[2]);
        Assert.Equal($"{Stopwatch.Frequency} True", lines[3]);
        Assert.InRange(long.Parse(lines[4], CultureInfo.InvariantCulture), ticks, ticks + 60_000);
        Assert.InRange(long.Parse(lines[5], CultureInfo.InvariantCulture), timestamp, timestamp + 60 * Stopwatch.Frequency);
        Assert.Matches(@"^2026-11-27 18:00:00\.[1-9] 18:00:00\.[1-9] \+00:00$", lines[6]);
    }
}
