; ChipSharp runtime for the Padauk PFS154: Pin.Input(pin, pull) (see pfs154_pin.s): the pin an input with no pull (pull 0:
; pfs154_pin_input.s) or with its pull-high on (1: PAPH or PBPH, the pin's bit set), with interrupts off. The chip has
; pull-highs only (no pull-low), so any other pull leaves the pin with none, as Pin ignores a pin the chip does not have.
;
;   void ChipSharp_Runtime_PinInputPull(chipsharp_i32 pin, chipsharp_i32 pull) __reentrant
;
; pull is the second parameter, SP - 10 to SP - 7, low byte first; a Pull is 0 to 2, so its low byte says. Its code is
; a file of its own: a program that never asks for a pull has none of it.

    .module pfs154_pin_input_pull
    .globl _ChipSharp_Runtime_PinInputPull
    .globl _ChipSharp_Runtime_PinInput
    .globl _pfs154_pin_of
    .globl _chipsharp_pdk14_interrupts_back

    pac = 0x11
    paph = 0x12
    pbc = 0x15
    pbph = 0x16

    .area CODE
_ChipSharp_Runtime_PinInputPull::
    mov.io a, sp
    add a, #-10
    mov p, a
    idxm a, p
    cneqsn a, #0
    goto _ChipSharp_Runtime_PinInput
    ceqsn a, #1
    goto _ChipSharp_Runtime_PinInput
    mov.io a, sp
    add a, #-9
    mov p, a
    idxm a, p
    ceqsn a, #0
    goto _ChipSharp_Runtime_PinInput
    mov a, #-5
    call _pfs154_pin_of
    cneqsn a, #0
    ret
    disgint
    t1sn p, #3
    goto 00001$
    mov p, a
    not p
    mov.io a, pbc
    and a, p
    mov.io pbc, a
    not p
    mov.io a, pbph
    or a, p
    mov.io pbph, a
    goto _chipsharp_pdk14_interrupts_back
00001$:
    mov p, a
    not p
    mov.io a, pac
    and a, p
    mov.io pac, a
    not p
    mov.io a, paph
    or a, p
    mov.io paph, a
    goto _chipsharp_pdk14_interrupts_back
