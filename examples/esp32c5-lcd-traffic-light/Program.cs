// ESP32-C5 traffic light with an LCD: the final M3 style (examples\pico-traffic-light-m3) with a heartbeat LED and a
// 16x2 character LCD on I2C that shows the lane and the time left. It is the Pico's PicoTrafficLightLcd program as it
// is: only the pin numbers, the I2C bus and this comment differ.
//
//     +----------------+
//     |Road 1   turn 8s|      the lane, and the seconds left in its whole turn
//     |green         4s|      the step, and its seconds left ("switching" in the pause before the next lane)
//     +----------------+
//
// One set of three lights shows the active lane's light (green, yellow, red, like a real traffic light: red on top);
// the LCD and the console say which lane that is. The LCD shows the step's seconds, and one 7-segment display
// counts the whole turn down (8 7 6 5 4 3 2 1). The button cuts a green short; the console is the USB Serial/JTAG
// port (the board's USB-C port marked USB). Without an LCD connected, Lcd.Open returns false and the traffic light
// runs without it. The LCD (Lcd) and the 7-segment display (SevenSegment: no digit table in this program) come from
// ChipSharp.Components, the parts you connect to a chip; Pin, Timing and I2c from ChipSharp.Hardware.
//
// Wiring on the ESP32-C5-DevKitC-1 (pins labelled by their GPIO number). 14 pins are needed and the board leaves only
// 10 plain ones, so some are special pins, chosen so that nothing wired to them can change how the chip boots:
//   lights      red 6, yellow 8, green 9 (each LED's anode through 220 ohm, the cathode to GND): plain pins
//   button      between 10 and 3V3 (Pull.Down: the pin reads low until it is pressed): a plain pin, as a button held
//               down at reset on a strapping pin could pick another boot mode
//   LCD         I2C backpack at 0x27 on I2C bus 0: SDA to 4, SCL to 5, VCC to 5V (3V3 if the module works on it),
//               GND to GND: 4 and 5 are the JTAG pins MTCK and MTDO, free while JTAG is on USB (its default)
//   heartbeat   an LED on 23 (the Pico blinks its own LED; the C5's is an RGB LED on GPIO 27, not a pin to toggle)
//   7-segment   segments A to G on 0, 1, 2, 3, 11, 12 and 24 (common cathode, each segment through 220 ohm to the
//               pin): outputs that sit low, which is what 2 and 3 (strapping pins MTMS and MTDI) read through an LED
//               to GND; 0 and 1 are the 32 kHz crystal pins, free as no crystal is fitted; 11 and 12 are the UART
//               bridge's pins, which this program does not use (flash through the USB port, not the UART one)
//   left alone  7, 25, 26, 27 and 28: strapping pins (28 the Boot button, 27 the RGB LED), where a wire could change
//               the boot mode
// (On a real board, a 5 V LCD module may need 5 V for good contrast; its I2C lines must then not be pulled up to 5 V,
// as the C5's pins take at most 3.3 V.)
//
// Build:     dotnet build -c Release
//            chipsharp build bin\Release\net10.0\Esp32c5LcdTrafficLight.dll -t esp32c5-devkitc
// Simulate:  chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5LcdTrafficLight.bin --ms 16000 --i2c 0:0x27=lcd1602
//            --input GPIO10=high@2500ms --input GPIO10=low@2700ms      (the button is pressed at 2.5 s)

using System;
using System.Collections.Generic;
using ChipSharp.Components;
using ChipSharp.Hardware;

List<Lane> lanes = [new(1), new(2)];

var signal = new Signal(red: 6, yellow: 8, green: 9);
var screen = new Screen(bus: 0, sda: 4, scl: 5, address: 0x27);
var crossing = new Crossing(lanes, signal, turnDisplay: new(a: 0, b: 1, c: 2, d: 3, e: 11, f: 12, g: 24), screen);
crossing.Interrupted += (lane, next) =>
    Console.WriteLine($"lane : {lane.Number} interrupted, move to lane {next.Number}");

Heartbeat.Start();
Button.Watch();
crossing.Run();

enum Light { Red, Yellow, Green }

// One step of a lane's turn: which light, for how long, and whether the button may cut it short.
readonly record struct Step(Light Light, int Seconds, bool CanBeCut = false)
{
    // "green", "yellow" or "red", as printed over the serial port and on the LCD
    public string Name => Light.ToString().ToLowerInvariant();
}

// A lane: its number is all it needs now that one signal shows whichever lane is active.
readonly record struct Lane(int Number);

// The three lights, shared by the lanes: they show the active lane's light.
sealed class Signal
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

// The 16x2 LCD. A whole line is written only when it changes (a lane's turn, a step); each second only the digit
// changes, a single character, so updating the screen takes about a millisecond and the timing stays as it was.
sealed class Screen
{
    const int SecondsColumn = 14;        // "...8s": the digit in column 14, the "s" in column 15

    public Screen(int bus, int sda, int scl, int address)
    {
        I2c.Open(bus, sda, scl, frequency: 100_000);
        Lcd.Open(bus, address, columns: 16, rows: 2);   // false when no LCD answers: the Lcd calls then do nothing
    }

    public void Road(int lane, int turnLeft) => Lcd.Line(0, $"Road {lane}   turn {turnLeft}s");

    public void Step(string name, int left) => Lcd.Line(1, $"{name,-14}{left}s");

    public void Switching() => Lcd.Line(1, "switching");

    public void Seconds(int left, int turnLeft)
    {
        Lcd.Write(SecondsColumn, 1, left);
        Lcd.Write(SecondsColumn, 0, turnLeft);
    }

    public void TurnSeconds(int turnLeft) => Lcd.Write(SecondsColumn, 0, turnLeft);
}

// An LED on 23, toggled by a timer interrupt every half second: it blinks once a second whatever the main loop
// is doing, which shows on a real board that the program runs and the timer keeps time.
static class Heartbeat
{
    const int Led = 23;

    public static void Start()
    {
        Pin.Output(Led);
        Timing.EveryMilliseconds(500);   // Tick runs every 500 ms from now on
    }

    [Interrupt(Event.Timer)]
    static void Tick() => Pin.Toggle(Led);
}

// The button on GPIO 10. Its interrupt only sets a flag: a handler must not wait, create objects or store them.
static class Button
{
    const int PinNumber = 10;

    // set by the interrupt, read and cleared by the main loop, so it must be volatile
    static volatile bool pressed;

    public static bool WasPressed => pressed;

    public static void Watch()
    {
        Pin.Input(PinNumber, Pull.Down);
        Pin.WatchRising(PinNumber);      // a press runs Pressed() below
    }

    public static void Forget() => pressed = false;

    [Interrupt(Event.Pin)]
    static void Pressed() => pressed = true;
}

// The lanes taking turns, with the signal, the turn's countdown display and the LCD.
sealed class Crossing(List<Lane> lanes, Signal signal, SevenSegment turnDisplay, Screen screen)
{
    const int SwitchDelaySeconds = 1;    // the pause before the other lane goes green

    static readonly Step[] Steps =
    [
        new(Light.Green, 4, CanBeCut: true),
        new(Light.Yellow, 1),
        new(Light.Red, 2),               // both lanes red
    ];

    int turnLeft;                        // seconds left in the current lane's whole turn

    // Raised when the button cuts a green short: the lane, and the lane whose turn comes next.
    public event Action<Lane, Lane>? Interrupted;

    public void Run()
    {
        while (true)
        {
            foreach (var (lane, next) in Turns())
            {
                Turn(lane, next);
            }
        }
    }

    // Each lane in order, with the lane whose turn comes after it.
    IEnumerable<(Lane Lane, Lane Next)> Turns()
    {
        for (int i = 0; i < lanes.Count; i++)
        {
            yield return (lanes[i], lanes[(i + 1) % lanes.Count]);
        }
    }

    // One lane's turn: its steps on the signal, then the pause before the next lane (the signal stays red).
    void Turn(Lane lane, Lane next)
    {
        Console.WriteLine($"road : {lane.Number}");
        turnLeft = SecondsFrom(0) + SwitchDelaySeconds;
        screen.Road(lane.Number, turnLeft);
        Button.Forget();                 // a press before this green doesn't count

        for (int i = 0; i < Steps.Length; i++)
        {
            var step = Steps[i];
            signal.Show(step.Light);
            screen.Step(step.Name, step.Seconds);
            if (CountDown(step))
            {
                Interrupted?.Invoke(lane, next);
                turnLeft = SecondsFrom(i + 1) + SwitchDelaySeconds;   // the turn goes on with what is left of it
            }
        }

        // the pause has no step of its own: the LCD says "switching", the turn display shows its last second
        turnDisplay.Show(turnLeft);
        screen.Switching();
        screen.TurnSeconds(turnLeft);
        Timing.Delay(TimeSpan.FromSeconds(SwitchDelaySeconds));
    }

    // Counts one step down, second by second, on the LCD, the turn display and over the serial port. Returns true when
    // the button cut it short.
    bool CountDown(Step step)
    {
        for (int left = step.Seconds; left > 0; left--)
        {
            Console.WriteLine($"    {step.Name} {left}");
            turnDisplay.Show(turnLeft);
            screen.Seconds(left, turnLeft);
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
