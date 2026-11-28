// ChipSharp runtime for Padauk PDK14 chips: what the architecture's files give a chip runtime's C.
//
// The functions of the hardware contract a handler may call while Main is inside them (CHIPSHARP_REENTRANT, SDCC's
// __reentrant) have their parameters on the stack, which SDCC's C reaches with several instructions a byte. So a chip
// runtime writes each as a few instructions of assembly that turn interrupts off, copy the parameters to
// chipsharp_pdk14_arguments (pdk14_arguments.s), call a function in C that keeps its values at fixed addresses as SDCC
// does without __reentrant (nothing can run it again meanwhile: interrupts are off), and turn interrupts back on:
//
//   void ChipSharp_Runtime_PinWatch(chipsharp_i32 pin) CHIPSHARP_REENTRANT __naked
//   {
//       (void)pin;
//       __asm
//           disgint
//           call _chipsharp_pdk14_argument_1
//           call _pfs154_watch_both
//           goto _chipsharp_pdk14_interrupts_back
//       __endasm;
//   }
//
// and one that returns 8 bytes (chipsharp_i64) puts them in chipsharp_pdk14_arguments and ends with goto
// _chipsharp_pdk14_result_8 instead.

#ifndef PDK14_RUNTIME_H
#define PDK14_RUNTIME_H

#include "chipsharp_hardware.h"

// 1 while the interrupt handler runs (pdk14_interrupt.s).
extern volatile unsigned char chipsharp_pdk14_in_interrupt;

// After a disgint: interrupts on again, unless the interrupt handler runs (pdk14_interrupt.s).
void chipsharp_pdk14_interrupts_back(void);

#define PDK14_INTERRUPTS_OFF() __asm__("disgint")
#define PDK14_INTERRUPTS_BACK() chipsharp_pdk14_interrupts_back()

// The parameters of the function of the contract that runs (pdk14_arguments.s), and its result.
typedef union
{
    unsigned char bytes[8];
    chipsharp_i32 i32[2];
    chipsharp_u32 u32[2];
    chipsharp_i64 i64;
    unsigned long long u64;
} chipsharp_pdk14_values;

extern chipsharp_pdk14_values chipsharp_pdk14_arguments;

#endif
