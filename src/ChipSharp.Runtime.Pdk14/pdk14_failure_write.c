// ChipSharp runtime for Padauk PDK14 chips: chipsharp_failure_write when the chip runtime has none: the text goes
// nowhere, as the chip has no console. A file of its own in the library: a chip runtime's, in a file linked whole, takes
// its place.

#include "chipsharp_hardware.h"

void chipsharp_failure_write(const char *text, chipsharp_count count)
{
    (void)text;
    (void)count;
}
