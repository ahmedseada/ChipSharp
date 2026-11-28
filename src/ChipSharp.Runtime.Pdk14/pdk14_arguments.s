; ChipSharp runtime for Padauk PDK14 chips: the parameters and results of the hardware contract's functions that a
; handler may call while Main is inside them (pdk14_runtime.h). SDCC passes such a function's parameters on the stack:
; the caller pushes the last first, each low byte first, so the first is nearest the return address; for a result of
; more than 2 bytes it pushes last the address to put it at. So at the function's start SP - 2 and SP - 1 hold the
; return address and below them come its parameters (or the result's address at SP - 4, then the parameters).
;
;   chipsharp_pdk14_arguments    8 bytes: the parameters the functions below copy, then the result
;   chipsharp_pdk14_argument_1   called first by a function of one 4-byte parameter (chipsharp_i32): it to bytes 0-3
;   chipsharp_pdk14_argument_2   by a function of two: the first to bytes 0-3, the second to 4-7
;   chipsharp_pdk14_argument_8   by a function of one 8-byte parameter (chipsharp_i64): it to bytes 0-7
;                                (pdk14_argument_8.s)
;   chipsharp_pdk14_result_8     where a function that returns 8 bytes goes (goto) at its end, with interrupts off and
;                                its result in bytes 0-7: it copies them to the caller's address, then interrupts back
;                                on (chipsharp_pdk14_interrupts_back), which returns from the function
;                                (pdk14_result_8.s)
;
; Called from the function, the copies find its return address at SP - 4 and SP - 3, so its first parameter's last byte
; at SP - 5.

    .module pdk14_arguments
    .globl _chipsharp_pdk14_arguments
    .globl _chipsharp_pdk14_argument_1
    .globl _chipsharp_pdk14_argument_2

    .area DATA
_chipsharp_pdk14_arguments::
    .ds 8

    .area CODE
_chipsharp_pdk14_argument_2::
    mov.io a, sp
    add a, #-9
    mov p, a
    idxm a, p
    mov _chipsharp_pdk14_arguments+7, a
    dec p
    idxm a, p
    mov _chipsharp_pdk14_arguments+6, a
    dec p
    idxm a, p
    mov _chipsharp_pdk14_arguments+5, a
    dec p
    idxm a, p
    mov _chipsharp_pdk14_arguments+4, a
_chipsharp_pdk14_argument_1::
    mov.io a, sp
    add a, #-5
    mov p, a
    idxm a, p
    mov _chipsharp_pdk14_arguments+3, a
    dec p
    idxm a, p
    mov _chipsharp_pdk14_arguments+2, a
    dec p
    idxm a, p
    mov _chipsharp_pdk14_arguments+1, a
    dec p
    idxm a, p
    mov _chipsharp_pdk14_arguments+0, a
    ret

