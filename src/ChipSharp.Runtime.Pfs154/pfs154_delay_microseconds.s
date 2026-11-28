; ChipSharp runtime for the Padauk PFS154: Timing.DelayMicroseconds, which watches T16's count of microseconds go by
; (pfs154_t16.s), as DelayMilliseconds does: at least the time given (0 or less: none), a count more, and at most a
; look at T16 more. Its parameter, where SDCC's callers put it, is what is left to wait, less what T16 counts between
; two looks.
;
;   void ChipSharp_Runtime_DelayMicroseconds(chipsharp_i32 microseconds)

    .module pfs154_delay_microseconds
    .globl _ChipSharp_Runtime_DelayMicroseconds
    .globl _ChipSharp_Runtime_DelayMicroseconds_PARM_1
    .globl _pfs154_t16

    .area DATA
_ChipSharp_Runtime_DelayMicroseconds_PARM_1::
    .ds 4
start:
    .ds 2
now:
    .ds 2

    .area CODE
_ChipSharp_Runtime_DelayMicroseconds::
    mov a, _ChipSharp_Runtime_DelayMicroseconds_PARM_1+3
    and a, #0x80
    ceqsn a, #0
    ret
    mov a, _ChipSharp_Runtime_DelayMicroseconds_PARM_1+0
    or a, _ChipSharp_Runtime_DelayMicroseconds_PARM_1+1
    or a, _ChipSharp_Runtime_DelayMicroseconds_PARM_1+2
    or a, _ChipSharp_Runtime_DelayMicroseconds_PARM_1+3
    cneqsn a, #0
    ret
    inc _ChipSharp_Runtime_DelayMicroseconds_PARM_1+0
    addc _ChipSharp_Runtime_DelayMicroseconds_PARM_1+1
    addc _ChipSharp_Runtime_DelayMicroseconds_PARM_1+2
    addc _ChipSharp_Runtime_DelayMicroseconds_PARM_1+3
    call _pfs154_t16
    mov start+0, a
    mov a, p
    mov start+1, a
    ; what T16 counted since the last look (16 bits), off what is left: done when that is 0 or less
00001$:
    call _pfs154_t16
    mov now+0, a
    mov a, p
    mov now+1, a
    mov a, now+0
    sub a, start+0
    mov p, a
    mov a, now+1
    subc a, start+1
    xch a, p
    sub _ChipSharp_Runtime_DelayMicroseconds_PARM_1+0, a
    mov a, p
    subc _ChipSharp_Runtime_DelayMicroseconds_PARM_1+1, a
    subc _ChipSharp_Runtime_DelayMicroseconds_PARM_1+2
    subc _ChipSharp_Runtime_DelayMicroseconds_PARM_1+3
    t0sn.io 0x00, #1
    ret
    mov a, _ChipSharp_Runtime_DelayMicroseconds_PARM_1+0
    or a, _ChipSharp_Runtime_DelayMicroseconds_PARM_1+1
    or a, _ChipSharp_Runtime_DelayMicroseconds_PARM_1+2
    or a, _ChipSharp_Runtime_DelayMicroseconds_PARM_1+3
    cneqsn a, #0
    ret
    mov a, now+0
    mov start+0, a
    mov a, now+1
    mov start+1, a
    goto 00001$
