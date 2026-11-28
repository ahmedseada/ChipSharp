; ChipSharp runtime for the Padauk PFS154: T16's count, microseconds (the IHRC / 16, as chipsharp_chip_init starts it).
;
;   pfs154_t16   the count, as SDCC returns 16 bits: the low byte in A, the high in p. ldt16 copies the count whole
;                into a word-aligned pair of bytes, p's (at 0); p+1 is put back as it was

    .module pfs154_t16
    .globl _pfs154_t16

    .area CODE
_pfs154_t16::
    mov a, p+1
    push af
    ldt16 p
    pop af
    xch a, p+1
    xch a, p
    ret
