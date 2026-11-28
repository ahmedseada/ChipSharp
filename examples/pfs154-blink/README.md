# pfs154-blink

Blinks an LED on a Padauk PFS154: on for half a second, off for half a second, forever (`Pin.Toggle` and
`Timing.DelayMilliseconds(500)` in a loop). The Pico's and the Uno's blink with another pin number.

## Wiring

| PFS154 | To |
|---|---|
| PA4 (pin 4) | an LED with its resistor, to GND |
| VDD, GND | 5 V and GND (the program's clock is trimmed at 5.0 V when it is written) |

Pins are numbered port * 8 + bit: PA0-PA7 are 0-7, PB0-PB7 are 8-15.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Pfs154Blink.dll -t pfs154-bare
chipsharp flash bin\Release\net10.0\Pfs154Blink.dll -t pfs154-bare
```

`build` makes `chipsharp-output\pfs154-bare\Pfs154Blink.hex`: **264 words** of the 2048 words of program memory and
**9 bytes** of the 128 bytes of RAM. `flash` builds it, then writes it with the
[Easy PDK programmer](https://github.com/free-pdk/easy-pdk-programmer-hardware) (`easypdkprog`, the chip in its
socket), which also trims the chip's oscillator to the 2 MHz the program runs at; see the main README, "Flashing the
PFS154 and the STM8S103".

Without a chip, run it in ChipSharp's simulator (`src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim pfs154 chipsharp-output\pfs154-bare\Pfs154Blink.hex --ms 2000
```

It prints PA4's changes: `PA4 high` at 0.4 ms, then low and high every 500 ms.
