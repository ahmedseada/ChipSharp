# esp32c5-lcd-traffic-light

The owner's two-lane traffic light with an LCD (`PicoTrafficLightLcd`, in the final M3 style: a `List<Lane>`, `readonly record struct`s, a `Signal` class, a `Screen` for the LCD, a `Heartbeat` on the timer event, a `Button` on the pin event, a `Crossing` with an iterator over a `static readonly Step[]`, and a `SevenSegment` for the turn countdown) **as it is on the Pico: only the pin numbers, the I2C bus (0 here, 1 there) and the header comment differ.** It prints the same console lines and puts the same text on the LCD:

```
+----------------+
|Road 1   turn 8s|      the lane, and the seconds left in its whole turn
|green         4s|      the step, and its seconds left ("switching" in the pause before the next lane)
+----------------+
```

One set of three lights shows the active lane's light, one 7-segment display counts the whole turn down, a button cuts a green short, and an LED blinks once a second from the timer interrupt. The console (USB) says `road : 1`, `    green 4`, ... and `lane : 1 interrupted, move to lane 2` when the button cuts a green.

## Wiring

14 pins are needed and the board leaves only 10 plain ones (0, 1, 4, 5, 6, 8, 9, 10, 23, 24), so the pins were chosen so that nothing wired to them can change how the chip boots:

| Board | To | Why this pin |
|---|---|---|
| `6`, `8`, `9` | red, yellow and green LED anodes, each through 220 ohm | plain pins |
| `10` | one leg of a push button, the other to `3V3` (`Pull.Down`: no resistor) | a plain pin: a button held at reset on a strapping pin could pick another boot mode |
| `4` (SDA), `5` (SCL) | the LCD backpack's SDA and SCL (address 0x27, I2C bus 0), its VCC to `5V` (or `3V3` if the module works on it), its GND to `GND` | the JTAG pins MTCK and MTDO, free while JTAG is on USB, its default |
| `23` | an LED's anode through 220 ohm: the heartbeat | a plain pin (the board's own LED is an RGB LED on GPIO 27, not a pin to toggle) |
| `0`, `1`, `2`, `3`, `11`, `12`, `24` | 7-segment segments A, B, C, D, E, F, G (common cathode to `GND`), each through 220 ohm | 0 and 1 are the 32 kHz crystal pins (no crystal is fitted); 2 and 3 are strapping pins (MTMS, MTDI) that a segment to GND leaves low, as at reset; 11 and 12 are the UART bridge's pins, which this program does not use |
| `GND` | every LED's cathode, the display's common cathode | |
| the USB-C port marked **USB** | the PC, for the console lines | |

Left alone: 7, 25, 26, 27 and 28 (strapping pins; 28 is the Boot button, 27 the RGB LED). Because 11 and 12 are the UART bridge's pins, flash through the **USB** port, not the **UART** one. A real LED needs a resistor in series; Wokwi does not.

The example uses the portable API alone (`Pin`, `Timing`, `I2c`, `[Interrupt]`, `Console`, `Lcd`, `SevenSegment`): for a Pico, change the pin numbers and the bus and build with `-t pico`.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Esp32c5LcdTrafficLight.dll -t esp32c5-devkitc
chipsharp flash bin\Release\net10.0\Esp32c5LcdTrafficLight.dll -t esp32c5-devkitc
```

It takes **20.2 KB** of the 8 MB of flash (`build` says `SRAM 21.6 KB + heap 224 KB of 369.4 KB`). `build` writes two ESP images (the program runs from the chip's SRAM): `Esp32c5LcdTrafficLight.bin`, the **ESP-IDF app** (what you upload to Wokwi, or flash at `0x10000` behind an ESP-IDF bootloader), and `Esp32c5LcdTrafficLight.rom.bin`, the **ROM-direct image**, which `flash` writes at flash offset `0x2000`, where the chip's ROM loads it with no bootloader, with esptool through the USB-C port of the board marked **USB** (the chip's own USB Serial/JTAG); `--port COM6` says which port when more are plugged in. If esptool cannot connect, hold **BOOT**, press and release **RESET**, release **BOOT**, and flash again; the program starts at the next reset. Open the **USB** port's serial port in a terminal to read the console lines.

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5LcdTrafficLight.bin --ms 16000 --i2c 0:0x27=lcd1602 --input GPIO10=high@2500ms --input GPIO10=low@2700ms
```

It prints the console lines (`USB: road : 1`, ...) and the LCD (`LCD 0x27: |Road 1   turn 8s|`, ...) with the button pressed at 2.5 s, during lane 1's green: the same lines and the same LCD text as the Pico version prints in its simulator.

## Wokwi

Wokwi has a part for this board (`board-esp32-c5-devkitc-1`, marked beta), and `diagram.json` here is the circuit, wired directly (Wokwi needs no resistors). It has **not been tried in Wokwi**: it was written from the part's pin names (`esp:4`, `esp:GND.3`) alone, so a wire may need nudging.

To run the program in Wokwi, upload `Esp32c5LcdTrafficLight.bin` (not the `.rom.bin`; F1, "Upload Firmware and Start Simulation..."; nothing to merge): Wokwi's board puts an uploaded `.bin` at `0x10000` behind ESP-IDF's own bootloader, which accepts it. `chipsim esp32c5 Esp32c5LcdTrafficLight.bin --idf-bootloader` runs it as that bootloader leaves the chip; `chipsim esp32c5 Esp32c5LcdTrafficLight.rom.bin` runs the ROM-direct image.
