// A traffic light on a Padauk PFS154 (2K words of program memory, 128 bytes of RAM, no heap, no console): red, yellow
// and green lights, a 7-segment display that counts the turn down, a pedestrian button and a heartbeat LED.
//
// It is the Uno's traffic light (examples/uno-traffic-light) with what the PFS154 has: the same steps and times, the
// same button and heartbeat, in heap-free C# (an enum for the lights, static classes for the parts). The chip has no
// serial port and no I2C bus, so nothing is printed and there is no LCD; with nothing to tell two lanes apart on one
// set of lights, they go round for one road. And 128 bytes of RAM hold the program's variables and its stack (about
// 48 bytes when the timer event comes while a pin is written): the steps are a method's parameters, as in the Pico's
// traffic light, rather than the Uno's table of record structs, which as C for SDCC takes more RAM than the chip has.
//
// A turn: green (4 s), yellow (1 s), red (2 s: the pedestrians cross), then 1 s more before green again. The 7-segment
// display counts the whole turn down (8 7 6 5 4 3 2 1). A press of the pedestrian button ends the current green at
// once: the light goes to yellow, and the turn goes on with what is left of it (the display jumps to 4). A press
// during yellow or red does not count. The heartbeat LED blinks once a second from the timer event, whatever the main
// loop is doing.
//
// Wiring (README.md has it pin by pin): the lights red PA3, yellow PA6, green PA7; the display's segments A to G on
// PB1 to PB7, its common cathode to GND; the heartbeat LED on PA4; the button between PA0 and GND (Pull.Up: the
// pin's own pull-high holds it high until pressed, so it needs no resistor). Every LED and segment with its resistor.
// It uses ChipSharp.Hardware alone, so the same C# builds for "pico" or "uno" with other pin numbers.
//
// Build it with ChipSharp for the target "pfs154-bare", then write it into the chip with the Easy PDK programmer:
//   dotnet build -c Release
//   chipsharp build bin\Release\net10.0\Pfs154TrafficLight.dll -t pfs154-bare
//   chipsharp flash bin\Release\net10.0\Pfs154TrafficLight.dll -t pfs154-bare
// Simulate (the button pressed at 2.5 s, during the first green; PA0 high from the start, as its pull-up holds it):
//   chipsim pfs154 chipsharp-output\pfs154-bare\Pfs154TrafficLight.hex --ms 16000
//     --input PA0=low@2500ms --input PA0=high@2700ms

using ChipSharp.Hardware;

Signal.Start();
TurnDisplay.Start();
Heartbeat.Start();
Button.Watch();
Crossing.Run();

enum Light : byte { Red, Yellow, Green }

// The three lights.
static class Signal
{
    const int Red = 3, Yellow = 6, Green = 7;        // PA3, PA6, PA7

    public static void Start()
    {
        Pin.Output(Red);
        Pin.Output(Yellow);
        Pin.Output(Green);
        Show(Light.Red);                 // it starts at red
    }

    // The light given on, the other two off.
    public static void Show(Light light)
    {
        Pin.Write(Red, false);
        Pin.Write(Yellow, false);
        Pin.Write(Green, false);
        Pin.Write(light switch { Light.Red => Red, Light.Yellow => Yellow, _ => Green }, true);
    }
}

// A common-cathode 7-segment display: a segment lights while its pin is high.
static class TurnDisplay
{
    const int A = 9;                     // segments A to G on PB1 to PB7, pins 9 to 15

    public static void Start()
    {
        for (int segment = 0; segment < 7; segment++)
        {
            Pin.Output(A + segment);
        }
    }

    public static void Show(int digit)
    {
        // which segments each digit lights: bit 0 = A, bit 1 = B, ... bit 6 = G ('-' for any other number)
        int segments = digit switch
        {
            0 => 0x3F, 1 => 0x06, 2 => 0x5B, 3 => 0x4F, 4 => 0x66,
            5 => 0x6D, 6 => 0x7D, 7 => 0x07, 8 => 0x7F, 9 => 0x6F,
            _ => 0x40,
        };
        for (int segment = 0; segment < 7; segment++)
        {
            Pin.Write(A + segment, (segments & 1) != 0);
            segments >>= 1;
        }
    }
}

// The LED on PA4, toggled by the timer event every half second: it blinks once a second whatever the main loop is
// doing, which shows on a real chip that the program runs and the timer keeps time.
static class Heartbeat
{
    const int Led = 4;                   // PA4

    public static void Start()
    {
        Pin.Output(Led);
        Timing.EveryMilliseconds(500);   // Tick runs every 500 ms from now on (TM2, on the PFS154)
    }

    [Interrupt(Event.Timer)]
    static void Tick() => Pin.Toggle(Led);
}

// The pedestrian button on PA0, to GND. Its interrupt only sets a flag: a handler must not wait.
static class Button
{
    const int PinNumber = 0;             // PA0: the PFS154's pin event is on PA0 and PB0

    // set by the interrupt, read and cleared by the main loop, so it must be volatile
    static volatile bool pressed;

    public static bool WasPressed => pressed;

    public static void Watch()
    {
        Pin.Input(PinNumber, Pull.Up);   // high through the pin's pull-up until pressed
        Pin.WatchFalling(PinNumber);     // a press (PA0 to GND) runs Pressed() below
    }

    public static void Forget() => pressed = false;

    [Interrupt(Event.Pin)]
    static void Pressed() => pressed = true;
}

// The turns, one after the other, on the signal and the turn's countdown display.
static class Crossing
{
    const int GreenSeconds = 4;
    const int YellowSeconds = 1;
    const int RedSeconds = 2;            // the pedestrians cross
    const int SwitchDelaySeconds = 1;    // the pause before green again

    static int turnLeft;                 // seconds left in the current turn

    public static void Run()
    {
        while (true)
        {
            turnLeft = GreenSeconds + YellowSeconds + RedSeconds + SwitchDelaySeconds;
            Button.Forget();             // a press before this green doesn't count
            if (Step(Light.Green, GreenSeconds, canBeCut: true))
            {
                turnLeft = YellowSeconds + RedSeconds + SwitchDelaySeconds;   // the turn goes on with what is left
            }

            Step(Light.Yellow, YellowSeconds);
            Step(Light.Red, RedSeconds);

            // the pause has no step of its own: the signal stays red, the display shows the turn's last second
            TurnDisplay.Show(turnLeft);
            Timing.DelayMilliseconds(SwitchDelaySeconds * 1000);
        }
    }

    // Shows one step's light and counts it down, second by second, on the turn display. A step that can be cut
    // (green) ends at once when the button is pressed; it returns true then.
    static bool Step(Light light, int seconds, bool canBeCut = false)
    {
        Signal.Show(light);
        for (int left = seconds; left > 0; left--)
        {
            TurnDisplay.Show(turnLeft);
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
}
