// A character LCD on I2C: an HD44780 (Hitachi ADE-207-272) behind a PCF8574 backpack, as the common modules and Wokwi's
// wokwi-lcd1602 with "pins": "i2c" are wired. Each byte written to the PCF8574 sets its eight outputs at once:
//
//     P0 RS   0 for an instruction, 1 for a character      P3 the backlight (on when high)
//     P1 RW   0 for a write (a read cycle is ignored)       P4-P7 D4-D7, the HD44780's upper data lines
//     P2 E    the HD44780 takes D4-D7 (and RS) as E falls
//
// The HD44780 starts in 8-bit mode (D0-D3 are not wired, and read as 0), so a function set of 4-bit data, one E pulse,
// puts it in 4-bit mode; from then on each byte is two pulses, its upper four bits first. It models what a program
// sees: function set (4/8-bit data, one or two lines), display on/off, clear, return home, entry mode (left or right,
// display shift), cursor and display shift, DDRAM addresses (row 0 at 0x00, row 1 at 0x40; on four rows, rows 2 and 3
// after rows 0 and 1, at 0x00 + columns and 0x40 + columns), data writes, and CGRAM writes (kept apart: they never
// change the text). It is busy while an instruction runs (37 us; clear and return home 1.52 ms; the first function
// sets after power-up 4.1 ms and 100 us, as the datasheet's initialisation by instruction waits), and for 40 ms after
// power-up: a write that comes while it is busy is lost, as on a real one, and printed.
//
// What the display shows is printed once it settles, 5 ms after the last write to it (or 50 ms after the first while
// writes go on), when it differs from what was printed last: one line per row between bars, with the backlight and the
// display's on/off when they change:
//
//     [   58.230 ms] LCD 0x27 backlight on
//     [   58.230 ms] LCD 0x27: |ChipSharp LCD   |
//     [   58.230 ms] LCD 0x27: |count 0         |
//     [  300.000 ms] LCD 0x27 display off
//     [    0.912 ms] LCD 0x27 busy: lost a write 0.912 ms into power-up (40.000 ms)
//
// The characters are the ROM A00's: ASCII from ' ' to '}', with '¥' for 0x5C, '→' for 0x7E and '←' for 0x7F, half-width
// katakana from 0xA1 to 0xDF, and the nearest single character for the symbols from 0xE0; the eight custom characters
// (CGRAM, 0x00-0x0F) show as '▯' and the codes with no glyph as a space. The cursor is not shown.

const RS = 0x01;
const RW = 0x02;
const E = 0x04;
const LIGHT = 0x08;

const MS = 1e6; // nanoseconds
const US = 1e3;
const POWER_UP = 40 * MS;
const SETTLE = 5 * MS;
const SETTLE_AT_MOST = 50 * MS;

// A00 from 0xE0 to 0xFF, one character each
const SYMBOLS = 'αäβεμσρg√¹jˣ¢£ñöpqθ∞ΩüΣπxy千万円÷ █';

function glyph(code) {
  if (code >= 0x20 && code <= 0x7d) {
    return code === 0x5c ? '¥' : String.fromCharCode(code);
  }

  if (code === 0x7e || code === 0x7f) {
    return code === 0x7e ? '→' : '←';
  }

  if (code < 0x10) {
    return '▯';
  }

  if (code >= 0xa1 && code <= 0xdf) {
    return String.fromCharCode(0xff61 + code - 0xa1);
  }

  return code >= 0xe0 ? SYMBOLS[code - 0xe0] : ' ';
}

function hex(value) {
  return value.toString(16).padStart(2, '0');
}

export class Lcd {
  // A display of columns x rows characters whose backpack is at address; clock is the simulation's, log prints a line.
  constructor(address, columns, rows, clock, log) {
    this.name = 'LCD';
    this.label = `LCD 0x${hex(address)}`;
    this.columns = columns;
    this.rows = rows;
    this.clock = clock;
    this.log = log;

    // the PCF8574's outputs: all high after power-up (quasi-bidirectional pins, pulled up)
    this.port = 0xff;

    // the HD44780 after its power-on reset: display cleared, 8-bit data, one line, display off, address moving right
    this.ddram = new Uint8Array(0x80).fill(0x20);
    this.cgram = new Uint8Array(0x40);
    this.address = 0;
    this.toCgram = false;
    this.shift = 0;
    this.eightBits = true;
    this.twoLines = false;
    this.displayOn = false;
    this.increment = true;
    this.shiftDisplay = false;
    this.upper = null; // in 4-bit mode: the upper four bits of a byte, until the lower four come
    this.functionSets = 0; // the function sets of 8-bit data so far, for the waits of the initialisation
    this.busyUntil = POWER_UP;
    this.busyWith = 'power-up';
    this.busyFor = POWER_UP;

    // what was printed last, and the print that waits for the display to settle
    this.printed = { light: null, on: null, text: null };
    this.lastWrite = 0;
    this.firstWrite = null;
    this.settled = clock.createAlarm(() => this.settle());
  }

  // A start addressed to the backpack: it always answers.
  start() {
    return true;
  }

  // A byte for the PCF8574: its outputs change, and E falling hands RS and D4-D7 (as they were while E was high) to
  // the HD44780.
  write(value) {
    const before = this.port;
    this.port = value & 0xff;
    if (before & E && !(this.port & E) && !(before & RW)) {
      this.pulse(before >> 4, (before & RS) !== 0);
    }

    this.changed();
    return true;
  }

  // A read gives the outputs' levels.
  read() {
    return this.port;
  }

  stop() {}

  // Four bits from D4-D7: half a byte in 4-bit mode, or in 8-bit mode the upper half of one (D0-D3 read as 0).
  pulse(bits, select) {
    const now = this.clock.nanos;
    if (now < this.busyUntil) {
      const since = (now - (this.busyUntil - this.busyFor)) / MS;
      this.log(`${this.label} busy: lost a write ${since.toFixed(3)} ms into ${this.busyWith} ` +
        `(${(this.busyFor / MS).toFixed(3)} ms)`);
      return;
    }

    if (this.eightBits) {
      this.execute(bits << 4, select);
    } else if (this.upper === null) {
      this.upper = bits;
    } else {
      const value = (this.upper << 4) | bits;
      this.upper = null;
      this.execute(value, select);
    }
  }

  execute(value, select) {
    if (select) {
      this.data(value);
      return;
    }

    if (value & 0x80) {
      this.address = value & 0x7f;
      this.toCgram = false;
      this.busy('DDRAM address', 37 * US);
    } else if (value & 0x40) {
      this.address = value & 0x3f;
      this.toCgram = true;
      this.busy('CGRAM address', 37 * US);
    } else if (value & 0x20) {
      // function set: DL (bit 4), N (bit 3); the font (bit 2) does not change the text
      this.eightBits = (value & 0x10) !== 0;
      this.twoLines = (value & 0x08) !== 0;
      this.upper = null;
      if (this.eightBits && this.functionSets < 2) {
        this.functionSets++;
        this.busy('function set', this.functionSets === 1 ? 4.1 * MS : 100 * US);
      } else {
        this.functionSets = 2;
        this.busy('function set', 37 * US);
      }
    } else if (value & 0x10) {
      // cursor or display shift: S/C (bit 3), R/L (bit 2)
      const right = (value & 0x04) !== 0;
      if (value & 0x08) {
        this.shift += right ? -1 : 1;
      } else {
        this.move(right);
      }

      this.busy('shift', 37 * US);
    } else if (value & 0x08) {
      this.displayOn = (value & 0x04) !== 0; // the cursor (bit 1) and its blinking (bit 0) are not shown
      this.busy('display control', 37 * US);
    } else if (value & 0x04) {
      this.increment = (value & 0x02) !== 0;
      this.shiftDisplay = (value & 0x01) !== 0;
      this.busy('entry mode', 37 * US);
    } else if (value & 0x02) {
      this.address = 0;
      this.toCgram = false;
      this.shift = 0;
      this.busy('return home', 1.52 * MS);
    } else if (value & 0x01) {
      this.ddram.fill(0x20);
      this.address = 0;
      this.toCgram = false;
      this.shift = 0;
      this.increment = true;
      this.busy('clear', 1.52 * MS);
    }
  }

  // A character written at the address, which then moves (and the display with it, in entry mode with shift).
  data(value) {
    if (this.toCgram) {
      this.cgram[this.address] = value;
      this.address = (this.address + (this.increment ? 1 : -1)) & 0x3f;
    } else {
      this.ddram[this.address] = value;
      this.move(this.increment);
      if (this.shiftDisplay) {
        this.shift += this.increment ? 1 : -1;
      }
    }

    this.busy('data write', 37 * US);
  }

  // The DDRAM address one step right or left: within 0x00-0x27 and 0x40-0x67 with two lines, 0x00-0x4F with one.
  move(right) {
    if (this.toCgram) {
      this.address = (this.address + (right ? 1 : -1)) & 0x3f;
      return;
    }

    if (!this.twoLines) {
      this.address = (this.address + (right ? 1 : 79)) % 80;
      return;
    }

    const line = this.address & 0x40;
    const column = ((this.address & 0x3f) + (right ? 1 : 39)) % 40;
    const wrapped = right ? column === 0 : column === 39;
    this.address = (wrapped ? line ^ 0x40 : line) | column;
  }

  busy(what, nanos) {
    this.busyWith = what;
    this.busyFor = nanos;
    this.busyUntil = this.clock.nanos + nanos;
  }

  // The text of each row as the display shows it.
  text() {
    const rows = [];
    for (let row = 0; row < this.rows; row++) {
      let text = '';
      for (let column = 0; column < this.columns; column++) {
        let code = 0x20;
        if (this.twoLines) {
          const position = (((row >= 2 ? this.columns : 0) + column + this.shift) % 40 + 40) % 40;
          code = this.ddram[(row & 1 ? 0x40 : 0) + position];
        } else if (row === 0) {
          code = this.ddram[((column + this.shift) % 80 + 80) % 80];
        }

        text += glyph(code);
      }

      rows.push(text);
    }

    return rows;
  }

  // A write to the backpack: the display is printed once it settles.
  changed() {
    const now = this.clock.nanos;
    this.lastWrite = now;
    if (this.firstWrite === null) {
      this.firstWrite = now;
      this.settled.schedule(SETTLE);
    }
  }

  settle() {
    const now = this.clock.nanos;
    if (now - this.lastWrite < SETTLE && now - this.firstWrite < SETTLE_AT_MOST) {
      this.settled.schedule(this.lastWrite + SETTLE - now);
      return;
    }

    this.firstWrite = null;
    this.print();
  }

  // Prints what differs from what was printed last: the backlight, the display on or off, and the rows.
  print() {
    const light = (this.port & LIGHT) !== 0;
    if (light !== this.printed.light) {
      this.log(`${this.label} backlight ${light ? 'on' : 'off'}`);
      this.printed.light = light;
    }

    if (!this.displayOn) {
      if (this.printed.on !== false) {
        this.log(`${this.label} display off`);
        this.printed.on = false;
      }

      return;
    }

    const text = this.text().join('\n');
    if (this.printed.on !== true || text !== this.printed.text) {
      for (const row of text.split('\n')) {
        this.log(`${this.label}: |${row}|`);
      }

      this.printed.on = true;
      this.printed.text = text;
    }
  }

  // The run ends: what has not settled yet is printed now.
  finish() {
    if (this.firstWrite !== null) {
      this.settled.cancel();
      this.firstWrite = null;
      this.print();
    }
  }
}
