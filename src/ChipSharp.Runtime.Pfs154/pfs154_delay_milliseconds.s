; ChipSharp runtime for the Padauk PFS154: Timing.DelayMilliseconds, which watches T16's count of microseconds go by
; (pfs154_t16.s), so that a handler that runs meanwhile does not make it longer than asked, and any system clock times
; it the same. It waits at least the time given (0 or less: none), a count of T16 past each 1000 since the last (T16
; may have counted part of a microsecond when it starts), and at most a look at T16 more, a few microseconds. It never
; runs in a handler (it waits: chipsharp check refuses it), so its parameter is where SDCC's callers put it, at a fixed
; address, and it counts it down. A file of its own, as is DelayMicroseconds: a program has the one it uses.
;
;   void ChipSharp_Runtime_DelayMilliseconds(chipsharp_i32 milliseconds)

    .module pfs154_delay_milliseconds
    .globl _ChipSharp_Runtime_DelayMilliseconds
    .globl _ChipSharp_Runtime_DelayMilliseconds_PARM_1
    .globl _pfs154_t16

    .area DATA
_ChipSharp_Runtime_DelayMilliseconds_PARM_1::
    .ds 4
start:
    .ds 2

    .area CODE
_ChipSharp_Runtime_DelayMilliseconds::
    mov a, _ChipSharp_Runtime_DelayMilliseconds_PARM_1+3
    and a, #0x80
    ceqsn a, #0
    ret
    mov a, _ChipSharp_Runtime_DelayMilliseconds_PARM_1+0
    or a, _ChipSharp_Runtime_DelayMilliseconds_PARM_1+1
    or a, _ChipSharp_Runtime_DelayMilliseconds_PARM_1+2
    or a, _ChipSharp_Runtime_DelayMilliseconds_PARM_1+3
    cneqsn a, #0
    ret
    call _pfs154_t16
    mov start+0, a
    mov a, p
    mov start+1, a
    ; until T16 - start is above 1000 (0x03e8): its high byte above 3, or 3 and its low byte above 0xe8
00001$:
    call _pfs154_t16
    sub a, start+0
    xch a, p
    subc a, start+1
    sub a, #3
    t0sn.io 0x00, #1
    goto 00001$
    ceqsn a, #0
    goto 00002$
    mov a, #0xe8
    sub a, p
    t1sn.io 0x00, #1
    goto 00001$
00002$:
    mov a, #0xe8
    add start+0, a
    mov a, #0x03
    addc start+1, a
    dec _ChipSharp_Runtime_DelayMilliseconds_PARM_1+0
    subc _ChipSharp_Runtime_DelayMilliseconds_PARM_1+1
    subc _ChipSharp_Runtime_DelayMilliseconds_PARM_1+2
    subc _ChipSharp_Runtime_DelayMilliseconds_PARM_1+3
    mov a, _ChipSharp_Runtime_DelayMilliseconds_PARM_1+0
    or a, _ChipSharp_Runtime_DelayMilliseconds_PARM_1+1
    or a, _ChipSharp_Runtime_DelayMilliseconds_PARM_1+2
    or a, _ChipSharp_Runtime_DelayMilliseconds_PARM_1+3
    ceqsn a, #0
    goto 00001$
    ret
