# stm8-traffic-light

The Uno's traffic light on the blue STM8S103F3P6 board, without its LCD: red, yellow and green lights, a 7-segment
display that counts the turn down (8 7 6 5 4 3 2 1), a pedestrian button that ends a green at once (the pin event), the
board's LED blinking once a second (the timer event), and over the serial port exactly what the Pico's and the Uno's
traffic lights print, line for line:

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
traffic light. The Uno's table of record structs, its signal and display passed from call to call, and
`Timing.Delay(TimeSpan)` take 10.8 KB as C for SDCC (9.0 KB without the `TimeSpan`, whose code is 64-bit
arithmetic), more than the 8 KB there is; written this way, with no table built and no struct copied from call to
call, it takes 6.4 KB. The LCD's driver alone takes most of the 8 KB (see `stm8-lcd`).

## Wiring

The board's pins are labelled on it without the P: A1, C3, D4, ...

| Board | To |
|---|---|
| PA1 | the red LED |
| PA2 | the yellow LED |
| PA3 | the green LED |
| PC3, PC4, PC5, PC6, PC7, PD2, PD3 | a common-cathode 7-segment display's segments A, B, C, D, E, F, G; its COM to GND |
| PD4 | a push button, to GND |
| PD5 (TX), PD6 (RX) | a 3.3 V USB-to-serial adapter's RX and TX, and GND: the serial output, 115200 baud |
| PB5 | the board's own LED, the heartbeat |

Each LED and segment through a 220 ohm resistor, from the pin to the LED's + (its - to GND). PD4 needs no resistor:
`Pin.Input(pin, Pull.Up)` turns the chip's own pull-up on, which holds it high until the button takes it to GND
(`Pin.WatchFalling`; the STM8S103F3 has no pull-down, so a button to 3V3 would need a resistor to GND). The display takes the seven
push-pull pins of one header but PD1, which is SWIM, the ST-LINK's; PB4 is free (true open-drain, like PB5: it can
only pull low, so it could not light a segment).

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Stm8TrafficLight.dll -t stm8s103-board
chipsharp flash bin\Release\net10.0\Stm8TrafficLight.dll -t stm8s103-board
```

It takes **6.4 KB** of the 8 KB of flash and **54 bytes** of the 1 KB of RAM; `flash` writes it through an ST-LINK V2,
as `stm8-blink`'s README says.

Without a board (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`), the button pressed at 2.5 s,
during lane 1's green:

```
chipsim stm8s103 chipsharp-output\stm8s103-board\Stm8TrafficLight.hex --ms 16000 --input PD4=low@2500ms --input PD4=high@2700ms
```

PA3 (green) goes low 10 ms after the press and PA2 (yellow) high; UART1's lines are the ones above; PC3-PC7, PD2 and
PD3 change once a second (a second a few milliseconds long: 100 waits of 10 ms, and the line printed), PB5 every
500 ms.
