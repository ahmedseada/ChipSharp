# ch32v003-blink

Blinks an LED on WCH's CH32V003 evaluation board: on for half a second, off for half a second, forever (`Pin.Toggle`
and `Timing.DelayMilliseconds(500)` in a loop). The Pico's, the Uno's and the STM8's blink with another pin number.

## Wiring

The board's LEDs are on the header P4, each from VCC through 1K, wired to no pin until a wire does: one from **LED1** to
**PD0** (pin 24 by ChipSharp's numbers, port * 8 + bit) lights it while PD0 is low. `Pin.Output` drives the pin low
(lit), the first toggle high (dark). PD0 is the pin WCH's own GPIO_Toggle example blinks.

| Board | To |
|---|---|
| `PD0` | `LED1` on the header P4 |
| The debug header: SWIO (PD1), GND, VCC | A WCH-LinkE's SWDIO, GND and 3V3, to flash it; it powers the board too |

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Ch32v003Blink.dll -t ch32v003-evt
chipsharp flash bin\Release\net10.0\Ch32v003Blink.dll -t ch32v003-evt
```

`build` makes `chipsharp-output\ch32v003-evt\Ch32v003Blink.bin`: **2.3 KB** of the 16 KB of flash and **52 bytes** of
the 2 KB of SRAM (the stack's 512 bytes besides). `flash` writes it through a WCH-LinkE with wlink; see the main
README, "Flashing the CH32V003".

Without a board, run it in ChipSharp's simulator (`src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim ch32v003 chipsharp-output\ch32v003-evt\Ch32v003Blink.bin --ms 2000
```

It prints PD0's changes: `PD0 low` (lit) and `PD0 high` (dark) at once, then low and high every 500 ms.
