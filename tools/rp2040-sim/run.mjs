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
//                                              [--input <pin>=<high|low>]...
//
// --input drives a pin from outside, as a button or another chip would; the program reads it with Gpio.Read.
// (rp2040js reads only these outside levels: a pin the program drives itself reads as low.)
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
//     [ 1000.000 ms] stopped: <why>              the last line
//
// Exit code: 0 when the program ran (to its end or for the whole time), 1 when the image is not valid, 2 for
// wrong arguments, 3 when the program went wrong: it hit a breakpoint (the runtime's division by zero), got stuck
// in an exception handler (e.g. HardFault), or did something the simulator stops on (e.g. an unaligned access).

import { readFileSync, writeFileSync } from 'node:fs';
import { ConsoleLogger, GPIOPinState, LogLevel, RP2040 } from 'rp2040js';

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
    '[--input <pin>=<high|low>]...');
  process.exit(2);
}

function fail(message) {
  console.error(`run.mjs: ${message}`);
  process.exit(1);
}

function parseArguments(args) {
  const options = { image: null, ms: 1000, uart0: null, inputs: [] };
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === '--ms' || arg === '--uart0' || arg === '--input') {
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
        const match = /^(\d+)=(high|low)$/.exec(value);
        if (match === null || Number(match[1]) > 29) {
          usage(`--input takes a pin from 0 to 29 and a level, e.g. 3=high, not '${value}'.`);
        }

        options.inputs.push({ pin: Number(match[1]), high: match[2] === 'high' });
      } else {
        options.uart0 = value;
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
mcu.gpio.forEach((pin, index) => {
  pin.addListener((state) => {
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

for (const { pin, high } of options.inputs) {
  mcu.gpio[pin].setInputValue(high);
}

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

if (uartLine.length > 0) {
  console.log(`${milliseconds(clock.nanos)} UART0: ${Buffer.from(uartLine).toString('latin1')} (no line ending)`);
}

if (options.uart0 !== null) {
  writeFileSync(options.uart0, Buffer.from(uartBytes));
}

console.log(`${milliseconds(clock.nanos)} stopped: ${reason}`);
process.exitCode = exitCode;
