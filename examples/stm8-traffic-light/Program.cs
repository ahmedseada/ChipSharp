// A traffic light on the blue STM8S103F3P6 board: red, yellow and green LEDs, a pedestrian button and a 7-segment
// countdown, with no heap, in the board's 8 KB of flash and 1 KB of RAM.
//
// It is the Uno's traffic light (examples/uno-traffic-light) without its LCD, whose driver alone takes most of the
// 8 KB, and it prints exactly what the Pico's and the Uno's traffic lights print, line for line. To fit, it is written
// as the heap-free Pico traffic light is (examples/pico-traffic-light): the steps of a turn are calls rather than a
// table, the pins are constants, and the display and the signal are static rather than passed from call to call, so
// that the 8-bit STM8 copies no struct around.
//
// Two lanes take turns. In each turn the active lane goes green (4 s), yellow (1 s), red (2 s), then waits 1 s more
// before the other lane's turn; one set of three lights shows the active lane's light, and the serial output says
// which lane that is. The 7-segment display counts the whole turn down (8 7 6 5 4 3 2 1). A press of the button (a
// pedestrian asking to cross) ends the current green at once: the lane goes to yellow, and the turn goes on with what
// is left of it. The board's LED (PB5) blinks once a second from the timer event, whatever the main loop is doing.
// Over the serial port (UART1: PD5 sends, 115200 baud), every second:
//   road : 1
//       green 4
//       green 3
//       ...
// and when the button cuts a green short:  lane : 1 interrupted, move to lane 2
//
// Wiring (README.md has it pin by pin, by the labels on the board: A1, C3, D4, ...):
//   the lights:      red PA1, yellow PA2, green PA3 (each LED: the pin -> 220 ohm resistor -> LED + ; LED - -> GND)
//   the display:     a common-cathode 7-segment display, segments A PC3, B PC4, C PC5, D PC6, E PC7, F PD2, G PD3
//                    (each through a 220 ohm resistor), COM to GND
//   the button:      between PD4 and 3V3, with a 10 kohm resistor from PD4 to GND (Pin.Input leaves the STM8's
//                    pull-up off, and it has no pull-down)
// PD5 and PD6 are the serial port's, PD1 the ST-LINK's (SWIM), PB5 the LED's; PB4 is free.
//
// Build it with ChipSharp for the target "stm8s103-board", then flash the .hex with an ST-LINK V2 (see README.md):
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\Stm8TrafficLight.dll -t stm8s103-board
//   chipsharp flash bin\Release\net10.0\Stm8TrafficLight.dll -t stm8s103-board
// Simulate (the button pressed at 2.5 s, during lane 1's green):
//   chipsim stm8s103 chipsharp-output\stm8s103-board\Stm8TrafficLight.hex --ms 16000 --input PD4=high@2500ms
//     --input PD4=low@2700ms

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
    const int Red = 1, Yellow = 2, Green = 3;        // PA1, PA2, PA3

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

// The board's LED (PB5), toggled by the timer event every half second: it blinks once a second whatever the main loop
// is doing, which shows on a real board that the program runs and the timer keeps time.
static class Heartbeat
{
    const int Led = 13;                  // PB5

    public static void Start()
    {
        Pin.Output(Led);
        Timing.EveryMilliseconds(500);   // Tick runs every 500 ms from now on (TIM1's compare 1)
    }

    [Interrupt(Event.Timer)]
    static void Tick() => Pin.Toggle(Led);
}

// The button on PD4, to 3V3. Its interrupt only sets a flag: a handler must not wait, create objects or store them.
static class Button
{
    const int PinNumber = 28;            // PD4

    // set by the interrupt, read and cleared by the main loop, so it must be volatile
    static volatile bool pressed;

    public static bool WasPressed => pressed;

    public static void Watch()
    {
        Pin.Input(PinNumber);            // low, through the resistor to GND, until pressed
        Pin.WatchRising(PinNumber);      // a press (PD4 to 3V3) runs Pressed() below
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
