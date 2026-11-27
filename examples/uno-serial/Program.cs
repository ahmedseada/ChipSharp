// A count on the Arduino Uno's console, once a second, and what is typed into it sent straight back.
//
// The console is USART0, the Uno's serial port: D1 sends and D0 receives, at 115200 baud, through the board's USB
// chip, so the Arduino IDE's serial monitor (or any terminal at 115200 baud) shows it; in Wokwi, the serial monitor
// under the circuit does, and what is typed there comes in on D0. Console.WriteLine of text and numbers is printed
// text: nothing is made on a heap, which the Uno has none of. Uart.Open(0, ...) has the chip receive USART0 into a
// buffer of 64 bytes, filled by an interrupt, so nothing typed between two looks is lost; Uart.Available and
// Uart.ReadByte take what came, without waiting, and Uart.WriteByte sends each byte back.
// Build and flash it as examples/uno-blink says, with UnoSerial.dll.

using ChipSharp.Hardware;

const int Console0 = 0;      // USART0, the console's port: TX D1, RX D0

Uart.Open(Console0, 1, 0, 115200);
Console.WriteLine("ChipSharp on an Arduino Uno: a count every second; type, and it comes back");
for (var count = 0; ; count++)
{
    Console.WriteLine($"count {count}");
    for (var step = 0; step < 100; step++)  // a second, in steps of 10 ms, sending back what came in meanwhile
    {
        while (Uart.Available(Console0) > 0)
        {
            Uart.WriteByte(Console0, (byte)Uart.ReadByte(Console0));
        }

        Timing.DelayMilliseconds(10);
    }
}
