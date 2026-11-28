# ch32v003-serial

A command prompt on the board's serial port: `on` and `off` switch the LED, `status` says whether it is on and how many
commands have come, anything else is answered with what was typed. Each char typed comes back as it comes (the echo),
a backspace takes the last one back, and a line ends with Enter as any terminal sends it (`\r\n`, `\r` or `\n`).

The console is USART1, PD5 (TX) and PD6 (RX), at 115200 baud: `Console.Write` sends, `Console.Read` waits for the next
char, which the chip receives on its own into a buffer of 16 bytes. It is examples/stm8-serial with the board's pins:
the line is read into a span on the stack (`stackalloc`), as `Console.ReadLine` would make a string, which needs a heap
the chip's 2 KB of SRAM has no room for.

## Wiring

| Board | To |
|---|---|
| `PD0` | `LED1` on the header P4 (lit while PD0 is low) |
| `PD5` (USART1's TX) | a WCH-LinkE's `RX` |
| `PD6` (USART1's RX) | the WCH-LinkE's `TX` |

The LinkE's own USB serial port (a standard USB serial device on Windows) is then the board's console.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Ch32v003Serial.dll -t ch32v003-evt
chipsharp flash bin\Release\net10.0\Ch32v003Serial.dll -t ch32v003-evt
```

It takes **4.7 KB** of the 16 KB of flash and **88 bytes** of the 2 KB of SRAM. Without a board:

```
chipsim ch32v003 chipsharp-output\ch32v003-evt\Ch32v003Serial.bin --ms 300 --send "on\r\n@20ms" --send "status\r@60ms"
```

It prints the greeting, `> on` and `LED on` (PD0 low at 20 ms), then `> status` and `LED on, 2 commands`.
