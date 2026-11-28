; ChipSharp runtime for the Padauk PFS154: the date of the clock (DateTime.UtcNow, DateTime.Now, Clock.Start), DateTime's
; ticks of 100 ns: those of the clock's start, pfs154_clock_base (1 January 2000 until Clock.Start gives another date),
; plus 10 a microsecond of the clock since (pfs154_clock.s), times 10 as (4 + 1) * 2 with shifts and adds through
; pfs154_clock_scratch (RAM: 16 bytes). A chip keeps no time zone: its local time is UTC. With interrupts off, so that
; a handler never sees half of one.
;
;   chipsharp_i64 ChipSharp_Runtime_ClockUtcTicks(void) __reentrant     DateTime.UtcNow's ticks
;   chipsharp_i64 ChipSharp_Runtime_ClockLocalTicks(void) __reentrant   DateTime.Now's: the same
;   void ChipSharp_Runtime_ClockStart(chipsharp_i64 ticks) __reentrant  Clock.Start: the clock counts from these ticks

    .module pfs154_date
    .globl _ChipSharp_Runtime_ClockUtcTicks
    .globl _ChipSharp_Runtime_ClockLocalTicks
    .globl _ChipSharp_Runtime_ClockStart
    .globl _pfs154_clock_now
    .globl _chipsharp_pdk14_arguments
    .globl _chipsharp_pdk14_argument_8
    .globl _chipsharp_pdk14_result_8
    .globl _chipsharp_pdk14_interrupts_back

    .area DATA
_pfs154_clock_base::
    .ds 8
_pfs154_clock_scratch::
    .ds 8

    ; DateTime's ticks of 1 January 2000, 00:00: 630822816000000000, 0x08c1220247e44000
    .area GSINIT
    mov a, #0x00
    mov _pfs154_clock_base+0, a
    mov a, #0x40
    mov _pfs154_clock_base+1, a
    mov a, #0xe4
    mov _pfs154_clock_base+2, a
    mov a, #0x47
    mov _pfs154_clock_base+3, a
    mov a, #0x02
    mov _pfs154_clock_base+4, a
    mov a, #0x22
    mov _pfs154_clock_base+5, a
    mov a, #0xc1
    mov _pfs154_clock_base+6, a
    mov a, #0x08
    mov _pfs154_clock_base+7, a

    .area CODE
; The clock's microseconds times 10 in the arguments: ticks since start-up.
ticks:
    call _pfs154_clock_now
    mov a, _chipsharp_pdk14_arguments+0
    mov _pfs154_clock_scratch+0, a
    mov a, _chipsharp_pdk14_arguments+1
    mov _pfs154_clock_scratch+1, a
    mov a, _chipsharp_pdk14_arguments+2
    mov _pfs154_clock_scratch+2, a
    mov a, _chipsharp_pdk14_arguments+3
    mov _pfs154_clock_scratch+3, a
    mov a, _chipsharp_pdk14_arguments+4
    mov _pfs154_clock_scratch+4, a
    mov a, _chipsharp_pdk14_arguments+5
    mov _pfs154_clock_scratch+5, a
    mov a, _chipsharp_pdk14_arguments+6
    mov _pfs154_clock_scratch+6, a
    mov a, _chipsharp_pdk14_arguments+7
    mov _pfs154_clock_scratch+7, a
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
    mov a, _pfs154_clock_scratch+0
    add _chipsharp_pdk14_arguments+0, a
    mov a, _pfs154_clock_scratch+1
    addc _chipsharp_pdk14_arguments+1, a
    mov a, _pfs154_clock_scratch+2
    addc _chipsharp_pdk14_arguments+2, a
    mov a, _pfs154_clock_scratch+3
    addc _chipsharp_pdk14_arguments+3, a
    mov a, _pfs154_clock_scratch+4
    addc _chipsharp_pdk14_arguments+4, a
    mov a, _pfs154_clock_scratch+5
    addc _chipsharp_pdk14_arguments+5, a
    mov a, _pfs154_clock_scratch+6
    addc _chipsharp_pdk14_arguments+6, a
    mov a, _pfs154_clock_scratch+7
    addc _chipsharp_pdk14_arguments+7, a
    sl _chipsharp_pdk14_arguments+0
    slc _chipsharp_pdk14_arguments+1
    slc _chipsharp_pdk14_arguments+2
    slc _chipsharp_pdk14_arguments+3
    slc _chipsharp_pdk14_arguments+4
    slc _chipsharp_pdk14_arguments+5
    slc _chipsharp_pdk14_arguments+6
    slc _chipsharp_pdk14_arguments+7
    ret

_ChipSharp_Runtime_ClockUtcTicks::
_ChipSharp_Runtime_ClockLocalTicks::
    disgint
    call ticks
    mov a, _pfs154_clock_base+0
    add _chipsharp_pdk14_arguments+0, a
    mov a, _pfs154_clock_base+1
    addc _chipsharp_pdk14_arguments+1, a
    mov a, _pfs154_clock_base+2
    addc _chipsharp_pdk14_arguments+2, a
    mov a, _pfs154_clock_base+3
    addc _chipsharp_pdk14_arguments+3, a
    mov a, _pfs154_clock_base+4
    addc _chipsharp_pdk14_arguments+4, a
    mov a, _pfs154_clock_base+5
    addc _chipsharp_pdk14_arguments+5, a
    mov a, _pfs154_clock_base+6
    addc _chipsharp_pdk14_arguments+6, a
    mov a, _pfs154_clock_base+7
    addc _chipsharp_pdk14_arguments+7, a
    goto _chipsharp_pdk14_result_8

_ChipSharp_Runtime_ClockStart::
    disgint
    call _chipsharp_pdk14_argument_8
    mov a, _chipsharp_pdk14_arguments+0
    mov _pfs154_clock_base+0, a
    mov a, _chipsharp_pdk14_arguments+1
    mov _pfs154_clock_base+1, a
    mov a, _chipsharp_pdk14_arguments+2
    mov _pfs154_clock_base+2, a
    mov a, _chipsharp_pdk14_arguments+3
    mov _pfs154_clock_base+3, a
    mov a, _chipsharp_pdk14_arguments+4
    mov _pfs154_clock_base+4, a
    mov a, _chipsharp_pdk14_arguments+5
    mov _pfs154_clock_base+5, a
    mov a, _chipsharp_pdk14_arguments+6
    mov _pfs154_clock_base+6, a
    mov a, _chipsharp_pdk14_arguments+7
    mov _pfs154_clock_base+7, a
    call ticks
    mov a, _chipsharp_pdk14_arguments+0
    sub _pfs154_clock_base+0, a
    mov a, _chipsharp_pdk14_arguments+1
    subc _pfs154_clock_base+1, a
    mov a, _chipsharp_pdk14_arguments+2
    subc _pfs154_clock_base+2, a
    mov a, _chipsharp_pdk14_arguments+3
    subc _pfs154_clock_base+3, a
    mov a, _chipsharp_pdk14_arguments+4
    subc _pfs154_clock_base+4, a
    mov a, _chipsharp_pdk14_arguments+5
    subc _pfs154_clock_base+5, a
    mov a, _chipsharp_pdk14_arguments+6
    subc _pfs154_clock_base+6, a
    mov a, _chipsharp_pdk14_arguments+7
    subc _pfs154_clock_base+7, a
    goto _chipsharp_pdk14_interrupts_back
