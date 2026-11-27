// A servo follows a potentiometer on an Arduino Uno: turn the knob, and the servo's arm turns with it, from 0 to 180
// degrees. A hobby servo reads the length of a pulse that comes 50 times a second: 1 ms turns it to one end (0
// degrees), 2 ms to the other (180 degrees), 1.5 ms to the middle. The Uno reads the knob on an analog input, 0 to
// 1023 for 0 to 5 V (Adc.Read), and makes the pulses on D9 (Pwm.Output at 50 Hz, Pwm.PulseMicroseconds for the
// length): D9 is Timer1's, whose edges come to the half microsecond, the Uno's pin for a servo. The chip makes the
// pulses on its own, so the program only says how long. Each change of 5 degrees or more goes to the console
// (USART0: D1 sends, 115200 baud).
//
// Wire the potentiometer's ends to 5V and GND and its wiper to A0; the servo's signal to D9, its + to 5V and its - to
// GND (a real servo may draw more current than USB gives while it moves: power it from its own 5 V then, its - to the
// Uno's GND too). No heap: numbers, and text printed straight away. Build it with ChipSharp for the target "uno", as
// examples/uno-blink says, with UnoServo.dll; simulate it with the knob turned to 3.75 V after half a second:
//   node ..\..\tools\avr-sim\run.mjs chipsharp-output\uno\UnoServo.hex --ms 1000 --adc A0=0V --adc A0=3.75V@500ms

using ChipSharp.Hardware;

const int Knob = 14;     // A0, the potentiometer's wiper: 0 to 5 V
const int Servo = 9;     // D9

Adc.Input(Knob);
Pwm.Output(Servo, 50);   // 50 pulses a second, 20 ms apart
var shown = -100;
while (true)
{
    var reading = Adc.Read(Knob);                  // 0 to 1023
    var angle = reading * 180 / 1023;              // 0 to 180 degrees
    Pwm.PulseMicroseconds(Servo, 1000 + angle * 1000 / 180);
    if (angle >= shown + 5 || angle <= shown - 5)
    {
        Console.WriteLine($"knob {reading}, servo at {angle} degrees");
        shown = angle;
    }

    Timing.DelayMilliseconds(20);                  // one pulse at each reading
}
