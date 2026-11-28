# esp32c5-button

A push button toggles an LED: each press turns it on or off, through a pin interrupt, while `Main` does nothing at all.

`Pin.Input(5, Pull.Up)` turns the pad's pull-up on, so the pin is high until the button takes it to GND and the button needs no resistor; `Pin.WatchFalling(5)` starts the pin event, which runs the `[Interrupt(Event.Pin)]` method on each press. A real button bounces, so one press may toggle the LED more than once.

## Wiring

| Board | To |
|---|---|
| `4` (GPIO 4) | the LED's anode, through a 220 ohm resistor |
| `GND` | the LED's cathode |
| `5` (GPIO 5) | one leg of a push button |
| `GND` | the button's other leg |

The board's pins are labelled by their GPIO number. These are among the pins `chipsharp check` leaves a program on the ESP32-C5-DevKitC-1 (0, 1, 4, 5, 6, 8, 9, 10, 23 and 24: the others are strapping pins, USB, the UART, flash or the RGB LED), and the ones least likely to be taken away. A real LED needs a resistor (220 ohm) in series; Wokwi does not.

The example uses the portable API alone (`Pin`, `Timing`, `[Interrupt]`, `Console`): for a Pico, change the pin numbers and build with `-t pico`.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Esp32c5Button.dll -t esp32c5-devkitc
chipsharp flash bin\Release\net10.0\Esp32c5Button.dll -t esp32c5-devkitc
```

It takes **2.0 KB** of the 8 MB of flash (`build` says `SRAM 2.2 KB of 369.4 KB`). `build` writes two ESP images (the program runs from the chip's SRAM): `Esp32c5Button.bin`, the **ESP-IDF app** (what you upload to Wokwi, or flash at `0x10000` behind an ESP-IDF bootloader), and `Esp32c5Button.rom.bin`, the **ROM-direct image**, which `flash` writes at flash offset `0x2000`, where the chip's ROM loads it with no bootloader, with esptool through either USB-C port of the board, the one marked **USB** (the chip's
own USB Serial/JTAG) or the one marked **UART** (the bridge); `--port COM6` says which when both are plugged in. If
esptool cannot connect, hold **BOOT**, press and release **RESET**, release **BOOT**, and flash again; the program
starts at the next reset. 

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5Button.bin --ms 1600 --input GPIO5=low@200ms --input GPIO5=high@400ms
```

It prints `GPIO5 input pull-up`, then `GPIO4 high` when GPIO 5 goes low at 200 ms; the release does nothing, and the next press (`--input GPIO5=low@600ms`) turns the LED off.

## Wokwi

Wokwi has a part for this board (`board-esp32-c5-devkitc-1`, marked beta), and `diagram.json` here is the circuit, wired
directly (Wokwi needs no resistors). It has **not been tried in Wokwi**: it was written from the part's pin names
(`esp:4`, `esp:GND.3`) alone, so a wire may need nudging.

To run the program in Wokwi, upload `Esp32c5Button.bin` (not the `.rom.bin`; F1, "Upload Firmware and Start Simulation..."; nothing to merge): Wokwi's board puts an uploaded `.bin` at `0x10000` behind ESP-IDF's own bootloader, which accepts it. `chipsim esp32c5 Esp32c5Button.bin --idf-bootloader` runs it as that bootloader leaves the chip; `chipsim esp32c5 Esp32c5Button.rom.bin` runs the ROM-direct image.
