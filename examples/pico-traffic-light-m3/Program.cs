// A traffic light for the Raspberry Pi Pico, M3 style: the same lights, displays, button and serial output as
// examples\pico-traffic-light, written with classes, a list, an enum, a table of steps and an event instead of loose
// methods and parameters.
//
// Two lanes take turns. In each turn the lane goes green (4 s), yellow (1 s), red (2 s, both lanes red), then waits
// 1 s more before the other lane's turn starts. The step display counts the step down, the turn display the whole
// turn. A press of the button on GPIO 22 (caught by an interrupt) ends the current green at once.
// Over the serial port (GPIO 0, 115200 baud):
//   road : 1
//       green 4
//       ...
//   lane : 1 interrupted, move to lane 2
//
// Wiring: the same as examples\pico-traffic-light (lane 1 GP16-18, lane 2 GP19-21, step display GP2-8, turn display
// GP9-15, each LED and segment through a 220 ohm resistor, button GP22 to 3V3); diagram.json in this folder is the
// circuit for Wokwi (wokwi.com).
//
// Build (after dotnet build -c Release of the repository), in this folder:
//   dotnet build -c Release
//   ..\..\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build bin\Release\net10.0\PicoTrafficLightM3.dll -t pico
// Simulate (the button pressed at 2.5 s, during lane 1's green):
//   node ..\..\tools\rp2040-sim\run.mjs chipsharp-output\pico\PicoTrafficLightM3.uf2 --ms 16000 --input 22=high@2500ms --input 22=low@2700ms
// Flash:     hold BOOTSEL while plugging the Pico in, then copy PicoTrafficLightM3.uf2 onto the RPI-RP2 drive.
//
// Unlike examples\pico-traffic-light, this one uses the heap (the list, the lanes, the event, the step names), so it
// needs a chip with one: fine on the Pico, too big for a CH32V003.

using System;
using System.Collections.Generic;
using ChipSharp.Hardware;

var lanes = new List<Lane>
{
    new Lane(1, red: 16, yellow: 17, green: 18),
    new Lane(2, red: 19, yellow: 20, green: 21),
};

var crossing = new Crossing(lanes, stepDisplay: new SevenSegment(firstPin: 2), turnDisplay: new SevenSegment(firstPin: 9));
crossing.Interrupted += (lane, next) =>
    Console.WriteLine($"lane : {lane.Number} interrupted, move to lane {next.Number}");

Button.Watch();
crossing.Run();

enum Light { Red, Yellow, Green }

// One step of a lane's turn: which light, for how long, and whether the button may cut it short.
readonly struct Step
{
    public readonly Light Light;
    public readonly int Seconds;
    public readonly bool CanBeCut;

    public Step(Light light, int seconds, bool canBeCut = false)
    {
        Light = light;
        Seconds = seconds;
        CanBeCut = canBeCut;
    }

    // "green", "yellow" or "red", as printed over the serial port
    public string Name => Light.ToString().ToLowerInvariant();
}

// One lane's three lights.
sealed class Lane
{
    readonly int red, yellow, green;

    public Lane(int number, int red, int yellow, int green)
    {
        Number = number;
        this.red = red;
        this.yellow = yellow;
        this.green = green;
        Pin.Output(red);
        Pin.Output(yellow);
        Pin.Output(green);
        Show(Light.Red);                 // every lane starts at red
    }

    public int Number { get; }

    public void Show(Light light)
    {
        Pin.Write(red, light == Light.Red);
        Pin.Write(yellow, light == Light.Yellow);
        Pin.Write(green, light == Light.Green);
    }
}

// A 7-segment display on 7 pins in a row (segments A..G), common cathode.
sealed class SevenSegment
{
    const bool CommonAnode = false;      // true for displays whose segments light when their pin is low

    // which segments each digit lights: bit 0 = A, bit 1 = B, ... bit 6 = G (a constant table: it stays in flash)
    static readonly byte[] Digits = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };

    readonly int firstPin;

    public SevenSegment(int firstPin)
    {
        this.firstPin = firstPin;
        for (int segment = 0; segment < 7; segment++)
        {
            Pin.Output(firstPin + segment);
        }
    }

    public void Show(int digit)
    {
        int pattern = Digits[digit];
        for (int segment = 0; segment < 7; segment++)
        {
            bool on = ((pattern >> segment) & 1) != 0;
            Pin.Write(firstPin + segment, on != CommonAnode);
        }
    }

    public void Blank()
    {
        for (int segment = 0; segment < 7; segment++)
        {
            Pin.Write(firstPin + segment, CommonAnode);
        }
    }
}

// The button on GPIO 22. Its interrupt only sets a flag: a handler must not wait, create objects or store them.
static class Button
{
    const int PinNumber = 22;

    // set by the interrupt, read and cleared by the main loop, so it must be volatile
    static volatile bool pressed;

    public static bool WasPressed => pressed;

    public static void Watch()
    {
        Pin.Input(PinNumber, Pull.Down); // the button to 3V3: low until pressed
        Pin.WatchRising(PinNumber);      // a press runs Pressed() below
    }

    public static void Forget() => pressed = false;

    [Interrupt(Event.Pin)]
    static void Pressed() => pressed = true;
}

// The lanes taking turns, with the two countdown displays.
sealed class Crossing
{
    const int SwitchDelaySeconds = 1;    // the pause before the other lane goes green

    static readonly Step[] Steps =
    {
        new Step(Light.Green, 4, canBeCut: true),
        new Step(Light.Yellow, 1),
        new Step(Light.Red, 2),          // both lanes red
    };

    readonly List<Lane> lanes;
    readonly SevenSegment stepDisplay, turnDisplay;
    int turnLeft;                        // seconds left in the current lane's whole turn

    public Crossing(List<Lane> lanes, SevenSegment stepDisplay, SevenSegment turnDisplay)
    {
        this.lanes = lanes;
        this.stepDisplay = stepDisplay;
        this.turnDisplay = turnDisplay;
    }

    // Raised when the button cuts a green short: the lane, and the lane whose turn comes next.
    public event Action<Lane, Lane>? Interrupted;

    public void Run()
    {
        while (true)
        {
            for (int i = 0; i < lanes.Count; i++)
            {
                Turn(lanes[i], lanes[(i + 1) % lanes.Count]);
            }
        }
    }

    // One lane's turn: its steps, then the pause before the next lane. The other lanes stay red throughout.
    void Turn(Lane lane, Lane next)
    {
        Console.WriteLine($"road : {lane.Number}");
        turnLeft = SecondsFrom(0) + SwitchDelaySeconds;
        Button.Forget();                 // a press before this green doesn't count

        for (int i = 0; i < Steps.Length; i++)
        {
            lane.Show(Steps[i].Light);
            if (CountDown(Steps[i]))
            {
                Interrupted?.Invoke(lane, next);
                turnLeft = SecondsFrom(i + 1) + SwitchDelaySeconds;   // the turn goes on with what is left of it
            }
        }

        // the pause has no step of its own: the step display goes blank, the turn display shows its last second
        stepDisplay.Blank();
        turnDisplay.Show(turnLeft);
        Timing.Delay(TimeSpan.FromSeconds(SwitchDelaySeconds));
    }

    // Counts one step down, second by second, on both displays and over the serial port. Returns true when the
    // button cut it short.
    bool CountDown(Step step)
    {
        for (int left = step.Seconds; left > 0; left--)
        {
            Console.WriteLine($"    {step.Name} {left}");
            stepDisplay.Show(left);
            turnDisplay.Show(turnLeft);
            for (int waited = 0; waited < 1000; waited += 10)
            {
                if (step.CanBeCut && Button.WasPressed)
                {
                    return true;
                }

                Timing.DelayMilliseconds(10);
            }

            turnLeft--;
        }

        return false;
    }

    // The seconds of the steps from the given one to the last.
    static int SecondsFrom(int first)
    {
        int seconds = 0;
        for (int i = first; i < Steps.Length; i++)
        {
            seconds += Steps[i].Seconds;
        }

        return seconds;
    }
}
