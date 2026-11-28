# ch32v003-clock

The time once a second on the board's serial port: `Clock.Start` gives the clock a date (28 November 2026, 12:00), and
from then on `DateTime.Now` is that date and the time since, counted by SysTick (six counts a microsecond).
Each line is written as the second changes (`Timing.DelayMilliseconds(1000 - DateTime.Now.Millisecond)`), with
`Environment.TickCount64`, the milliseconds since start-up. The chip has no real-time clock: the clock starts again at
the date each time the chip starts (a DS1307 on I2C, as the Uno's and the Pico's clocks have, comes with the
CH32V003's I2C).

## Wiring

| Board | To |
|---|---|
| `PD5` (USART1's TX) | a WCH-LinkE's `RX`: the text on the LinkE's own serial port, at 115200 baud |

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Ch32v003Clock.dll -t ch32v003-evt
chipsharp flash bin\Release\net10.0\Ch32v003Clock.dll -t ch32v003-evt
```

It takes **6.1 KB** of the 16 KB of flash and **56 bytes** of the 2 KB of SRAM. Without a board:

```
chipsim ch32v003 chipsharp-output\ch32v003-evt\Ch32v003Clock.bin --ms 3500
```

It prints `12:00:00 (0 ms since start-up)`, `12:00:01 (1001 ms since start-up)` and so on, a second apart.
