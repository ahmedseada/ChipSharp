# stm8-serial

A command prompt on the serial port of the blue STM8S103F3P6 board: type a command and press Enter.

| Command | What it does |
|---|---|
| `on` | switches the board's LED (PB5) on |
| `off` | switches it off |
| `status` | says whether it is on, and how many commands have come: `LED on, 3 commands` |
| anything else | is answered with what was typed: `you said: hello` |

The console is UART1 at 115200 baud: `Console.Write` sends on PD5, `Console.Read` takes what came in on PD6, which the
chip receives on its own into a buffer of 32 bytes. Each char is sent back as it comes, so a terminal shows what is
typed (a backspace takes the last one back). It is the Pico's prompt (`examples/pico-uart-echo`) without a heap, which
the board's 1 KB of RAM has no room for: `Console.ReadLine` makes a string, so the line is read char by char into a
span on the stack (`stackalloc`), and the `switch` is on that span, against constant strings in flash.

## Wiring

| Board | To |
|---|---|
| PD5 (D5, TX) | a 3.3 V USB-to-serial adapter's RX |
| PD6 (D6, RX) | the adapter's TX |
| GND | the adapter's GND |
| The SWIM header | an ST-LINK V2, to flash it (see `stm8-blink`) |

Open the adapter's port in a terminal (PuTTY, the Arduino IDE's serial monitor, ...) at 115200 baud, 8N1. The LED is
the board's own, on PB5, lit while the pin is low: the program says so with `LedLitWhenHigh = false` (true on a Pico or
an Uno, whose LEDs light while their pin is high).

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Stm8Serial.dll -t stm8s103-board
chipsharp flash bin\Release\net10.0\Stm8Serial.dll -t stm8s103-board
```

It takes **6.7 KB** of the 8 KB of flash and **64 bytes** of the 1 KB of RAM, the receive buffer's 32 among them;
`flash` writes it through an ST-LINK V2, as `stm8-blink`'s README says.

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`), with commands typed at 20,
60 and 100 ms:

```
chipsim stm8s103 chipsharp-output\stm8s103-board\Stm8Serial.hex --ms 200 --send "on\r\n@20ms" --send "status\r\n@60ms" --send "off\r\n@100ms"
```

It prints each line UART1 sends (`UART1: > on`, `UART1: LED on`, ...) and PB5 going low (lit) and let go (dark).
