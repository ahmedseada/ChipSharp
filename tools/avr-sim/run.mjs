// Runs an Arduino Uno (ATmega328P at 16 MHz) .hex image in the avr8js simulator (the one Wokwi is built on) and prints
// what the program does: every change of a pin, named as on the Uno's headers (D0-D13, A0-A5), and every line written
// to USART0 (the Uno's serial port, D0 and D1), each with the simulated time it happened at.
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
// The ATmega328P has the Uno's 32 KB of flash, 2 KB of RAM (data addresses 0x100-0x8ff), ports B, C and D, the
// timers 0, 1 and 2, and USART0. The image is loaded at address 0 (no bootloader) and the CPU starts there, at the
// reset vector, as after a reset with the fuses Wokwi uses.
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
//     [ 1000.000 ms] stopped: <why>              the last line
//
// Exit code: 0 when the program ran (to its end or for the whole time), 1 when the image is not valid, 2 for wrong
// arguments, 3 when the program went wrong: it hit BREAK, or ran into flash the image does not fill.

import { readFileSync, writeFileSync } from 'node:fs';
import {
  AVRIOPort, AVRTimer, AVRUSART, CPU, PinState, avrInstruction, portBConfig, portCConfig, portDConfig, timer0Config,
  timer1Config, timer2Config, usart0Config,
} from 'avr8js';

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
    '[--input <pin>=<high|low>[@<milliseconds>ms]]... [--send <text>[@<milliseconds>ms]]...');
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

function parseArguments(args) {
  const options = { image: null, ms: 1000, usart0: null, inputs: [], sends: [] };
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === '--ms' || arg === '--usart0' || arg === '--input' || arg === '--send') {
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

// the pins: every change printed; an input no --input drives reads its pull-up
const driven = new Set(); // the pins an --input drives, from its time on
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

      if (state !== states[bit]) {
        states[bit] = state;
        print(`${pins[bit]} ${PIN_STATES[state] ?? `state ${state}`}`);
      }
    }
  });
  return port;
});

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

if (usartLine.length > 0) {
  print(`USART0: ${Buffer.from(usartLine).toString('latin1')} (no line ending)`);
}

if (options.usart0 !== null) {
  writeFileSync(options.usart0, Buffer.from(usartBytes));
}

print(`stopped: ${reason}`);
process.exitCode = exitCode;
