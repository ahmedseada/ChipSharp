; ChipSharp runtime for the Padauk PFS154: Environment.TickCount64, the milliseconds since start-up: the clock's
; microseconds (pfs154_clock.s) divided by 1000, bit by bit (the PFS154 has no division): each round the top bit of
; what is left goes into the rest, chipsharp_pdk14_arguments' bytes 6 and 7, and a bit of the quotient into the bottom.
; 48 rounds, about 0.5 ms at 2 MHz, with interrupts off.
;
;   chipsharp_i64 ChipSharp_Runtime_ClockTickCount64(void) __reentrant

    .module pfs154_tick_count
    .globl _ChipSharp_Runtime_ClockTickCount64
    .globl _pfs154_clock_now
    .globl _chipsharp_pdk14_arguments
    .globl _chipsharp_pdk14_result_8

    .area CODE
_ChipSharp_Runtime_ClockTickCount64::
    disgint
    call _pfs154_clock_now
    mov a, #48
    mov p, a
00001$:
    sl _chipsharp_pdk14_arguments+0
    slc _chipsharp_pdk14_arguments+1
    slc _chipsharp_pdk14_arguments+2
    slc _chipsharp_pdk14_arguments+3
    slc _chipsharp_pdk14_arguments+4
    slc _chipsharp_pdk14_arguments+5
    slc _chipsharp_pdk14_arguments+6
    slc _chipsharp_pdk14_arguments+7
    ; the rest (below 2000) at least 1000 (0x03e8): its high byte above 3, or 3 and its low byte at least 0xe8
    mov a, _chipsharp_pdk14_arguments+7
    sub a, #3
    t0sn.io 0x00, #1
    goto 00003$
    ceqsn a, #0
    goto 00002$
    mov a, _chipsharp_pdk14_arguments+6
    sub a, #0xe8
    t0sn.io 0x00, #1
    goto 00003$
00002$:
    mov a, #0xe8
    sub _chipsharp_pdk14_arguments+6, a
    mov a, #0x03
    subc _chipsharp_pdk14_arguments+7, a
    mov a, _chipsharp_pdk14_arguments+0
    or a, #1
    mov _chipsharp_pdk14_arguments+0, a
00003$:
    dzsn p
    goto 00001$
    clear _chipsharp_pdk14_arguments+6
    clear _chipsharp_pdk14_arguments+7
    goto _chipsharp_pdk14_result_8
