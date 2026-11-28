namespace ChipSharp.Tests.Conformance;

/// <summary>
/// WCH's CH32V003 evaluation board's slot (ch32v003-evt, milestone M5): its cases are skipped, saying why, until the
/// chip's simulator (M5's wave 0: chipsim's "ch32v003", ChipSharp's own, in src/ChipSharp.Simulators, the ESP32-C5's
/// RISC-V core as RV32EC with the chip's PFIC, RCC, GPIO, SysTick and USART1) and the chip's runtime that implements the
/// hardware contract (wave 1: pins, the delays and the clock, the console on USART1, the events) come; then this adapter
/// runs the image there. Pins are ChipSharp's numbers, 8 * the port's letter + the bit (PC0 16, PD0 24): the program's
/// are among those the board leaves free (PC0-PC7, PD0, PD2-PD6: PD1 is SWIO, PD7 NRST, PA1 and PA2 the crystal's), PD0
/// the output WCH's examples blink; USART1 (PD5 TX, PD6 RX) is serial port 0 and the console.
/// </summary>
internal sealed class Ch32v003ChipSimulator() : ChipSimulator("ch32v003-evt")
{
    public override string? Missing =>
        "The CH32V003's simulator and its runtime's hardware contract (pins, clock, console, events) are not in ChipSharp " +
        "yet (milestone M5, waves 0 and 1): 'ch32v003-evt' builds to a linked .elf so far, and runs the conformance suite " +
        "once they come.";

    public override ChipPins Pins { get; } = new(Output: 24, Second: 16, Pass: 19, Fail: 26, Input: 27)
    {
        Uart0 = (29, 30),
    };

    // 48 MHz; the runtime's clock comes with it: the Pico's tolerances until then
    public override ChipTolerances Tolerances { get; } = new(DelaySlackMilliseconds: 0.020, LatencyMilliseconds: 0.010,
        ClockMilliseconds: 0.010);

    public override Task<ChipRun> RunAsync(string image, ChipStimulus stimulus) =>
        throw new NotSupportedException(Missing);
}
