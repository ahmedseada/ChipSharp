// Runs an Arduino Uno (ATmega328P at 16 MHz) .hex image in the avr8js simulator (the one Wokwi is built on) and prints
// what the program does: every change of a pin, named as on the Uno's headers (D0-D13, A0-A5), every line written to
// USART0 (the Uno's serial port, D0 and D1), the PWM outputs, as measured, and what the I2C and SPI buses do with the
// simulated devices on them, each with the simulated time it happened at.
//
// One-time setup (needs Node.js 18 or later):
//
//     cd tools/avr-sim
//     npm ci
//
// Usage:
//
//     node tools/avr-sim/run.mjs <image.hex> [--ms <simulated milliseconds, default 1000>]
//                                            [--usart0 <file to write USART0's bytes to>]
//                                            [--input <pin>=<high|low>[@<simulated milliseconds>ms]]...
//                                            [--send <text>[@<simulated milliseconds>ms]]...
//                                            [--adc <A0-A5|temp>=<volts>V|<reading>|<celsius>C[@<...>ms]]...
//                                            [--i2c 0:<address>=<device>]... [--spi 0=<device>[@<pin>]]...
//
// --input drives a pin from outside, as a button or another chip would, e.g. --input D2=high, or --input A0=low@100ms
// (the pin changes then, as a button pressed after 100 ms, and the change is printed). Give a pin several times to
// change it several times. A pin no --input drives reads high when the program turns its pull-up on, else low.
//
// --send sends text to USART0's receiver (RX, D0), as the Arduino IDE's serial monitor would, e.g. --send "on\n@100ms":
// from that time (or the start), byte after byte at the port's speed (10 bits a byte, 8N1), its UTF-8 bytes; \r, \n,
// \t, \\ and \xHH stand for those bytes. Sends follow each other. A byte that arrives while the receiver is off is
// lost, as on the chip.
//
// --adc sets the voltage an analog input reads (A0-A5, 0 to 5 V against AVcc: 2.5V reads 512 of 1023; or 512, a
// reading), or the chip's temperature (temp=30C; 25 C unless given, which reads 352 against the 1.1 V reference, 1.28
// more a degree, as the runtime's formula has it), from the start or from a time on, e.g. --adc A0=2.5V@100ms.
//
// --i2c and --spi put the Pico simulator's devices (tools/rp2040-sim/devices: catalog.mjs lists them) on the Uno's
// buses, I2C0 (the TWI, A4 and A5) and SPI0 (D13, D11, D12): --i2c 0:0x27=lcd1602 a 16x2 LCD on a PCF8574 backpack at
// 0x27, --i2c 0:0x68=ds1307 a DS1307 real-time clock (ds1307@2026-11-27T18:00:00 from a date, ds1307@halted a new one),
// --i2c 0:0x68=mpu6050, 0:0x76=bmp280, 0:0x50=stuck (holds the clock low), --spi 0=74hc595@D10 a 74HC595 shift
// register with its latch on D10 (or 10: a pin by its name or number), --spi 0=loopback RX wired to TX (@<pin>: only
// while that pin is low). An I2C address without a device does not answer.
//
// The ATmega328P has the Uno's 32 KB of flash, 2 KB of RAM (data addresses 0x100-0x8ff), ports B, C and D, the
// timers 0, 1 and 2, USART0, the TWI (I2C), the SPI controller and the ADC. The image is loaded at address 0 (no
// bootloader) and the CPU starts there, at the reset vector, as after a reset with the fuses Wokwi uses.
//
// The run stops after the simulated time, or earlier:
//  - when the program has finished: it runs SLEEP with interrupts off (cli; sleep), so that nothing can wake the CPU
//    (ChipSharp's AVR runtime ends a program this way);
//  - when the program runs BREAK (the runtime's stop for a fatal error, such as a division by zero);
//  - when the CPU runs into flash the image does not fill (e.g. a jump to a wrong address).
//
// Output lines:
//
//     [    0.123 ms] D13 high                   a pin changed: low or high when driven, else input or input pull-up
//                                                (pins without an Uno header: PB6, PB7, PC6)
//     [    0.200 ms] USART0 baud 115200          the program set USART0's speed (from UBRR0 and U2X0)
//     [    1.234 ms] USART0: Hello               a line written to USART0 (without its line ending)
//     [  100.000 ms] input: D2 high              an --input with a time changed the pin from outside
//     [  100.000 ms] input: USART0 "on\n"        a --send starts sending
//     [  100.000 ms] input: A0 2.500 V (512)      an --adc with a time changed an analog input
//     [   12.000 ms] D9 PWM 50.00 Hz, duty 7.50% (1500.00 us high)
//                                                a PWM output (its timer's compare output on), measured from its edges
//                                                (rising to rising, rising to falling): printed when a period differs
//                                                from the one before, instead of every edge
//     [   15.000 ms] D9 PWM steady low           a PWM output with no edge for 2.5 periods
//     [    2.000 ms] I2C0 0x68 write 00, read 30 a transfer on the I2C bus (catalog.mjs lists the lines)
//     [    3.000 ms] SPI0 mode 0, 1000000 Hz     the SPI bus's mode and clock, when a transfer first uses them
//     [    3.000 ms] SPI0 74HC595 Q3 high        an output of a device on the SPI bus changed
//     [   58.230 ms] LCD 0x27: |count 12        |  a row of an LCD, when what it shows changes and settles
//     [ 1000.000 ms] stopped: <why>              the last line
//
// Exit code: 0 when the program ran (to its end or for the whole time), 1 when the image is not valid, 2 for wrong
// arguments, 3 when the program went wrong: it hit BREAK, or ran into flash the image does not fill.

import { readFileSync, writeFileSync } from 'node:fs';
import {
  ADCMuxInputType, AVRADC, AVRIOPort, AVRSPI, AVRTimer, AVRTWI, AVRUSART, CPU, PinState, adcConfig, avrInstruction,
  portBConfig, portCConfig, portDConfig, spiConfig, timer0Config, timer1Config, timer2Config, twiConfig, usart0Config,
} from 'avr8js';
import {
  busUsage, I2cBus, makeI2cDevices, makeSpiDevices, parseI2cOption, parseSpiOption,
} from '../rp2040-sim/devices/catalog.mjs';

const CLOCK_HZ = 16e6;
const FLASH_BYTES = 32 * 1024;
const SRAM_BYTES = 2 * 1024;
const SLEEP = 0x9588; // the opcodes this script watches for (avr8js runs both as nothing)
const BREAK = 0x9598;
const SREG_I = 0x80; // the global interrupt enable flag
const PIN_STATES = {
  [PinState.Low]: 'low',
  [PinState.High]: 'high',
  [PinState.Input]: 'input',
  [PinState.InputPullUp]: 'input pull-up',
};

// The Uno's names of the pins of ports B, C and D: D8-D13 and A0-A5, D0-D7; the crystal's and the reset pin by port.
const PORTS = [
  { name: 'B', config: portBConfig, pins: ['D8', 'D9', 'D10', 'D11', 'D12', 'D13', 'PB6', 'PB7'] },
  { name: 'C', config: portCConfig, pins: ['A0', 'A1', 'A2', 'A3', 'A4', 'A5', 'PC6', null] },
  { name: 'D', config: portDConfig, pins: ['D0', 'D1', 'D2', 'D3', 'D4', 'D5', 'D6', 'D7'] },
];

function usage(message) {
  console.error(`run.mjs: ${message}`);
  console.error('Usage: node run.mjs <image.hex> [--ms <simulated milliseconds>] [--usart0 <output file>] ' +
    '[--input <pin>=<high|low>[@<milliseconds>ms]]... [--send <text>[@<milliseconds>ms]]... ' +
    `[--adc <A0-A5|temp>=<volts>V|<reading>|<celsius>C[@<milliseconds>ms]]... ${busUsage(BUSES)}`);
  process.exit(2);
}

function fail(message) {
  console.error(`run.mjs: ${message}`);
  process.exit(1);
}

// The port and bit of an Uno pin name (D0-D13, A0-A5), or null.
function pinByName(name) {
  for (const [port, { pins }] of PORTS.entries()) {
    const bit = pins.indexOf(name.toUpperCase());
    if (bit >= 0 && /^[DA]\d+$/i.test(name)) {
      return { port, bit, name: pins[bit] };
    }
  }

  return null;
}

// The Uno's buses: I2C0 and SPI0.
const BUSES = [0];

// An Uno pin by its name (D10, A4) or its number (10, 18: the numbers programs give, 0-13 D0-D13 and 14-19 A0-A5), as
// that number; null for none.
function unoPin(text) {
  const pin = /^\d+$/.test(text) ? Number(text) : pinByName(text) === null ? NaN
    : (({ port, bit }) => (port === 2 ? bit : port === 0 ? 8 + bit : 14 + bit))(pinByName(text));
  return pin >= 0 && pin < 20 ? pin : null;
}

// The port (an index of PORTS) and the bit of an Uno pin number.
function placeOf(pin) {
  return pin < 8 ? { port: 2, bit: pin } : pin < 14 ? { port: 0, bit: pin - 8 } : { port: 1, bit: pin - 14 };
}

// ---- The analog inputs (--adc) -----------------------------------------------------------------------------------

const AVCC = 5; // the reference of A0-A5: the Uno's 5 V
const INTERNAL_REFERENCE = 1.1;
const TEMPERATURE = 25; // degrees Celsius, unless an --adc says otherwise

// The temperature sensor's voltage at a temperature: what reads, against the 1.1 V reference, as the runtime's formula
// (atmega328p_adc.c) turns into those degrees: 352 at 25 C, 1.28 more a degree.
function sensorVolts(celsius) {
  return ((Math.round(352 + (celsius - 25) * 1.28) + 0.5) * INTERNAL_REFERENCE) / 1024;
}

// One --adc: { channel (0-5, or 'temp'), volts, what, reading, at }.
function parseAdc(value) {
  const match = value === undefined ? null
    : /^([Aa][0-5]|temp)=(-?\d+(?:\.\d+)?)(V|C)?(?:@(\d+(?:\.\d+)?)ms)?$/.exec(value);
  if (match === null || (match[1] === 'temp') !== (match[3] === 'C')) {
    usage(`--adc takes an analog pin (A0 to A5) and volts or a reading (0 to 1023), or temp and degrees Celsius, and ` +
      `maybe a time, e.g. A0=2.5V, A1=512, temp=30C or A0=1V@100ms, not '${value}'.`);
  }

  const number = Number(match[2]);
  if (match[1] === 'temp') {
    const volts = sensorVolts(number);
    const reading = Math.floor((volts / INTERNAL_REFERENCE) * 1024);
    return { channel: 'temp', volts, what: `temperature ${number} C`, reading, at: timeOf(match[4]) };
  }

  const volts = match[3] === 'V' ? number : ((number + 0.5) * AVCC) / 1024;
  const reading = Math.min(Math.floor((volts / AVCC) * 1024), 1023);
  if (match[3] === 'V' ? !(volts >= 0 && volts <= AVCC) : !(Number.isInteger(number) && number >= 0 && number <= 1023)) {
    usage(`--adc: '${value}' is outside what the ADC reads: 0 to 5 V, 0 to 1023.`);
  }

  const name = match[1].toUpperCase();
  return { channel: Number(name[1]), volts, what: `${name} ${(match[3] === 'V' ? volts : (reading * AVCC) / 1024).toFixed(3)} V`,
    reading, at: timeOf(match[4]) };
}

function parseArguments(args) {
  const options = { image: null, ms: 1000, usart0: null, inputs: [], sends: [], adc: [], buses: [] };
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === '--i2c' || arg === '--spi') {
      // a simulated device on a bus (tools/rp2040-sim/devices/catalog.mjs)
      const value = args[++i] ?? '';
      const device = arg === '--i2c' ? parseI2cOption(value, BUSES) : parseSpiOption(value, BUSES, unoPin, 'D10');
      if (device.error !== undefined) {
        usage(device.error);
      }

      options.buses.push(device);
    } else if (arg === '--adc') {
      options.adc.push(parseAdc(args[++i]));
    } else if (arg === '--ms' || arg === '--usart0' || arg === '--input' || arg === '--send') {
      const value = args[++i];
      if (value === undefined) {
        usage(`${arg} needs a value.`);
      }

      if (arg === '--ms') {
        options.ms = Number(value);
        if (!Number.isFinite(options.ms) || options.ms <= 0) {
          usage(`--ms must be a positive number of milliseconds, not '${value}'.`);
        }
      } else if (arg === '--input') {
        const match = /^([DA]\d+)=(high|low)(?:@(\d+(?:\.\d+)?)ms)?$/i.exec(value);
        const pin = match === null ? null : pinByName(match[1]);
        if (pin === null) {
          usage(`--input takes an Uno pin (D0 to D13, A0 to A5), a level and maybe a time, e.g. D2=high or ` +
            `A0=low@100ms, not '${value}'.`);
        }

        options.inputs.push({ ...pin, high: match[2].toLowerCase() === 'high', at: timeOf(match[3]) });
      } else if (arg === '--send') {
        const match = /^(.*?)(?:@(\d+(?:\.\d+)?)ms)?$/s.exec(value);
        options.sends.push({ bytes: unescape(match[1]), at: timeOf(match[2]) });
      } else {
        options.usart0 = value;
      }
    } else if (arg.startsWith('--')) {
      usage(`unknown option '${arg}'.`);
    } else if (options.image === null) {
      options.image = arg;
    } else {
      usage(`only one image, please ('${options.image}' and '${arg}').`);
    }
  }

  if (options.image === null) {
    usage('which .hex image?');
  }

  return options;
}

function timeOf(milliseconds) {
  return milliseconds === undefined ? null : Number(milliseconds);
}

// The bytes of --send's text: its UTF-8, with \r, \n, \t, \\ and \xHH as those bytes.
function unescape(text) {
  const bytes = [];
  for (let i = 0; i < text.length; i++) {
    if (text[i] !== '\\' || i + 1 === text.length) {
      bytes.push(...Buffer.from(text[i], 'utf8'));
      continue;
    }

    const next = text[++i];
    const simple = { r: 0x0d, n: 0x0a, t: 0x09, '\\': 0x5c }[next];
    if (simple !== undefined) {
      bytes.push(simple);
    } else if (next === 'x' && /^[0-9a-fA-F]{2}$/.test(text.slice(i + 1, i + 3))) {
      bytes.push(parseInt(text.slice(i + 1, i + 3), 16));
      i += 2;
    } else {
      usage(`--send: '\\${next}' is not one of \\r, \\n, \\t, \\\\ and \\xHH.`);
    }
  }

  return bytes;
}

// The bytes as --send takes them: printable ASCII as it is, the rest escaped.
function escaped(bytes) {
  const named = { 0x0d: '\\r', 0x0a: '\\n', 0x09: '\\t', 0x5c: '\\\\' };
  return bytes.map((byte) => named[byte] ??
    (byte >= 0x20 && byte < 0x7f ? String.fromCharCode(byte) : `\\x${byte.toString(16).padStart(2, '0')}`)).join('');
}

// Reads an Intel HEX file into flash: data records (00) at their addresses, with the extended segment (02) and linear
// (04) address records; start address records (03, 05) are allowed and not needed (the CPU starts at the reset vector).
// Every record's checksum is checked. Returns how many bytes were loaded; marks each word loaded in `loaded`.
function loadHex(path, flash, loaded) {
  let text;
  try {
    text = readFileSync(path, 'latin1');
  } catch (e) {
    fail(`cannot read '${path}': ${e.message}`);
  }

  let base = 0;
  let bytes = 0;
  let ended = false;
  const lines = text.split('\n');
  for (let number = 1; number <= lines.length; number++) {
    const line = lines[number - 1].trim();
    if (line.length === 0) {
      continue;
    }

    const where = `line ${number} of '${path}'`;
    if (ended) {
      fail(`${where} comes after the end-of-file record.`);
    }

    if (!/^:([0-9A-Fa-f]{2})+$/.test(line) || line.length < 11) {
      fail(`${where} is not an Intel HEX record (':' and pairs of hex digits): '${line}'.`);
    }

    const record = Buffer.from(line.slice(1), 'hex');
    const [length, addressHigh, addressLow, type] = record;
    if (record.length !== length + 5) {
      fail(`${where} says it has ${length} data bytes but has ${record.length - 5}.`);
    }

    if (record.reduce((sum, byte) => sum + byte, 0) & 0xff) {
      fail(`${where} has a wrong checksum.`);
    }

    const data = record.subarray(4, 4 + length);
    if (type === 0x00) {
      const address = base + ((addressHigh << 8) | addressLow);
      if (address + length > FLASH_BYTES) {
        fail(`${where} writes ${length} bytes at 0x${address.toString(16)}, past the ATmega328P's ` +
          `${FLASH_BYTES / 1024} KB of flash.`);
      }

      flash.set(data, address);
      for (let at = address; at < address + length; at++) {
        loaded[at >> 1] = 1;
      }

      bytes += length;
    } else if (type === 0x01) {
      ended = true;
    } else if (type === 0x02 && length === 2) {
      base = ((data[0] << 8) | data[1]) << 4;
    } else if (type === 0x04 && length === 2) {
      base = ((data[0] << 8) | data[1]) * 0x10000;
    } else if (!((type === 0x03 || type === 0x05) && length === 4)) {
      fail(`${where} is a record of type ${type} with ${length} bytes, which is not one Intel HEX has.`);
    }
  }

  if (!ended) {
    fail(`'${path}' has no end-of-file record (:00000001FF); is it cut short?`);
  }

  if (bytes === 0) {
    fail(`'${path}' has no data to load.`);
  }

  return bytes;
}

function milliseconds(cycles) {
  return `[${((cycles * 1000) / CLOCK_HZ).toFixed(3).padStart(10)} ms]`;
}

// A flash address as the .elf and its disassembly give it: in bytes (the CPU counts words).
function hex(wordAddress) {
  return `0x${(wordAddress * 2).toString(16).padStart(4, '0')}`;
}

const options = parseArguments(process.argv.slice(2));
const program = new Uint16Array(FLASH_BYTES / 2);
const loaded = new Uint8Array(FLASH_BYTES / 2);
const bytes = loadHex(options.image, new Uint8Array(program.buffer), loaded);
const cpu = new CPU(program, SRAM_BYTES);
const print = (text) => console.log(`${milliseconds(cpu.cycles)} ${text}`);

console.log(`booting ${options.image} (${bytes} bytes of flash), running ${options.ms} ms of simulated time`);

// the timers (delays, PWM), with the ports they drive
for (const config of [timer0Config, timer1Config, timer2Config]) {
  new AVRTimer(cpu, config);
}

// the simulation's clock, as the devices of tools/rp2040-sim take it (rp2040js's): its time in nanoseconds, and alarms
const clock = {
  get nanos() {
    return (cpu.cycles * 1e9) / CLOCK_HZ;
  },
  createAlarm(callback) {
    let pending = null;
    const fire = () => {
      pending = null;
      callback();
    };
    return {
      schedule(nanos) {
        this.cancel();
        pending = cpu.addClockEvent(fire, Math.max(1, Math.round((nanos * CLOCK_HZ) / 1e9)));
      },
      cancel() {
        if (pending !== null) {
          cpu.clearClockEvent(pending);
          pending = null;
        }
      },
    };
  },
};

// ---- PWM outputs, measured -----------------------------------------------------------------------------------------

// The pins a timer's compare output drives: its TCCRnA, the compare output mode's bits there, and the timer. A pin is
// a PWM output while those bits are not 0; its edges are measured then rather than printed.
const PWM_PINS = {
  D6: { tccra: 0x44, mask: 0xc0, timer: 0 }, // OC0A
  D5: { tccra: 0x44, mask: 0x30, timer: 0 }, // OC0B
  D9: { tccra: 0x80, mask: 0xc0, timer: 1 }, // OC1A
  D10: { tccra: 0x80, mask: 0x30, timer: 1 }, // OC1B
  D11: { tccra: 0xb0, mask: 0xc0, timer: 2 }, // OC2A
  D3: { tccra: 0xb0, mask: 0x30, timer: 2 }, // OC2B
};

// A PWM output's period in clock cycles until one is measured, from its timer's registers: TOP + 1 counts (510 phase
// correct, up and down) of Timer0's or Timer2's divider. Timer1 runs in normal mode as the runtime's clock, and its
// compare A times D9's edges itself, at any frequency from 1 Hz: a second.
function timerCycles(timer) {
  if (timer === 1) {
    return CLOCK_HZ;
  }

  const [tccra, tccrb, dividers] = timer === 0 ? [0x44, 0x45, [0, 1, 8, 64, 256, 1024]]
    : [0xb0, 0xb1, [0, 1, 8, 32, 64, 128, 256, 1024]];
  return ((cpu.data[tccra] & 0x03) === 1 ? 510 : 256) * (dividers[cpu.data[tccrb] & 0x07] ?? 0) || CLOCK_HZ;
}

// What each PWM output did last: its last rising and falling edges, its period, the last line printed, and the alarm
// that says it is steady when no edge comes.
const pwmOutputs = new Map();

// A PWM output's edge: a whole period (rising edge to rising edge, high until the falling edge between) is printed when
// it differs from the one before; no edge for 2.5 periods is printed as steady low or high.
function pwmEdge(name, high, level) {
  let output = pwmOutputs.get(name);
  const say = (text) => {
    if (text !== output.printed) {
      print(`${name} PWM ${text}`);
      output.printed = text;
    }
  };
  if (output === undefined) {
    output = { rise: null, fall: null, period: null, printed: null };
    output.steady = clock.createAlarm(() => {
      say(`steady ${level() ? 'high' : 'low'}`);
      output.rise = null; // the next period starts with the next rising edge
      output.fall = null;
    });
    pwmOutputs.set(name, output);
  }

  const now = clock.nanos;
  if (high && output.rise !== null && output.fall !== null && output.fall > output.rise) {
    const period = now - output.rise;
    const highTime = output.fall - output.rise;
    output.period = period;
    say(`${(1e9 / period).toFixed(2)} Hz, duty ${((100 * highTime) / period).toFixed(2)}% ` +
      `(${(highTime / 1000).toFixed(2)} us high)`);
  }

  if (high) {
    output.rise = now;
  } else {
    output.fall = now;
  }

  const timer = PWM_PINS[name].timer;
  output.steady.schedule(2.5 * (output.period ?? (timerCycles(timer) * 1e9) / CLOCK_HZ));
}

// the pins: every change printed (a PWM output's measured instead); an input no --input drives reads its pull-up; the
// pins a shift register's latch is on are watched for their rising edges
const driven = new Set(); // the pins an --input drives, from its time on
const latches = new Map(); // port * 8 + bit: what to call when the pin goes high
const ports = PORTS.map(({ config, pins }, index) => {
  const port = new AVRIOPort(cpu, config);
  const states = pins.map(() => PinState.Input);
  port.addListener(() => {
    for (let bit = 0; bit < 8; bit++) {
      if (pins[bit] === null) {
        continue;
      }

      const state = port.pinState(bit);
      if (!driven.has(index * 8 + bit)) {
        port.setPin(bit, state === PinState.InputPullUp);
      }

      if (state === states[bit]) {
        continue;
      }

      const before = states[bit];
      states[bit] = state;
      const pwm = PWM_PINS[pins[bit]];
      if (pwm !== undefined && (cpu.data[pwm.tccra] & pwm.mask) !== 0) {
        pwmEdge(pins[bit], state === PinState.High, () => port.pinState(bit) === PinState.High);
      } else {
        if (pwmOutputs.has(pins[bit])) {
          pwmOutputs.get(pins[bit]).steady.cancel();
          pwmOutputs.delete(pins[bit]);
        }

        print(`${pins[bit]} ${PIN_STATES[state] ?? `state ${state}`}`);
      }

      if (state === PinState.High && before !== PinState.High) {
        latches.get(index * 8 + bit)?.forEach((latched) => latched());
      }
    }
  });
  return port;
});

// The state of an Uno pin by its number.
function pinState(pin) {
  const { port, bit } = placeOf(pin);
  return ports[port].pinState(bit);
}

for (const { port, bit, name, high, at } of options.inputs) {
  if (at === null) {
    driven.add(port * 8 + bit);
    ports[port].setPin(bit, high);
  } else {
    cpu.addClockEvent(() => {
      print(`input: ${name} ${high ? 'high' : 'low'}`);
      driven.add(port * 8 + bit);
      ports[port].setPin(bit, high);
    }, Math.round((at * CLOCK_HZ) / 1000));
  }
}

// USART0: what the program writes, a line at a time; its speed when it changes; what --send sends to it
const usart = new AVRUSART(cpu, usart0Config, CLOCK_HZ);
const usartBytes = [];
let usartLine = [];
usart.onByteTransmit = (value) => {
  usartBytes.push(value);
  if (value === 0x0a) {
    print(`USART0: ${Buffer.from(usartLine).toString('latin1').replace(/\r$/, '')}`);
    usartLine = [];
  } else {
    usartLine.push(value);
  }
};
let baud = null;
usart.onConfigurationChange = () => {
  if (usart.baudRate !== baud && (usart.txEnable || usart.rxEnable)) {
    baud = usart.baudRate;
    print(`USART0 baud ${baud}`);
  }
};

if (options.sends.length > 0) {
  // one send after the other, in the order given, each from its time (or once the one before is done)
  const queue = [...options.sends];
  let send = null;
  let next = 0;
  const sendByte = () => {
    if (send === null) {
      send = queue.shift();
      next = 0;
      print(`input: USART0 "${escaped(send.bytes)}"`);
    }

    if (next < send.bytes.length) {
      usart.writeByte(send.bytes[next++], true);
    }

    const byteCycles = Math.round((10 * CLOCK_HZ) / (usart.baudRate || 9600));
    if (next < send.bytes.length) {
      cpu.addClockEvent(sendByte, byteCycles);
    } else if (queue.length > 0) {
      send = null;
      cpu.addClockEvent(sendByte, Math.max(byteCycles, Math.round(((queue[0].at ?? 0) * CLOCK_HZ) / 1000) - cpu.cycles));
    }
  };
  cpu.addClockEvent(sendByte, Math.round(((queue[0].at ?? 0) * CLOCK_HZ) / 1000));
}

// ---- The buses and their devices (--i2c, --spi) --------------------------------------------------------------------

// I2C0: the TWI, whose steps (start, address, byte, stop) avr8js hands to an event handler, which the devices answer
const i2cDevices = makeI2cDevices(options.buses, 0, clock, print);
const twi = new AVRTWI(cpu, twiConfig, CLOCK_HZ);
const i2c = new I2cBus('I2C0', i2cDevices, print);
twi.eventHandler = {
  start() {
    i2c.start();
    twi.completeStart();
  },
  stop() {
    i2c.stop();
    twi.completeStop();
  },
  connectToSlave(address, write) {
    const answered = i2c.connect(address, !write, Math.round(twi.sclFrequency));
    if (answered !== null) {
      twi.completeConnect(answered); // else the device holds the clock low: the step never ends
    }
  },
  writeByte(value) {
    twi.completeWrite(i2c.write(value));
  },
  readByte() {
    twi.completeRead(i2c.read());
  },
};

// SPI0: each byte to the devices, and the selected one's answer back once the byte's time (8 bits of SCK) has passed
const spiDevices = makeSpiDevices(options.buses, 0, (text) => print(`SPI0 ${text}`), (pin, latched) => {
  const { port, bit } = placeOf(pin);
  const key = port * 8 + bit;
  latches.set(key, [...(latches.get(key) ?? []), latched]);
});
const spi = new AVRSPI(cpu, spiConfig, CLOCK_HZ);
let spiSettings = null;
spi.onByte = (value) => {
  // SPCR: CPOL (bit 3), the clock's level when idle, and CPHA (bit 2), the edge data is read on
  const spcr = cpu.data[spiConfig.SPCR];
  const mode = (spcr & 0x08 ? 2 : 0) + (spcr & 0x04 ? 1 : 0);
  const now = `mode ${mode}, ${Math.round(CLOCK_HZ / spi.clockDivider)} Hz`;
  if (now !== spiSettings) {
    print(`SPI0 ${now}`);
    spiSettings = now;
  }

  let answer = 0;
  for (const { device, select } of spiDevices) {
    if (select === null || pinState(select) === PinState.Low) {
      answer = device.transfer(value, mode) ?? answer;
    }
  }

  cpu.addClockEvent(() => spi.completeTransfer(answer), spi.transferCycles);
};

// ---- The analog inputs (--adc) -------------------------------------------------------------------------------------

// each conversion reads the voltage of its input against the reference the program chose (avr8js: AVcc, AREF or the
// internal 1.1 V), as the chip's 10 bits, after the ADC's 13 (the first 25) clocks
const adc = new AVRADC(cpu, adcConfig);
const analogVolts = [0, 0, 0, 0, 0, 0, 0, 0];
let temperatureVolts = sensorVolts(TEMPERATURE);
adc.onADCRead = (input) => {
  const volts = input.type === ADCMuxInputType.SingleEnded ? analogVolts[input.channel] ?? 0
    : input.type === ADCMuxInputType.Temperature ? temperatureVolts
      : input.type === ADCMuxInputType.Constant ? input.voltage
        : 0;
  const reading = Math.min(Math.max(Math.floor((volts / adc.referenceVoltage) * 1024), 0), 1023);
  cpu.addClockEvent(() => adc.completeADCRead(reading), adc.sampleCycles);
};

for (const { channel, volts, what, reading, at } of options.adc) {
  const set = () => {
    if (channel === 'temp') {
      temperatureVolts = volts;
    } else {
      analogVolts[channel] = volts;
    }
  };
  if (at === null) {
    set();
  } else {
    cpu.addClockEvent(() => {
      print(`input: ${what} (${reading})`);
      set();
    }, Math.round((at * CLOCK_HZ) / 1000));
  }
}

const end = Math.round((options.ms * CLOCK_HZ) / 1000);
let reason = `${options.ms} ms of simulated time have passed`;
let exitCode = 0;
while (cpu.cycles < end) {
  const pc = cpu.pc;
  const opcode = program[pc];
  if (!loaded[pc]) {
    reason = `the program ran into flash the image does not fill, at pc ${hex(pc)}`;
    exitCode = 3;
    break;
  }

  if (opcode === BREAK) {
    // the runtime stops this way on a fatal error
    reason = `the program hit a breakpoint (break) at pc ${hex(pc)}, e.g. a division by zero`;
    exitCode = 3;
    break;
  }

  if (opcode === SLEEP && !(cpu.SREG & SREG_I)) {
    reason = 'the program has finished: the CPU sleeps with interrupts off, so nothing can wake it';
    break;
  }

  avrInstruction(cpu);
  cpu.tick();
}

// what the devices have not printed yet (an LCD that has not settled)
i2cDevices.forEach((device) => device.finish?.());

if (usartLine.length > 0) {
  print(`USART0: ${Buffer.from(usartLine).toString('latin1')} (no line ending)`);
}

if (options.usart0 !== null) {
  writeFileSync(options.usart0, Buffer.from(usartBytes));
}

print(`stopped: ${reason}`);
process.exitCode = exitCode;
