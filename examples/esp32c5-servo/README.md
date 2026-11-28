# esp32c5-servo

A servo sweeps on a PWM pin: its arm turns from 0 to 180 degrees and back, two degrees every 20 ms. A hobby servo reads the length of a pulse that comes 50 times a second (`Pwm.Output(6, 50)`): `Pwm.PulseMicroseconds` gives 1000 us for 0 degrees, 1500 us for the middle and 2000 us for 180 degrees. The chip makes the pulses on its own. Every 30 degrees the console says where the servo is:

```
servo at 0 degrees: pulse 1000 us
servo at 30 degrees: pulse 1166 us
```

## Wiring

| Board | To |
|---|---|
| `6` | the servo's signal (orange) |
| `5V` | the servo's + (red) |
| `GND` | the servo's - (brown) |
| the USB-C port marked **USB** | the PC, for the console lines |

GPIO 6 is a plain pin the board leaves a program. A real servo may draw more current than a USB port gives while it moves: power it from its own 5 V then, its - to the board's `GND` too.

The example uses the portable API alone (`Pwm`, `Timing`, `Console`): for a Pico, change the pin number and build with `-t pico`.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Esp32c5Servo.dll -t esp32c5-devkitc
chipsharp flash bin\Release\net10.0\Esp32c5Servo.dll -t esp32c5-devkitc
```

It takes **7.0 KB** of the 8 MB of flash (`build` says `SRAM 7.3 KB of 369.4 KB`). `build` writes `Esp32c5Servo.bin`, the ESP image the chip's ROM loads from flash offset `0x2000` (there is no second-stage bootloader: the program is the image, and it runs from the chip's SRAM). `flash` writes it with esptool through either USB-C port of the board, the one marked **USB** (the chip's own USB Serial/JTAG) or the one marked **UART** (the bridge); `--port COM6` says which when both are plugged in. If esptool cannot connect, hold **BOOT**, press and release **RESET**, release **BOOT**, and flash again; the program starts at the next reset. Open the **USB** port's serial port in a terminal to read the console lines.

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5Servo.bin --ms 4000
```

It prints the console lines and, for each pulse, `GPIO6 PWM 50.08 Hz, duty 5.00% (998.38 us high)`, ... as measured on the pin.

## Wokwi

Wokwi has a part for this board (`board-esp32-c5-devkitc-1`, marked beta), and `diagram.json` here is the circuit, wired directly (Wokwi needs no resistors). It has **not been tried in Wokwi**: it was written from the part's pin names (`esp:6`, `esp:5V`) alone, so a wire may need nudging.
