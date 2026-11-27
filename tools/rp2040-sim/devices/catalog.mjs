// The simulated devices of the I2C and SPI buses as both simulators put them on their chip's buses (buses.mjs here, for
// rp2040js; tools/avr-sim/run.mjs, for avr8js): the devices an --i2c or --spi option names, the options themselves, and
// what an I2C bus prints of each transfer. Plain JavaScript, with no simulator's package, so that tools/avr-sim imports
// it from here as it is.
//
//     --i2c <bus>:<address>=<device>     a device at an address (0x68 or 104) on an I2C bus:
//                                          mpu6050   accelerometer and gyroscope (mpu6050.mjs)
//                                          bmp280    pressure and temperature (bmp280.mjs)
//                                          lcd1602   a 16x2 character LCD on a PCF8574 backpack (lcd1602.mjs)
//                                          lcd2004   the same, 20x4
//                                          stuck     a device that holds the clock low for ever once addressed
//                                          ds1307[@<date>]  a real-time clock (ds1307.mjs), from the PC's time, or
//                                                    from a date (2026-11-27T18:00:00), or halted (@halted)
//     --spi <bus>=<device>[@<pin>]       a device on an SPI bus:
//                                          74hc595@<latch pin>   a shift register (shift-register.mjs)
//                                          loopback[@<chip select pin>]   RX wired to TX; with a pin, only while
//                                          the program holds that pin low
//
// Every I2C address without a device leaves the controller without an answer (a NACK), as an empty bus does. A device
// is made with the simulation's clock ({ nanos, createAlarm(callback) }, whose alarms schedule(nanos) and cancel(), as
// rp2040js's clock has them) and the log, which prints a line with the simulated time.

import { Bmp280 } from './bmp280.mjs';
import { Ds1307, parseStart } from './ds1307.mjs';
import { Lcd } from './lcd1602.mjs';
import { Mpu6050 } from './mpu6050.mjs';
import { Loopback, ShiftRegister } from './shift-register.mjs';

// A device that answers its address, then never lets the clock go: every transfer on its bus waits for it from then on.
class Stuck {
  constructor() {
    this.name = 'stuck';
    this.holdsClock = true;
  }
}

// Each device made for its address, with the simulation's clock and the log (the LCDs print what they show), and
// what the option says after '@' (the DS1307's date).
const I2C_DEVICES = {
  mpu6050: () => new Mpu6050(),
  bmp280: () => new Bmp280(),
  lcd1602: (address, clock, log) => new Lcd(address, 16, 2, clock, log),
  lcd2004: (address, clock, log) => new Lcd(address, 20, 4, clock, log),
  stuck: () => new Stuck(),
  ds1307: (address, clock, log, at) => new Ds1307(address, clock, log, at),
};

// The usage of the two options, for a chip with the buses given (e.g. [0, 1]).
export function busUsage(buses) {
  const bus = buses.length === 1 ? `${buses[0]}` : '<bus>';
  return `[--i2c ${bus}:<address>=<mpu6050|bmp280|lcd1602|lcd2004|stuck|ds1307[@<date>]>]... ` +
    `[--spi ${bus}=<74hc595@<pin>|loopback[@<pin>]>]...`;
}

// "0 or 1", or "0".
function busList(buses) {
  return buses.length === 1 ? `${buses[0]}` : `${buses.slice(0, -1).join(', ')} or ${buses.at(-1)}`;
}

// The device an --i2c value names, e.g. "0:0x68=mpu6050", for a chip with the I2C buses given; { error } when it is not
// valid.
export function parseI2cOption(value, buses) {
  const match = /^(\d+):(0x[0-9a-fA-F]+|\d+)=([a-z0-9]+)(?:@([0-9a-zA-Z:-]+))?$/.exec(value);
  const address = match === null ? NaN : Number(match[2]);
  if (match === null || !buses.includes(Number(match[1])) || !(address >= 0 && address <= 0x7f) ||
      !(match[3] in I2C_DEVICES)) {
    return { error: `--i2c takes a bus (${busList(buses)}), an address from 0 to 0x7f and a device ` +
      `(${Object.keys(I2C_DEVICES).join(', ')}), e.g. ${buses[0]}:0x68=mpu6050, not '${value}'.` };
  }

  if (match[4] !== undefined && (match[3] !== 'ds1307' || parseStart(match[4]) === null)) {
    return { error: `--i2c takes a date after '@' only for a ds1307, from 2000 to 2099 (2026-11-27T18:00:00, or ` +
      `halted), not '${value}'.` };
  }

  return { kind: 'i2c', bus: Number(match[1]), address, device: match[3], at: match[4] ?? null };
}

// The device an --spi value names, e.g. "0=74hc595@17", for a chip with the SPI buses given; pinOf(text) is the chip's
// pin a name or number stands for (null when it has none), example a pin as the chip names it. { error } when the value
// is not valid.
export function parseSpiOption(value, buses, pinOf, example) {
  const match = /^(\d+)=(74hc595|loopback)(?:@([A-Za-z]*\d+))?$/.exec(value);
  const pin = match === null || match[3] === undefined ? null : pinOf(match[3]);
  if (match === null || !buses.includes(Number(match[1])) || (match[3] !== undefined && pin === null) ||
      (match[2] === '74hc595' && pin === null)) {
    return { error: `--spi takes a bus (${busList(buses)}) and a device: 74hc595@<its latch pin> or loopback, maybe ` +
      `@<its chip select pin>, e.g. ${buses[0]}=74hc595@${example}, not '${value}'.` };
  }

  return { kind: 'spi', bus: Number(match[1]), device: match[2], pin };
}

// The I2C devices the options put on the bus, by address.
export function makeI2cDevices(options, bus, clock, log) {
  const devices = new Map();
  for (const option of options.filter((o) => o.kind === 'i2c' && o.bus === bus)) {
    devices.set(option.address, I2C_DEVICES[option.device](option.address, clock, log, option.at));
  }

  return devices;
}

// The SPI devices the options put on the bus: { device, select }, select the pin that must be low for the device to
// answer (null: always). A shift register's latch is watched with watchLatch(pin, latched), which calls latched when the
// pin goes high; report prints a line of the bus's (the shift register's outputs).
export function makeSpiDevices(options, bus, report, watchLatch) {
  const devices = [];
  for (const option of options.filter((o) => o.kind === 'spi' && o.bus === bus)) {
    if (option.device === 'loopback') {
      devices.push({ device: new Loopback(), select: option.pin });
    } else {
      // the shift register takes every byte; its latch pin shows them
      const register = new ShiftRegister(option.pin, report);
      watchLatch(option.pin, () => register.latched());
      devices.push({ device: register, select: null });
    }
  }

  return devices;
}

function hex(value) {
  return value.toString(16).padStart(2, '0');
}

// An I2C bus with its devices, as the chip's controller drives it, which prints what happens on it:
//
//     I2C0 100000 Hz                     the clock the program set, when a transfer first uses it
//     I2C0 0x68 write 75, read 68        a transfer, from its start to its stop: the bytes each way, in hex, each
//                                        part after a repeated start after a comma
//     I2C0 0x23 write: no answer         nothing answered the address
//     I2C0 0x68 write 6b 00 (refused)    the device refused a byte
//     I2C0 0x50 write: the stuck device holds the clock low
//
// The simulator calls start at a start or repeated start, connect with the address, write and read for each byte, and
// stop at the stop.
export class I2cBus {
  constructor(name, devices, log) {
    this.name = name;
    this.devices = devices;
    this.log = log;
    this.parts = null; // the transfer so far: each part from a (repeated) start
    this.address = 0;
    this.device = null;
    this.frequency = null;
  }

  start() {
    this.parts ??= [];
  }

  // The device at address answers (true) or not (false), for a read or a write, at the bus's clock (hertz); null when
  // it holds the clock low, and the controller never hears from it.
  connect(address, read, hertz) {
    if (hertz !== this.frequency) {
      this.log(`${this.name} ${hertz} Hz`);
      this.frequency = hertz;
    }

    this.parts ??= [];
    this.address = address;
    this.device = this.devices.get(address) ?? null;
    if (this.device?.holdsClock) {
      this.log(`${this.name} 0x${hex(address)} ${read ? 'read' : 'write'}: the ${this.device.name} device holds the ` +
        'clock low');
      return null;
    }

    const answered = this.device !== null && this.device.start(read);
    this.parts.push({ read, answered, bytes: [], refused: false });
    return answered;
  }

  // A byte the controller writes: true when the device took it.
  write(value) {
    const part = this.parts.at(-1);
    const taken = this.device.write(value);
    part.bytes.push(value);
    part.refused = !taken;
    return taken;
  }

  // The byte the device sends.
  read() {
    const value = this.device.read() & 0xff;
    this.parts.at(-1).bytes.push(value);
    return value;
  }

  stop() {
    this.device?.stop?.();
    if (this.parts !== null && this.parts.length > 0) {
      const text = this.parts.map((part) => (part.read ? 'read' : 'write') + (!part.answered ? ': no answer'
        : part.bytes.map((value) => ' ' + hex(value)).join('') + (part.refused ? ' (refused)' : '')));
      this.log(`${this.name} 0x${hex(this.address)} ${text.join(', ')}`);
    }

    this.parts = null;
    this.device = null;
  }
}
