// A command prompt on the ESP32-C5-DevKitC-1's USB console, M3 style: type a command and press Enter.
//
//     on          switches the LED on
//     off         switches it off
//     blink 200   blinks it, half a period of 200 ms (from the timer event, while the prompt waits for the next line)
//     status      says what the LED does, and how many commands have come
//     anything else is answered with what was typed
//
// The console is the chip's own USB Serial/JTAG port, the USB-C port marked "USB" ("ESP32-C5 USB Type-C Port"): it shows
// up as a serial port (COM6, /dev/ttyACM0), and any terminal opened on it shows what the program prints and sends what
// is typed (the baud rate does not matter). Console.ReadLine waits for a line that ends with Enter and returns it
// without its end. What is typed is not echoed back: turn on your terminal's local echo to see it. The lines are
// strings and the commands a Dictionary of methods, so this program uses the heap (the C5 has 224 KB of it).
//
// Wiring (the board's labels): GPIO 4 ("4") to the LED's anode through a resistor of 220 ohm, the LED's cathode to "GND".
//
// Build (after dotnet build -c Release of the repository), in this folder:
//   dotnet build -c Release
//   ..\..\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build bin\Release\net10.0\Esp32c5Serial.dll -t esp32c5-devkitc
// Flash:     chipsharp flash bin\Release\net10.0\Esp32c5Serial.dll -t esp32c5-devkitc (see README.md)
// Simulate:  chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5Serial.bin --ms 700 --send USB="on\n@100ms" --send USB="blink 200\n@200ms" --send USB="status\n@500ms"

using System;
using System.Collections.Generic;
using ChipSharp.Hardware;

var led = new BlinkingLed(pin: 4);
var prompt = new Prompt(new Dictionary<string, Action<string>>
{
    ["on"] = _ => { led.Set(true); Console.WriteLine("LED on"); },
    ["off"] = _ => { led.Set(false); Console.WriteLine("LED off"); },
    ["blink"] = argument =>
    {
        if (int.TryParse(argument, out var milliseconds) && milliseconds > 0)
        {
            led.Blink(milliseconds);
            Console.WriteLine($"LED blinking every {milliseconds} ms");
        }
        else
        {
            Console.WriteLine("blink needs a number of milliseconds: blink 200");
        }
    },
    ["status"] = _ => Console.WriteLine($"LED {led.State}, {Prompt.Commands} commands"),
});

Console.WriteLine("ChipSharp on an ESP32-C5. Commands: on, off, blink <ms>, status");
prompt.Run();

// The LED, which the timer event may be blinking.
static class LedPin
{
    // the pin and what the timer event does, set by BlinkingLed: the event is a static method
    public static volatile int Number;
    public static volatile bool Blinking;

    [Interrupt(Event.Timer)]
    static void Tick() => Pin.Toggle(Number);
}

sealed class BlinkingLed
{
    public BlinkingLed(int pin)
    {
        LedPin.Number = pin;
        Pin.Output(pin);
    }

    public string State => LedPin.Blinking ? "blinking" : Pin.Read(LedPin.Number) ? "on" : "off";

    public void Set(bool on)
    {
        Stop();
        Pin.Write(LedPin.Number, on);
    }

    public void Blink(int halfPeriodMilliseconds)
    {
        LedPin.Blinking = true;
        Timing.EveryMilliseconds(halfPeriodMilliseconds);   // Tick runs every half period, from now
    }

    void Stop()
    {
        if (LedPin.Blinking)
        {
            Timing.Stop();
            LedPin.Blinking = false;
        }
    }
}

// Reads lines, splits each into a command and its argument and runs the method the command names.
sealed class Prompt
{
    readonly Dictionary<string, Action<string>> commands;

    public Prompt(Dictionary<string, Action<string>> commands) => this.commands = commands;

    public static int Commands { get; private set; }

    public void Run()
    {
        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine()!.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            Commands++;
            var space = line.IndexOf(' ');
            var name = space < 0 ? line : line.Substring(0, space);
            var argument = space < 0 ? "" : line.Substring(space + 1).Trim();
            if (commands.TryGetValue(name, out var command))
            {
                command(argument);
            }
            else
            {
                Console.WriteLine($"you said: {line}");
            }
        }
    }
}
