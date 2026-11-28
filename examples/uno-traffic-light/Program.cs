// A traffic light on an Arduino Uno, with a 16x2 character LCD on I2C that shows the lane and the time left:
//
//     +----------------+
//     |Road 1   turn 8s|      the lane, and the seconds left in its whole turn
//     |green         4s|      the step, and its seconds left ("switching" in the pause before the next lane)
//     +----------------+
//
// It is the Pico traffic light with an LCD (PicoTrafficLightLcd) brought to the Uno, which has 2 KB of RAM and no
// heap: what made objects there is written here with what needs none. The lanes and the steps are record structs
// in static tables (in RAM), the signal is a struct of three pins, the crossing a static class; the lanes take turns
// in a plain loop instead of an iterator, the interruption is a method instead of an event and a lambda, the names of
// the lights are literals (fixed text in flash) instead of Light.ToString().ToLowerInvariant(), and the LCD's lines
// are written as literals and numbers (Lcd.Line, then Lcd.Write of a number) instead of $"..." strings. It prints
// exactly what the Pico's does over the serial port, line for line, and shows the same on the LCD.
//
// Two lanes take turns. In each turn the active lane goes green (4 s), yellow (1 s), red (2 s), then waits 1 s more
// before the other lane's turn; one set of three lights shows the active lane's light, and the LCD and the serial
// output say which lane that is. A 7-segment display counts the whole turn down (8 7 6 5 4 3 2 1). A press of the
// button ends the current green at once: the lane goes to yellow, and the turn goes on with what is left of it. The
// Uno's LED "L" (D13) blinks once a second from the timer event, whatever the main loop is doing.
// Over the serial port (USART0: D1 sends, 115200 baud; the Arduino IDE's serial monitor, or Wokwi's), every second:
//   road : 1
//       green 4
//       green 3
//       ...
// and when the button cuts a green short:  lane : 1 interrupted, move to lane 2
//
// Wiring (diagram.json is the same circuit for Wokwi):
//   the lights:      red A0, yellow A1, green A2 (each LED: the pin -> 220 ohm resistor -> LED + ; LED - -> GND)
//   the display:     a common-cathode 7-segment display, segments A D3, B D4, C D7, D D8, E D9, F D11, G D10 (each
//                    through a 220 ohm resistor), COM to GND
//   the button:      between D2 and GND (Pull.Up turns the pin's own pull-up on, so it needs no resistor)
//   the LCD:         its I2C backpack (address 0x27): SDA to A4, SCL to A5, VCC to 5V, GND to GND
// D0 and D1 are the serial port's, D13 the LED "L"'s; D5, D6, D12 and A3 are free. Without an LCD connected,
// Lcd.Open returns false and the traffic light runs without it.
//
// Build it with ChipSharp for the target "uno", then flash the .hex through the Uno's USB port (see the README):
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\UnoTrafficLight.dll -t uno
//   chipsharp flash bin\Release\net10.0\UnoTrafficLight.dll -t uno
// Simulate (the button pressed at 2.5 s, during lane 1's green; an LCD at 0x27 on the Uno's I2C bus):
//   node ..\..\tools\avr-sim\run.mjs chipsharp-output\uno\UnoTrafficLight.hex --ms 16000 --input D2=low@2500ms
//     --input D2=high@2700ms --i2c 0:0x27=lcd1602

using System;
using ChipSharp.Components;
using ChipSharp.Hardware;

var signal = new Signal(red: 14, yellow: 15, green: 16);         // A0, A1, A2
Screen.Open();
var turnDisplay = new SevenSegment(a: 3, b: 4, c: 7, d: 8, e: 9, f: 11, g: 10);

Heartbeat.Start();
Button.Watch();
Crossing.Run(signal, turnDisplay);

enum Light { Red, Yellow, Green }

// One step of a lane's turn: which light, for how long, and whether the button may cut it short.
readonly record struct Step(Light Light, int Seconds, bool CanBeCut = false)
{
    // "green", "yellow" or "red", as printed over the serial port and on the LCD: literals, so no string is made
    public string Name => Light switch
    {
        Light.Green => "green",
        Light.Yellow => "yellow",
        _ => "red",
    };
}

// A lane: its number is all it needs, as one signal shows whichever lane is active.
readonly record struct Lane(int Number);

// The three lights, shared by the lanes: they show the active lane's light.
readonly struct Signal
{
    readonly (int Red, int Yellow, int Green) pins;

    public Signal(int red, int yellow, int green)
    {
        pins = (red, yellow, green);
        Pin.Output(red);
        Pin.Output(yellow);
        Pin.Output(green);
        Show(Light.Red);                 // it starts at red
    }

    public void Show(Light light)
    {
        var (red, yellow, green) = pins;
        Pin.Write(red, light == Light.Red);
        Pin.Write(yellow, light == Light.Yellow);
        Pin.Write(green, light == Light.Green);
    }
}

// The 16x2 LCD on the Uno's I2C bus (bus 0: SDA A4, SCL A5). A whole line is written only when it changes (a lane's
// turn, a step); each second only the digits change, single characters, so updating the screen takes about a
// millisecond and the timing stays as it was. The lines are fixed text and numbers written where they go, so no
// string is made: "Road 1   turn 8s" is "Road     turn  s" with the lane in column 5 and the seconds in column 14.
static class Screen
{
    const int Bus = 0;
    const int SecondsColumn = 14;        // "...8s": the digit in column 14, the "s" in column 15

    public static void Open()
    {
        I2c.Open(Bus, sda: 18, scl: 19, frequency: 100_000);    // A4 and A5
        Lcd.Open(Bus, 0x27, columns: 16, rows: 2);   // false when no LCD answers: the Lcd calls then do nothing
    }

    public static void Road(int lane, int turnLeft)
    {
        Lcd.Line(0, "Road     turn  s");
        Lcd.Write(5, 0, lane);
        Lcd.Write(SecondsColumn, 0, turnLeft);
    }

    public static void Step(string name, int left)
    {
        Lcd.Line(1, name);               // the name, then spaces to the end of the row
        Lcd.Write(SecondsColumn, 1, left);
        Lcd.Write(SecondsColumn + 1, 1, 's');
    }

    public static void Switching() => Lcd.Line(1, "switching");

    public static void Seconds(int left, int turnLeft)
    {
        Lcd.Write(SecondsColumn, 1, left);
        Lcd.Write(SecondsColumn, 0, turnLeft);
    }

    public static void TurnSeconds(int turnLeft) => Lcd.Write(SecondsColumn, 0, turnLeft);
}

// The Uno's LED "L" (D13), toggled by the timer event every half second: it blinks once a second whatever the main
// loop is doing, which shows on a real board that the program runs and the timer keeps time.
static class Heartbeat
{
    const int Led = 13;

    public static void Start()
    {
        Pin.Output(Led);
        Timing.EveryMilliseconds(500);   // Tick runs every 500 ms from now on (Timer1's compare B)
    }

    [Interrupt(Event.Timer)]
    static void Tick() => Pin.Toggle(Led);
}

// The button on D2, to GND. Its interrupt only sets a flag: a handler must not wait, create objects or store them.
static class Button
{
    const int PinNumber = 2;

    // set by the interrupt, read and cleared by the main loop, so it must be volatile
    static volatile bool pressed;

    public static bool WasPressed => pressed;

    public static void Watch()
    {
        Pin.Input(PinNumber, Pull.Up);   // high until pressed
        Pin.WatchFalling(PinNumber);     // a press (D2 to GND) runs Pressed() below
    }

    public static void Forget() => pressed = false;

    [Interrupt(Event.Pin)]
    static void Pressed() => pressed = true;
}

// The lanes taking turns, with the signal, the turn's countdown display and the LCD.
static class Crossing
{
    const int SwitchDelaySeconds = 1;    // the pause before the other lane goes green

    static readonly Lane[] Lanes = [new(1), new(2)];

    static readonly Step[] Steps =
    [
        new(Light.Green, 4, CanBeCut: true),
        new(Light.Yellow, 1),
        new(Light.Red, 2),               // both lanes red
    ];

    static int turnLeft;                 // seconds left in the current lane's whole turn

    public static void Run(Signal signal, SevenSegment turnDisplay)
    {
        while (true)
        {
            // each lane in order, with the lane whose turn comes after it
            for (int i = 0; i < Lanes.Length; i++)
            {
                Turn(Lanes[i], Lanes[(i + 1) % Lanes.Length], signal, turnDisplay);
            }
        }
    }

    // One lane's turn: its steps on the signal, then the pause before the next lane (the signal stays red).
    static void Turn(Lane lane, Lane next, Signal signal, SevenSegment turnDisplay)
    {
        Console.WriteLine($"road : {lane.Number}");
        turnLeft = SecondsFrom(0) + SwitchDelaySeconds;
        Screen.Road(lane.Number, turnLeft);
        Button.Forget();                 // a press before this green doesn't count

        for (int i = 0; i < Steps.Length; i++)
        {
            var step = Steps[i];
            signal.Show(step.Light);
            Screen.Step(step.Name, step.Seconds);
            if (CountDown(step, turnDisplay))
            {
                Interrupted(lane, next);
                turnLeft = SecondsFrom(i + 1) + SwitchDelaySeconds;   // the turn goes on with what is left of it
            }
        }

        // the pause has no step of its own: the LCD says "switching", the turn display shows its last second
        turnDisplay.Show(turnLeft);
        Screen.Switching();
        Screen.TurnSeconds(turnLeft);
        Timing.Delay(TimeSpan.FromSeconds(SwitchDelaySeconds));
    }

    // When the button cuts a green short: the lane, and the lane whose turn comes next.
    static void Interrupted(Lane lane, Lane next) =>
        Console.WriteLine($"lane : {lane.Number} interrupted, move to lane {next.Number}");

    // Counts one step down, second by second, on the LCD, the turn display and over the serial port. Returns true when
    // the button cut it short.
    static bool CountDown(Step step, SevenSegment turnDisplay)
    {
        for (int left = step.Seconds; left > 0; left--)
        {
            Console.WriteLine($"    {step.Name} {left}");
            turnDisplay.Show(turnLeft);
            Screen.Seconds(left, turnLeft);
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
        foreach (var step in Steps.AsSpan(first))
        {
            seconds += step.Seconds;
        }

        return seconds;
    }
}
