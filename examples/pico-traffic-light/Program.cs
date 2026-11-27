// A traffic light for the Raspberry Pi Pico: two lanes, two countdown displays and a button, with no heap. It uses
// only what compiles on a chip without one: static methods, arrays kept in Main and passed down, a ref parameter,
// fixed text, a volatile static field and an interrupt. examples\pico-traffic-light-m3 is the same program written
// with classes, a list and an event, and prints exactly the same.
//
// Two lanes take turns. In each turn the lane goes green (4 s), yellow (1 s), red (2 s, both lanes red), then
// waits 1 s more before the other lane's turn starts. The other lane stays red during the whole turn.
// Two 7-segment displays count down:
//   step display:  seconds left in the current step (green 4 3 2 1, yellow 1, red 2 1; blank during the pause)
//   turn display:  seconds left in the lane's whole turn (8 7 6 5 4 3 2 1)
// A press of the button on GPIO 22 (caught by an interrupt) ends the current green at once: the lane goes to yellow,
// and the turn display drops to what is left of the turn (yellow + red + pause = 4). A press during yellow or red
// doesn't count.
// Over the serial port (GPIO 0, 115200 baud), every second:
//   road : 1
//       green 4
//       green 3
//       ...
//       yellow 1
//       red 2
//       red 1
// and when the button cuts a green short:  lane : 1 interrupted, move to lane 2
//
// Wiring (each LED: GPIO -> 220 ohm resistor -> LED long leg (+); LED short leg (-) -> GND):
//   lane 1:        red GP16 (pin 21), yellow GP17 (pin 22), green GP18 (pin 24)
//   lane 2:        red GP19 (pin 25), yellow GP20 (pin 26), green GP21 (pin 27)
//   step display:  segments A..G on GP2..GP8 (pins 4, 5, 6, 7, 9, 10, 11), COM to GND
//   turn display:  segments A..G on GP9..GP15 (pins 12, 14, 15, 16, 17, 19, 20), COM to GND
//                  (each segment through a 220 ohm resistor; common-cathode displays: for common-anode ones set
//                  CommonAnode to true and connect COM to 3V3)
//   button:        between GP22 (pin 29) and 3V3(OUT) (pin 36)
//   GND:           pins 23, 28, 33 or 38
// diagram.json in this folder is the same circuit for Wokwi (wokwi.com): see the README.
//
// Build (after dotnet build -c Release of the repository), in this folder:
//   dotnet build -c Release
//   ..\..\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build bin\Release\net10.0\PicoTrafficLight.dll -t pico
// That makes chipsharp-output\pico\PicoTrafficLight.uf2, and says "RAM ... of 264 KB" with no heap after it.
// Simulate (the button pressed at 2.5 s, during lane 1's green):
//   node ..\..\tools\rp2040-sim\run.mjs chipsharp-output\pico\PicoTrafficLight.uf2 --ms 16000 --input 22=high@2500ms --input 22=low@2700ms
// Flash:     hold BOOTSEL while plugging the Pico in, then copy PicoTrafficLight.uf2 onto the RPI-RP2 drive.

using System;
using ChipSharp.Hardware;

const int Lane1Red = 16, Lane1Yellow = 17, Lane1Green = 18;
const int Lane2Red = 19, Lane2Yellow = 20, Lane2Green = 21;
const int Button = 22;
const bool CommonAnode = false;          // true for displays whose segments light when their pin is low

const int GreenSeconds = 4;
const int YellowSeconds = 1;
const int RedSeconds = 2;                // both lanes red
const int SwitchDelaySeconds = 1;        // the pause before the other lane goes green

int[] lights = { Lane1Red, Lane1Yellow, Lane1Green, Lane2Red, Lane2Yellow, Lane2Green };
int[] stepDisplay = { 2, 3, 4, 5, 6, 7, 8 };          // segments A..G
int[] turnDisplay = { 9, 10, 11, 12, 13, 14, 15 };    // segments A..G
byte[] digits =
{
    // which segments each digit lights: bit 0 = A, bit 1 = B, ... bit 6 = G
    0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F,
};

for (int i = 0; i < lights.Length; i++)
{
    Pin.Output(lights[i]);
}

for (int i = 0; i < 7; i++)
{
    Pin.Output(stepDisplay[i]);
    Pin.Output(turnDisplay[i]);
}

Pin.Input(Button);
Pin.WatchRising(Button);                 // a press runs Pressed() below

// both lanes start at red
Pin.Write(Lane1Red, true);
Pin.Write(Lane2Red, true);

while (true)
{
    Turn(1, Lane1Red, Lane1Yellow, Lane1Green, stepDisplay, turnDisplay, digits);
    Turn(2, Lane2Red, Lane2Yellow, Lane2Green, stepDisplay, turnDisplay, digits);
}

// One lane's turn: green, yellow, red, then the pause before the other lane. The other lane stays red throughout.
static void Turn(int lane, int red, int yellow, int green, int[] stepDisplay, int[] turnDisplay, byte[] digits)
{
    Console.WriteLine($"road : {lane}");
    int turnLeft = GreenSeconds + YellowSeconds + RedSeconds + SwitchDelaySeconds;

    cutShort = false;                    // a press before this green doesn't count
    Pin.Write(red, false);
    Pin.Write(green, true);
    bool wasCut = Step("green", GreenSeconds, true, ref turnLeft, stepDisplay, turnDisplay, digits);
    if (wasCut)
    {
        Console.WriteLine($"lane : {lane} interrupted, move to lane {(lane == 1 ? 2 : 1)}");

        // the turn goes on with what is left of it: yellow, red and the pause
        turnLeft = YellowSeconds + RedSeconds + SwitchDelaySeconds;
    }

    Pin.Write(green, false);
    Pin.Write(yellow, true);
    Step("yellow", YellowSeconds, false, ref turnLeft, stepDisplay, turnDisplay, digits);

    Pin.Write(yellow, false);
    Pin.Write(red, true);
    Step("red", RedSeconds, false, ref turnLeft, stepDisplay, turnDisplay, digits);

    // the pause has no step of its own: the step display goes blank, the turn display shows its last second
    Blank(stepDisplay);
    Show(turnLeft, turnDisplay, digits);
    Timing.Delay(TimeSpan.FromSeconds(SwitchDelaySeconds));
}

// Counts one step down, second by second, on the step display and over the serial port ("    green 4"), and the
// whole turn along with it on the turn display. A step that can be cut (green) ends at once when the button is
// pressed; it returns true then.
static bool Step(string name, int seconds, bool canBeCut, ref int turnLeft, int[] stepDisplay, int[] turnDisplay,
    byte[] digits)
{
    for (int left = seconds; left > 0; left--)
    {
        Console.WriteLine($"    {name} {left}");
        Show(left, stepDisplay, digits);
        Show(turnLeft, turnDisplay, digits);
        for (int waited = 0; waited < 1000; waited += 10)
        {
            if (canBeCut && cutShort)
            {
                return true;
            }

            Timing.DelayMilliseconds(10);
        }

        turnLeft--;
    }

    return false;
}

// Lights the segments of one digit (0 to 9) on a display.
static void Show(int digit, int[] display, byte[] digits)
{
    int pattern = digits[digit];
    for (int segment = 0; segment < 7; segment++)
    {
        bool on = ((pattern >> segment) & 1) != 0;
        Pin.Write(display[segment], on != CommonAnode);
    }
}

// Turns every segment of a display off.
static void Blank(int[] display)
{
    for (int segment = 0; segment < 7; segment++)
    {
        Pin.Write(display[segment], CommonAnode);
    }
}

partial class Program
{
    // set by the interrupt, read and cleared by the main loop, so it must be volatile
    static volatile bool cutShort;

    [Interrupt(Event.Pin)]
    static void Pressed()
    {
        cutShort = true;
    }
}
