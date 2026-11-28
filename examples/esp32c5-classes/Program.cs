// A traffic light for the ESP32-C5-DevKitC-1 that shows what its heap is for, M3 style: a record struct for each lane,
// a class for each signal, an iterator for the steps of a turn, a List of lanes, strings, and a button whose interrupt
// cuts a green short. It is the Pico's examples\pico-traffic-light-m3 without the displays and with the status lines
// over the USB console; an LCD comes with I2C.
//
// Two lanes take turns. In each turn the lane goes green (4 s), yellow (1 s), red (2 s, both lanes red). The console
// (the USB-C port marked "USB") says each second what the turn is and what the signal shows:
//   turn 1   signal: green 4
//   turn 1   signal: green 3
//   turn 1   signal: yellow 1
//   turn 1   signal: red 2
//   turn 1 : green cut short
//   turn 2   signal: green 4
// A press of the button (an interrupt that only sets a flag) ends the current green at once.
//
// Wiring (the board's labels; each LED's anode to its pin through a resistor of 220 ohm, its cathode to "GND"):
//   lane 1: red GPIO 4 ("4"), yellow GPIO 5 ("5"), green GPIO 6 ("6")
//   lane 2: red GPIO 10 ("10"), yellow GPIO 23 ("23"), green GPIO 24 ("24")
//   button: GPIO 9 ("9") to one leg of a push button, the other leg to "GND" (Pull.Up: no resistor)
// These are pins the board leaves a program (0, 1, 4, 5, 6, 8, 9, 10, 23 and 24); the board's RGB LED (GPIO 27) is not one.
//
// Build (after dotnet build -c Release of the repository), in this folder:
//   dotnet build -c Release
//   ..\..\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build bin\Release\net10.0\Esp32c5Classes.dll -t esp32c5-devkitc
// Flash:     chipsharp flash bin\Release\net10.0\Esp32c5Classes.dll -t esp32c5-devkitc (see README.md)
// Simulate (the button pressed at 2.5 s, during lane 1's green):
//   chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5Classes.bin --ms 16000 --input GPIO9=low@2500ms --input GPIO9=high@2700ms

using System;
using System.Collections.Generic;
using ChipSharp.Hardware;

var lanes = new List<Lane>
{
    new Lane(1, Red: 4, Yellow: 5, Green: 6),
    new Lane(2, Red: 10, Yellow: 23, Green: 24),
};

Button.Watch();
var signals = new List<Signal>();
foreach (var lane in lanes)
{
    signals.Add(new Signal(lane));
}

while (true)
{
    for (int i = 0; i < signals.Count; i++)
    {
        signals[i].Turn();
    }
}

enum Light { Red, Yellow, Green }

// One lane's three pins: a value, kept in a list.
readonly record struct Lane(int Number, int Red, int Yellow, int Green);

// The button on GPIO 9. Its interrupt only sets a flag: a handler must not wait, create objects or store them.
static class Button
{
    const int PinNumber = 9;

    // set by the interrupt, read and cleared by the main loop, so it must be volatile
    static volatile bool pressed;

    public static bool WasPressed => pressed;

    public static void Watch()
    {
        Pin.Input(PinNumber, Pull.Up);   // the button to GND: high until pressed
        Pin.WatchFalling(PinNumber);     // a press runs Pressed() below
    }

    public static void Forget() => pressed = false;

    [Interrupt(Event.Pin)]
    static void Pressed() => pressed = true;
}

// A lane's signal: shows one light at a time and runs the lane's turn.
sealed class Signal
{
    readonly Lane lane;
    static int turns;

    public Signal(Lane lane)
    {
        this.lane = lane;
        Pin.Output(lane.Red);
        Pin.Output(lane.Yellow);
        Pin.Output(lane.Green);
        Show(Light.Red);                 // every lane starts at red
    }

    void Show(Light light)
    {
        Pin.Write(lane.Red, light == Light.Red);
        Pin.Write(lane.Yellow, light == Light.Yellow);
        Pin.Write(lane.Green, light == Light.Green);
    }

    // The turn's steps, one per second: the light, and the seconds left of its step (Turn skips the rest of a green
    // the button cuts).
    static IEnumerable<(Light Light, int Left)> Steps()
    {
        for (int left = 4; left > 0; left--)
        {
            yield return (Light.Green, left);
        }

        yield return (Light.Yellow, 1);
        for (int left = 2; left > 0; left--)
        {
            yield return (Light.Red, left);
        }
    }

    public void Turn()
    {
        turns++;
        Button.Forget();                 // a press before this green doesn't count
        var cut = false;
        foreach (var (light, left) in Steps())
        {
            if (cut && light == Light.Green)
            {
                continue;                // the rest of the green is skipped
            }

            Show(light);
            Console.WriteLine($"turn {turns}   signal: {light.ToString().ToLowerInvariant()} {left}");
            for (int waited = 0; waited < 1000; waited += 10)
            {
                if (light == Light.Green && Button.WasPressed)
                {
                    cut = true;          // cut at once, not at the next second
                    Console.WriteLine($"turn {turns} : green cut short");
                    Button.Forget();
                    break;
                }

                Timing.DelayMilliseconds(10);
            }
        }

        Show(Light.Red);
    }
}
