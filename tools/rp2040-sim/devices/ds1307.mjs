// The Maxim DS1307 real-time clock, at I2C address 0x68, as its datasheet (DS1307 rev. 4) describes it: 64 registers,
// the time in BCD in the first seven (seconds with CH, clock halt, in bit 7; minutes; hours, in 24-hour form, or
// 12-hour with AM/PM when bit 6 is set; the day of the week, 1 to 7; the day of the month; the month; the year, 00 to
// 99, for 2000 to 2099), the control register (0x07) and 56 bytes of RAM (0x08 to 0x3F). A write's first byte sets the
// register pointer, the bytes after it are written from there on; a read reads from the pointer on; the pointer goes
// from 0x3F back to 0x00.
//
// It keeps time with the simulation's clock: a start of a transfer copies the time into the time registers (the
// DS1307's "secondary registers", so a read never sees half of one second and half of the next), and a transfer that
// wrote any of them sets the clock from them when it stops (counting from that moment, as the DS1307's countdown
// chain restarts when its seconds are written). CH set halts it: the registers then keep what they hold. The time
// it starts from:
//
//     ds1307                              the PC's local time when the simulation starts, running
//     ds1307@2026-11-27T18:00:00          that date and time, running
//     ds1307@halted                       a new DS1307: halted at 1 January 2000 0:00 (CH set)
//
// A write of the time prints a line, e.g. "DS1307 0x68 set to 2026-11-27 18:00:00 (Friday, day 6), running".

import { RegisterDevice } from './registers.mjs';

const SECONDS = 0x00;
const HOURS = 0x02;
const DAY_OF_WEEK = 0x03;
const YEAR = 0x06;
const HALT = 0x80;
const TWELVE_HOURS = 0x40;
const AFTERNOON = 0x20;
const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

function toBcd(value) {
  return ((Math.floor(value / 10) % 10) << 4) | (value % 10);
}

function fromBcd(value) {
  return (value >> 4) * 10 + (value & 0x0f);
}

// What the option after '@' says to start from: { ms, halted }, the wall time in milliseconds as if it were UTC; null
// when it is no date.
export function parseStart(text) {
  if (text === undefined || text === null) {
    const now = new Date();
    return { ms: now.getTime() - now.getTimezoneOffset() * 60000, halted: false };
  }

  if (text === 'halted') {
    return { ms: Date.UTC(2000, 0, 1), halted: true };
  }

  const ms = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/.test(text) ? Date.parse(`${text}Z`) : NaN;
  return Number.isNaN(ms) || ms < Date.UTC(2000, 0, 1) || ms >= Date.UTC(2100, 0, 1) ? null : { ms, halted: false };
}

export class Ds1307 extends RegisterDevice {
  constructor(address, clock, log, start) {
    super('DS1307');
    this.address = address;
    this.clock = clock;
    this.log = log;
    this.wroteTime = false;
    const { ms, halted } = parseStart(start);
    // the time it keeps: whole seconds of wall time at simulated nanos baseNanos, and the day of the week then (1 to 7)
    this.baseMs = Math.floor(ms / 1000) * 1000;
    this.baseNanos = clock.nanos;
    this.baseDayOfWeek = new Date(this.baseMs).getUTCDay() + 1;
    this.halted = halted;
    this.registers.fill(0);
    this.copyTime(this.baseMs, this.baseDayOfWeek, false);
    if (halted) {
      this.registers[SECONDS] |= HALT;
    }
  }

  // The time registers from wall time ms, in the hours' form the program chose (24 hours unless bit 6 is set).
  copyTime(ms, dayOfWeek, twelveHours) {
    const time = new Date(ms);
    this.registers[SECONDS] = toBcd(time.getUTCSeconds());
    this.registers[0x01] = toBcd(time.getUTCMinutes());
    const hour = time.getUTCHours();
    this.registers[HOURS] = twelveHours
      ? TWELVE_HOURS | (hour >= 12 ? AFTERNOON : 0) | toBcd(hour % 12 === 0 ? 12 : hour % 12)
      : toBcd(hour);
    this.registers[DAY_OF_WEEK] = dayOfWeek;
    this.registers[0x04] = toBcd(time.getUTCDate());
    this.registers[0x05] = toBcd(time.getUTCMonth() + 1);
    this.registers[YEAR] = toBcd(time.getUTCFullYear() % 100);
  }

  // The wall time now, and the day of the week: the base and the whole seconds the simulation has run since.
  now() {
    const ms = this.baseMs + Math.floor((this.clock.nanos - this.baseNanos) / 1e9) * 1000;
    const days = Math.floor(ms / 86400000) - Math.floor(this.baseMs / 86400000);
    return { ms, dayOfWeek: ((((this.baseDayOfWeek - 1 + days) % 7) + 7) % 7) + 1 };
  }

  start(read) {
    // the time registers take the time now at a start, while the clock runs
    if (!this.halted) {
      const { ms, dayOfWeek } = this.now();
      this.copyTime(ms, dayOfWeek, (this.registers[HOURS] & TWELVE_HOURS) !== 0);
    }

    return super.start(read);
  }

  write(value) {
    if (this.chosen) {
      this.registers[this.pointer] = value;
      this.wroteTime ||= this.pointer <= YEAR;
      this.pointer = (this.pointer + 1) & 0x3f;
      return true;
    }

    this.pointer = value & 0x3f;
    this.chosen = true;
    return true;
  }

  read() {
    const value = this.registers[this.pointer];
    this.pointer = (this.pointer + 1) & 0x3f;
    return value;
  }

  stop() {
    if (!this.wroteTime) {
      return;
    }

    // the clock counts from what was written, from now on
    this.wroteTime = false;
    const r = this.registers;
    let hour;
    if (r[HOURS] & TWELVE_HOURS) {
      hour = (fromBcd(r[HOURS] & 0x1f) % 12) + (r[HOURS] & AFTERNOON ? 12 : 0);
    } else {
      hour = fromBcd(r[HOURS] & 0x3f);
    }

    const ms = Date.UTC(2000 + fromBcd(r[YEAR]), fromBcd(r[0x05] & 0x1f) - 1, fromBcd(r[0x04] & 0x3f), hour,
      fromBcd(r[0x01] & 0x7f), fromBcd(r[SECONDS] & 0x7f));
    this.baseMs = ms;
    this.baseNanos = this.clock.nanos;
    this.baseDayOfWeek = r[DAY_OF_WEEK] & 0x07;
    this.halted = (r[SECONDS] & HALT) !== 0;
    const text = new Date(ms).toISOString().replace('T', ' ').slice(0, 19);
    const day = DAYS[(this.baseDayOfWeek + 6) % 7];
    this.log(`DS1307 0x${this.address.toString(16)} set to ${text} (${day}, day ${this.baseDayOfWeek}), ` +
      `${this.halted ? 'halted' : 'running'}`);
  }
}
