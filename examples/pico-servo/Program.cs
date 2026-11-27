// A servo sweeps from 0 to 180 degrees and back, over and over. A hobby servo reads the length of a pulse that comes
// 50 times a second: 1 ms turns it to one end (0 degrees), 2 ms to the other (180 degrees), 1.5 ms to the middle.
// Pwm.Output makes the 50 Hz, Pwm.PulseMicroseconds the length; the chip makes the pulses on its own, so the program
// only says where to go. Each 45 degrees goes to the console (UART0, GPIO 0, 115200 baud).
//
// Wire the servo's signal to GPIO 22, its + to VBUS (5 V from USB) and its - to GND. No heap. Build it with ChipSharp
// for the target "pico", as examples/pico-blink says.

using ChipSharp.Hardware;

const int Servo = 22;

Pwm.Output(Servo, 50);
while (true)
{
    for (var angle = 0; angle <= 180; angle += 5)
    {
        Turn(angle);
    }

    for (var angle = 175; angle > 0; angle -= 5)
    {
        Turn(angle);
    }
}

static void Turn(int angle)
{
    Pwm.PulseMicroseconds(Servo, 1000 + angle * 1000 / 180);
    if (angle % 45 == 0)
    {
        Console.WriteLine($"servo at {angle} degrees");
    }

    Timing.DelayMilliseconds(40); // two pulses at each angle: time for the servo to get there
}
