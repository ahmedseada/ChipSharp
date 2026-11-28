// ChipSharp runtime for the Padauk PFS154: what its files share. The chip's registers, interrupts and pins are those of
// the headers ChipSharp.Chips writes from chips/pfs154.json (pfs154_registers.h: SDCC's __sfr __at, one name for each
// register of the I/O space), the system clock that of the target (pfs154_clock.h, which the runtime writes for it).
//
// Time is T16's count, which chipsharp_chip_init starts: the IHRC (16 MHz, trimmed) / 16, a count each microsecond
// whatever the system clock, read with ldt16 (pfs154_t16.s). The delays watch it go by; the clock (pfs154_clock.s) also
// counts its rounds, so that it has 48 bits of microseconds; the timer event (pfs154_timer.s) wakes up through TM2 when
// the clock says its time has come. Most of the runtime is assembly, as SDCC's C keeps every value it works out in RAM
// of its own; the C here keeps its expressions simple for the same reason.
//
// A program's mistake (a pin that cannot do what it is asked, a frequency the chip cannot make) stops it with
// PFS154_MISTAKE(): the failure stop at once, .NET's exception and message in a comment where it is found. The PFS154
// has no console for the message, and chipsharp_hardware_failure would keep its numbers in RAM (18 bytes) and cost a
// word of program memory for each character of its text.

#ifndef PFS154_RUNTIME_H
#define PFS154_RUNTIME_H

#include "chipsharp_hardware.h"
#include "pdk14_runtime.h"
#include "pfs154_registers.h"
#include "pfs154_interrupts.h"
#include "pfs154_pins.h"
#include "pfs154_clock.h"

typedef unsigned char u8;
typedef unsigned int u16;
typedef unsigned long u32;
typedef long i32;
typedef unsigned long long u64;

#define PFS154_MISTAKE() chipsharp_halt_failure()

// T16's count: microseconds (pfs154_t16.s).
u16 pfs154_t16(void);

#endif
