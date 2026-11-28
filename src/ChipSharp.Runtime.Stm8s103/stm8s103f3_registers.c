// ChipSharp runtime for the STM8S103F3: ChipSharp.Hardware.Registers. The chip's registers are bytes, so a read or
// write is one byte at that address (the value's low 8 bits), e.g. 0x5005 is PB_ODR, 0x500d PC_CR1.

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

chipsharp_u32 ChipSharp_Runtime_RegisterRead(chipsharp_u32 address)
{
    return *(volatile u8 *)(u16)address;
}

void ChipSharp_Runtime_RegisterWrite(chipsharp_u32 address, chipsharp_u32 value)
{
    *(volatile u8 *)(u16)address = (u8)value;
}
