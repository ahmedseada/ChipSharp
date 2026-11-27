// The simulated devices on the RP2040's I2C and SPI buses, for run.mjs's --i2c and --spi options, through rp2040js's
// own hooks (RPI2C's onStart, onConnect, onWriteByte, onReadByte and onStop; RPSPI's onTransmit). The devices, the
// options and what an I2C bus prints are catalog.mjs's, which tools/avr-sim shares:
//
//     --i2c <bus>:<address>=<device>     a device at an address (0x68 or 104) on I2C0 or I2C1:
//                                          mpu6050   accelerometer and gyroscope (mpu6050.mjs)
//                                          bmp280    pressure and temperature (bmp280.mjs)
//                                          lcd1602   a 16x2 character LCD on a PCF8574 backpack (lcd1602.mjs)
//                                          lcd2004   the same, 20x4
//                                          stuck     a device that holds the clock low for ever once addressed
//                                          ds1307[@<date>]  a real-time clock (ds1307.mjs), from the PC's time, or
//                                                    from a date (2026-11-27T18:00:00), or halted (@halted)
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
import { busUsage, I2cBus, makeI2cDevices, makeSpiDevices, parseI2cOption, parseSpiOption } from './catalog.mjs';

const BUSES = [0, 1];

export const USAGE = busUsage(BUSES);

// The device an --i2c or --spi value names, e.g. "0:0x68=mpu6050" or "0=74hc595@17"; a message when it is not valid.
export function parseBusOption(option, value) {
  if (option === '--i2c') {
    return parseI2cOption(value, BUSES);
  }

  return parseSpiOption(value, BUSES, (text) => (/^\d+$/.test(text) && Number(text) <= 29 ? Number(text) : null), 17);
}

// Answers I2C<bus>'s transfers with the devices at their addresses, and prints each transfer.
function attachI2c(mcu, bus, devices, log) {
  const controller = mcu.i2c[bus];
  const i2c = new I2cBus(`I2C${bus}`, devices, log);

  controller.onStart = () => {
    i2c.start();
    controller.completeStart();
  };

  controller.onConnect = (target, mode) => {
    const hertz = Math.round(mcu.clkSys / (controller.sclHighPeriod + controller.sclLowPeriod));
    const answered = i2c.connect(target, mode === I2CMode.Read, hertz);
    if (answered !== null) {
      controller.completeConnect(answered);
    }
  };

  controller.onWriteByte = (value) => controller.completeWrite(i2c.write(value));
  controller.onReadByte = () => controller.completeRead(i2c.read());
  controller.onStop = () => {
    i2c.stop();
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
  for (const bus of BUSES) {
    const devices = makeI2cDevices(options, bus, mcu.clock, log);
    all.push(...devices.values());
    attachI2c(mcu, bus, devices, log);

    const spiDevices = makeSpiDevices(options, bus, (text) => log(`SPI${bus} ${text}`), (pin, latched) => {
      mcu.gpio[pin].addListener((state, previous) => {
        if (state === GPIOPinState.High && previous !== GPIOPinState.High) {
          latched();
        }
      });
    });
    attachSpi(mcu, bus, spiDevices, log);
  }

  return () => all.forEach((device) => device.finish?.());
}
