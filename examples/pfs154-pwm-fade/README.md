# pfs154-pwm-fade

Fades an LED on a Padauk PFS154 up and down, forever: `Pwm.Output(13, 1000)` makes PB5 a PWM output, and
`Pwm.PulseMicroseconds` makes its pulse 20 us longer every 20 ms, up to the whole period in a second, then shorter
again. On the PFS154, PB5, PB6 and PB7 are TM3's PWM pins (PA3, PB2 and PB4 TM2's, while the timer event is off); TM3
makes 1008 Hz for 1000, a period of 992 us in 64 steps of 15.5 us. `Pwm.Duty` takes a `double`, which SDCC has not, so
the fade gives the pulse in microseconds.

## Wiring

| PFS154 | To |
|---|---|
| PB5 (pin 13) | an LED with its resistor, to GND |
| VDD, GND | 5 V and GND |

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Pfs154PwmFade.dll -t pfs154-bare
chipsharp flash bin\Release\net10.0\Pfs154PwmFade.dll -t pfs154-bare
```

It takes **860 words** of the 2048 words of program memory and **53 bytes** of the 128 bytes of RAM. `flash` writes it
with the Easy PDK programmer (`easypdkprog`), as `pfs154-blink`'s README says.

Without a chip (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim pfs154 chipsharp-output\pfs154-bare\Pfs154PwmFade.hex --ms 2100
```

It prints each new period of PB5: `PB5 PWM 1008.06 Hz, duty 1.56% (15.50 us high)` at 24 ms, longer pulses to
`PWM steady high` at about 1 s, shorter ones to `PWM steady low` at about 2 s.
