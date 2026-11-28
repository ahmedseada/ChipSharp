// A command prompt on the serial port of WCH's CH32V003 evaluation board: type a command and press Enter.
//
//     on           switches the LED on
//     off          switches it off
//     status       says whether it is on, and how many commands have come
//     anything else is answered with what was typed
//
// The console is USART1: the chip sends on PD5 (TX) and receives on PD6 (RX), at 115200 baud; a WCH-LinkE's RX on
// PD5 and TX on PD6 bring it to the LinkE's own serial port. Console.Read waits for the next char; the chip receives on
// its own meanwhile, into a buffer of 16 bytes, so nothing typed is lost while the program is busy. Each char is sent
// back as it comes (the echo), so a terminal shows what is typed.
//
// It is examples/stm8-serial with the board's pins: a line is read char by char into a span on the stack (stackalloc),
// as Console.ReadLine would make a string, which needs a heap the chip's 2 KB of RAM has no room for; the switch is on
// that span. No object is made. The LED: a wire from the header's LED1 to PD0, lit while PD0 is low.
//
// Build (after dotnet build -c Release of the repository), in this folder:
//   dotnet build -c Release
//   ..\..\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build bin\Release\net10.0\Ch32v003Serial.dll -t ch32v003-evt
// Flash:     chipsharp flash bin\Release\net10.0\Ch32v003Serial.dll -t ch32v003-evt (see README.md)
// Simulate:  chipsim ch32v003 chipsharp-output\ch32v003-evt\Ch32v003Serial.bin --ms 300 --send "on\r\n@20ms"

using System;
using ChipSharp.Hardware;

const int Led = 24;                 // PD0, wired to the board's LED1
const bool LedLitWhenHigh = false;  // wired from VCC to PD0, it is lit while the pin is low (true on a Pico, an Uno)

Pin.Output(Led);
Pin.Write(Led, !LedLitWhenHigh);    // off
var on = false;
var commands = 0;
Span<char> line = stackalloc char[24];
Console.WriteLine("ChipSharp on a CH32V003 board. Commands: on, off, status");
while (true)
{
    Console.Write("> ");
    var typed = line[..Prompt.ReadLine(line)];
    commands++;
    switch (typed)
    {
        case "on":
            on = true;
            Pin.Write(Led, on == LedLitWhenHigh);
            Console.WriteLine("LED on");
            break;
        case "off":
            on = false;
            Pin.Write(Led, on == LedLitWhenHigh);
            Console.WriteLine("LED off");
            break;
        case "status":
            Console.WriteLine($"LED {(on ? "on" : "off")}, {commands} commands");
            break;
        default:
            Console.Write("you said: ");
            foreach (var c in typed)
            {
                Console.Write(c);
            }

            Console.WriteLine();
            break;
    }
}

// Reads a line from the console as Console.ReadLine does: up to Enter ("\r", "\n" or "\r\n", as a terminal sends it),
// without it. Each char is sent back as it comes; a backspace takes the last one back; what does not fit is dropped.
static class Prompt
{
    static int previous;                // the char before: a "\n" right after a "\r" ends no line of its own

    public static int ReadLine(Span<char> line)
    {
        var length = 0;
        while (true)
        {
            var c = Console.Read();
            var last = previous;
            previous = c;
            if (c == '\n' && last == '\r')
            {
                continue;
            }

            if (c is '\r' or '\n')
            {
                Console.WriteLine();
                return length;
            }

            if (c is '\b' or 127)
            {
                if (length > 0)
                {
                    length--;
                    Console.Write("\b \b");
                }
            }
            else if (length < line.Length)
            {
                line[length++] = (char)c;
                Console.Write((char)c);
            }
        }
    }
}
