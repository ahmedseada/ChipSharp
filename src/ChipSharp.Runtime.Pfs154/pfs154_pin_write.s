; ChipSharp runtime for the Padauk PFS154: Pin.Write (see pfs154_pin.s): the pin's output latch high (high not 0) or
; low, with interrupts off.
;
;   void ChipSharp_Runtime_PinWrite(chipsharp_i32 pin, chipsharp_i32 high) __reentrant
;
; high is the second parameter, SP - 10 to SP - 7; once it is kept on the stack (1 or 0), the pin is 2 bytes further.

    .module pfs154_pin_write
    .globl _ChipSharp_Runtime_PinWrite
    .globl _pfs154_pin_of
    .globl _chipsharp_pdk14_interrupts_back

    pa = 0x10
    pb = 0x14

    .area CODE
_ChipSharp_Runtime_PinWrite::
    mov.io a, sp
    add a, #-10
    mov p, a
    idxm a, p
    ceqsn a, #0
    goto 00001$
    inc p
    idxm a, p
    ceqsn a, #0
    goto 00001$
    inc p
    idxm a, p
    ceqsn a, #0
    goto 00001$
    inc p
    idxm a, p
    ceqsn a, #0
00001$:
    mov a, #1
    push af
    mov a, #-7
    call _pfs154_pin_of
    cneqsn a, #0
    goto 00009$
    t1sn p, #3
    goto 00004$
    mov p, a
    pop af
    disgint
    ceqsn a, #0
    goto 00002$
    not p
    mov.io a, pb
    and a, p
    mov.io pb, a
    goto _chipsharp_pdk14_interrupts_back
00002$:
    mov.io a, pb
    or a, p
    mov.io pb, a
    goto _chipsharp_pdk14_interrupts_back
00004$:
    mov p, a
    pop af
    disgint
    ceqsn a, #0
    goto 00005$
    not p
    mov.io a, pa
    and a, p
    mov.io pa, a
    goto _chipsharp_pdk14_interrupts_back
00005$:
    mov.io a, pa
    or a, p
    mov.io pa, a
    goto _chipsharp_pdk14_interrupts_back
00009$:
    pop af
    ret
