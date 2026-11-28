# esp32c5-classes

A two-lane traffic light that shows what the heap is for, in the M3 style of `examples/pico-traffic-light-m3` (without its displays; an LCD comes with I2C): a `readonly record struct Lane` for each lane's pins, a `Signal` class for each lane's lights, an iterator (`yield return`) for the steps of a turn, a `List<Lane>` and a `List<Signal>`, interpolated strings, and a button whose `[Interrupt(Event.Pin)]` method cuts a green short.

In each turn the lane goes green (4 s), yellow (1 s), red (2 s), and then the other lane's turn starts. The console says each second which turn it is and what the signal shows (turns 1, 3, ... are lane 1's, 2, 4, ... lane 2's); a press during a green ends it at once:

```
turn 1   signal: green 4
turn 1   signal: green 3
turn 1   signal: green 2
turn 1 : green cut short
turn 1   signal: yellow 1
turn 1   signal: red 2
turn 1   signal: red 1
turn 2   signal: green 4
```

## Wiring

| Board | To |
|---|---|
| `4`, `5`, `6` | lane 1's red, yellow and green LEDs' anodes, each through a 220 ohm resistor |
| `10`, `23`, `24` | lane 2's red, yellow and green LEDs' anodes, the same |
| `9` | one leg of a push button, the other to `GND` (`Pull.Up`: no resistor) |
| `GND` | every LED's cathode |
| the USB-C port marked **USB** | the PC, for the status lines |

The board's pins are labelled by their GPIO number. These are among the pins `chipsharp check` leaves a program on the ESP32-C5-DevKitC-1 (0, 1, 4, 5, 6, 8, 9, 10, 23 and 24: the others are strapping pins, USB, the UART, flash or the RGB LED), and the ones least likely to be taken away. A real LED needs a resistor (220 ohm) in series; Wokwi does not.

The example uses the portable API alone (`Pin`, `Timing`, `[Interrupt]`, `Console`): for a Pico, change the pin numbers and build with `-t pico`.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Esp32c5Classes.dll -t esp32c5-devkitc
chipsharp flash bin\Release\net10.0\Esp32c5Classes.dll -t esp32c5-devkitc
```

It takes **13.9 KB** of the 8 MB of flash (`build` says `SRAM 15.2 KB + heap 224 KB of 369.4 KB`). `build` writes two ESP images (the program runs from the chip's SRAM): `Esp32c5Classes.bin`, the **ESP-IDF app** (what you upload to Wokwi, or flash at `0x10000` behind an ESP-IDF bootloader), and `Esp32c5Classes.rom.bin`, the **ROM-direct image**, which `flash` writes at flash offset `0x2000`, where the chip's ROM loads it with no bootloader, with esptool through either USB-C port of the board, the one marked **USB** (the chip's
own USB Serial/JTAG) or the one marked **UART** (the bridge); `--port COM6` says which when both are plugged in. If
esptool cannot connect, hold **BOOT**, press and release **RESET**, release **BOOT**, and flash again; the program
starts at the next reset. Open the **USB** port's serial port in a terminal to read the status lines.

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5Classes.bin --ms 16000 --input GPIO9=low@2500ms --input GPIO9=high@2700ms
```

It prints the lines above (`USB: turn 1   signal: green 4`, ...) with the button pressed at 2.5 s, during lane 1's green.

## Wokwi

Wokwi has a part for this board (`board-esp32-c5-devkitc-1`, marked beta), and `diagram.json` here is the circuit, wired
directly (Wokwi needs no resistors). It has **not been tried in Wokwi**: it was written from the part's pin names
(`esp:4`, `esp:GND.3`) alone, so a wire may need nudging.

To run the program in Wokwi, upload `Esp32c5Classes.bin` (not the `.rom.bin`; F1, "Upload Firmware and Start Simulation..."; nothing to merge): Wokwi's board puts an uploaded `.bin` at `0x10000` behind ESP-IDF's own bootloader, which accepts it. `chipsim esp32c5 Esp32c5Classes.bin --idf-bootloader` runs it as that bootloader leaves the chip; `chipsim esp32c5 Esp32c5Classes.rom.bin` runs the ROM-direct image.
