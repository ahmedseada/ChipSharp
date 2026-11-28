# stm8-blink

Blinks the LED of the blue STM8S103F3P6 board: on for half a second, off for half a second, forever (`Pin.Toggle` and
`Timing.DelayMilliseconds(500)` in a loop). The Pico's and the Uno's blink with another pin number.

## Wiring

Nothing to wire: the board's own LED is on PB5 (pin 13 by ChipSharp's numbers, port * 8 + bit), from 3.3 V through a
resistor, so it is lit while PB5 is low. `Pin.Output` drives the pin low (lit), the first toggle lets it go (dark).
PB5 is a true open-drain pin: "high" is the pin let go, which the LED's resistor pulls up.

| Board | To |
|---|---|
| The SWIM header: 3V3, SWIM (PD1), GND, NRST | An ST-LINK V2's 3.3V, SWIM, GND and RST, to flash it; it powers the board too |

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Stm8Blink.dll -t stm8s103-board
chipsharp flash bin\Release\net10.0\Stm8Blink.dll -t stm8s103-board
```

`build` makes `chipsharp-output\stm8s103-board\Stm8Blink.hex`: **762 bytes** of the 8 KB of flash and **16 bytes** of
the 1 KB of RAM. `flash` builds it, then writes it through an ST-LINK V2 (a clone will do) with `stm8flash`; see the
main README, "Flashing the PFS154 and the STM8S103" (a board that comes read-out protected has to be unlocked first, by hand, which
erases it: `stm8flash -c stlinkv2 -p stm8s103f3 -u`).

Without a board, run it in ChipSharp's simulator (`src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim stm8s103 chipsharp-output\stm8s103-board\Stm8Blink.hex --ms 2000
```

It prints PB5's changes: `PB5 low` (lit) and `PB5 input` (let go: dark) at once, then low and let go every 500 ms.
