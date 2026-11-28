// ChipSharp runtime for Padauk PDK14 chips: chipsharp_hardware_failure, the stop of a program that gave a pin, port,
// bus, frequency or mode the chip does not have. A PDK14 chip has no console for its message, so it is the failure stop
// (chipsharp_halt_failure); a file of its own in the library, as SDCC keeps its parameters in RAM (18 bytes), which
// only a program that calls it gives them. The chip runtimes stop at their mistakes with chipsharp_halt_failure
// themselves, each with .NET's message in a comment.

#include "chipsharp_hardware.h"

CHIPSHARP_NORETURN void chipsharp_hardware_failure(CHIPSHARP_MESSAGE_CHAR *text, chipsharp_i32 n0, chipsharp_i32 n1,
    chipsharp_i32 n2, chipsharp_i32 n3)
{
    (void)text;
    (void)n0;
    (void)n1;
    (void)n2;
    (void)n3;
    chipsharp_halt_failure();
}
