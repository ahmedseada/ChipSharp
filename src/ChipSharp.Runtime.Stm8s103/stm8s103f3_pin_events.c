// ChipSharp runtime for the STM8S103F3: the pin event, Pin.Watch, WatchRising, WatchFalling and Unwatch, which run the
// program's [Interrupt(Event.Pin)] method (its entry chipsharp_isr_irq30, stm8s103f3.json's "interrupts.pin.irq").
//
// Every pin can be watched: each port has an external interrupt (EXTI, vectors 3-6), on for a pin by its bit of CR2
// while it is an input. The edges that interrupt are the port's, one sensitivity for all its pins (EXTI_CR1), so a port
// with a watched pin takes both, and its handler compares the levels with those it saw last and runs the method for the
// edges watched: one call for the pins that changed together. A pulse shorter than the handler's entry (a few
// microseconds) may pass unseen. EXTI_CR1 may be written only with interrupts off (I1:I0 at 11), as it is here.

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

// By port (A-D): the pins watched for each edge, and the levels the handler saw last.
static u8 rising[STM8S103F3_PORT_COUNT];
static u8 falling[STM8S103F3_PORT_COUNT];
static u8 levels[STM8S103F3_PORT_COUNT];

static void changed(u8 port)
{
    u8 now = *(volatile u8 *)(0x5000u + (u8)((u8)(port << 2) + port) + PORT_IDR); // 5 registers a port
    u8 edges = now ^ levels[port];
    levels[port] = now;
    if ((edges & now & rising[port]) != 0 || (edges & (u8)~now & falling[port]) != 0)
    {
        PIN_EVENT();
    }
}

static void watch(chipsharp_i32 pin, u8 rise, u8 fall)
{
    u8 mask;
    volatile u8 *registers = chipsharp_pin_port(pin, &mask);
    if (registers == 0)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: The STM8S103F3 has no pin {0}: its pins are "
            "PA1-PA3 (1-3), PB4 and PB5 (12, 13), PC3-PC7 (19-23) and PD1-PD6 (25-30). (Parameter 'pin')", pin, 0, 0, 0);
    }

    u8 port = (u8)((u8)pin >> 3);
    u8 cc = chipsharp_interrupts_off();
    chipsharp_pin_hook = changed;
    rising[port] = rise ? rising[port] | mask : rising[port] & (u8)~mask;
    falling[port] = fall ? falling[port] | mask : falling[port] & (u8)~mask;
    levels[port] = (u8)((levels[port] & (u8)~mask) | (registers[PORT_IDR] & mask)); // its changes before are forgotten
    EXTI_CR1 |= (u8)(EXTI_CR1_PAIS_BOTH << (2u * port));
    registers[PORT_CR2] = rise | fall ? registers[PORT_CR2] | mask : registers[PORT_CR2] & (u8)~mask;
    chipsharp_interrupts_back(cc);
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
