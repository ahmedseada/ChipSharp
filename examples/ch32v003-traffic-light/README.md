# ch32v003-traffic-light

The STM8S103 board's traffic light on WCH's CH32V003 evaluation board: red, yellow and green lights, a 7-segment
display that counts the turn down (8 7 6 5 4 3 2 1), a pedestrian button that ends a green at once (the pin event),
the board's LED1 blinking once a second (the timer event), and over the serial port exactly what the Pico's, the Uno's
and the STM8's traffic lights print, line for line:

```
road : 1
    green 4
    green 3
    green 2
lane : 1 interrupted, move to lane 2
    yellow 1
    red 2
    red 1
road : 2
    green 4
    ...
```

Two lanes take turns on one set of lights: green 4 s, yellow 1 s, red 2 s while the pedestrians cross, then 1 s more
before the other lane's green; a press during green goes to yellow, and the turn goes on with what is left of it (the
display jumps to 4). No heap: an enum and static classes, the steps a method's parameters, as in the Pico's heap-free
traffic light and the STM8's.

The C# is the STM8's but for four pin numbers, the lights' and the heartbeat's. The CH32V003F4P6 has the STM8S103F3P6's pinout, and ChipSharp
numbers both chips' pins port * 8 + bit, so the display (PC3-PC7, PD2, PD3) and the button (PD4) keep the STM8's pins
and numbers. The lights move from PA1-PA3 to PC0-PC2: the evaluation board wires PA1 and PA2 to its crystal, and the
CH32V003 has no PA3 (its leg is PC0). The heartbeat is the board's LED1, on a wire to PD0, where the STM8 board's own
LED is PB5.

## Wiring

| Board | To |
|---|---|
| `PC0` | the red LED |
| `PC1` | the yellow LED |
| `PC2` | the green LED |
| `PC3`, `PC4`, `PC5`, `PC6`, `PC7`, `PD2`, `PD3` | a common-cathode 7-segment display's segments A, B, C, D, E, F, G; its COM to `GND` |
| `PD4` | a push button, to `GND` |
| `PD0` | `LED1` on the header P4 (from VCC through 1K: lit while PD0 is low), the heartbeat |
| `PD5` (TX), `PD6` (RX) | a WCH-LinkE's RX and TX (or a 3.3 V USB-to-serial adapter's), and `GND`: the serial output, 115200 baud |

Each LED and segment through a 220 ohm resistor, from the pin to the LED's + (its - to GND). PD4 needs no resistor:
`Pin.Input(pin, Pull.Up)` turns the chip's own pull-up on, which holds it high until the button takes it to GND
(`Pin.WatchFalling`). The button is the one pin the program watches, so it alone takes an EXTI line (line 4; PC4, a
segment, is an output and takes none). That uses every pin the board leaves a program: PD1 is SWIO, the WCH-LinkE's,
PD7 NRST, and PA1 and PA2 the crystal's.

There is no `diagram.json`: Wokwi has no CH32V003 part (its boards are the AVRs, the ESP32s, three STM32s and the
Picos).

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Ch32v003TrafficLight.dll -t ch32v003-evt
chipsharp flash bin\Release\net10.0\Ch32v003TrafficLight.dll -t ch32v003-evt
```

It takes **6.6 KB** of the 16 KB of flash and **80 bytes** of the 2 KB of SRAM; `flash` writes it through a WCH-LinkE
(its SWDIO to the board's SWIO, PD1, GND to GND, 3V3 to VCC), as the [README](../../README.md#flashing-the-ch32v003)
says.

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`), the button pressed at 2.5 s,
during lane 1's green:

```
chipsim ch32v003 chipsharp-output\ch32v003-evt\Ch32v003TrafficLight.bin --ms 16000 --input PD4=low@2500ms --input PD4=high@2700ms
```

PC2 (green) goes low within 10 ms of the press and PC1 (yellow) high; USART1's lines are the ones above; PC3-PC7, PD2
and PD3 change once a second (a second a few milliseconds long: 100 waits of 10 ms, and the line printed), PD0 every
500 ms.
