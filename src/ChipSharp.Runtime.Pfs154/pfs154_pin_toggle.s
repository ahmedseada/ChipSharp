; ChipSharp runtime for the Padauk PFS154: Pin.Toggle (see pfs154_pin.s): the pin's output latch the other way, with
; one instruction (xor.io), which changes no other pin.
;
;   void ChipSharp_Runtime_PinToggle(chipsharp_i32 pin) __reentrant

    .module pfs154_pin_toggle
    .globl _ChipSharp_Runtime_PinToggle
    .globl _pfs154_pin_of

    pa = 0x10
    pb = 0x14

    .area CODE
_ChipSharp_Runtime_PinToggle::
    mov a, #-5
    call _pfs154_pin_of
    cneqsn a, #0
    ret
    t1sn p, #3
    xor.io pa, a
    t0sn p, #3
    xor.io pb, a
    ret
