using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Tests.Sdcc;

namespace ChipSharp.Tests.Conformance;

/// <summary>
/// The slot of a chip built through C with SDCC (milestone M6): its target, the pins the suite uses on it and its
/// tolerances are here, and its cases are skipped, saying why, until the build pipeline has its parts (the code generator
/// "c", the chip's runtime, which implements the hardware contract) and its simulator is wired in: <see cref="Run"/>,
/// which gets the image and the stimulus and gives back what the chip did, as the Pico's and the Uno's adapters do.
/// </summary>
internal abstract class SdccChipSimulator(string target) : ChipSimulator(target)
{
    public override string? Missing
    {
        get
        {
            if (SdccInstall.Missing.Value is { } sdcc)
            {
                return sdcc;
            }

            var registry = DefaultComponents.CreateRegistry(new BuildContext(new BuildOptions(), Description, "Program",
                Path.GetTempPath()));
            if (!registry.TryGet<ICodeGenerator>(Description.Backend, out _))
            {
                return $"The code generator \"{Description.Backend}\" (C, for SDCC) is not in ChipSharp yet: " +
                    $"'{Target}' joins the conformance suite once it is.";
            }

            if (!registry.TryGet<IArchRuntime>(Description.Runtime!, out _))
            {
                return $"The runtime \"{Description.Runtime}\" is not in ChipSharp yet: '{Target}' joins the conformance " +
                    "suite once it implements the hardware contract.";
            }

            return Run is null
                ? $"No simulator runs '{Target}' for the conformance suite yet: wire one into {GetType().Name}.Run."
                : null;
        }
    }

    /// <summary>The chip's simulator, once there is one: runs an image with a stimulus. Null until then.</summary>
    protected virtual Func<string, ChipStimulus, Task<ChipRun>>? Run => null;

    public override Task<ChipRun> RunAsync(string image, ChipStimulus stimulus) =>
        Run is { } run ? run(image, stimulus) : throw new NotSupportedException(Missing);
}

/// <summary>
/// The Padauk PFS154 at 2 MHz (pfs154-bare): pins are port * 8 + bit (PA0-PA7 0-7, PB0-PB7 8-15); PA0 is one of its
/// two pins with an interrupt, PB5 TM3's PWM output. No serial port, ADC or bus.
/// </summary>
internal sealed class Pfs154ChipSimulator() : SdccChipSimulator("pfs154-bare")
{
    public override ChipPins Pins { get; } = new(Output: 4, Second: 3, Pass: 9, Fail: 10, Input: 0) { Pwm = 13 };

    // 2 MHz, an instruction a cycle (mostly), and 32-bit arithmetic in software: a delay's loop and an event's entry
    // take tens of microseconds
    public override ChipTolerances Tolerances { get; } = new(DelaySlackMilliseconds: 0.300, LatencyMilliseconds: 0.150,
        ClockMilliseconds: 0.200)
    {
        PwmMicroseconds = 8,
    };
}

/// <summary>
/// The STM8S103F3 at 16 MHz (stm8s103-board): pins are port * 8 + bit (PA1 1, PB5 13, PC3 19, PD6 30); UART1 (PD5, PD6)
/// is port 0, I2C on PB5 and PB4, SPI on PC5-PC7, AIN3 on PD2, TIM2's channel 1 on PD4.
/// </summary>
internal sealed class Stm8ChipSimulator() : SdccChipSimulator("stm8s103-board")
{
    public override ChipPins Pins { get; } = new(Output: 1, Second: 2, Pass: 3, Fail: 19, Input: 20)
    {
        Adc = 26,
        Pwm = 28,
        Uart0 = (29, 30),
        I2c0 = (13, 12),
        Spi0 = (21, 22, 23),
    };

    public override ChipTolerances Tolerances { get; } = new(DelaySlackMilliseconds: 0.060, LatencyMilliseconds: 0.040,
        ClockMilliseconds: 0.040)
    {
        PwmMicroseconds = 1,
    };

    // AIN0-AIN6 in 10 bits against VDD, the board's 3.3 V
    public override ChipAdc Adc { get; } = new(1024, 3.3);
}
