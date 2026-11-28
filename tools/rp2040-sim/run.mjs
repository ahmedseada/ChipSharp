// Runs a Raspberry Pi Pico .uf2 image in the rp2040js simulator and prints what the program does: every change
// of a GPIO pin and every line written to UART0, each with the simulated time it happened at.
//
// One-time setup (needs Node.js 18 or later):
//
//     cd tools/rp2040-sim
//     npm ci
//
// Usage:
//
//     node tools/rp2040-sim/run.mjs <image.uf2> [--ms <simulated milliseconds, default 1000>]
//                                              [--uart0 <file to write UART0's bytes to>]
//                                              [--input <pin>=<high|low>[@<simulated milliseconds>ms]]...
//                                              [--send <uart>=<text>[@<simulated milliseconds>ms]]...
//                                              [--adc <pin|temp>=<volts>V|<celsius>C|<reading>[@<...>ms]]...
//                                              [--i2c <bus>:<address>=<device>]... [--spi <bus>=<device>[@<pin>]]...
//
// --input drives a pin from outside, as a button or another chip would; the program reads it with Pin.Read, and
// Pin.Watch... sees it change. Without a time it is the level from the start; with one, e.g. --input 15=high@100ms,
// the pin changes then (as a button pressed after 100 ms), and the change is printed. Give a pin several times to
// change it several times. A pin no --input drives follows its pad: it reads high with its pull-up on, else low. (rp2040js
// reads only outside levels: a pin the program drives itself reads as low.)
//
// --send sends text to UART0's or UART1's RX pin, as a terminal or another chip would, e.g. --send "0=led on\r@100ms":
// from that time (or the start), byte after byte at the port's speed (10 bits a byte, 8N1), its UTF-8 bytes; \r, \n,
// \t, \\ and \xHH stand for those bytes. Sends to one port follow each other. --adc sets the voltage an analog input
// reads (GPIO 26-29, 0 to 3.3 V: 1.65V reads 2048 of 4095), or the chip's temperature (temp=30C; 27 C unless given),
// from the start or from a time on.
//
// --i2c and --spi put simulated devices on the I2C and SPI buses (devices/buses.mjs): --i2c 0:0x68=mpu6050 an
// MPU-6050 at address 0x68 on I2C0, --i2c 0:0x76=bmp280 a BMP280, --spi 0=74hc595@17 a 74HC595 shift register on
// SPI0 with its latch on GPIO 17, --spi 0=loopback RX wired to TX, --i2c 1:0x27=lcd1602 a 16x2 character LCD on a
// PCF8574 backpack at 0x27 on I2C1 (lcd2004: 20x4), --i2c 1:0x68=ds1307 a DS1307 real-time clock that keeps the PC's
// time (ds1307@2026-11-27T18:00:00 from a date, ds1307@halted a new one). An I2C address without a device does not
// answer.
//
// Booting: the RP2040's boot ROM checks the CRC-32 of the first 256 bytes of flash (the second-stage
// bootloader, boot2), copies them to the top of RAM (0x20041f00) and runs them with lr = 0; boot2 sets up the
// flash and jumps through the vector table at 0x10000100. rp2040js does not include the boot ROM, so this script
// does the ROM's part itself: it checks the CRC the same way (and refuses an image the ROM would refuse), copies
// boot2 and starts the core there, with the stack where the ROM leaves it.
//
// The run stops after the simulated time, or earlier when the program has finished: when the core sleeps (WFI or
// WFE) and nothing is scheduled to wake it. Output lines:
//
//     [    0.123 ms] GPIO25 low                  a pin changed: low or high when driven, else input, input
//                                                pull-up, input pull-down or input bus-keeper
//     [    0.200 ms] UART0 baud 115200           the program set UART0's baud rate
//     [    1.234 ms] UART0: Hello                a line written to UART0 (without its line ending)
//     [  100.000 ms] input: GPIO15 high          an --input with a time changed the pin from outside
//     [    2.000 ms] UART1: Hello                the same as UART0's for UART1 (and "UART1 baud 9600")
//     [  100.000 ms] input: UART0 "led on\r"     a --send starts sending
//     [  100.000 ms] input: GPIO26 1.650 V (2048)  an --adc with a time changed an analog input
//     [   12.000 ms] GPIO25 PWM 1000.00 Hz, duty 25.00% (250.00 us high)
//                                                a PWM output, measured from its edges (rising to rising, rising
//                                                to falling): printed when a period differs from the one before,
//                                                instead of every edge
//     [   15.000 ms] GPIO25 PWM steady low       a PWM output with no edge for 2.5 periods (duty 0, or 1: high)
//     [    2.000 ms] I2C0 0x68 write 75, read 68 a transfer on an I2C bus (devices/buses.mjs lists them all)
//     [    3.000 ms] SPI0 74HC595 Q3 high        an output of a device on an SPI bus changed
//     [   58.230 ms] LCD 0x27: |count 12        |  a row of an LCD (each row a line), when what it shows changes and
//                                                settles; also "LCD 0x27 backlight on" (devices/lcd1602.mjs)
//     [ 1000.000 ms] stopped: <why>              the last line
//
// Exit code: 0 when the program ran (to its end or for the whole time), 1 when the image is not valid, 2 for
// wrong arguments, 3 when the program went wrong: it hit a breakpoint (the runtime's division by zero), got stuck
// in an exception handler (e.g. HardFault), or did something the simulator stops on (e.g. an unaligned access).

import { readFileSync, writeFileSync } from 'node:fs';
import { ConsoleLogger, GPIOPinState, LogLevel, RP2040 } from 'rp2040js';
import { attachBuses, parseBusOption, USAGE as BUS_USAGE } from './devices/buses.mjs';

const UF2_MAGIC_START0 = 0x0a324655;
const UF2_MAGIC_START1 = 0x9e5d5157;
const UF2_MAGIC_END = 0x0ab16f30;
const UF2_FLAG_FAMILY_ID = 0x00002000;
const RP2040_FAMILY_ID = 0xe48bff56;
const FLASH_START = 0x10000000;
const BOOT2_SIZE = 256;
const BOOT2_RAM_ADDRESS = 0x20041f00;
const ROM_STACK_TOP = BOOT2_RAM_ADDRESS; // the ROM's stack ends just below the copy of boot2
const PIN_STATES = {
  [GPIOPinState.Low]: 'low',
  [GPIOPinState.High]: 'high',
  [GPIOPinState.Input]: 'input',
  [GPIOPinState.InputPullUp]: 'input pull-up',
  [GPIOPinState.InputPullDown]: 'input pull-down',
  [GPIOPinState.InputBusKeeper]: 'input bus-keeper',
};

function usage(message) {
  console.error(`run.mjs: ${message}`);
  console.error('Usage: node run.mjs <image.uf2> [--ms <simulated milliseconds>] [--uart0 <output file>] ' +
    `[--input <pin>=<high|low>[@<milliseconds>ms]]... ${BUS_USAGE} ` +
    '[--send <uart>=<text>[@<milliseconds>ms]]... [--adc <pin|temp>=<volts>V|<celsius>C|<reading>[@<milliseconds>ms]]...');
  process.exit(2);
}

function fail(message) {
  console.error(`run.mjs: ${message}`);
  process.exit(1);
}

function parseArguments(args) {
  const options = { image: null, ms: 1000, uart0: null, inputs: [], buses: [], sends: [], adc: [] };
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (SERIAL_ANALOG_OPTIONS.has(arg)) {
      parseSerialAnalogOption(arg, args[++i], options);
    } else if (arg === '--ms' || arg === '--uart0' || arg === '--input') {
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
        const match = /^(\d+)=(high|low)(?:@(\d+(?:\.\d+)?)ms)?$/.exec(value);
        if (match === null || Number(match[1]) > 29) {
          usage(`--input takes a pin from 0 to 29, a level and maybe a time, e.g. 3=high or 3=high@100ms, ` +
            `not '${value}'.`);
        }

        const at = match[3] === undefined ? null : Number(match[3]);
        options.inputs.push({ pin: Number(match[1]), high: match[2] === 'high', at });
      } else {
        options.uart0 = value;
      }
    } else if (arg === '--i2c' || arg === '--spi') {
      // a simulated device on a bus (devices/buses.mjs)
      const device = parseBusOption(arg, args[++i] ?? '');
      if (device.error !== undefined) {
        usage(device.error);
      }

      options.buses.push(device);
    } else if (arg.startsWith('--')) {
      usage(`unknown option '${arg}'.`);
    } else if (options.image === null) {
      options.image = arg;
    } else {
      usage(`only one image, please ('${options.image}' and '${arg}').`);
    }
  }

  if (options.image === null) {
    usage('which .uf2 image?');
  }

  return options;
}

// Writes every block's payload into flash; returns how many blocks there were.
function loadUf2(path, flash) {
  let data;
  try {
    data = readFileSync(path);
  } catch (e) {
    fail(`cannot read '${path}': ${e.message}`);
  }

  if (data.length === 0 || data.length % 512 !== 0) {
    fail(`'${path}' is ${data.length} bytes long; a UF2 file is made of 512-byte blocks.`);
  }

  const view = new DataView(data.buffer, data.byteOffset, data.length);
  const blocks = data.length / 512;
  for (let block = 0; block < blocks; block++) {
    const at = block * 512;
    const word = (offset) => view.getUint32(at + offset, true);
    if (word(0) !== UF2_MAGIC_START0 || word(4) !== UF2_MAGIC_START1 || word(508) !== UF2_MAGIC_END) {
      fail(`block ${block} of '${path}' does not have the UF2 magic numbers.`);
    }

    const flags = word(8);
    const address = word(12);
    const size = word(16);
    if (flags & UF2_FLAG_FAMILY_ID && word(28) !== RP2040_FAMILY_ID) {
      fail(`block ${block} is for family 0x${word(28).toString(16)}, not the RP2040 (0x${RP2040_FAMILY_ID.toString(16)}).`);
    }

    if (size > 476 || address < FLASH_START || address + size > FLASH_START + flash.length) {
      fail(`block ${block} writes ${size} bytes at 0x${address.toString(16)}, which is not in flash.`);
    }

    flash.set(data.subarray(at + 32, at + 32 + size), address - FLASH_START);
  }

  return blocks;
}

// CRC-32/MPEG-2 (polynomial 0x04c11db7, not reflected, starting at 0xffffffff, not inverted): the boot ROM's.
function bootRomCrc(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte << 24;
    for (let bit = 0; bit < 8; bit++) {
      crc = crc & 0x80000000 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
    }
  }

  return crc >>> 0;
}

function milliseconds(nanos) {
  return `[${(nanos / 1e6).toFixed(3).padStart(10)} ms]`;
}

function hex(value) {
  return `0x${(value >>> 0).toString(16).padStart(8, '0')}`;
}

function exceptionName(number) {
  const names = { 2: 'NMI', 3: 'HardFault', 11: 'SVCall', 14: 'PendSV', 15: 'SysTick' };
  return names[number] ?? (number >= 16 ? `IRQ ${number - 16}` : `exception ${number}`);
}

// ---- Serial input, analog inputs and PWM outputs (--send, --adc, UART1's output, PWM measured) -------------------

const SERIAL_ANALOG_OPTIONS = new Set(['--send', '--adc']);
const FUNCTION_PWM = 4; // a GPIO's function select (IO_BANK0 GPIOn_CTRL) when its PWM slice drives it
const ADC_REFERENCE = 3.3; // the Pico's: ADC_VREF is its 3.3 V supply
const ADC_TEMPERATURE = 4; // the ADC input of the temperature sensor: 0.706 V at 27 C, 1.721 mV less per degree

// Parses one --send or --adc into options.sends or options.adc.
function parseSerialAnalogOption(arg, value, options) {
  if (value === undefined) {
    usage(`${arg} needs a value.`);
  }

  if (arg === '--send') {
    const match = /^([01])=(.*?)(?:@(\d+(?:\.\d+)?)ms)?$/s.exec(value);
    if (match === null) {
      usage(`--send takes a UART (0 or 1), text and maybe a time, e.g. "0=led on\\r" or "1=hello\\n@100ms", ` +
        `not '${value}'.`);
    }

    options.sends.push({ port: Number(match[1]), bytes: unescape(match[2]), at: timeOf(match[3]) });
    return;
  }

  const match = /^(26|27|28|29|temp)=(-?\d+(?:\.\d+)?)(V|C)?(?:@(\d+(?:\.\d+)?)ms)?$/.exec(value);
  if (match === null || (match[1] === 'temp') !== (match[3] === 'C')) {
    usage(`--adc takes an analog pin (26 to 29) and volts or a reading (0 to 4095), or temp and degrees Celsius, and ` +
      `maybe a time, e.g. 26=1.65V, 27=2048, temp=30C or 26=0.5V@100ms, not '${value}'.`);
  }

  const number = Number(match[2]);
  const volts = match[1] === 'temp' ? 0.706 - (number - 27) * 0.001721 : match[3] === 'V' ? number : null;
  const reading = volts === null ? number : Math.round((volts / ADC_REFERENCE) * 4096);
  if (!Number.isInteger(reading) || reading < 0 || reading > 4096) {
    usage(`--adc: '${value}' is outside what the ADC reads: 0 to 3.3 V, 0 to 4095.`);
  }

  const shown = volts ?? (reading * ADC_REFERENCE) / 4096;
  options.adc.push({
    input: match[1] === 'temp' ? ADC_TEMPERATURE : Number(match[1]) - 26,
    what: match[1] === 'temp' ? `temperature ${number} C` : `GPIO${match[1]} ${shown.toFixed(3)} V`,
    reading: Math.min(reading, 4095),
    at: timeOf(match[4]),
  });
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

// UART1's output, as UART0's; the sends, byte after byte at the port's speed; the analog inputs' voltages.
function startSerialAnalog(options) {
  let uart1Line = [];
  mcu.uart[1].onByte = (value) => {
    if (value === 0x0a) {
      console.log(`${milliseconds(clock.nanos)} UART1: ${Buffer.from(uart1Line).toString('latin1').replace(/\r$/, '')}`);
      uart1Line = [];
    } else {
      uart1Line.push(value);
    }
  };
  mcu.uart[1].onBaudRateChange = (baud) => console.log(`${milliseconds(clock.nanos)} UART1 baud ${baud}`);

  for (const port of [0, 1]) {
    const queue = options.sends.filter((send) => send.port === port);
    if (queue.length === 0) {
      continue;
    }

    // one send after the other, in the order given, each from its time (or once the one before is done)
    const uart = mcu.uart[port];
    let send = null;
    let next = 0;
    const alarm = clock.createAlarm(() => {
      if (send === null) {
        send = queue.shift();
        next = 0;
        console.log(`${milliseconds(clock.nanos)} input: UART${port} "${escaped(send.bytes)}"`);
      }

      if (next < send.bytes.length) {
        uart.feedByte(send.bytes[next++]);
      }

      const byteNanos = (10 * 1e9) / (uart.baudRate || 115200);
      if (next < send.bytes.length) {
        alarm.schedule(byteNanos);
      } else if (queue.length > 0) {
        send = null;
        alarm.schedule(Math.max(byteNanos, (queue[0].at ?? 0) * 1e6 - clock.nanos));
      }
    });
    alarm.schedule((queue[0].at ?? 0) * 1e6);
  }

  // the temperature sensor reads 27 C unless an --adc says otherwise (rp2040js starts every input at 0 V)
  mcu.adc.channelValues[ADC_TEMPERATURE] = Math.round((0.706 / ADC_REFERENCE) * 4096);
  for (const { input, what, reading, at } of options.adc) {
    if (at === null) {
      mcu.adc.channelValues[input] = reading;
    } else {
      clock.createAlarm(() => {
        console.log(`${milliseconds(clock.nanos)} input: ${what} (${reading})`);
        mcu.adc.channelValues[input] = reading;
      }).schedule(at * 1e6);
    }
  }
}

// What each PWM output did last: its last rising and falling edges, the last line printed, and the alarm that says it
// is steady when no edge comes.
const pwmOutputs = new Map();

// Called for every change of a GPIO's level: true when the pin is a PWM output, whose edges are measured rather than
// printed. A whole period (rising edge to rising edge, high until the falling edge between) is printed when it
// differs from the one before; no edge for 2.5 of the slice's periods is printed as steady low or high.
function pwmEdge(index, state) {
  const pin = mcu.gpio[index];
  let output = pwmOutputs.get(index);
  if (pin.functionSelect !== FUNCTION_PWM) {
    output?.steady.cancel();
    pwmOutputs.delete(index);
    return false;
  }

  const print = (text) => {
    if (text !== output.printed) {
      console.log(`${milliseconds(clock.nanos)} GPIO${index} PWM ${text}`);
      output.printed = text;
    }
  };
  if (output === undefined) {
    output = { rise: null, fall: null, printed: null };
    output.steady = clock.createAlarm(() => {
      print(`steady ${pin.outputValue ? 'high' : 'low'}`);
      output.rise = null; // the next period starts with the next rising edge
      output.fall = null;
    });
    pwmOutputs.set(index, output);
  }

  const now = clock.nanos;
  const high = state === GPIOPinState.High;
  if (high && output.rise !== null && output.fall !== null && output.fall > output.rise) {
    const period = now - output.rise;
    const highTime = output.fall - output.rise;
    print(`${(1e9 / period).toFixed(2)} Hz, duty ${((100 * highTime) / period).toFixed(2)}% ` +
      `(${(highTime / 1000).toFixed(2)} us high)`);
  }

  if (high) {
    output.rise = now;
  } else {
    output.fall = now;
  }

  // the slice's period: its counter's wraps, through its divider
  const slice = mcu.pwm.channels[(index >> 1) & 7];
  output.steady.schedule((2.5 * (slice.timer.top + 1) * slice.timer.prescaler * 1e9) / mcu.clkSys);
  return true;
}

const options = parseArguments(process.argv.slice(2));
const mcu = new RP2040();
// errors only (the peripherals rp2040js does not model warn on every access); an error stops the run
mcu.logger = new ConsoleLogger(LogLevel.Error, true);
const blocks = loadUf2(options.image, mcu.flash);

const boot2 = mcu.flash.subarray(0, BOOT2_SIZE);
const expected = new DataView(boot2.buffer, boot2.byteOffset + 252, 4).getUint32(0, true);
const actual = bootRomCrc(boot2.subarray(0, 252));
if (actual !== expected) {
  fail(`the boot ROM would not start this image: the CRC of boot2 (the first 252 bytes of flash) is ` +
    `0x${actual.toString(16)}, but bytes 252-255 say 0x${expected.toString(16)}.`);
}

console.log(`booting ${options.image} (${blocks} UF2 blocks), running ${options.ms} ms of simulated time`);

const { clock, core } = mcu;
// the pins an --input drives (from its time on): the others follow their pad's pull, as the chip's pads do (a pin with
// a pull-up and nothing attached reads high, with a pull-down or none low; rp2040js leaves an undriven input low)
const driven = new Set();
mcu.gpio.forEach((pin, index) => {
  pin.addListener((state) => {
    if (!driven.has(index) && (state === GPIOPinState.Input || state === GPIOPinState.InputPullUp
      || state === GPIOPinState.InputPullDown)) {
      pin.setInputValue(state === GPIOPinState.InputPullUp);
    }

    if (pwmEdge(index, state)) {
      return; // a PWM output: measured below, not printed edge by edge
    }

    console.log(`${milliseconds(clock.nanos)} GPIO${index} ${PIN_STATES[state] ?? `state ${state}`}`);
  });
});

const uartBytes = [];
let uartLine = [];
mcu.uart[0].onByte = (value) => {
  uartBytes.push(value);
  if (value === 0x0a) {
    const text = Buffer.from(uartLine).toString('latin1').replace(/\r$/, '');
    console.log(`${milliseconds(clock.nanos)} UART0: ${text}`);
    uartLine = [];
  } else {
    uartLine.push(value);
  }
};
mcu.uart[0].onBaudRateChange = (baud) => console.log(`${milliseconds(clock.nanos)} UART0 baud ${baud}`);

// the simulated devices on the I2C and SPI buses (--i2c, --spi), and what the buses do
const finishBuses = attachBuses(mcu, options.buses, (text) => console.log(`${milliseconds(clock.nanos)} ${text}`));

// the levels from the start, then the changes at their times, as alarms of the simulated clock: a core asleep until
// one of them wakes up for it, as the chip would
for (const { pin, high, at } of options.inputs) {
  if (at === null) {
    driven.add(pin);
    mcu.gpio[pin].setInputValue(high);
  } else {
    clock.createAlarm(() => {
      console.log(`${milliseconds(clock.nanos)} input: GPIO${pin} ${high ? 'high' : 'low'}`);
      driven.add(pin);
      mcu.gpio[pin].setInputValue(high);
    }).schedule(at * 1e6);
  }
}

startSerialAnalog(options);

// the boot ROM's part: boot2 copied to the top of RAM and run from there, as if called with lr = 0
mcu.sram.set(boot2, BOOT2_RAM_ADDRESS - 0x20000000);
core.SP = ROM_STACK_TOP;
core.LR = 0;
core.PC = BOOT2_RAM_ADDRESS;

let breakpoint = null;
mcu.onBreak = (code) => {
  breakpoint = code;
};

const end = options.ms * 1e6;
let reason = `${options.ms} ms of simulated time have passed`;
let exitCode = 0;
while (clock.nanos < end) {
  if (core.waiting) {
    if (clock.nextAlarm === null) {
      if (core.IPSR === 0) {
        reason = 'the program has finished: the core sleeps and nothing is scheduled to wake it';
      } else {
        reason = `the program is stuck in the ${exceptionName(core.IPSR)} handler: the core sleeps there and ` +
          'nothing is scheduled to wake it';
        exitCode = 3;
      }

      break;
    }

    clock.tick(Math.min(clock.nanosToNextAlarm, end - clock.nanos));
  } else {
    const pc = core.PC;
    try {
      const cycles = core.executeInstruction();
      clock.tick((cycles * 1e9) / mcu.clkSys);
    } catch (e) {
      reason = `the simulator stopped at pc ${hex(pc)}: ${e.message}`;
      exitCode = 3;
      break;
    }

    if (breakpoint !== null) {
      // the runtime stops this way on a division by zero
      reason = `the program hit a breakpoint (bkpt #${breakpoint}) at pc ${hex(pc)}, e.g. a division by zero`;
      exitCode = 3;
      break;
    }
  }
}

// what the devices have not printed yet (an LCD that has not settled)
finishBuses();

if (uartLine.length > 0) {
  console.log(`${milliseconds(clock.nanos)} UART0: ${Buffer.from(uartLine).toString('latin1')} (no line ending)`);
}

if (options.uart0 !== null) {
  writeFileSync(options.uart0, Buffer.from(uartBytes));
}

console.log(`${milliseconds(clock.nanos)} stopped: ${reason}`);
process.exitCode = exitCode;
