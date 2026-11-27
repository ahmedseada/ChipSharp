// An I2C device made of 8-bit registers, as most sensors are: a write's first byte chooses the register, the bytes
// after it are written from there on, and a read reads from the chosen register on, one register further each byte.
// A device of this kind extends it and gives its registers' values (read), and what writing them does (written).

export class RegisterDevice {
  constructor(name) {
    this.name = name;
    this.registers = new Uint8Array(256);
    this.pointer = 0;
    this.chosen = false; // whether this write has chosen its register yet
  }

  // A start (or a repeated start) addressed to the device: true to answer it.
  start(read) {
    if (!read) {
      this.chosen = false;
    }

    return true;
  }

  // A byte the controller writes: true to take it.
  write(value) {
    if (!this.chosen) {
      this.pointer = value;
      this.chosen = true;
    } else {
      this.registers[this.pointer] = value;
      this.written(this.pointer, value);
      this.pointer = (this.pointer + 1) & 0xff;
    }

    return true;
  }

  // The byte the device sends when the controller reads one.
  read() {
    const value = this.value(this.pointer);
    this.pointer = (this.pointer + 1) & 0xff;
    return value;
  }

  stop() {}

  // A register's value, as a read sees it: what it holds, unless the device works it out.
  value(register) {
    return this.registers[register];
  }

  written(register, value) {}
}

// Puts a 16-bit value into two registers, most significant byte first (big-endian), as the MPU-6050 has them.
export function setBigEndian(registers, at, value) {
  registers[at] = (value >> 8) & 0xff;
  registers[at + 1] = value & 0xff;
}

// Puts a 16-bit value into two registers, least significant byte first (little-endian), as the BMP280's calibration.
export function setLittleEndian(registers, at, value) {
  registers[at] = value & 0xff;
  registers[at + 1] = (value >> 8) & 0xff;
}
