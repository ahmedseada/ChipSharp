// ChipSharp runtime for the WCH CH32V003: ChipSharp.Hardware.Pin, and the pin event (Pin.Watch, WatchRising,
// WatchFalling and Unwatch), which runs the program's [Interrupt(Event.Pin)] method: its entry is EXTI7_0's handler,
// chipsharp_isr_irq20 (ch32v003.json's "interrupts.pin"), which calls chipsharp_clear_pin_event first.
//
// Pins by number, port * 8 + bit, as chipsharp_hardware.h numbers a Padauk's and an STM8's: PA1 and PA2 are 1 and 2
// (the evaluation board's crystal), PC0-PC7 16-23, PD0-PD7 24-31. PD1 (25) is SWIO, the debug wire a WCH-LinkE programs
// the chip through, and PD7 (31) NRST, the reset pin (the option bytes' RST_MODE): the runtime leaves both alone, as a
// pin the chip does not have, so that no program cuts the chip off from its programmer (chipsharp check refuses them as
// constants: the target's "pinFunctions.pin"). Each port has from its address on (ch32v003_chip.h): CFGLR (four bits a
// pin: input or output, floating, pulled or driven), INDR (the levels), OUTDR (the levels driven; an input's pull, 1 up
// and 0 down), BSHR (a 1 sets a pin) and BCR (a 1 clears it). RM 7 (GPIO and AFIO).
//
//   ChipSharp_Runtime_PinOutput      the pin drives low until written, push-pull
//   ChipSharp_Runtime_PinInput       the pin stops driving, floating (no pull, whatever was on it)
//   ChipSharp_Runtime_PinInputPull   the same with pull 1 (Pull.Up) or 2 (Pull.Down): CFGLR's input with a pull, OUTDR's
//                                    bit choosing which; the chip has both (the target's "pullDown": true)
//   ChipSharp_Runtime_PinWrite, PinToggle, PinRead
//   ChipSharp_Runtime_PinWatch, ...  the pin event, on EXTI lines 0-7 (RM 6.4): line n watches pin n of one port, the one
//                                    AFIO_EXTICR names, for the edges RTENR and FTENR say; its flag (INTFR), with INTENR,
//                                    is the interrupt EXTI7_0 (vector 20). So the chip watches one pin of each number at
//                                    a time: PC3 or PD3, not both (a program that watches the second while the first is
//                                    watched stops, saying so)
//   chipsharp_clear_pin_event        clears the flags of the edges that made the interrupt come

#include "chipsharp_hardware.h"
#include "ch32v003_chip.h"

#define MASK(pin) (1u << ((u32)(pin) & 7u))

// The pin's four bits of CFGLR, with interrupts off: a handler may change another pin of the same port.
static void configure(chipsharp_i32 pin, u32 bits)
{
    u32 shift = 4u * ((u32)pin & 7u);
    u32 enabled = ch32_interrupts_off();
    CH32_CFGLR(pin) = (CH32_CFGLR(pin) & ~(0xfu << shift)) | (bits << shift);
    ch32_interrupts_back(enabled);
}

// OUTDR's bit cleared first, so that the pin never drives high on its way.
void ChipSharp_Runtime_PinOutput(chipsharp_i32 pin)
{
    if (!CH32_IS_USABLE(pin))
    {
        return;
    }

    CH32_BCR(pin) = MASK(pin);
    configure(pin, CH32_CFG_OUTPUT);
}

void ChipSharp_Runtime_PinInput(chipsharp_i32 pin)
{
    if (!CH32_IS_USABLE(pin))
    {
        return;
    }

    configure(pin, CH32_CFG_FLOATING);
}

// OUTDR's bit first (1 pulls up, 0 down), then the input with a pull; any other number is no pull.
void ChipSharp_Runtime_PinInputPull(chipsharp_i32 pin, chipsharp_i32 pull)
{
    if (!CH32_IS_USABLE(pin))
    {
        return;
    }

    if (pull == 1)
    {
        CH32_BSHR(pin) = MASK(pin);
    }
    else if (pull == 2)
    {
        CH32_BCR(pin) = MASK(pin);
    }

    configure(pin, pull == 1 || pull == 2 ? CH32_CFG_PULL : CH32_CFG_FLOATING);
}

// A bool arrives as an int; anything but 0 is high. BSHR and BCR change the pin's bit alone.
void ChipSharp_Runtime_PinWrite(chipsharp_i32 pin, chipsharp_i32 high)
{
    if (!CH32_IS_USABLE(pin))
    {
        return;
    }

    if (high != 0)
    {
        CH32_BSHR(pin) = MASK(pin);
    }
    else
    {
        CH32_BCR(pin) = MASK(pin);
    }
}

// The level OUTDR drives, then the other, with interrupts off (a handler may toggle the same pin).
void ChipSharp_Runtime_PinToggle(chipsharp_i32 pin)
{
    if (!CH32_IS_USABLE(pin))
    {
        return;
    }

    u32 mask = MASK(pin);
    u32 enabled = ch32_interrupts_off();
    if ((CH32_OUTDR(pin) & mask) != 0)
    {
        CH32_BCR(pin) = mask;
    }
    else
    {
        CH32_BSHR(pin) = mask;
    }

    ch32_interrupts_back(enabled);
}

// Returns a bool as 0 or 1.
chipsharp_i32 ChipSharp_Runtime_PinRead(chipsharp_i32 pin)
{
    if (!CH32_IS_USABLE(pin))
    {
        return 0;
    }

    return (chipsharp_i32)((CH32_INDR(pin) >> ((u32)pin & 7u)) & 1u);
}

// ---- The pin event -----------------------------------------------------------------------------------------------

// The interrupt entry of [Interrupt(Event.Pin)] calls this before the method (the target's "interrupts.pin.clear"): a
// 1 clears a flag (one call of the method for the pins that changed together).
void chipsharp_clear_pin_event(void)
{
    EXTI_INTFR = EXTI_INTFR & 0xffu;
}

// Line pin & 7 watches pin for the edges given (none: Unwatch), from now on: its flag from before is cleared. The line
// watching another pin (of another port) is a mistake, unless this is Unwatch, which then has nothing to do.
static void watch(chipsharp_i32 pin, u32 rising, u32 falling)
{
    if (!CH32_IS_USABLE(pin))
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: The CH32V003 has no pin {0} to watch: its pins "
            "are PA1 and PA2 (1, 2), PC0-PC7 (16-23), PD0 (24) and PD2-PD6 (26-30); PD1 is its debug wire and PD7 its "
            "reset. (Parameter 'pin')", pin, 0, 0, 0);
    }

    u32 line = (u32)pin & 7u;
    u32 bit = 1u << line;
    u32 shift = 2u * line;
    u32 port = (u32)pin >> 3;
    u32 enabled = ch32_interrupts_off();
    u32 mapped = (AFIO_EXTICR >> shift) & 3u;
    if ((EXTI_INTENR & bit) != 0 && mapped != port)
    {
        ch32_interrupts_back(enabled);
        if (rising == 0 && falling == 0)
        {
            return;
        }

        chipsharp_hardware_failure("System.InvalidOperationException: Pin {0} cannot be watched while pin {1} is: the "
            "CH32V003 watches one pin of each number at a time (EXTI line {2}: PA{2}, PC{2} or PD{2}). Unwatch pin {1} "
            "first.", pin, (chipsharp_i32)(8u * mapped + line), (chipsharp_i32)line, 0);
    }

    EXTI_INTENR &= ~bit;
    AFIO_EXTICR = (AFIO_EXTICR & ~(3u << shift)) | (port << shift);
    EXTI_RTENR = rising != 0 ? EXTI_RTENR | bit : EXTI_RTENR & ~bit;
    EXTI_FTENR = falling != 0 ? EXTI_FTENR | bit : EXTI_FTENR & ~bit;
    EXTI_INTFR = bit;
    if (rising != 0 || falling != 0)
    {
        EXTI_INTENR |= bit;
        ch32_enable(CH32V003_IRQ_EXTI7_0);
    }

    ch32_interrupts_back(enabled);
}

void ChipSharp_Runtime_PinWatch(chipsharp_i32 pin)
{
    watch(pin, 1, 1);
}

void ChipSharp_Runtime_PinWatchRising(chipsharp_i32 pin)
{
    watch(pin, 1, 0);
}

void ChipSharp_Runtime_PinWatchFalling(chipsharp_i32 pin)
{
    watch(pin, 0, 1);
}

void ChipSharp_Runtime_PinUnwatch(chipsharp_i32 pin)
{
    watch(pin, 0, 0);
}
