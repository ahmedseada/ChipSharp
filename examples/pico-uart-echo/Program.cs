// A command prompt on the Raspberry Pi Pico's console: type a command and press Enter.
//
//     led on       switches the LED on
//     led off      switches it off
//     status       says whether it is on, and how many commands have come
//     anything else is answered with what was typed
//
// The console is UART0: the Pico sends on GPIO 0 and receives on GPIO 1, at 115200 baud. Console.ReadLine waits for a
// line that ends with Enter ("\r", "\n" or "\r\n", as a terminal sends it) and returns it without its end; the chip
// receives on its own meanwhile, into a buffer, so nothing typed is lost while the program is busy. What is typed is
// not echoed back: turn on your terminal's local echo to see it. The line is a string, so this program uses the heap.
// Build it with ChipSharp for the target "pico", as examples/pico-blink says; in Wokwi, type into the serial monitor.

using ChipSharp.Hardware;

const int Led = 25; // the Pico's LED

Pin.Output(Led);
var on = false;
var commands = 0;
Console.WriteLine("ChipSharp on a Pico. Commands: led on, led off, status");
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    commands++;
    switch (line)
    {
        case "led on":
            on = true;
            Pin.Write(Led, on);
            Console.WriteLine("LED on");
            break;
        case "led off":
            on = false;
            Pin.Write(Led, on);
            Console.WriteLine("LED off");
            break;
        case "status":
            Console.WriteLine($"LED {(on ? "on" : "off")}, {commands} commands");
            break;
        default:
            Console.WriteLine($"you said: {line}");
            break;
    }
}
