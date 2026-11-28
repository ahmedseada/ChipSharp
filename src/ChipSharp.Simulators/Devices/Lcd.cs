using System.Globalization;
using System.Text;

namespace ChipSharp.Simulators.Devices;

/// <summary>
/// A character LCD on I2C: an HD44780 (Hitachi ADE-207-272) behind a PCF8574 backpack, as the common modules and Wokwi's
/// wokwi-lcd1602 with "pins": "i2c" are wired, as tools/rp2040-sim/devices/lcd1602.mjs models it. Each byte written to
/// the PCF8574 sets its eight outputs at once: P0 RS (0 for an instruction, 1 for a character), P1 RW (0 for a write; a
/// read cycle is ignored), P2 E (the HD44780 takes D4-D7 and RS as E falls), P3 the backlight (on when high), P4-P7
/// D4-D7.
/// <para>
/// The HD44780 starts in 8-bit mode (D0-D3 are not wired, and read as 0), so a function set of 4-bit data, one E pulse,
/// puts it in 4-bit mode; from then on each byte is two pulses, its upper four bits first. It models what a program sees:
/// function set, display on/off, clear, return home, entry mode, cursor and display shift, DDRAM addresses (row 0 at
/// 0x00, row 1 at 0x40; on four rows, rows 2 and 3 after rows 0 and 1), data writes, and CGRAM writes (kept apart: they
/// never change the text). It is busy while an instruction runs (37 us; clear and return home 1.52 ms; the first
/// function sets after power-up 4.1 ms and 100 us), and for 40 ms after power-up: a write that comes while it is busy is
/// lost, as on a real one, and printed.
/// </para>
/// <para>
/// What the display shows is printed once it settles, 5 ms after the last write to it (or 50 ms after the first while
/// writes go on), when it differs from what was printed last: one line per row between bars, with the backlight and the
/// display's on/off when they change ("LCD 0x27: |count 12        |", "LCD 0x27 backlight on"). The characters are the
/// ROM A00's; the eight custom characters show as '▯' and the codes with no glyph as a space.
/// </para>
/// </summary>
public sealed class Lcd : II2cDevice
{
    private const int Rs = 0x01, Rw = 0x02, E = 0x04, Light = 0x08;
    private const double Ms = 1e6, Us = 1e3; // nanoseconds
    private const double PowerUp = 40 * Ms, Settle = 5 * Ms, SettleAtMost = 50 * Ms;

    // A00 from 0xE0 to 0xFF, one character each
    private const string Symbols = "αäβεμσρg√¹jˣ¢£ñöpqθ∞ΩüΣπxy千万円÷ █";

    private readonly SimulatedChip _chip;
    private readonly Action<string> _log;
    private readonly string _label;
    private readonly int _columns;
    private readonly int _rows;
    private readonly byte[] _ddram = new byte[0x80];
    private readonly byte[] _cgram = new byte[0x40];
    private readonly DeviceAlarm _settled;

    // the PCF8574's outputs: all high after power-up (quasi-bidirectional pins, pulled up)
    private int _port = 0xFF;
    private int _address;
    private bool _toCgram;
    private int _shift;
    private bool _eightBits = true;
    private bool _twoLines;
    private bool _displayOn;
    private bool _increment = true;
    private bool _shiftDisplay;
    private int? _upper; // in 4-bit mode: the upper four bits of a byte, until the lower four come
    private int _functionSets;
    private double _busyUntil = PowerUp;
    private string _busyWith = "power-up";
    private double _busyFor = PowerUp;
    private bool? _printedLight;
    private bool? _printedOn;
    private string? _printedText;
    private double _lastWrite;
    private double? _firstWrite;

    /// <summary>A display of columns x rows characters whose backpack is at address.</summary>
    public Lcd(int address, int columns, int rows, SimulatedChip chip, Action<string> log)
    {
        _label = string.Create(CultureInfo.InvariantCulture, $"LCD 0x{address:x2}");
        _columns = columns;
        _rows = rows;
        _chip = chip;
        _log = log;
        Array.Fill(_ddram, (byte)0x20);
        _settled = chip.CreateAlarm(Settled);
    }

    public string Name => "LCD";

    /// <summary>A start addressed to the backpack: it always answers.</summary>
    public bool Start(bool read) => true;

    /// <summary>
    /// A byte for the PCF8574: its outputs change, and E falling hands RS and D4-D7 (as they were while E was high) to
    /// the HD44780.
    /// </summary>
    public bool Write(byte value)
    {
        var before = _port;
        _port = value;
        if ((before & E) != 0 && (_port & E) == 0 && (before & Rw) == 0)
        {
            Pulse(before >> 4, (before & Rs) != 0);
        }

        Changed();
        return true;
    }

    /// <summary>A read gives the outputs' levels.</summary>
    public byte Read() => (byte)_port;

    public void Stop()
    {
    }

    /// <summary>The run ends: what has not settled yet is printed now.</summary>
    public void Finish()
    {
        if (_firstWrite is not null)
        {
            _settled.Cancel();
            _firstWrite = null;
            Print();
        }
    }

    private static char Glyph(int code) => code switch
    {
        0x5C => '¥',
        >= 0x20 and <= 0x7D => (char)code,
        0x7E => '→',
        0x7F => '←',
        < 0x10 => '▯',
        >= 0xA1 and <= 0xDF => (char)(0xFF61 + code - 0xA1),
        >= 0xE0 => Symbols[code - 0xE0],
        _ => ' ',
    };

    // Four bits from D4-D7: half a byte in 4-bit mode, or in 8-bit mode the upper half of one (D0-D3 read as 0).
    private void Pulse(int bits, bool select)
    {
        var now = _chip.Nanoseconds;
        if (now < _busyUntil)
        {
            var since = (now - (_busyUntil - _busyFor)) / Ms;
            _log(string.Create(CultureInfo.InvariantCulture,
                $"{_label} busy: lost a write {since:F3} ms into {_busyWith} ({_busyFor / Ms:F3} ms)"));
            return;
        }

        if (_eightBits)
        {
            Execute(bits << 4, select);
        }
        else if (_upper is not { } upper)
        {
            _upper = bits;
        }
        else
        {
            _upper = null;
            Execute((upper << 4) | bits, select);
        }
    }

    private void Execute(int value, bool select)
    {
        if (select)
        {
            Data(value);
        }
        else if ((value & 0x80) != 0)
        {
            _address = value & 0x7F;
            _toCgram = false;
            Busy("DDRAM address", 37 * Us);
        }
        else if ((value & 0x40) != 0)
        {
            _address = value & 0x3F;
            _toCgram = true;
            Busy("CGRAM address", 37 * Us);
        }
        else if ((value & 0x20) != 0)
        {
            // function set: DL (bit 4), N (bit 3); the font (bit 2) does not change the text
            _eightBits = (value & 0x10) != 0;
            _twoLines = (value & 0x08) != 0;
            _upper = null;
            if (_eightBits && _functionSets < 2)
            {
                _functionSets++;
                Busy("function set", _functionSets == 1 ? 4.1 * Ms : 100 * Us);
            }
            else
            {
                _functionSets = 2;
                Busy("function set", 37 * Us);
            }
        }
        else if ((value & 0x10) != 0)
        {
            // cursor or display shift: S/C (bit 3), R/L (bit 2)
            var right = (value & 0x04) != 0;
            if ((value & 0x08) != 0)
            {
                _shift += right ? -1 : 1;
            }
            else
            {
                Move(right);
            }

            Busy("shift", 37 * Us);
        }
        else if ((value & 0x08) != 0)
        {
            _displayOn = (value & 0x04) != 0; // the cursor (bit 1) and its blinking (bit 0) are not shown
            Busy("display control", 37 * Us);
        }
        else if ((value & 0x04) != 0)
        {
            _increment = (value & 0x02) != 0;
            _shiftDisplay = (value & 0x01) != 0;
            Busy("entry mode", 37 * Us);
        }
        else if ((value & 0x02) != 0)
        {
            _address = 0;
            _toCgram = false;
            _shift = 0;
            Busy("return home", 1.52 * Ms);
        }
        else if ((value & 0x01) != 0)
        {
            Array.Fill(_ddram, (byte)0x20);
            _address = 0;
            _toCgram = false;
            _shift = 0;
            _increment = true;
            Busy("clear", 1.52 * Ms);
        }
    }

    // A character written at the address, which then moves (and the display with it, in entry mode with shift).
    private void Data(int value)
    {
        if (_toCgram)
        {
            _cgram[_address] = (byte)value;
            _address = (_address + (_increment ? 1 : -1)) & 0x3F;
        }
        else
        {
            _ddram[_address] = (byte)value;
            Move(_increment);
            if (_shiftDisplay)
            {
                _shift += _increment ? 1 : -1;
            }
        }

        Busy("data write", 37 * Us);
    }

    // The DDRAM address one step right or left: within 0x00-0x27 and 0x40-0x67 with two lines, 0x00-0x4F with one.
    private void Move(bool right)
    {
        if (_toCgram)
        {
            _address = (_address + (right ? 1 : -1)) & 0x3F;
            return;
        }

        if (!_twoLines)
        {
            _address = (_address + (right ? 1 : 79)) % 80;
            return;
        }

        var line = _address & 0x40;
        var column = ((_address & 0x3F) + (right ? 1 : 39)) % 40;
        var wrapped = right ? column == 0 : column == 39;
        _address = (wrapped ? line ^ 0x40 : line) | column;
    }

    private void Busy(string what, double nanoseconds)
    {
        _busyWith = what;
        _busyFor = nanoseconds;
        _busyUntil = _chip.Nanoseconds + nanoseconds;
    }

    // The text of each row as the display shows it.
    private List<string> Text()
    {
        var rows = new List<string>();
        for (var row = 0; row < _rows; row++)
        {
            var text = new StringBuilder();
            for (var column = 0; column < _columns; column++)
            {
                var code = 0x20;
                if (_twoLines)
                {
                    var position = (((row >= 2 ? _columns : 0) + column + _shift) % 40 + 40) % 40;
                    code = _ddram[((row & 1) != 0 ? 0x40 : 0) + position];
                }
                else if (row == 0)
                {
                    code = _ddram[((column + _shift) % 80 + 80) % 80];
                }

                text.Append(Glyph(code));
            }

            rows.Add(text.ToString());
        }

        return rows;
    }

    // A write to the backpack: the display is printed once it settles.
    private void Changed()
    {
        var now = _chip.Nanoseconds;
        _lastWrite = now;
        if (_firstWrite is null)
        {
            _firstWrite = now;
            _settled.Schedule(Settle);
        }
    }

    private void Settled()
    {
        var now = _chip.Nanoseconds;
        if (now - _lastWrite < Settle && now - _firstWrite < SettleAtMost)
        {
            _settled.Schedule(_lastWrite + Settle - now);
            return;
        }

        _firstWrite = null;
        Print();
    }

    // Prints what differs from what was printed last: the backlight, the display on or off, and the rows.
    private void Print()
    {
        var light = (_port & Light) != 0;
        if (light != _printedLight)
        {
            _log($"{_label} backlight {(light ? "on" : "off")}");
            _printedLight = light;
        }

        if (!_displayOn)
        {
            if (_printedOn != false)
            {
                _log($"{_label} display off");
                _printedOn = false;
            }

            return;
        }

        var text = string.Join('\n', Text());
        if (_printedOn != true || text != _printedText)
        {
            foreach (var row in text.Split('\n'))
            {
                _log($"{_label}: |{row}|");
            }

            _printedOn = true;
            _printedText = text;
        }
    }
}
