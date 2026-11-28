# ch32v003-timer-blink

Blinks an LED from the timer event while `Main` prints the uptime once a second: `Timing.EveryMilliseconds(500)` runs
the `[Interrupt(Event.Timer)]` method every 500 ms, whatever `Main` is doing, and `Main` writes how long the program
has run (`Stopwatch.GetTimestamp` and `GetElapsedTime`) and how many times the LED has changed. The timer event and
the delays share SysTick's compare; the event comes within a few microseconds of its time, and never drifts.

## Wiring

| Board | To |
|---|---|
| `PD0` | `LED1` on the header P4 (lit while PD0 is low) |
| `PD5` (USART1's TX) | a WCH-LinkE's `RX`: the text on the LinkE's own serial port, at 115200 baud |

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Ch32v003TimerBlink.dll -t ch32v003-evt
chipsharp flash bin\Release\net10.0\Ch32v003TimerBlink.dll -t ch32v003-evt
```

It takes **5.7 KB** of the 16 KB of flash and **72 bytes** of the 2 KB of SRAM. Without a board:

```
chipsim ch32v003 chipsharp-output\ch32v003-evt\Ch32v003TimerBlink.bin --ms 3500
```

It prints the first line on USART1, PD0's changes every 500 ms, and `uptime 1005 ms, 2 toggles` and so on each second
(the second's delay comes after the line before it is sent).
