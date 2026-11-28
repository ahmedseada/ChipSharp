// ChipSharp runtime for the STM8S103F3: ChipSharp.Hardware.Pin, each function a module of its own (the STM8 runtime cuts
// the file at its "//@module" lines): a program has only those it calls.
//
// Pins by number, port * 8 + bit, as chipsharp_hardware.h numbers an STM8's: PA1-PA3 are 1-3, PB4 and PB5 12 and 13 (the
// board's LED, on when PB5 is low), PC3-PC7 19-23, PD1-PD6 25-30 (PD1 is SWIM, the ST-LINK's); a number no pin of the
// chip has (stm8s103f3_pins.h's STM8S103F3_PINS) is ignored, and reads as low. Each port has five registers from its
// address on: ODR (the level driven), IDR (the pins' levels), DDR (1: an output), CR1 (an input's pull-up, an output
// push-pull) and CR2 (an input's interrupt, an output's fast mode).
//
//   ChipSharp_Runtime_PinOutput   the pin drives low until written, push-pull (PB4 and PB5 are true open-drain: they only
//                                 pull low, and a high is the pull-up of whatever is wired to them)
//   ChipSharp_Runtime_PinInput    the pin stops driving, with no pull-up (CR1 clear)
//   ChipSharp_Runtime_PinInputPull  the same, then pull 1 (Pull.Up) sets CR1's bit: a button to GND needs it; the chip has
//                                 no pull-down
//   ChipSharp_Runtime_PinWrite, PinToggle, PinRead

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

// In the module "port": sets (high) or clears the bits of mask in a port's register, with interrupts off: a handler may
// change the same port.
void chipsharp_pin_change(volatile u8 *address, u8 mask, u8 high) CHIPSHARP_REENTRANT;

//@module port
// The pins of each port, from the pins' mask (bit n for pin n): one byte each, worked out when this is compiled.
static const u8 pins[STM8S103F3_PORT_COUNT] = {
    (u8)STM8S103F3_PINS, (u8)(STM8S103F3_PINS >> 8), (u8)(STM8S103F3_PINS >> 16), (u8)(STM8S103F3_PINS >> 24),
};

volatile u8 *chipsharp_pin_port(i32 pin, u8 *mask)
{
    if ((u32)pin >= 8u * STM8S103F3_PORT_COUNT)
    {
        return 0;
    }

    u8 port = (u8)pin >> 3;
    u8 bit = (u8)(1u << ((u8)pin & 7u));
    if ((pins[port] & bit) == 0)
    {
        return 0;
    }

    *mask = bit;
    return (volatile u8 *)(0x5000u + (u8)((u8)(port << 2) + port)); // 5 registers a port, with no multiplication
}

void chipsharp_pin_change(volatile u8 *address, u8 mask, u8 high)
{
    u8 cc = chipsharp_interrupts_off();
    *address = high ? *address | mask : *address & (u8)~mask;
    chipsharp_interrupts_back(cc);
}

//@module output
// ODR first, so that the pin never drives high on its way; push-pull (CR1) once it drives; slow (CR2), as after reset.
void ChipSharp_Runtime_PinOutput(chipsharp_i32 pin)
{
    u8 mask;
    volatile u8 *port = chipsharp_pin_port(pin, &mask);
    if (port != 0)
    {
        chipsharp_pin_change(port + PORT_ODR, mask, 0);
        chipsharp_pin_change(port + PORT_CR2, mask, 0);
        chipsharp_pin_change(port + PORT_DDR, mask, 1);
        chipsharp_pin_change(port + PORT_CR1, mask, 1);
    }
}

//@module input
// CR1 first: an output that drove high only lets go (open-drain), and the input it becomes has no pull-up on its way.
void ChipSharp_Runtime_PinInput(chipsharp_i32 pin)
{
    u8 mask;
    volatile u8 *port = chipsharp_pin_port(pin, &mask);
    if (port != 0)
    {
        chipsharp_pin_change(port + PORT_CR1, mask, 0);
        chipsharp_pin_change(port + PORT_DDR, mask, 0);
    }
}

//@module inputpull
// 0 none, 1 the pull-up (CR1's bit of an input); the chip has no pull-down.
void ChipSharp_Runtime_PinInputPull(chipsharp_i32 pin, chipsharp_i32 pull)
{
    u8 mask;
    volatile u8 *port = chipsharp_pin_port(pin, &mask);
    if ((u32)pull > 1u)
    {
        chipsharp_hardware_failure("System.ArgumentOutOfRangeException: The STM8S103F3 has no pull-down: the pull ({0}) is "
            "not Pull.None or Pull.Up. (Parameter 'pull')", pull, 0, 0, 0);
    }

    if (port != 0)
    {
        chipsharp_pin_change(port + PORT_CR1, mask, 0);
        chipsharp_pin_change(port + PORT_DDR, mask, 0);
        chipsharp_pin_change(port + PORT_CR1, mask, (u8)pull);
    }
}

//@module write
// A bool arrives as an int; anything but 0 is high.
void ChipSharp_Runtime_PinWrite(chipsharp_i32 pin, chipsharp_i32 high)
{
    u8 mask;
    volatile u8 *port = chipsharp_pin_port(pin, &mask);
    if (port != 0)
    {
        chipsharp_pin_change(port + PORT_ODR, mask, high != 0);
    }
}

//@module toggle
void ChipSharp_Runtime_PinToggle(chipsharp_i32 pin)
{
    u8 mask;
    volatile u8 *port = chipsharp_pin_port(pin, &mask);
    if (port != 0)
    {
        u8 cc = chipsharp_interrupts_off();
        port[PORT_ODR] ^= mask;
        chipsharp_interrupts_back(cc);
    }
}

//@module read
// Returns a bool as 0 or 1.
chipsharp_i32 ChipSharp_Runtime_PinRead(chipsharp_i32 pin)
{
    u8 mask;
    volatile u8 *port = chipsharp_pin_port(pin, &mask);
    return port != 0 && (port[PORT_IDR] & mask) != 0;
}
