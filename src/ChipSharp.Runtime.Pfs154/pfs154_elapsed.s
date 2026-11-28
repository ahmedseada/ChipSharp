; ChipSharp runtime for the Padauk PFS154: Stopwatch.GetElapsedTime's ticks from one timestamp to another, (end - start)
; * 10, in 64 bits; with interrupts off, as it uses chipsharp_pdk14_arguments. Its parameters are on the stack (the
; result's address at SP - 4, start at SP - 12 to SP - 5, end at SP - 20 to SP - 13): end - start in the arguments, d,
; kept in start's place, then (d * 4 + d) * 2 with shifts and adds: the PFS154 has no multiplication. Between the bytes
; of a sum, push af and pop af keep its carry while p moves on.
;
;   chipsharp_i64 ChipSharp_Runtime_ClockElapsedTicks(chipsharp_i64 start, chipsharp_i64 end) __reentrant

    .module pfs154_elapsed
    .globl _ChipSharp_Runtime_ClockElapsedTicks
    .globl _chipsharp_pdk14_arguments
    .globl _chipsharp_pdk14_result_8

    .area CODE
_ChipSharp_Runtime_ClockElapsedTicks::
    disgint
    ; the arguments: end
    mov.io a, sp
    add a, #-20
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
    ; minus start
    mov.io a, sp
    add a, #-12
    mov p, a
    idxm a, p
    sub _chipsharp_pdk14_arguments+0, a
    push af
    inc p
    pop af
    idxm a, p
    subc _chipsharp_pdk14_arguments+1, a
    push af
    inc p
    pop af
    idxm a, p
    subc _chipsharp_pdk14_arguments+2, a
    push af
    inc p
    pop af
    idxm a, p
    subc _chipsharp_pdk14_arguments+3, a
    push af
    inc p
    pop af
    idxm a, p
    subc _chipsharp_pdk14_arguments+4, a
    push af
    inc p
    pop af
    idxm a, p
    subc _chipsharp_pdk14_arguments+5, a
    push af
    inc p
    pop af
    idxm a, p
    subc _chipsharp_pdk14_arguments+6, a
    push af
    inc p
    pop af
    idxm a, p
    subc _chipsharp_pdk14_arguments+7, a
    ; d in start's place
    mov.io a, sp
    add a, #-12
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
    ; times 4
    sl _chipsharp_pdk14_arguments+0
    slc _chipsharp_pdk14_arguments+1
    slc _chipsharp_pdk14_arguments+2
    slc _chipsharp_pdk14_arguments+3
    slc _chipsharp_pdk14_arguments+4
    slc _chipsharp_pdk14_arguments+5
    slc _chipsharp_pdk14_arguments+6
    slc _chipsharp_pdk14_arguments+7
    sl _chipsharp_pdk14_arguments+0
    slc _chipsharp_pdk14_arguments+1
    slc _chipsharp_pdk14_arguments+2
    slc _chipsharp_pdk14_arguments+3
    slc _chipsharp_pdk14_arguments+4
    slc _chipsharp_pdk14_arguments+5
    slc _chipsharp_pdk14_arguments+6
    slc _chipsharp_pdk14_arguments+7
    ; plus d
    mov.io a, sp
    add a, #-12
    mov p, a
    idxm a, p
    add _chipsharp_pdk14_arguments+0, a
    push af
    inc p
    pop af
    idxm a, p
    addc _chipsharp_pdk14_arguments+1, a
    push af
    inc p
    pop af
    idxm a, p
    addc _chipsharp_pdk14_arguments+2, a
    push af
    inc p
    pop af
    idxm a, p
    addc _chipsharp_pdk14_arguments+3, a
    push af
    inc p
    pop af
    idxm a, p
    addc _chipsharp_pdk14_arguments+4, a
    push af
    inc p
    pop af
    idxm a, p
    addc _chipsharp_pdk14_arguments+5, a
    push af
    inc p
    pop af
    idxm a, p
    addc _chipsharp_pdk14_arguments+6, a
    push af
    inc p
    pop af
    idxm a, p
    addc _chipsharp_pdk14_arguments+7, a
    ; times 2
    sl _chipsharp_pdk14_arguments+0
    slc _chipsharp_pdk14_arguments+1
    slc _chipsharp_pdk14_arguments+2
    slc _chipsharp_pdk14_arguments+3
    slc _chipsharp_pdk14_arguments+4
    slc _chipsharp_pdk14_arguments+5
    slc _chipsharp_pdk14_arguments+6
    slc _chipsharp_pdk14_arguments+7
    goto _chipsharp_pdk14_result_8
