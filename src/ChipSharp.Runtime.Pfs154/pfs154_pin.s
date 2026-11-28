; ChipSharp runtime for the Padauk PFS154: the pins (ChipSharp.Hardware.Pin) by ChipSharp's numbers, port * 8 + bit:
; PA0 and PA3-PA7 are 0 and 3-7, PB0-PB7 8-15. A number no pin has (1, 2, 16 and up, below 0) is ignored, and reads as
; low.
;
; The functions are in assembly, each in a file of its own (pfs154_pin_*.s), as they are called often and a handler may
; call one while Main is inside it (the contract's CHIPSHARP_REENTRANT, SDCC's __reentrant): their parameters are on the
; stack, the first nearest the return address, each low byte first (pdk14_arguments.s), and they keep nothing in RAM.
; Toggle changes the pin with one instruction (xor.io), which changes no other pin; the others read, change and write a
; register of the port with interrupts off, so a handler that changes another pin of the port meanwhile loses nothing.
; A port's data register reads the pins' levels (as set0 and set1 do), so writing one pin's output writes the latch of
; each input of the port with its level: Pin.Output sets the latch low first, so an input made an output is low until
; written.
;
;   pfs154_pin_of   the pin of a chipsharp_i32 whose last byte is at SP + A, A below 0, from inside pfs154_pin_of: its
;                   mask in A, 0 when no pin has the number; and the number in p (bit 3 set for port B)

    .module pfs154_pin
    .globl _pfs154_pin_of

    .area CODE
_pfs154_pin_of::
    mov p, a
    mov.io a, sp
    add a, p
    mov p, a
    idxm a, p
    ceqsn a, #0
    ret #0
    dec p
    idxm a, p
    ceqsn a, #0
    ret #0
    dec p
    idxm a, p
    ceqsn a, #0
    ret #0
    dec p
    idxm a, p
    mov p, a
    sub a, #16
    t1sn.io 0x00, #1
    ret #0
    mov a, p
    add a, #1
    pcadd a
    ret #0x01
    ret #0x00
    ret #0x00
    ret #0x08
    ret #0x10
    ret #0x20
    ret #0x40
    ret #0x80
    ret #0x01
    ret #0x02
    ret #0x04
    ret #0x08
    ret #0x10
    ret #0x20
    ret #0x40
    ret #0x80
