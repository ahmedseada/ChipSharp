// A traffic light on WCH's CH32V003 evaluation board: red, yellow and green LEDs, a pedestrian button and a 7-segment
// countdown, with no heap, in the chip's 16 KB of flash and 2 KB of SRAM.
//
// It is the STM8S103 board's traffic light (examples/stm8-traffic-light), and so the Uno's without its LCD, with four
// pin numbers changed, and it prints exactly what the Pico's, the Uno's and the STM8's traffic lights print, line for
// line. The CH32V003F4P6 has the STM8S103F3P6's pinout, and ChipSharp numbers both chips' pins port * 8 + bit, so the
// display and the button keep the STM8's pins and numbers (PC3-PC7, PD2, PD3; PD4). The lights move from PA1-PA3 to
// PC0-PC2: the board wires PA1 and PA2 to its crystal, and the CH32V003 has no PA3 (that leg is PC0). The heartbeat
// moves from the STM8 board's LED (PB5) to the wire from LED1 (PD0). It is written as the heap-free Pico traffic light
// is (examples/pico-traffic-light): the steps of a turn are calls rather than a table, the pins are constants, and the
// display and the signal are static.
//
// Two lanes take turns. In each turn the active lane goes green (4 s), yellow (1 s), red (2 s), then waits 1 s more
// before the other lane's turn; one set of three lights shows the active lane's light, and the serial output says
// which lane that is. The 7-segment display counts the whole turn down (8 7 6 5 4 3 2 1). A press of the button (a
// pedestrian asking to cross) ends the current green at once: the lane goes to yellow, and the turn goes on with what
// is left of it. The board's LED1 (on PD0) blinks once a second from the timer event, whatever the main loop is doing.
// Over the serial port (USART1: PD5 sends, 115200 baud; a WCH-LinkE's RX on PD5), every second:
//   road : 1
//       green 4
//       green 3
//       ...
// and when the button cuts a green short:  lane : 1 interrupted, move to lane 2
//
// Wiring (README.md has it pin by pin, by the labels on the board: PC0, PD4, ...):
//   the lights:      red PC0, yellow PC1, green PC2 (each LED: the pin -> 220 ohm resistor -> LED + ; LED - -> GND)
//   the display:     a common-cathode 7-segment display, segments A PC3, B PC4, C PC5, D PC6, E PC7, F PD2, G PD3
//                    (each through a 220 ohm resistor), COM to GND
//   the button:      between PD4 and GND (Pull.Up: the pin's own pull-up holds it high until the button is pressed)
//   the heartbeat:   a wire from the header P4's LED1 to PD0 (LED1 is wired from VCC through 1K: lit while PD0 is low)
// PD5 and PD6 are the serial port's, PD1 the WCH-LinkE's (SWIO), PD7 NRST; PA1 and PA2 the crystal's. That leaves no
// pin free: the board's twelve others are all used.
//
// Build it with ChipSharp for the target "ch32v003-evt", then flash the .bin through a WCH-LinkE (see README.md):
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\Ch32v003TrafficLight.dll -t ch32v003-evt
//   chipsharp flash bin\Release\net10.0\Ch32v003TrafficLight.dll -t ch32v003-evt
// Simulate (the button pressed at 2.5 s, during lane 1's green):
//   chipsim ch32v003 chipsharp-output\ch32v003-evt\Ch32v003TrafficLight.bin --ms 16000 --input PD4=low@2500ms
//     --input PD4=high@2700ms

using System;
using ChipSharp.Components;
using ChipSharp.Hardware;

Signal.Start();
Crossing.Start();
Heartbeat.Start();
Button.Watch();
while (true)
{
    Crossing.Turn(lane: 1, next: 2);
    Crossing.Turn(lane: 2, next: 1);
}

enum Light { Red, Yellow, Green }

// The three lights, shared by the lanes: they show the active lane's light.
static class Signal
{
    const int Red = 16, Yellow = 17, Green = 18;     // PC0, PC1, PC2

    public static void Start()
    {
        Pin.Output(Red);
        Pin.Output(Yellow);
        Pin.Output(Green);
        Show(Light.Red);                 // it starts at red
    }

    public static void Show(Light light)
    {
        Pin.Write(Red, light == Light.Red);
        Pin.Write(Yellow, light == Light.Yellow);
        Pin.Write(Green, light == Light.Green);
    }
}

// The board's LED1 (wired to PD0), toggled by the timer event every half second: it blinks once a second whatever the
// main loop is doing, which shows on a real board that the program runs and the timer keeps time.
static class Heartbeat
{
    const int Led = 24;                  // PD0

    public static void Start()
    {
        Pin.Output(Led);
        Timing.EveryMilliseconds(500);   // Tick runs every 500 ms from now on (SysTick's compare)
    }

    [Interrupt(Event.Timer)]
    static void Tick() => Pin.Toggle(Led);
}

// The button on PD4, to GND. Its interrupt only sets a flag: a handler must not wait, create objects or store them.
static class Button
{
    const int PinNumber = 28;            // PD4: EXTI line 4, the one pin the program watches

    // set by the interrupt, read and cleared by the main loop, so it must be volatile
    static volatile bool pressed;

    public static bool WasPressed => pressed;

    public static void Watch()
    {
        Pin.Input(PinNumber, Pull.Up);   // high, through the pin's pull-up, until pressed
        Pin.WatchFalling(PinNumber);     // a press (PD4 to GND) runs Pressed() below
    }

    public static void Forget() => pressed = false;

    [Interrupt(Event.Pin)]
    static void Pressed() => pressed = true;
}

// A lane's turn on the signal, counted down on the 7-segment display and over the serial port.
static class Crossing
{
    const int GreenSeconds = 4;
    const int YellowSeconds = 1;
    const int RedSeconds = 2;            // both lanes red
    const int SwitchDelaySeconds = 1;    // the pause before the other lane goes green

    static SevenSegment turnDisplay;     // the seconds left in the lane's whole turn
    static int turnLeft;

    public static void Start() =>
        turnDisplay = new SevenSegment(a: 19, b: 20, c: 21, d: 22, e: 23, f: 26, g: 27);   // PC3-PC7, PD2, PD3

    // One lane's turn: green, yellow, red, then the pause before the next lane (the signal stays red).
    public static void Turn(int lane, int next)
    {
        Console.WriteLine($"road : {lane}");
        turnLeft = GreenSeconds + YellowSeconds + RedSeconds + SwitchDelaySeconds;
        Button.Forget();                 // a press before this green doesn't count

        Signal.Show(Light.Green);
        if (CountDown(Light.Green, GreenSeconds, canBeCut: true))
        {
            Console.WriteLine($"lane : {lane} interrupted, move to lane {next}");
            turnLeft = YellowSeconds + RedSeconds + SwitchDelaySeconds;   // the turn goes on with what is left of it
        }

        Signal.Show(Light.Yellow);
        CountDown(Light.Yellow, YellowSeconds, canBeCut: false);
        Signal.Show(Light.Red);
        CountDown(Light.Red, RedSeconds, canBeCut: false);

        // the pause has no step of its own: the display shows the turn's last second
        turnDisplay.Show(turnLeft);
        Timing.DelayMilliseconds(SwitchDelaySeconds * 1000);
    }

    // Counts one step down, second by second, on the display and over the serial port ("    green 4"). Returns true
    // when the button cut it short (only a green can be).
    static bool CountDown(Light light, int seconds, bool canBeCut)
    {
        for (int left = seconds; left > 0; left--)
        {
            Console.WriteLine($"    {Name(light)} {left}");
            turnDisplay.Show(turnLeft);
            for (int waited = 0; waited < 1000; waited += 10)
            {
                if (canBeCut && Button.WasPressed)
                {
                    return true;
                }

                Timing.DelayMilliseconds(10);
            }

            turnLeft--;
        }

        return false;
    }

    // "green", "yellow" or "red": literals, fixed text in flash, so no string is made
    static string Name(Light light) => light switch
    {
        Light.Green => "green",
        Light.Yellow => "yellow",
        _ => "red",
    };
}
