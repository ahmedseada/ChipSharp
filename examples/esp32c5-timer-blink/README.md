# esp32c5-timer-blink

Blinks an LED from the timer event while `Main` prints how long the program has run, once a second:

```
uptime 1 s (Stopwatch), 1 s (DateTime), 2 toggles
uptime 2 s (Stopwatch), 2 s (DateTime), 4 toggles
```

`Timing.EveryMilliseconds(500)` starts the timer; the chip runs the `[Interrupt(Event.Timer)]` method every 500 ms whatever `Main` is doing (it counts its toggles in a `volatile` field, which `Main` prints). The uptime is read from a `Stopwatch` and from `DateTime.Now` (which starts on 1 January 2000 unless `Clock.Start` gives a date): both come from the chip's SYSTIMER. `Stopwatch` and the printed text make objects, so this example uses the heap.

## Wiring

| Board | To |
|---|---|
| `4` (GPIO 4) | the LED's anode, through a 220 ohm resistor |
| `GND` | the LED's cathode |
| the USB-C port marked **USB** | the PC, to read the uptime in a terminal |

The board's pins are labelled by their GPIO number. These are among the pins `chipsharp check` leaves a program on the ESP32-C5-DevKitC-1 (0, 1, 4, 5, 6, 8, 9, 10, 23 and 24: the others are strapping pins, USB, the UART, flash or the RGB LED), and the ones least likely to be taken away. A real LED needs a resistor (220 ohm) in series; Wokwi does not.

The example uses the portable API alone (`Pin`, `Timing`, `[Interrupt]`, `Console`): for a Pico, change the pin numbers and build with `-t pico`.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Esp32c5TimerBlink.dll -t esp32c5-devkitc
chipsharp flash bin\Release\net10.0\Esp32c5TimerBlink.dll -t esp32c5-devkitc
```

It takes **13.1 KB** of the 8 MB of flash (`build` says `SRAM 13.5 KB + heap 224 KB of 369.4 KB`). `build` writes two ESP images (the program runs from the chip's SRAM): `Esp32c5TimerBlink.bin`, the **ESP-IDF app** (what you upload to Wokwi, or flash at `0x10000` behind an ESP-IDF bootloader), and `Esp32c5TimerBlink.rom.bin`, the **ROM-direct image**, which `flash` writes at flash offset `0x2000`, where the chip's ROM loads it with no bootloader, with esptool through either USB-C port of the board, the one marked **USB** (the chip's
own USB Serial/JTAG) or the one marked **UART** (the bridge); `--port COM6` says which when both are plugged in. If
esptool cannot connect, hold **BOOT**, press and release **RESET**, release **BOOT**, and flash again; the program
starts at the next reset. Open the **USB** port's serial port in a terminal to read the uptime.

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5TimerBlink.bin --ms 3500
```

It prints GPIO 4 changing every 500 ms and `USB: uptime 1 s (Stopwatch), 1 s (DateTime), 2 toggles` each second.

## Wokwi

Wokwi has a part for this board (`board-esp32-c5-devkitc-1`, marked beta), and `diagram.json` here is the circuit, wired
directly (Wokwi needs no resistors). It has **not been tried in Wokwi**: it was written from the part's pin names
(`esp:4`, `esp:GND.3`) alone, so a wire may need nudging.

To run the program in Wokwi, upload `Esp32c5TimerBlink.bin` (not the `.rom.bin`; F1, "Upload Firmware and Start Simulation..."; nothing to merge): Wokwi's board puts an uploaded `.bin` at `0x10000` behind ESP-IDF's own bootloader, which accepts it. `chipsim esp32c5 Esp32c5TimerBlink.bin --idf-bootloader` runs it as that bootloader leaves the chip; `chipsim esp32c5 Esp32c5TimerBlink.rom.bin` runs the ROM-direct image.
