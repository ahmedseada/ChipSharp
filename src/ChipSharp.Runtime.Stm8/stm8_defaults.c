// ChipSharp runtime for STM8 chips: the hooks of the hardware contract a chip runtime may define, empty, for a program
// built with the architecture's runtime alone. SDCC has no weak functions: each is a module of its own (Stm8Runtime
// makes one of each "//@module" part below), and a chip runtime that defines one leaves its module out.
//
//   chipsharp_chip_init        the chip's set-up before the program: nothing
//   chipsharp_before_halt      what runs before the program stops: nothing
//   chipsharp_failure_write    where the failure messages go: nowhere, the chip has no console
//   ChipSharp_Runtime_LocalUtcOffset   the offset of local time from UTC that a date's z and K formats write: 0, a
//                              chip keeps no time zone (ChipSharp.Library's Runtime.LocalUtcOffset)

#include "chipsharp_hardware.h"

//@module local_utc_offset
chipsharp_i64 ChipSharp_Runtime_LocalUtcOffset(void)
{
    return 0;
}

//@module chip_init
void chipsharp_chip_init(void)
{
}

//@module before_halt
void chipsharp_before_halt(void)
{
}

//@module failure_write
void chipsharp_failure_write(const char *text, chipsharp_count count)
{
    (void)text;
    (void)count;
}
