; ChipSharp runtime for the Padauk PFS154: the clock, T16's count of microseconds (pfs154_t16.s) and the rounds it has
; gone, 65536 microseconds each, which its interrupt counts when bit 15 falls: 48 bits of microseconds, nearly nine
; years. The time of the timer event (pfs154_timer.s), Stopwatch.GetTimestamp and Stopwatch.Frequency; the rest of the
; clock is in files of their own, for the programs that use them (pfs154_tick_count.s, pfs154_elapsed.s,
; pfs154_date.s).
;
;   pfs154_clock_rounds                   T16's rounds since start-up, 32 bits (RAM: 4 bytes)
;   pfs154_clock_now                      the microseconds, into chipsharp_pdk14_arguments, 8 bytes; interrupts off. A
;                                         round T16 has gone but its interrupt has not counted yet shows as its request
;                                         bit (INTRQ bit 2): the count is then below 0x8000, and the round is counted
;   ChipSharp_Runtime_ClockTimestamp      Stopwatch.GetTimestamp: the microseconds since start-up
;   ChipSharp_Runtime_ClockFrequency      Stopwatch.Frequency: 1000000
;
; And its parts of the start-up (GSINIT: T16's interrupt on) and of the interrupt handler (INTSRC: a round counted),
; which the linker puts there when a program has this file.

    .module pfs154_clock
    .globl _pfs154_clock_rounds
    .globl _pfs154_clock_now
    .globl _ChipSharp_Runtime_ClockTimestamp
    .globl _ChipSharp_Runtime_ClockFrequency
    .globl _pfs154_t16
    .globl _chipsharp_pdk14_arguments
    .globl _chipsharp_pdk14_result_8


    .area DATA
_pfs154_clock_rounds::
    .ds 4

    .area GSINIT
    set1.io 0x04, #2

    .area INTSRC (CODE)
    t0sn.io 0x04, #2
    t1sn.io 0x05, #2
    goto 00001$
    set0.io 0x05, #2
    inc _pfs154_clock_rounds+0
    addc _pfs154_clock_rounds+1
    addc _pfs154_clock_rounds+2
    addc _pfs154_clock_rounds+3
00001$:

    .area CODE
_pfs154_clock_now::
    call _pfs154_t16
    mov _chipsharp_pdk14_arguments+0, a
    mov a, p
    mov _chipsharp_pdk14_arguments+1, a
    mov a, _pfs154_clock_rounds+0
    mov _chipsharp_pdk14_arguments+2, a
    mov a, _pfs154_clock_rounds+1
    mov _chipsharp_pdk14_arguments+3, a
    mov a, _pfs154_clock_rounds+2
    mov _chipsharp_pdk14_arguments+4, a
    mov a, _pfs154_clock_rounds+3
    mov _chipsharp_pdk14_arguments+5, a
    clear _chipsharp_pdk14_arguments+6
    clear _chipsharp_pdk14_arguments+7
    t1sn.io 0x05, #2
    ret
    mov a, _chipsharp_pdk14_arguments+1
    and a, #0x80
    ceqsn a, #0
    ret
    inc _chipsharp_pdk14_arguments+2
    addc _chipsharp_pdk14_arguments+3
    addc _chipsharp_pdk14_arguments+4
    addc _chipsharp_pdk14_arguments+5
    ret

_ChipSharp_Runtime_ClockTimestamp::
    disgint
    call _pfs154_clock_now
    goto _chipsharp_pdk14_result_8

_ChipSharp_Runtime_ClockFrequency::
    disgint
    mov a, #0x40
    mov _chipsharp_pdk14_arguments+0, a
    mov a, #0x42
    mov _chipsharp_pdk14_arguments+1, a
    mov a, #0x0f
    mov _chipsharp_pdk14_arguments+2, a
    clear _chipsharp_pdk14_arguments+3
    clear _chipsharp_pdk14_arguments+4
    clear _chipsharp_pdk14_arguments+5
    clear _chipsharp_pdk14_arguments+6
    clear _chipsharp_pdk14_arguments+7
    goto _chipsharp_pdk14_result_8
