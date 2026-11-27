using ChipSharp.Hardware;

namespace ChipSharp.Components;

/// <summary>
/// A DS1307 real-time clock on an I2C bus, at address 0x68: a clock chip with a battery of its own (a CR2032 on the
/// common modules), so the date and time it keeps go on while the board has no power. <see cref="I2c.Open"/> the bus,
/// then <see cref="Open"/> the clock; read it once at start-up and give the time to the chip's clock, which keeps it
/// from then on:
/// <code>
/// I2c.Open(1, sda: 26, scl: 27, frequency: 100_000);
/// if (Ds1307.Open(1))
/// {
///     Clock.Start(Ds1307.Read());             // DateTime.Now counts from the DS1307's time
/// }
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// What it keeps, as its datasheet (Maxim, DS1307 rev. 4) lays it out: seven registers from 0x00, the seconds (with
/// CH, clock halt, in bit 7), minutes, hours (24-hour, or 12-hour with AM/PM when bit 6 is set), the day of the week
/// (1 to 7), the day of the month, the month and the year (00 to 99), each in BCD; a control register (0x07, its
/// square-wave output) and 56 bytes of RAM (0x08 to 0x3F) follow. A read starts at register 0 and takes all seven in
/// one transfer, which the DS1307 copies at the transfer's start, so the time read is never half of one second and half
/// of the next. It counts whole seconds only: a time read has no fraction of one.
/// </para>
/// <para>
/// <see cref="Write"/> sets the clock, in 24-hour form, and starts it (CH 0); the years it keeps are 2000 to 2099. A new
/// DS1307, or one whose battery ran out, is halted at 1 January 2000: <see cref="Running"/> says whether it runs.
/// Nothing allocates and nothing stops the program: before <see cref="Open"/> (or after one that said false), or when
/// the clock does not answer, <see cref="Read"/> gives <c>default(DateTime)</c> (1 January 0001) and
/// <see cref="Write"/> false. One clock at a time: the last <see cref="Open"/> chooses it. Plain C# over
/// <see cref="I2c.WriteRead"/> and <see cref="I2c.Write"/>; a transfer takes about 1 ms at 100 kHz.
/// </para>
/// </remarks>
public static class Ds1307
{
    /// <summary>The DS1307's address, which it cannot change.</summary>
    public const int Address = 0x68;

    private const int Halt = 0x80;             // CH: the oscillator stopped
    private const int TwelveHours = 0x40;      // in the hours register: 12-hour form
    private const int Afternoon = 0x20;        // in 12-hour form: PM

    private static int _bus = -1;              // -1 until Open
    private static int _address;

    /// <summary>
    /// Chooses the DS1307 at <paramref name="address"/> (0x68) on <paramref name="bus"/>, which <see cref="I2c.Open"/>
    /// has set up. True when it answers; false when nothing answers, and then the other methods do nothing.
    /// </summary>
    public static bool Open(int bus, int address = Address)
    {
        _bus = -1;
        Span<byte> register = stackalloc byte[1];
        if (!I2c.Write(bus, address, register))
        {
            return false;
        }

        _bus = bus;
        _address = address;
        return true;
    }

    /// <summary>
    /// The date and time the clock keeps, Kind Unspecified, to the second; <c>default(DateTime)</c> when it does not
    /// answer, before <see cref="Open"/>, or when its registers hold no date (a clock never set may hold anything).
    /// </summary>
    public static DateTime Read()
    {
        Span<byte> time = stackalloc byte[7];
        if (!ReadRegisters(time))
        {
            return default;
        }

        int second = Bcd(time[0] & 0x7F);
        int minute = Bcd(time[1] & 0x7F);
        int hour;
        if ((time[2] & TwelveHours) != 0)
        {
            // 12-hour form: 12 AM is 0, 12 PM is 12
            hour = Bcd(time[2] & 0x1F) % 12 + ((time[2] & Afternoon) != 0 ? 12 : 0);
        }
        else
        {
            hour = Bcd(time[2] & 0x3F);
        }

        int day = Bcd(time[4] & 0x3F);
        int month = Bcd(time[5] & 0x1F);
        int year = 2000 + Bcd(time[6]);
        if (second > 59 || minute > 59 || hour > 23 || month < 1 || month > 12 || day < 1
            || day > DateTime.DaysInMonth(year, month))
        {
            return default;
        }

        return new DateTime(year, month, day, hour, minute, second);
    }

    /// <summary>
    /// Sets the clock to <paramref name="time"/> (its fraction of a second dropped; a year outside 2000 to 2099 kept as
    /// its last two digits) and starts it. True when the clock took it.
    /// </summary>
    public static bool Write(DateTime time)
    {
        if (_bus < 0)
        {
            return false;
        }

        Span<byte> registers = stackalloc byte[8];
        registers[0] = 0x00;                   // from register 0 on
        registers[1] = ToBcd(time.Second);     // CH 0: running
        registers[2] = ToBcd(time.Minute);
        registers[3] = ToBcd(time.Hour);       // 24-hour form
        registers[4] = (byte)((int)time.DayOfWeek + 1);
        registers[5] = ToBcd(time.Day);
        registers[6] = ToBcd(time.Month);
        registers[7] = ToBcd(time.Year % 100);
        return I2c.Write(_bus, _address, registers);
    }

    /// <summary>True when the clock runs; false when it is halted (a new DS1307's state) or does not answer.</summary>
    public static bool Running
    {
        get
        {
            Span<byte> seconds = stackalloc byte[1];
            return ReadRegisters(seconds) && (seconds[0] & Halt) == 0;
        }
    }

    /// <summary>The registers from 0 on, as many as <paramref name="registers"/> holds; false when the clock does not answer.</summary>
    private static bool ReadRegisters(Span<byte> registers)
    {
        if (_bus < 0)
        {
            return false;
        }

        Span<byte> first = stackalloc byte[1];
        return I2c.WriteRead(_bus, _address, first, registers);
    }

    private static int Bcd(int value) => (value >> 4) * 10 + (value & 0x0F);

    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));
}
