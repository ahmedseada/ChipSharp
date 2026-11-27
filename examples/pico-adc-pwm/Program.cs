// A potentiometer sets an LED's brightness: the Raspberry Pi Pico reads the knob's voltage on an analog input and
// drives the LED with PWM, high for as much of each millisecond as the knob is turned up. Each change of 0.1 V or more
// goes to the console (UART0, GPIO 0, 115200 baud).
//
// Wire the potentiometer's ends to 3V3 and GND and its wiper to GPIO 26 (ADC0), and an LED from GPIO 15 to GND (on a
// real board through a 220 ohm resistor; Wokwi's diagram.json needs none). No heap: numbers, and text printed straight
// away. Build it with ChipSharp for the target "pico", as examples/pico-blink says.

using ChipSharp.Hardware;

const int Knob = 26; // the potentiometer's wiper: 0 to 3.3 V
const int Lamp = 15; // the LED

Adc.Input(Knob);
Pwm.Output(Lamp, 1000); // 1000 periods a second: too fast to see the LED blink
var shown = -1000;
while (true)
{
    var reading = Adc.Read(Knob);        // 0 to 4095
    Pwm.Duty(Lamp, reading / 4095.0);    // 0 (off) to 1 (fully on)
    var millivolts = reading * 3300 / 4096;
    if (millivolts >= shown + 100 || millivolts <= shown - 100)
    {
        Console.WriteLine($"knob {millivolts / 1000}.{millivolts % 1000:D3} V, LED {reading * 100 / 4095}%");
        shown = millivolts;
    }

    Timing.DelayMilliseconds(20);
}
