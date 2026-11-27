// The simulated devices on the RP2040's I2C and SPI buses, for run.mjs's --i2c and --spi options, through rp2040js's
// own hooks (RPI2C's onStart, onConnect, onWriteByte, onReadByte and onStop; RPSPI's onTransmit).
//
//     --i2c <bus>:<address>=<device>     a device at an address (0x68 or 104) on I2C0 or I2C1:
//                                          mpu6050   accelerometer and gyroscope (mpu6050.mjs)
//                                          bmp280    pressure and temperature (bmp280.mjs)
//                                          lcd1602   a 16x2 character LCD on a PCF8574 backpack (lcd1602.mjs)
//                                          lcd2004   the same, 20x4
//                                          stuck     a device that holds the clock low for ever once addressed
//     --spi <bus>=<device>[@<pin>]       a device on SPI0 or SPI1:
//                                          74hc595@<latch pin>   a shift register (shift-register.mjs)
//                                          loopback[@<chip select pin>]   RX wired to TX; with a pin, only while
//                                          the program holds that pin low
//
// Every I2C address without a device leaves the controller without an answer (a NACK), as an empty bus does. What
// the buses do is printed, like the pin changes, each line with its time:
//
//     I2C0 100000 Hz                     the clock the program set, when a transfer first uses it
//     I2C0 0x68 write 75, read 68        a transfer, from its start to its stop: the bytes each way, in hex, each
//                                        part after a repeated start after a comma
//     I2C0 0x23 write: no answer         nothing answered the address
//     I2C0 0x68 write 6b 00 (refused)    the device refused a byte
//     SPI0 mode 0, 992063 Hz             the mode and clock the program set, when a transfer first uses them
//     SPI0 74HC595 Q3 high               a shift register's output changed (its latch went high)
//     LCD 0x27: |count 12        |       a row of an LCD, each row on a line, when what it shows changes and settles
//     LCD 0x27 backlight on              (lcd1602.mjs lists them all)

import { GPIOPinState, I2CMode } from 'rp2040js';
import { Bmp280 } from './bmp280.mjs';
import { Lcd } from './lcd1602.mjs';
import { Mpu6050 } from './mpu6050.mjs';
import { Loopback, ShiftRegister } from './shift-register.mjs';

export const USAGE = '[--i2c <bus>:<address>=<mpu6050|bmp280|lcd1602|lcd2004|stuck>]... ' +
  '[--spi <bus>=<74hc595@<pin>|loopback[@<pin>]>]...';

// A device that answers its address, then never lets the clock go: every transfer on its bus waits for it from then on.
class Stuck {
  constructor() {
    this.name = 'stuck';
    this.holdsClock = true;
  }
}

// Each device made for its address, with the simulation's clock and the log (the LCDs print what they show).
const I2C_DEVICES = {
  mpu6050: () => new Mpu6050(),
  bmp280: () => new Bmp280(),
  lcd1602: (address, clock, log) => new Lcd(address, 16, 2, clock, log),
  lcd2004: (address, clock, log) => new Lcd(address, 20, 4, clock, log),
  stuck: () => new Stuck(),
};

// The device an --i2c or --spi value names, e.g. "0:0x68=mpu6050" or "0=74hc595@17"; a message when it is not valid.
export function parseBusOption(option, value) {
  if (option === '--i2c') {
    const match = /^([01]):(0x[0-9a-fA-F]+|\d+)=([a-z0-9]+)$/.exec(value);
    const address = match === null ? NaN : Number(match[2]);
    if (match === null || !(address >= 0 && address <= 0x7f) || !(match[3] in I2C_DEVICES)) {
      return { error: `--i2c takes a bus (0 or 1), an address from 0 to 0x7f and a device ` +
        `(${Object.keys(I2C_DEVICES).join(', ')}), e.g. 0:0x68=mpu6050, not '${value}'.` };
    }

    return { kind: 'i2c', bus: Number(match[1]), address, device: match[3] };
  }

  const match = /^([01])=(74hc595|loopback)(?:@(\d+))?$/.exec(value);
  const pin = match === null || match[3] === undefined ? null : Number(match[3]);
  if (match === null || (pin !== null && pin > 29) || (match[2] === '74hc595' && pin === null)) {
    return { error: `--spi takes a bus (0 or 1) and a device: 74hc595@<its latch pin> or loopback, maybe ` +
      `@<its chip select pin>, e.g. 0=74hc595@17, not '${value}'.` };
  }

  return { kind: 'spi', bus: Number(match[1]), device: match[2], pin };
}

function hex(value) {
  return value.toString(16).padStart(2, '0');
}

// Answers I2C<bus>'s transfers with the devices at their addresses, and prints each transfer.
function attachI2c(mcu, bus, devices, log) {
  const controller = mcu.i2c[bus];
  let parts = null;    // the transfer so far: each part from a (repeated) start
  let address = 0;
  let device = null;
  let frequency = null;

  controller.onStart = () => {
    parts ??= [];
    controller.completeStart();
  };

  controller.onConnect = (target, mode) => {
    const hertz = Math.round(mcu.clkSys / (controller.sclHighPeriod + controller.sclLowPeriod));
    if (hertz !== frequency) {
      log(`I2C${bus} ${hertz} Hz`);
      frequency = hertz;
    }

    const read = mode === I2CMode.Read;
    address = target;
    device = devices.get(target) ?? null;
    if (device?.holdsClock) {
      log(`I2C${bus} 0x${hex(target)} ${read ? 'read' : 'write'}: the ${device.name} device holds the clock low`);
      return; // and never lets go
    }

    const answered = device !== null && device.start(read);
    parts.push({ read, answered, bytes: [], refused: false });
    controller.completeConnect(answered);
  };

  controller.onWriteByte = (value) => {
    const part = parts.at(-1);
    const taken = device.write(value);
    part.bytes.push(value);
    part.refused = !taken;
    controller.completeWrite(taken);
  };

  controller.onReadByte = () => {
    const value = device.read() & 0xff;
    parts.at(-1).bytes.push(value);
    controller.completeRead(value);
  };

  controller.onStop = () => {
    device?.stop?.();
    if (parts !== null && parts.length > 0) {
      const text = parts.map((part) => (part.read ? 'read' : 'write') + (!part.answered ? ': no answer'
        : part.bytes.map((value) => ' ' + hex(value)).join('') + (part.refused ? ' (refused)' : '')));
      log(`I2C${bus} 0x${hex(address)} ${text.join(', ')}`);
    }

    parts = null;
    device = null;
    controller.completeStop();
  };
}

// Hands each byte SPI<bus> sends to its devices, and the byte the device selected sends back to the controller.
function attachSpi(mcu, bus, devices, log) {
  const controller = mcu.spi[bus];
  let settings = null;
  controller.onTransmit = (value) => {
    // SSPCR0: SPO, the clock's level when idle (bit 6), and SPH, the edge data is read on (bit 7); SCR, the divisor
    // less 1 (bits 15-8), after the prescale CPSDVSR
    const mode = (controller.control0 & 0x40 ? 2 : 0) + (controller.control0 & 0x80 ? 1 : 0);
    const divisor = controller.clockDivisor * (1 + ((controller.control0 >> 8) & 0xff));
    const now = `mode ${mode}, ${divisor === 0 ? 0 : Math.round(mcu.clkPeri / divisor)} Hz`;
    if (now !== settings) {
      log(`SPI${bus} ${now}`);
      settings = now;
    }

    let answer = 0;
    for (const { device, select } of devices) {
      if (select === null || mcu.gpio[select].value === GPIOPinState.Low) {
        answer = device.transfer(value, mode) ?? answer;
      }
    }

    controller.completeTransmit(answer);
  };
}

// Puts the devices the options name on their buses; log prints a line with the simulated time. Returns what to call
// when the run ends: the devices print what they have not printed yet (an LCD that has not settled).
export function attachBuses(mcu, options, log) {
  const all = [];
  for (const bus of [0, 1]) {
    const devices = new Map();
    for (const option of options.filter((o) => o.kind === 'i2c' && o.bus === bus)) {
      const device = I2C_DEVICES[option.device](option.address, mcu.clock, log);
      devices.set(option.address, device);
      all.push(device);
    }

    attachI2c(mcu, bus, devices, log);

    const spiDevices = [];
    for (const option of options.filter((o) => o.kind === 'spi' && o.bus === bus)) {
      if (option.device === 'loopback') {
        spiDevices.push({ device: new Loopback(), select: option.pin });
      } else {
        // the shift register takes every byte; its latch pin shows them
        const register = new ShiftRegister(option.pin, (text) => log(`SPI${bus} ${text}`));
        mcu.gpio[option.pin].addListener((state, previous) => {
          if (state === GPIOPinState.High && previous !== GPIOPinState.High) {
            register.latched();
          }
        });
        spiDevices.push({ device: register, select: null });
      }
    }

    attachSpi(mcu, bus, spiDevices, log);
  }

  return () => all.forEach((device) => device.finish?.());
}
