namespace ChipSharp.Tests.Conformance;

/// <summary>
/// The ESP32-C5-DevKitC-1's slot (esp32c5-devkitc, milestone M4): its cases are skipped, saying why, until the chip's
/// simulator (M4's wave 1: ChipSharp's own, in src/ChipSharp.Simulators, which loads the .elf the build makes and runs
/// its RISC-V core, GPIO, SYSTIMER, UART0 and USB Serial/JTAG) and the chip's runtime that implements the hardware
/// contract (pins, the clock and the delays, the console on UART0, the events) come; then this adapter runs the image
/// there. Pins are GPIO numbers: the program's pins are those the board leaves free (not the strapping pins 2, 3, 7 and
/// 25-28, the module's flash and PSRAM on 15-22, USB on 13 and 14, the RGB LED on 27); UART0 (GPIO 11 TX, 12 RX) is port
/// 0 and the console.
/// </summary>
internal sealed class Esp32c5ChipSimulator() : ChipSimulator("esp32c5-devkitc")
{
    public override string? Missing =>
        "The ESP32-C5's simulator and its runtime's hardware contract (pins, clock, console, events) are not in ChipSharp " +
        "yet (milestone M4, wave 1): 'esp32c5-devkitc' builds to a linked .elf so far, and runs the conformance suite " +
        "once they come.";

    public override ChipPins Pins { get; } = new(Output: 6, Second: 8, Pass: 9, Fail: 10, Input: 23)
    {
        Uart0 = (11, 12),
    };

    // up to 240 MHz; the runtime's clock comes with it: the Pico's tolerances until then
    public override ChipTolerances Tolerances { get; } = new(DelaySlackMilliseconds: 0.020, LatencyMilliseconds: 0.010,
        ClockMilliseconds: 0.010);

    public override Task<ChipRun> RunAsync(string image, ChipStimulus stimulus) =>
        throw new NotSupportedException(Missing);
}
