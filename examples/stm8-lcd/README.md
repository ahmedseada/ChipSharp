# stm8-lcd

A potentiometer's reading and its voltage on a 16x2 character LCD, on the blue STM8S103F3P6 board:

```
+----------------+
|Knob on PD3     |
|0512 = 1.650 V  |
+----------------+
```

The board reads the knob ten times a second on an analog input (`Adc.Read`: AIN4 on PD3, 0 to 1023 against its
3.3 V) and writes the row again when the reading has moved by 2 or more. The LCD is an HD44780 behind a PCF8574 I2C
backpack at 0x27, driven by `Lcd` from ChipSharp.Components, unchanged from the Pico's and the Uno's. No heap.

`Lcd` and the I2C bus take most of the 8 KB of flash: the numbers are written a digit at a time (`Lcd.Write` of a
char) in fixed places, because `Lcd.Write` of a number, beside the ADC, takes the program just past the 8 KB. Fixed widths need no clearing
either: `0099` leaves nothing of a `1023` before it.

## Wiring

The board's pins are labelled on it without the P: B5, B4, D3, ...

| Board | To |
|---|---|
| PB5 (SDA) | the backpack's SDA |
| PB4 (SCL) | the backpack's SCL |
| 5V | the backpack's VCC (a 1602 needs 5 V for its contrast) |
| GND | the backpack's GND and the potentiometer's one end |
| 3V3 | the potentiometer's other end |
| PD3 (AIN4) | the potentiometer's wiper |

- **PB5 is the board's LED too.** It is the chip's only SDA pin, so the LED, wired from 3.3 V to PB5, lights whenever
  the bus pulls SDA low: it glows while the LCD is written, and the program cannot use it as an LED.
- **The pull-ups.** The bus needs pull-up resistors on SDA and SCL; the backpack has them, to its VCC (5 V). PB4 and
  PB5 are true open-drain pins, which take up to 6.5 V, so they stand that; no other pin of the chip does, so keep the
  5 V on the bus alone. A 3.3 V display works on 3V3 as well.
- Without an LCD, `Lcd.Open` returns false and the `Lcd` calls do nothing.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Stm8Lcd.dll -t stm8s103-board
chipsharp flash bin\Release\net10.0\Stm8Lcd.dll -t stm8s103-board
```

It takes **7.5 KB** of the 8 KB of flash and **45 bytes** of the 1 KB of RAM; `flash` writes it through an ST-LINK V2,
as `stm8-blink`'s README says.

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`), with a simulated LCD at 0x27,
the knob at 0 V and at half way from 400 ms:

```
chipsim stm8s103 chipsharp-output\stm8s103-board\Stm8Lcd.hex --ms 1000 --i2c 0:0x27=lcd1602 --adc PD3=0V --adc PD3=1.65V@400ms
```

It prints the I2C transfers and, once they have settled, the LCD's rows: `LCD 0x27: |0000 = 0.000 V  |` at 104 ms
(`Lcd.Open` waits 50 ms for the display to power up), `LCD 0x27: |0512 = 1.650 V  |` at 424 ms.
