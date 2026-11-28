// A command prompt on the STM8S103 board's serial port: type a command and press Enter.
//
//     on           switches the board's LED on
//     off          switches it off
//     status       says whether it is on, and how many commands have come
//     anything else is answered with what was typed
//
// The console is UART1: the board sends on PD5 (TX) and receives on PD6 (RX), at 115200 baud. Console.Read waits for
// the next char; the chip receives on its own meanwhile, into a buffer of 32 bytes, so nothing typed is lost while
// the program is busy. Each char is sent back as it comes, so a terminal shows what is typed.
//
// It is examples/pico-uart-echo without a heap, which the board's 1 KB of RAM has no room for: Console.ReadLine makes a
// string, so the line is read here, char by char, into a span on the stack (stackalloc), and the switch is on that
// span; the constant strings it is compared with are fixed text in flash. No object is made.

using System;
using ChipSharp.Hardware;

const int Led = 13;             // PB5, the board's LED
const bool LedLitWhenHigh = false; // it is wired from 3.3 V to PB5: lit while the pin is low (true on a Pico or an Uno)

Pin.Output(Led);
Pin.Write(Led, !LedLitWhenHigh);    // off
var on = false;
var commands = 0;
Span<char> line = stackalloc char[24];
Console.WriteLine("ChipSharp on an STM8S103 board. Commands: on, off, status");
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
