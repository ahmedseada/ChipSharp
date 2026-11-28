# pfs154-button

A button toggles an LED on a Padauk PFS154: each press turns it on or off, through the pin event
(`Pin.WatchFalling` and an `[Interrupt(Event.Pin)]` method), while `Main` does nothing at all. The PFS154's pin event
is on PA0 and PB0 only; `chipsharp check` says so for any other pin.

## Wiring

| PFS154 | To |
|---|---|
| PA4 (pin 4) | an LED with its resistor, to GND |
| PA0 (pin 0) | a push button, to GND |
| VDD, GND | 5 V and GND |

`Pin.Input(pin, Pull.Up)` turns the chip's own pull-high on (PAPH), which holds PA0 high until the button takes it to
GND: no resistor is needed. The chip has no pull-low, so `Pull.Down` is refused by `chipsharp check`. (A real button
bounces, so one press may toggle the LED more than once.)

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Pfs154Button.dll -t pfs154-bare
chipsharp flash bin\Release\net10.0\Pfs154Button.dll -t pfs154-bare
```

It takes **435 words** of the 2048 words of program memory and **12 bytes** of the 128 bytes of RAM. `flash` writes it
with the Easy PDK programmer (`easypdkprog`), as `pfs154-blink`'s README says.

Without a chip (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`): the pull-up holds PA0 high,
each `--input ...=low` is a press:

```
chipsim pfs154 chipsharp-output\pfs154-bare\Pfs154Button.hex --ms 1000 --input PA0=low@200ms --input PA0=high@300ms --input PA0=low@500ms --input PA0=high@600ms
```

PA4 goes high at 200 ms and low at 500 ms, 40 us after each press.
