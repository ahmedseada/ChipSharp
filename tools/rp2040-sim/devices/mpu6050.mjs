// The InvenSense MPU-6050 accelerometer and gyroscope, at I2C address 0x68 (0x69 with AD0 high), as its register map
// (RM-MPU-6000A) describes it: WHO_AM_I (0x75) is 0x68; it starts asleep (PWR_MGMT_1, 0x6B, is 0x40), with its
// measurements reading 0 until the program wakes it (writes 0 there); ACCEL_CONFIG (0x1C) and GYRO_CONFIG (0x1B)
// choose the ranges; the measurements are big-endian 16-bit values from 0x3B on: acceleration X, Y, Z, temperature,
// rotation X, Y, Z.
//
// It measures a board lying still but tilted: 0.5 g, -0.25 g and 1 g of acceleration, a rotation of 10, -5 and
// 0 degrees per second, and 24 degrees Celsius. In the default ranges (2 g, 250 degrees per second) that is 8192,
// -4096, 16384; -4260 (the datasheet's temperature is raw / 340 + 36.53); 1310, -655, 0.

import { RegisterDevice, setBigEndian } from './registers.mjs';

const GYRO_CONFIG = 0x1b;
const ACCEL_CONFIG = 0x1c;
const ACCEL_XOUT_H = 0x3b;
const GYRO_ZOUT_L = 0x48;
const PWR_MGMT_1 = 0x6b;
const WHO_AM_I = 0x75;
const SLEEP = 0x40;
const DEVICE_RESET = 0x80;

export const ACCELERATION = [0.5, -0.25, 1.0]; // g
export const ROTATION = [10, -5, 0];            // degrees per second
export const TEMPERATURE = 24;                  // degrees Celsius

export class Mpu6050 extends RegisterDevice {
  constructor() {
    super('MPU-6050');
    this.reset();
  }

  reset() {
    this.registers.fill(0);
    this.registers[PWR_MGMT_1] = SLEEP;
    this.registers[WHO_AM_I] = 0x68;
  }

  written(register, value) {
    if (register === PWR_MGMT_1 && value & DEVICE_RESET) {
      this.reset();
    }
  }

  value(register) {
    if (register < ACCEL_XOUT_H || register > GYRO_ZOUT_L) {
      return this.registers[register];
    }

    // asleep, it has measured nothing
    if (this.registers[PWR_MGMT_1] & SLEEP) {
      return 0;
    }

    const measured = new Uint8Array(14);
    const accelerationRange = (this.registers[ACCEL_CONFIG] >> 3) & 3; // 2, 4, 8 or 16 g
    const rotationRange = (this.registers[GYRO_CONFIG] >> 3) & 3;       // 250, 500, 1000 or 2000 degrees/s
    ACCELERATION.forEach((g, axis) =>
      setBigEndian(measured, 2 * axis, Math.round((g * 16384) / (1 << accelerationRange))));
    setBigEndian(measured, 6, Math.round((TEMPERATURE - 36.53) * 340));
    ROTATION.forEach((dps, axis) =>
      setBigEndian(measured, 8 + 2 * axis, Math.round((dps * 131) / (1 << rotationRange))));
    return measured[register - ACCEL_XOUT_H];
  }
}
