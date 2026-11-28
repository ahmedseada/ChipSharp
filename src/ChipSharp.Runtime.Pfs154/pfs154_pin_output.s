; ChipSharp runtime for the Padauk PFS154: Pin.Output (see pfs154_pin.s): the pin's output latch low, then the pin an
; output (PAC, PBC), with interrupts off. PA5 is an open-drain output: low, or let go.
;
;   void ChipSharp_Runtime_PinOutput(chipsharp_i32 pin) __reentrant

    .module pfs154_pin_output
    .globl _ChipSharp_Runtime_PinOutput
    .globl _pfs154_pin_of
    .globl _chipsharp_pdk14_interrupts_back

    pa = 0x10
    pac = 0x11
    pb = 0x14
    pbc = 0x15

    .area CODE
_ChipSharp_Runtime_PinOutput::
    mov a, #-5
    call _pfs154_pin_of
    cneqsn a, #0
    ret
    disgint
    t1sn p, #3
    goto 00001$
    mov p, a
    not p
    mov.io a, pb
    and a, p
    mov.io pb, a
    not p
    mov.io a, pbc
    or a, p
    mov.io pbc, a
    goto _chipsharp_pdk14_interrupts_back
00001$:
    mov p, a
    not p
    mov.io a, pa
    and a, p
    mov.io pa, a
    not p
    mov.io a, pac
    or a, p
    mov.io pac, a
    goto _chipsharp_pdk14_interrupts_back
