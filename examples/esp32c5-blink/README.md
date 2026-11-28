# esp32c5-blink

Blinks an external LED on GPIO 4 of the ESP32-C5-DevKitC-1: half a second on, half a second off, with a delay loop.

The board's own LED is an addressable RGB LED (a WS2812 on GPIO 27, which wants timed 24-bit colors, not a high or low), so this example uses an LED of its own.

## Wiring

| Board | To |
|---|---|
| `4` (GPIO 4) | the LED's anode (long leg), through a 220 ohm resistor |
| `GND` (any) | the LED's cathode |

The board's pins are labelled by their GPIO number. These are among the pins `chipsharp check` leaves a program on the ESP32-C5-DevKitC-1 (0, 1, 4, 5, 6, 8, 9, 10, 23 and 24: the others are strapping pins, USB, the UART, flash or the RGB LED), and the ones least likely to be taken away. A real LED needs a resistor (220 ohm) in series; Wokwi does not.

The example uses the portable API alone (`Pin`, `Timing`, `[Interrupt]`, `Console`): for a Pico, change the pin numbers and build with `-t pico`.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Esp32c5Blink.dll -t esp32c5-devkitc
chipsharp flash bin\Release\net10.0\Esp32c5Blink.dll -t esp32c5-devkitc
```

It takes **1.8 KB** of the 8 MB of flash (`build` says `SRAM 2.1 KB of 369.4 KB, no heap`). `build` writes two ESP images (the program runs from the chip's SRAM): `Esp32c5Blink.bin`, the **ESP-IDF app** (what you upload to Wokwi, or flash at `0x10000` behind an ESP-IDF bootloader), and `Esp32c5Blink.rom.bin`, the **ROM-direct image**, which `flash` writes at flash offset `0x2000`, where the chip's ROM loads it with no bootloader, with esptool through either USB-C port of the board, the one marked **USB** (the chip's
own USB Serial/JTAG) or the one marked **UART** (the bridge); `--port COM6` says which when both are plugged in. If
esptool cannot connect, hold **BOOT**, press and release **RESET**, release **BOOT**, and flash again; the program
starts at the next reset. 

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5Blink.bin --ms 1600
```

It prints `GPIO4 low` as the program starts, then `GPIO4 high` at once, and a change every 500 ms.

## Wokwi

Wokwi has a part for this board (`board-esp32-c5-devkitc-1`, marked beta), and `diagram.json` here is the circuit, wired
directly (Wokwi needs no resistors). It has **not been tried in Wokwi**: it was written from the part's pin names
(`esp:4`, `esp:GND.3`) alone, so a wire may need nudging.

To run the program in Wokwi, upload `Esp32c5Blink.bin` (not the `.rom.bin`; F1, "Upload Firmware and Start Simulation..."; nothing to merge): Wokwi's board puts an uploaded `.bin` at `0x10000` behind ESP-IDF's own bootloader, which accepts it. `chipsim esp32c5 Esp32c5Blink.bin --idf-bootloader` runs it as that bootloader leaves the chip; `chipsim esp32c5 Esp32c5Blink.rom.bin` runs the ROM-direct image.
