// The Bosch BMP280 pressure and temperature sensor, at I2C address 0x76 (0x77 with SDO high), as its datasheet
// (BST-BMP280-DS001) describes it: chip id (0xD0) 0x58; reset (0xE0) on 0xB6; status (0xF3), ctrl_meas (0xF4) and
// config (0xF5); the calibration words dig_T1 to dig_P9, little-endian, from 0x88; the raw pressure (0xF7 to 0xF9)
// and temperature (0xFA to 0xFC), 20 bits each, most significant byte first.
//
// Its calibration and its measurements are the datasheet's own example (section 3.12): adc_T = 519888 and
// adc_P = 415148, 25.08 degrees Celsius and 100653.27 Pa by its floating-point formulas. Its integer ones (section 8.2)
// give t_fine = 128422 and T = 2508 (hundredths of a degree), and a pressure of 100656 Pa (32-bit) and 25767233 / 256
// = 100653.25 Pa (64-bit). A measurement it has not made (in sleep mode, as after a reset, or one skipped by an
// oversampling of 0) reads 0x80000, as on the chip.

import { RegisterDevice, setLittleEndian } from './registers.mjs';

const CALIBRATION = 0x88;
const CHIP_ID = 0xd0;
const RESET = 0xe0;
const STATUS = 0xf3;
const CTRL_MEAS = 0xf4;
const PRESS_MSB = 0xf7;
const TEMP_XLSB = 0xfc;
const RESET_WORD = 0xb6;
const NOT_MEASURED = 0x80000;

// dig_T1, dig_T2, dig_T3, dig_P1, ..., dig_P9 (T1 and P1 unsigned, the others signed)
export const CALIBRATION_WORDS = [27504, 26435, -1000, 36477, -10685, 3024, 2855, 140, -7, 15500, -14600, 6000];
export const ADC_T = 519888;
export const ADC_P = 415148;

export class Bmp280 extends RegisterDevice {
  constructor() {
    super('BMP280');
    this.reset();
  }

  reset() {
    this.registers.fill(0);
    this.registers[CHIP_ID] = 0x58;
    CALIBRATION_WORDS.forEach((word, index) => setLittleEndian(this.registers, CALIBRATION + 2 * index, word & 0xffff));
  }

  written(register, value) {
    if (register === RESET && value === RESET_WORD) {
      this.reset();
    }
  }

  value(register) {
    if (register === RESET) {
      return 0; // reads as 0
    }

    if (register === STATUS) {
      return 0; // never measuring, never copying its calibration: a measurement is there at once
    }

    if (register < PRESS_MSB || register > TEMP_XLSB) {
      return this.registers[register];
    }

    // ctrl_meas: osrs_t (bits 7-5), osrs_p (4-2), mode (1-0; 0 is sleep)
    const control = this.registers[CTRL_MEAS];
    const measuring = (control & 3) !== 0;
    const pressure = measuring && (control >> 2) & 7 ? ADC_P : NOT_MEASURED;
    const temperature = measuring && (control >> 5) & 7 ? ADC_T : NOT_MEASURED;
    const raw = register < 0xfa ? pressure : temperature;
    switch ((register - PRESS_MSB) % 3) {
      case 0:
        return (raw >> 12) & 0xff;
      case 1:
        return (raw >> 4) & 0xff;
      default:
        return (raw << 4) & 0xf0;
    }
  }
}
