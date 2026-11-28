using System.Globalization;
using System.Text.RegularExpressions;

namespace ChipSharp.Simulators.Devices;

/// <summary>
/// The Maxim DS1307 real-time clock, at I2C address 0x68, as its datasheet (DS1307 rev. 4) describes it and
/// tools/rp2040-sim/devices/ds1307.mjs models it: 64 registers, the time in BCD in the first seven (seconds with CH, clock
/// halt, in bit 7; minutes; hours, in 24-hour form, or 12-hour with AM/PM when bit 6 is set; the day of the week, 1 to 7;
/// the day of the month; the month; the year, 00 to 99, for 2000 to 2099), the control register (0x07) and 56 bytes of
/// RAM (0x08 to 0x3F). A write's first byte sets the register pointer, the bytes after it are written from there on; a
/// read reads from the pointer on; the pointer goes from 0x3F back to 0x00.
/// <para>
/// It keeps time with the simulation's clock: a start of a transfer copies the time into the time registers (so a read
/// never sees half of one second and half of the next), and a transfer that wrote any of them sets the clock from them
/// when it stops. CH set halts it. The time it starts from: the PC's local time when the simulation starts (ds1307), a
/// date and time (ds1307@2026-11-27T18:00:00), or a new DS1307's, halted at 1 January 2000 0:00 (ds1307@halted). A write
/// of the time prints a line, e.g. "DS1307 0x68 set to 2026-11-27 18:00:00 (Friday, day 6), running".
/// </para>
/// </summary>
public sealed partial class Ds1307 : RegisterDevice
{
    private const int Seconds = 0x00, Hours = 0x02, DayOfWeek = 0x03, Year = 0x06;
    private const int Halt = 0x80, TwelveHours = 0x40, Afternoon = 0x20;

    private readonly int _address;
    private readonly SimulatedChip _chip;
    private readonly Action<string> _log;
    private bool _wroteTime;
    private DateTime _base;
    private double _baseNanoseconds;
    private int _baseDayOfWeek;
    private bool _halted;

    public Ds1307(int address, SimulatedChip chip, Action<string> log, string? start)
        : base("DS1307")
    {
        _address = address;
        _chip = chip;
        _log = log;
        var (time, halted) = ParseStart(start) ?? throw new ArgumentException($"'{start}' is no DS1307 start.", nameof(start));
        _base = new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        _baseNanoseconds = chip.Nanoseconds;
        _baseDayOfWeek = (int)_base.DayOfWeek + 1;
        _halted = halted;
        CopyTime(_base, _baseDayOfWeek, false);
        if (halted)
        {
            Registers[Seconds] |= Halt;
        }
    }

    /// <summary>
    /// What the option after '@' says to start from: the time (wall time, kept as UTC) and whether it is halted; null when
    /// it is no date from 2000 to 2099.
    /// </summary>
    public static (DateTime Time, bool Halted)? ParseStart(string? text)
    {
        if (text is null)
        {
            return (DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Utc), false);
        }

        if (text == "halted")
        {
            return (new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), true);
        }

        return StartPattern().IsMatch(text) && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            && time.Year is >= 2000 and < 2100
            ? (time, false)
            : null;
    }

    private static byte ToBcd(int value) => (byte)(((value / 10 % 10) << 4) | (value % 10));

    private static int FromBcd(int value) => (value >> 4) * 10 + (value & 0x0F);

    /// <summary>The time registers from a time, in the hours' form the program chose (24 hours unless bit 6 is set).</summary>
    private void CopyTime(DateTime time, int dayOfWeek, bool twelveHours)
    {
        Registers[Seconds] = ToBcd(time.Second);
        Registers[0x01] = ToBcd(time.Minute);
        var hour = time.Hour;
        Registers[Hours] = twelveHours
            ? (byte)(TwelveHours | (hour >= 12 ? Afternoon : 0) | ToBcd(hour % 12 == 0 ? 12 : hour % 12))
            : ToBcd(hour);
        Registers[DayOfWeek] = (byte)dayOfWeek;
        Registers[0x04] = ToBcd(time.Day);
        Registers[0x05] = ToBcd(time.Month);
        Registers[Year] = ToBcd(time.Year % 100);
    }

    /// <summary>The time now, and the day of the week: the base and the whole seconds the simulation has run since.</summary>
    private (DateTime Time, int DayOfWeek) Now()
    {
        var time = _base.AddSeconds(Math.Floor((_chip.Nanoseconds - _baseNanoseconds) / 1e9));
        var days = (int)(time.Date - _base.Date).TotalDays;
        return (time, ((_baseDayOfWeek - 1 + days) % 7 + 7) % 7 + 1);
    }

    public override bool Start(bool read)
    {
        // the time registers take the time now at a start, while the clock runs
        if (!_halted)
        {
            var (time, dayOfWeek) = Now();
            CopyTime(time, dayOfWeek, (Registers[Hours] & TwelveHours) != 0);
        }

        return base.Start(read);
    }

    public override bool Write(byte value)
    {
        if (Chosen)
        {
            Registers[Pointer] = value;
            _wroteTime |= Pointer <= Year;
            Pointer = (Pointer + 1) & 0x3F;
            return true;
        }

        Pointer = value & 0x3F;
        Chosen = true;
        return true;
    }

    public override byte Read()
    {
        var value = Registers[Pointer];
        Pointer = (Pointer + 1) & 0x3F;
        return value;
    }

    public override void Stop()
    {
        if (!_wroteTime)
        {
            return;
        }

        // the clock counts from what was written, from now on
        _wroteTime = false;
        var r = Registers;
        var hour = (r[Hours] & TwelveHours) != 0
            ? FromBcd(r[Hours] & 0x1F) % 12 + ((r[Hours] & Afternoon) != 0 ? 12 : 0)
            : FromBcd(r[Hours] & 0x3F);
        var time = new DateTime(2000 + FromBcd(r[Year]), 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(Math.Clamp(FromBcd(r[0x05] & 0x1F), 1, 12) - 1).AddDays(FromBcd(r[0x04] & 0x3F) - 1)
            .AddHours(hour).AddMinutes(FromBcd(r[0x01] & 0x7F)).AddSeconds(FromBcd(r[Seconds] & 0x7F));
        _base = time;
        _baseNanoseconds = _chip.Nanoseconds;
        _baseDayOfWeek = r[DayOfWeek] & 0x07;
        _halted = (r[Seconds] & Halt) != 0;
        var day = ((System.DayOfWeek)((_baseDayOfWeek + 6) % 7)).ToString();
        _log(string.Create(CultureInfo.InvariantCulture,
            $"DS1307 0x{_address:x} set to {time:yyyy-MM-dd HH:mm:ss} ({day}, day {_baseDayOfWeek}), " +
            $"{(_halted ? "halted" : "running")}"));
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$")]
    private static partial Regex StartPattern();
}
