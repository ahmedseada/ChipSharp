# esp32c5-serial

A command prompt on the board's USB console, M3 style: type a command and press Enter.

| Command | What it does |
|---|---|
| `on` | switches the LED on |
| `off` | switches it off |
| `blink 200` | blinks it, 200 ms on and 200 ms off, from the timer event, while the prompt waits for the next line |
| `status` | says what the LED does, and how many commands have come: `LED blinking, 3 commands` |
| anything else | is answered with what was typed: `you said: hello` |

The commands are a `Dictionary<string, Action<string>>` (the method each name runs, and the rest of the line as its argument) and the lines are strings: it uses the heap, which the C5 has 224 KB of. `Console.ReadLine` waits for a line ended by Enter and does not echo what is typed, so turn on the terminal's local echo to see it.

## Wiring

| Board | To |
|---|---|
| `4` (GPIO 4) | the LED's anode, through a 220 ohm resistor |
| `GND` | the LED's cathode |
| the USB-C port marked **USB** | the PC: the console is the chip's USB Serial/JTAG port, which shows up as a serial port (COM6, `/dev/ttyACM0`); open it in any terminal (the baud rate does not matter) |

The board's pins are labelled by their GPIO number. These are among the pins `chipsharp check` leaves a program on the ESP32-C5-DevKitC-1 (0, 1, 4, 5, 6, 8, 9, 10, 23 and 24: the others are strapping pins, USB, the UART, flash or the RGB LED), and the ones least likely to be taken away. A real LED needs a resistor (220 ohm) in series; Wokwi does not.

The example uses the portable API alone (`Pin`, `Timing`, `[Interrupt]`, `Console`): for a Pico, change the pin numbers and build with `-t pico`.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Esp32c5Serial.dll -t esp32c5-devkitc
chipsharp flash bin\Release\net10.0\Esp32c5Serial.dll -t esp32c5-devkitc
```

It takes **21.3 KB** of the 8 MB of flash (`build` says `SRAM 22.6 KB + heap 224 KB of 369.4 KB`). `build` writes `Esp32c5Serial.bin`, the ESP image the chip's ROM
loads from flash offset `0x2000` (there is no second-stage bootloader: the program is the image, and it runs from the
chip's SRAM). `flash` writes it with esptool through either USB-C port of the board, the one marked **USB** (the chip's
own USB Serial/JTAG) or the one marked **UART** (the bridge); `--port COM6` says which when both are plugged in. If
esptool cannot connect, hold **BOOT**, press and release **RESET**, release **BOOT**, and flash again; the program
starts at the next reset. The console is the **USB** port's, so that is the one to type on (the **UART** port is the bridge to `Uart`, GPIO 11 and 12: the console is silent there).

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5Serial.bin --ms 700 --send USB="on\n@100ms" --send USB="blink 200\n@200ms" --send USB="status\n@500ms" --send USB="off\n@600ms"
```

It prints each line the console sends (`USB: > LED on`, `USB: > LED blinking every 200 ms`, ...) and GPIO 4 going high with `on`, toggling every 200 ms after `blink 200`, and settling with `off`.

## Wokwi

Wokwi has a part for this board (`board-esp32-c5-devkitc-1`, marked beta), and `diagram.json` here is the circuit, wired
directly (Wokwi needs no resistors). It has **not been tried in Wokwi**: it was written from the part's pin names
(`esp:4`, `esp:GND.3`) alone, so a wire may need nudging.
