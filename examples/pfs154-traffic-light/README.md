# pfs154-traffic-light

The Uno's traffic light on a Padauk PFS154, with what the chip has: red, yellow and green lights, a 7-segment display
that counts the turn down (8 7 6 5 4 3 2 1), a pedestrian button that ends a green at once (the pin event) and a
heartbeat LED that blinks once a second (the timer event). A turn: green 4 s, yellow 1 s, red 2 s while the pedestrians
cross, then 1 s more before green again; a press during green goes to yellow, and the turn goes on with what is left
of it (the display jumps to 4). No heap: an enum and static classes, the steps a method's parameters.

The PFS154 has no serial port and no I2C bus, so nothing is printed and there is no LCD; and with one set of lights
there are no lanes to tell apart. It has 128 bytes of RAM for the variables and the stack together; the stack takes
about 48 bytes when the timer event comes while a pin is written, so a table of record structs, as the Uno's steps are,
does not fit beside it (as C for SDCC, indexing it takes over 50 bytes).

## Wiring

| PFS154 | To |
|---|---|
| PA3 (pin 3) | the red LED, to GND |
| PA6 (pin 6) | the yellow LED, to GND |
| PA7 (pin 7) | the green LED, to GND |
| PB1-PB7 (pins 9-15) | a common-cathode 7-segment display's segments A-G, its COM to GND |
| PA4 (pin 4) | the heartbeat LED, to GND |
| PA0 (pin 0) | a push button, to GND |
| VDD, GND | 5 V and GND |

Each LED and segment with its resistor. PA0 needs none: `Pin.Input(pin, Pull.Up)` turns the chip's own pull-high on,
which holds it high until the button is pressed (`pfs154-button`'s README says more). PA5 (the reset pin, input or open-drain only) and
PB0 are free.

## Build, flash, simulate

In this folder, after `dotnet build -c Release` of the repository:

```
dotnet build -c Release
chipsharp build bin\Release\net10.0\Pfs154TrafficLight.dll -t pfs154-bare
chipsharp flash bin\Release\net10.0\Pfs154TrafficLight.dll -t pfs154-bare
```

It takes **1381 words** of the 2048 words of program memory and **72 bytes** of the 128 bytes of RAM, which leaves 56
for the stack. `flash` writes it with the Easy PDK programmer (`easypdkprog`), as `pfs154-blink`'s README says.

Without a chip (`chipsim` is `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`), the button pressed at 2.5 s,
during the first green:

```
chipsim pfs154 chipsharp-output\pfs154-bare\Pfs154TrafficLight.hex --ms 16000 --input PA0=low@2500ms --input PA0=high@2700ms
```

PA7 (green) goes low 6 ms after the press and PA6 (yellow) high; then PA3 (red) at 3.5 s, green again at 6.5 s, and so
on, each second a few milliseconds long (100 waits of 10 ms and the checks between them); PB1-PB7 change once a
second, PA4 every 500 ms.
