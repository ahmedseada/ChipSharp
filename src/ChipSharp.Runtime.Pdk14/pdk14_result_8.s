; ChipSharp runtime for Padauk PDK14 chips: chipsharp_pdk14_result_8 (see pdk14_arguments.s), where a function that
; returns 8 bytes (chipsharp_i64) goes (goto) at its end, with interrupts off and its result in chipsharp_pdk14_arguments'
; bytes 0-7: it copies them to the address the caller pushed last (SP - 4), then turns interrupts back on
; (chipsharp_pdk14_interrupts_back), which returns from the function. A file of its own in the library.

    .module pdk14_result_8
    .globl _chipsharp_pdk14_arguments
    .globl _chipsharp_pdk14_result_8
    .globl _chipsharp_pdk14_interrupts_back

    .area CODE
_chipsharp_pdk14_result_8::
    mov.io a, sp
    add a, #-4
    mov p, a
    idxm a, p
    mov p, a
    mov a, _chipsharp_pdk14_arguments+0
    idxm p, a
    inc p
    mov a, _chipsharp_pdk14_arguments+1
    idxm p, a
    inc p
    mov a, _chipsharp_pdk14_arguments+2
    idxm p, a
    inc p
    mov a, _chipsharp_pdk14_arguments+3
    idxm p, a
    inc p
    mov a, _chipsharp_pdk14_arguments+4
    idxm p, a
    inc p
    mov a, _chipsharp_pdk14_arguments+5
    idxm p, a
    inc p
    mov a, _chipsharp_pdk14_arguments+6
    idxm p, a
    inc p
    mov a, _chipsharp_pdk14_arguments+7
    idxm p, a
    goto _chipsharp_pdk14_interrupts_back
