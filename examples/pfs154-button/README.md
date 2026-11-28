# pfs154-button

A button toggles an LED on a Padauk PFS154: each press turns it on or off, through the pin event
(`Pin.WatchFalling` and an `[Interrupt(Event.Pin)]` method), while `Main` does nothing at all. The PFS154's pin event
is on PA0 and PB0 only; `chipsharp check` says so for any other pin.

## Wiring

| PFS154 | To |
|---|---|
| PA4 (pin 4) | an LED with its resistor, to GND |
| PA0 (pin 0) | a push button, to GND; and a 10 kOhm resistor to VDD |
| VDD, GND | 5 V and GND |

The resistor holds PA0 high until the button takes it to GND. `Pin.Input` leaves the chip's own pull-up off (as after
reset), and the portable API has no call that turns it on, so the program would need `Registers.Write` of PAPH to use
it: the resistor keeps the program the same C# as the Pico's and the Uno's. (A real button bounces, so one press may
toggle the LED more than once.)

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Pfs154Button.dll -t pfs154-bare
chipsharp flash bin\Release\net10.0\Pfs154Button.dll -t pfs154-bare
```

It takes **372 words** of the 2048 words of program memory and **12 bytes** of the 128 bytes of RAM. `flash` writes it
with the Easy PDK programmer (`easypdkprog`), as `pfs154-blink`'s README says.

Without a chip (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`): `--input PA0=high` is the
resistor, each `low` a press:

```
chipsim pfs154 chipsharp-output\pfs154-bare\Pfs154Button.hex --ms 1000 --input PA0=high --input PA0=low@200ms --input PA0=high@300ms --input PA0=low@500ms --input PA0=high@600ms
```

PA4 goes high at 200 ms and low at 500 ms, 40 us after each press.
