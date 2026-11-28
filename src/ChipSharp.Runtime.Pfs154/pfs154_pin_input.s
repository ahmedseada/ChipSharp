; ChipSharp runtime for the Padauk PFS154: Pin.Input (see pfs154_pin.s): the pin an input (PAC, PBC) with no pull-high (PAPH,
; PBPH), with interrupts off: it floats until something drives it. Pin.Input(pin, Pull.Up) is pfs154_pin_input_pull.s.
;
;   void ChipSharp_Runtime_PinInput(chipsharp_i32 pin) __reentrant

    .module pfs154_pin_input
    .globl _ChipSharp_Runtime_PinInput
    .globl _pfs154_pin_of
    .globl _chipsharp_pdk14_interrupts_back

    pac = 0x11
    paph = 0x12
    pbc = 0x15
    pbph = 0x16

    .area CODE
_ChipSharp_Runtime_PinInput::
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
    mov.io a, pbph
    and a, p
    mov.io pbph, a
    goto _chipsharp_pdk14_interrupts_back
00001$:
    mov p, a
    not p
    mov.io a, pac
    and a, p
    mov.io pac, a
    mov.io a, paph
    and a, p
    mov.io paph, a
    goto _chipsharp_pdk14_interrupts_back
