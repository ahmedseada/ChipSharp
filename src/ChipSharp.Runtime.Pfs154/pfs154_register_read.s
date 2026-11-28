; ChipSharp runtime for the Padauk PFS154: Registers.Read, a register of the I/O space (0x00-0x3f: PA 0x10, PAPH 0x12,
; TM2C 0x1c, ...) by its address; an address past it reads 0. A C# uint: SDCC returns its 4 bytes at the address the
; caller pushes last (SP - 4); the address is below it (SP - 8 to SP - 5). The I/O space is reached only by addresses
; in the instructions, so a table has a mov.io and a ret for each of the 64.
;
;   chipsharp_u32 ChipSharp_Runtime_RegisterRead(chipsharp_u32 address) __reentrant

    .module pfs154_register_read
    .globl _ChipSharp_Runtime_RegisterRead

    .area CODE
_ChipSharp_Runtime_RegisterRead::
    mov.io a, sp
    add a, #-5
    mov p, a
    idxm a, p
    ceqsn a, #0
    goto 00002$
    dec p
    idxm a, p
    ceqsn a, #0
    goto 00002$
    dec p
    idxm a, p
    ceqsn a, #0
    goto 00002$
    dec p
    idxm a, p
    mov p, a
    and a, #0xc0
    ceqsn a, #0
    goto 00002$
    mov a, p
    sl a
    add a, #1
    call 00009$
    goto 00003$
00002$:
    mov a, #0
00003$:
    push af
    mov.io a, sp
    add a, #-6
    mov p, a
    idxm a, p
    mov p, a
    pop af
    idxm p, a
    inc p
    mov a, #0
    idxm p, a
    inc p
    idxm p, a
    inc p
    idxm p, a
    ret
00009$:
    pcadd a
    mov.io a, 0x00
    ret
    mov.io a, 0x01
    ret
    mov.io a, 0x02
    ret
    mov.io a, 0x03
    ret
    mov.io a, 0x04
    ret
    mov.io a, 0x05
    ret
    mov.io a, 0x06
    ret
    mov.io a, 0x07
    ret
    mov.io a, 0x08
    ret
    mov.io a, 0x09
    ret
    mov.io a, 0x0a
    ret
    mov.io a, 0x0b
    ret
    mov.io a, 0x0c
    ret
    mov.io a, 0x0d
    ret
    mov.io a, 0x0e
    ret
    mov.io a, 0x0f
    ret
    mov.io a, 0x10
    ret
    mov.io a, 0x11
    ret
    mov.io a, 0x12
    ret
    mov.io a, 0x13
    ret
    mov.io a, 0x14
    ret
    mov.io a, 0x15
    ret
    mov.io a, 0x16
    ret
    mov.io a, 0x17
    ret
    mov.io a, 0x18
    ret
    mov.io a, 0x19
    ret
    mov.io a, 0x1a
    ret
    mov.io a, 0x1b
    ret
    mov.io a, 0x1c
    ret
    mov.io a, 0x1d
    ret
    mov.io a, 0x1e
    ret
    mov.io a, 0x1f
    ret
    mov.io a, 0x20
    ret
    mov.io a, 0x21
    ret
    mov.io a, 0x22
    ret
    mov.io a, 0x23
    ret
    mov.io a, 0x24
    ret
    mov.io a, 0x25
    ret
    mov.io a, 0x26
    ret
    mov.io a, 0x27
    ret
    mov.io a, 0x28
    ret
    mov.io a, 0x29
    ret
    mov.io a, 0x2a
    ret
    mov.io a, 0x2b
    ret
    mov.io a, 0x2c
    ret
    mov.io a, 0x2d
    ret
    mov.io a, 0x2e
    ret
    mov.io a, 0x2f
    ret
    mov.io a, 0x30
    ret
    mov.io a, 0x31
    ret
    mov.io a, 0x32
    ret
    mov.io a, 0x33
    ret
    mov.io a, 0x34
    ret
    mov.io a, 0x35
    ret
    mov.io a, 0x36
    ret
    mov.io a, 0x37
    ret
    mov.io a, 0x38
    ret
    mov.io a, 0x39
    ret
    mov.io a, 0x3a
    ret
    mov.io a, 0x3b
    ret
    mov.io a, 0x3c
    ret
    mov.io a, 0x3d
    ret
    mov.io a, 0x3e
    ret
    mov.io a, 0x3f
    ret
