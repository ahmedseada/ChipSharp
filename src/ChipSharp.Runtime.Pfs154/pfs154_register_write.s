; ChipSharp runtime for the Padauk PFS154: Registers.Write, a register of the I/O space (0x00-0x3f) by its address, the
; value's low byte (an address past it is ignored). The address is the first parameter (SP - 6 to SP - 3), the value the
; second (its low byte at SP - 10). The I/O space is reached only by addresses in the instructions, so a table has, for
; each of the 64, the value taken from p, a mov.io and a ret.
;
;   void ChipSharp_Runtime_RegisterWrite(chipsharp_u32 address, chipsharp_u32 value) __reentrant

    .module pfs154_register_write
    .globl _ChipSharp_Runtime_RegisterWrite

    .area CODE
_ChipSharp_Runtime_RegisterWrite::
    mov.io a, sp
    add a, #-3
    mov p, a
    idxm a, p
    ceqsn a, #0
    ret
    dec p
    idxm a, p
    ceqsn a, #0
    ret
    dec p
    idxm a, p
    ceqsn a, #0
    ret
    dec p
    idxm a, p
    mov p, a
    and a, #0xc0
    ceqsn a, #0
    ret
    ; the address times 3 and 1, for pcadd; then the value into p
    mov a, p
    add a, p
    add a, p
    add a, #1
    push af
    mov.io a, sp
    add a, #-12
    mov p, a
    idxm a, p
    mov p, a
    pop af
    pcadd a
    mov a, p
    mov.io 0x00, a
    ret
    mov a, p
    mov.io 0x01, a
    ret
    mov a, p
    mov.io 0x02, a
    ret
    mov a, p
    mov.io 0x03, a
    ret
    mov a, p
    mov.io 0x04, a
    ret
    mov a, p
    mov.io 0x05, a
    ret
    mov a, p
    mov.io 0x06, a
    ret
    mov a, p
    mov.io 0x07, a
    ret
    mov a, p
    mov.io 0x08, a
    ret
    mov a, p
    mov.io 0x09, a
    ret
    mov a, p
    mov.io 0x0a, a
    ret
    mov a, p
    mov.io 0x0b, a
    ret
    mov a, p
    mov.io 0x0c, a
    ret
    mov a, p
    mov.io 0x0d, a
    ret
    mov a, p
    mov.io 0x0e, a
    ret
    mov a, p
    mov.io 0x0f, a
    ret
    mov a, p
    mov.io 0x10, a
    ret
    mov a, p
    mov.io 0x11, a
    ret
    mov a, p
    mov.io 0x12, a
    ret
    mov a, p
    mov.io 0x13, a
    ret
    mov a, p
    mov.io 0x14, a
    ret
    mov a, p
    mov.io 0x15, a
    ret
    mov a, p
    mov.io 0x16, a
    ret
    mov a, p
    mov.io 0x17, a
    ret
    mov a, p
    mov.io 0x18, a
    ret
    mov a, p
    mov.io 0x19, a
    ret
    mov a, p
    mov.io 0x1a, a
    ret
    mov a, p
    mov.io 0x1b, a
    ret
    mov a, p
    mov.io 0x1c, a
    ret
    mov a, p
    mov.io 0x1d, a
    ret
    mov a, p
    mov.io 0x1e, a
    ret
    mov a, p
    mov.io 0x1f, a
    ret
    mov a, p
    mov.io 0x20, a
    ret
    mov a, p
    mov.io 0x21, a
    ret
    mov a, p
    mov.io 0x22, a
    ret
    mov a, p
    mov.io 0x23, a
    ret
    mov a, p
    mov.io 0x24, a
    ret
    mov a, p
    mov.io 0x25, a
    ret
    mov a, p
    mov.io 0x26, a
    ret
    mov a, p
    mov.io 0x27, a
    ret
    mov a, p
    mov.io 0x28, a
    ret
    mov a, p
    mov.io 0x29, a
    ret
    mov a, p
    mov.io 0x2a, a
    ret
    mov a, p
    mov.io 0x2b, a
    ret
    mov a, p
    mov.io 0x2c, a
    ret
    mov a, p
    mov.io 0x2d, a
    ret
    mov a, p
    mov.io 0x2e, a
    ret
    mov a, p
    mov.io 0x2f, a
    ret
    mov a, p
    mov.io 0x30, a
    ret
    mov a, p
    mov.io 0x31, a
    ret
    mov a, p
    mov.io 0x32, a
    ret
    mov a, p
    mov.io 0x33, a
    ret
    mov a, p
    mov.io 0x34, a
    ret
    mov a, p
    mov.io 0x35, a
    ret
    mov a, p
    mov.io 0x36, a
    ret
    mov a, p
    mov.io 0x37, a
    ret
    mov a, p
    mov.io 0x38, a
    ret
    mov a, p
    mov.io 0x39, a
    ret
    mov a, p
    mov.io 0x3a, a
    ret
    mov a, p
    mov.io 0x3b, a
    ret
    mov a, p
    mov.io 0x3c, a
    ret
    mov a, p
    mov.io 0x3d, a
    ret
    mov a, p
    mov.io 0x3e, a
    ret
    mov a, p
    mov.io 0x3f, a
    ret
