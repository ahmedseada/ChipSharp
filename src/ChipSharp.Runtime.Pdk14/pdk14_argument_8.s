; ChipSharp runtime for Padauk PDK14 chips: chipsharp_pdk14_argument_8 (see pdk14_arguments.s), called first by a
; function of one 8-byte parameter (chipsharp_i64), which it copies to chipsharp_pdk14_arguments' bytes 0-7. A file of
; its own in the library.

    .module pdk14_argument_8
    .globl _chipsharp_pdk14_arguments
    .globl _chipsharp_pdk14_argument_8

    .area CODE
_chipsharp_pdk14_argument_8::
    mov.io a, sp
    add a, #-12
    mov p, a
    idxm a, p
    mov _chipsharp_pdk14_arguments+0, a
    inc p
    idxm a, p
    mov _chipsharp_pdk14_arguments+1, a
    inc p
    idxm a, p
    mov _chipsharp_pdk14_arguments+2, a
    inc p
    idxm a, p
    mov _chipsharp_pdk14_arguments+3, a
    inc p
    idxm a, p
    mov _chipsharp_pdk14_arguments+4, a
    inc p
    idxm a, p
    mov _chipsharp_pdk14_arguments+5, a
    inc p
    idxm a, p
    mov _chipsharp_pdk14_arguments+6, a
    inc p
    idxm a, p
    mov _chipsharp_pdk14_arguments+7, a
    ret

