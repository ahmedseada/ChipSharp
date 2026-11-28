// A potentiometer's reading and voltage on a 16x2 character LCD, on the blue STM8S103F3P6 board, with no heap:
//
//   +----------------+
//   |Knob on PD3     |
//   |0512 = 1.650 V  |      the reading, 0 to 1023, and its voltage, 0 to 3.3 V
//   +----------------+
//
// The board reads the knob on an analog input (Adc.Read: AIN4, on PD3, in 10 bits against the board's 3.3 V) ten
// times a second, and writes the row again when the reading has moved by 2 or more. The LCD is an HD44780 behind a
// PCF8574 I2C backpack at address 0x27, on the chip's I2C bus: SDA on PB5, SCL on PB4. PB5 is also the board's LED,
// which so shows the bus's traffic: it glows while the LCD is written, and the program cannot blink it.
//
// Lcd comes from ChipSharp.Components, unchanged from the Pico's and the Uno's. It and the I2C bus take most of the
// 8 KB of flash, so the numbers are written digit by digit with Lcd.Write of a char, in fixed places: Lcd.Write of a
// number would take the program past the 8 KB beside the ADC. Fixed widths need no clearing either: "0099" leaves
// nothing of a "1023" before it.
//
// Wiring (README.md has it pin by pin): the backpack's SDA to PB5 (B5 on the board), SCL to PB4 (B4), VCC to 5V and
// GND to GND; the potentiometer's ends to 3V3 and GND and its wiper to PD3 (D3). The backpack's own pull-up resistors
// take SDA and SCL to 5 V, which PB4 and PB5, the chip's true open-drain pins, stand (the STM8's other pins do not).
// Without an LCD, Lcd.Open returns false and the Lcd calls do nothing.
//
// Build it with ChipSharp for the target "stm8s103-board", then flash the .hex with an ST-LINK V2 (see README.md):
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\Stm8Lcd.dll -t stm8s103-board
//   chipsharp flash bin\Release\net10.0\Stm8Lcd.dll -t stm8s103-board
// Simulate (an LCD at 0x27, the knob at 0 V, then at half way after 400 ms):
//   chipsim stm8s103 chipsharp-output\stm8s103-board\Stm8Lcd.hex --ms 1000 --i2c 0:0x27=lcd1602 --adc PD3=0V
//     --adc PD3=1.65V@400ms

using ChipSharp.Components;
using ChipSharp.Hardware;

const int Bus = 0;              // the chip's I2C bus: SDA PB5, SCL PB4
const int Knob = 27;            // PD3 (AIN4), the potentiometer's wiper: 0 to 3.3 V

Adc.Input(Knob);
I2c.Open(Bus, sda: 13, scl: 12, frequency: 100_000);
Lcd.Open(Bus, 0x27, columns: 16, rows: 2);  // false when no LCD answers: the Lcd calls then do nothing
Lcd.Line(0, "Knob on PD3");
Lcd.Line(1, "0000 = 0.000 V");
var shown = -100;
while (true)
{
    var reading = Adc.Read(Knob);                   // 0 to 1023
    if (reading >= shown + 2 || reading <= shown - 2)
    {
        var millivolts = reading * 3300 / 1024;     // as Adc.Volts has it: 3.3 V is 1024
        Screen.Digits(0, reading, 4);
        Screen.Digits(7, millivolts / 1000, 1);
        Screen.Digits(9, millivolts, 3);            // the last three digits: the millivolts after the point
        shown = reading;
    }

    Timing.DelayMilliseconds(100);
}

static class Screen
{
    // Writes the last count digits of value (0 or more) on the LCD's second row, from column on, with leading zeros.
    public static void Digits(int column, int value, int count)
    {
        for (var at = column + count - 1; at >= column; at--)
        {
            Lcd.Write(at, 1, (char)('0' + value % 10));
            value /= 10;
        }
    }
}
