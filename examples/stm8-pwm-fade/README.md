# stm8-pwm-fade

Fades an LED on the blue STM8S103F3P6 board up and down, forever: `Pwm.Output(28, 1000)` makes PD4 a PWM output, and
`Pwm.PulseMicroseconds` makes its pulse 20 us longer every 20 ms, up to the whole period in a second, then shorter
again. On the STM8S103, PD4, PD3 and PA3 are TIM2's channels 1 to 3, all at one frequency (TIM1 is the clock's); TIM2
makes 1000 Hz exactly, a period of 1000 us, each pulse to the microsecond. `Pwm.Duty` takes a `double`, which SDCC has
not, so the fade gives the pulse in microseconds. It is `pfs154-pwm-fade` with the board's pin: the same C#.

## Wiring

| Board | To |
|---|---|
| PD4 (D4 on the board) | an LED with a 220 ohm resistor, to GND |
| The SWIM header | an ST-LINK V2, to flash it (see `stm8-blink`) |

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Stm8PwmFade.dll -t stm8s103-board
chipsharp flash bin\Release\net10.0\Stm8PwmFade.dll -t stm8s103-board
```

It takes **3.3 KB** of the 8 KB of flash and **23 bytes** of the 1 KB of RAM; `flash` writes it through an ST-LINK V2,
as `stm8-blink`'s README says.

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`):

```
chipsim stm8s103 chipsharp-output\stm8s103-board\Stm8PwmFade.hex --ms 2100
```

It prints each new period of PD4: `PD4 PWM 1000.00 Hz, duty 2.00% (20.00 us high)` at 22 ms, 20 us more every 20 ms up
to `PD4 PWM steady high` at 1 s, then down to `PD4 PWM steady low` at 2 s, and up again.
