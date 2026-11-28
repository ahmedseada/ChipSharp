// ChipSharp runtime for the Padauk PFS154: its start-up and its stop, linked whole (they take the place of the PDK14
// runtime's empty ones).
//
//   __sdcc_external_startup   what SDCC's start-up calls first, before the variables are set and main(), interrupts
//                             off; as easypdkprog (the Easy PDK programmer's tool, src/ChipSharp.Flasher.EasyPdkProg)
//                             needs it, and before anything else: the factory's trim of the IHRC (call 0x7ed, a ret k
//                             at word 0x7ed that easypdkprog never erases; mov ihrcr, a), then the system clock CLKMD
//                             gives (pfs154_clock.h: IHRC / 8, 2 MHz, for pfs154-bare; the IHRC and ILRC on and the
//                             watchdog off, which is on after reset), then at once the ten words of easypdkprog's
//                             calibration marker (Free-PDK's EASY_PDK_CALIBRATE_IHRC: and a, #k for 'R', 'C', 1, the
//                             system clock in hertz, 5000 mV and IHRCR's address), which it finds in the image and
//                             replaces, when it writes the program, with the IHRCR that makes the system clock exact.
//                             Without that (in a simulator, or written with --nocalibrate) they are ANDs that change
//                             nothing but A, and IHRCR keeps the factory's trim, for 16 MHz. Returns 0: SDCC then sets
//                             the variables
//   chipsharp_chip_init       T16 counting microseconds for the delays, the clock and the timer event: the IHRC / 16,
//                             its interrupt (the clock's, once it is linked: pfs154_clock.c) when bit 15 falls, as it
//                             goes round; every pin's digital input on (PADIER, PBDIER), so that Pin.Read reads it and
//                             its change can wake the chip
//   chipsharp_before_halt     what the program's stop does first: no pin's change wakes the chip any more (PADIER,
//                             PBDIER off), so that stopsys stops it for good

#include "chipsharp_hardware.h"
#include "pfs154_runtime.h"

unsigned char __sdcc_external_startup(void)
{
    __asm__("call #0x7ed\n"
            "mov.io 0x0b, a\n"
            "mov a, #" PFS154_CLKMD "\n"
            "mov.io 0x03, a\n"
            PFS154_IHRC_CALIBRATION);
    return 0;
}

void chipsharp_chip_init(void)
{
    T16M = T16M_CLOCK_IHRC | T16M_DIVIDER_DIV16 | T16M_INTERRUPT(7);
    INTEGS = INTEGS_T16_FALLING_MASK;
    PADIER = 0xff;
    PBDIER = 0xff;
    INTRQ = 0;
}

void chipsharp_before_halt(void)
{
    PADIER = 0;
    PBDIER = 0;
}
