// The 74HC595 shift register on an SPI bus: TX to its serial input (DS, also called SER), SCK to its shift clock
// (SHCP, SRCLK), and a pin of the program's to its latch (STCP, RCLK). Each bit the bus sends moves into it on the
// clock's rising edge, so after a byte, sent most significant bit first, bit n of the byte is at output Qn; its eight
// outputs show what it holds when the latch pin goes from low to high. Output enable (OE) low and reset (MR) high: its
// outputs always drive.
//
// The bits move in on the clock's rising edge, so SPI modes 0 and 3 suit it; modes 1 and 2 change the data on that
// very edge, which a real one would read wrong: this one says so (once) and moves the bits in anyway.

export class ShiftRegister {
  constructor(latch, report) {
    this.name = '74HC595';
    this.latch = latch;
    this.report = report;
    this.shifted = 0; // the shift register
    this.outputs = 0; // the storage register, which drives Q0 to Q7
    this.warned = false;
  }

  // One byte from the bus; nothing comes back (its Q7S, the serial output, is not wired to RX).
  transfer(value, mode) {
    if ((mode === 1 || mode === 2) && !this.warned) {
      this.report(`74HC595: SPI mode ${mode} changes the data on the clock's rising edge, where the 74HC595 reads it; ` +
        'use mode 0 or 3');
      this.warned = true;
    }

    this.shifted = value & 0xff;
    return null;
  }

  // The latch pin went high: the outputs take what the shift register holds, and each that changes is reported.
  latched() {
    for (let bit = 0; bit < 8; bit++) {
      const mask = 1 << bit;
      if ((this.outputs ^ this.shifted) & mask) {
        this.report(`74HC595 Q${bit} ${this.shifted & mask ? 'high' : 'low'}`);
      }
    }

    this.outputs = this.shifted;
  }
}

// RX wired to TX: every byte comes back as it was sent, while its chip select (when it has one) is low.
export class Loopback {
  constructor() {
    this.name = 'loopback';
  }

  transfer(value) {
    return value;
  }
}
