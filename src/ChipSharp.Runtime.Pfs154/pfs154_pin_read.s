; ChipSharp runtime for the Padauk PFS154: Pin.Read (see pfs154_pin.s): 1 when the pin is high, else 0 (the port's data
; register reads the pins' levels), a C# int: SDCC returns its 4 bytes at the address the caller pushes last, SP - 4.
;
;   chipsharp_i32 ChipSharp_Runtime_PinRead(chipsharp_i32 pin) __reentrant

    .module pfs154_pin_read
    .globl _ChipSharp_Runtime_PinRead
    .globl _pfs154_pin_of

    pa = 0x10
    pb = 0x14

    .area CODE
_ChipSharp_Runtime_PinRead::
    mov a, #-7
    call _pfs154_pin_of
    cneqsn a, #0
    goto 00003$
    t1sn p, #3
    goto 00001$
    mov p, a
    mov.io a, pb
    goto 00002$
00001$:
    mov p, a
    mov.io a, pa
00002$:
    and a, p
    ceqsn a, #0
    mov a, #1
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
