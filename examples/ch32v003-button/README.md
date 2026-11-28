# ch32v003-button

A push button toggles an LED: each press turns it on or off, through a pin interrupt, while `Main` does nothing at all.

`Pin.Input(27, Pull.Up)` turns PD3's pull-up on, so the pin is high until the button takes it to GND and the button
needs no resistor; `Pin.WatchFalling(27)` starts the pin event, which runs the `[Interrupt(Event.Pin)]` method on each
press. A real button bounces, so one press may toggle the LED more than once. The CH32V003 watches one pin of each
number at a time (EXTI line 3 is PA3, PC3 or PD3): a program that also watched PC3 would stop, saying so.

## Wiring

| Board | To |
|---|---|
| `PD0` | `LED1` on the header P4 (from VCC through 1K: lit while PD0 is low) |
| `PD3` | one leg of a push button |
| `GND` | the button's other leg |

The example uses the portable API alone (`Pin`, `[Interrupt]`): for a Pico, change the pin numbers and build with
`-t pico`.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Ch32v003Button.dll -t ch32v003-evt
chipsharp flash bin\Release\net10.0\Ch32v003Button.dll -t ch32v003-evt
```

It takes **2.8 KB** of the 16 KB of flash and **52 bytes** of the 2 KB of SRAM. Without a board
(`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim ch32v003 chipsharp-output\ch32v003-evt\Ch32v003Button.bin --ms 1600 --input PD3=low@200ms --input PD3=high@400ms
```

It prints `PD0 low` (lit), `PD3 input pull-up`, then `PD0 high` when PD3 goes low at 200 ms; the release does nothing,
and the next press (`--input PD3=low@600ms`) lights the LED again.
