# esp32c5-lcd-clock

A clock: the date and the time on a 16x2 character LCD, once a second, its time from a DS1307 real-time clock.

```
+----------------+
|Fri 27 Nov 2026 |
|18:00:05        |
+----------------+
```

The program reads the DS1307 once, at start-up, and starts the chip's own clock from it (`Clock.Start`): from then on `DateTime.Now` is that time plus what the timer has counted since. A DS1307 that does not answer or does not run (a new one is halted) is set to a date to start from, and starts. The C5 has a heap, so `DateTime` and its format strings are used as on a PC; this is `examples/uno-lcd-clock` without the Uno's span. The console (USB) says where the time came from.

## Wiring

| Board | To |
|---|---|
| `4` | SDA of the LCD backpack (address 0x27) and of the DS1307 (0x68) |
| `5` | SCL of both |
| `5V` | VCC of both (the DS1307 module wants 5 V; see below) |
| `GND` | GND of both |
| the USB-C port marked **USB** | the PC, for the console lines |

GPIO 4 and 5 are the JTAG pins MTCK and MTDO, free while JTAG is on USB (its default); `I2c.Open` turns the pull-ups on. The C5's pins take at most 3.3 V: a module whose I2C lines are pulled up to its 5 V supply needs a level shifter, or take one whose pull-ups go to `3V3`.

The example uses the portable API alone (`I2c`, `Clock`, `Timing`, `Lcd`, `Ds1307`): for a Pico, change the pin numbers and the bus and build with `-t pico`.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Esp32c5LcdClock.dll -t esp32c5-devkitc
chipsharp flash bin\Release\net10.0\Esp32c5LcdClock.dll -t esp32c5-devkitc
```

It takes **25.5 KB** of the 8 MB of flash (`build` says `SRAM 26.0 KB + heap 224 KB of 369.4 KB`). `build` writes `Esp32c5LcdClock.bin`, the ESP image: the one file for every way to boot it (it runs from the chip's SRAM). `flash` writes it at flash offset `0x2000`, where the chip's ROM loads it directly (no bootloader), with esptool through either USB-C port of the board, the one marked **USB** (the chip's own USB Serial/JTAG) or the one marked **UART** (the bridge); `--port COM6` says which when both are plugged in. If esptool cannot connect, hold **BOOT**, press and release **RESET**, release **BOOT**, and flash again; the program starts at the next reset. Open the **USB** port's serial port in a terminal to read the console lines.

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5LcdClock.bin --i2c 0:0x27=lcd1602 --i2c 0:0x68=ds1307@2026-11-27T18:00:00 --ms 3500
```

It prints `USB: The time from the DS1307: 2026-11-27 18:00:00` and the LCD's two lines each second (`LCD 0x27: |Fri 27 Nov 2026 |`, `LCD 0x27: |18:00:01        |`, ...). Without `@...` the simulated DS1307 starts from the PC's time.

## Wokwi

Wokwi has a part for this board (`board-esp32-c5-devkitc-1`, marked beta), and `diagram.json` here is the circuit, wired directly (Wokwi needs no resistors). It has **not been tried in Wokwi**: it was written from the part's pin names (`esp:4`, `esp:5V`) alone, so a wire may need nudging.

To run the program in Wokwi, upload the plain `Esp32c5LcdClock.bin` (F1, "Upload Firmware and Start Simulation..."; nothing to merge): Wokwi's board puts an uploaded `.bin` at `0x10000` behind ESP-IDF's own bootloader, which accepts it (the image starts with ESP-IDF's application description, and its segments stay clear of the bootloader's memory), so the same file that `chipsharp flash` writes at `0x2000` runs there as an app. `chipsim esp32c5 Esp32c5LcdClock.bin --idf-bootloader` runs it as that bootloader leaves the chip.
