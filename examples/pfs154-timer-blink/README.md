# pfs154-timer-blink

Blinks an LED on a Padauk PFS154 from the timer event: `Timing.EveryMilliseconds(500)` starts it, and the chip runs
the `[Interrupt(Event.Timer)]` method every 500 ms while `Main` does nothing at all. On the PFS154 the timer event is
TM2's interrupt, timed by the clock T16 keeps. The Pico's and the Uno's timer blink with another pin number.

## Wiring

| PFS154 | To |
|---|---|
| PA4 (pin 4) | an LED with its resistor, to GND |
| VDD, GND | 5 V and GND |

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Pfs154TimerBlink.dll -t pfs154-bare
chipsharp flash bin\Release\net10.0\Pfs154TimerBlink.dll -t pfs154-bare
```

It takes **612 words** of the 2048 words of program memory and **23 bytes** of the 128 bytes of RAM (the timer event
and the clock it runs on). `flash` writes it with the Easy PDK programmer (`easypdkprog`), as `pfs154-blink`'s README
says.

Without a chip (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim pfs154 chipsharp-output\pfs154-bare\Pfs154TimerBlink.hex --ms 2000
```

PA4 changes 500 ms after start-up, and every 500 ms after that.
