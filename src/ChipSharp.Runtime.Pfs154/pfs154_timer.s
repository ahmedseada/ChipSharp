; ChipSharp runtime for the Padauk PFS154: the timer event (Timing.Every..., After..., Stop) of the program's
; [Interrupt(Event.Timer)] method, its entry chipsharp_isr_irq6 (the target's "interrupts.timer.irq": TM2's request bit).
;
; Its times are the clock's microseconds (pfs154_clock.s), compared by the sign of their difference, so the longest is
; 2147483 ms, about 35 minutes, as on the other chips, and Every keeps time with the clock: each time is the one before
; and the period, whenever the handler ran. TM2 only wakes the interrupt handler up then: it counts the IHRC (16 MHz)
; divided by 2^k, from 16 (k 4: one count a microsecond) to 2048 (k 11: 128 us), at most 256 counts, the fewest that
; reach the time (32.768 ms at most), rounded down; there the handler sees whether the time has come and, if not, sets
; TM2 for what is left, to the microsecond. So the method runs a few tens of microseconds after its time at 2 MHz, what
; the handler takes; a time missed while a handler ran too long comes at once, so Every runs the method as many times as
; the time says.
;
;   ChipSharp_Runtime_TimerEveryMilliseconds, ...Microseconds   Timing.Every...: every so long, the first that long
;                                     from now; 0 or less stops the timer
;   ChipSharp_Runtime_TimerAfterMilliseconds, ...Microseconds   Timing.After...: once (0 or less: at once)
;   ChipSharp_Runtime_TimerStop       Timing.Stop
;   pfs154_timer_interrupt            from the interrupt handler (INTSRC), when TM2 asks and the timer runs: when the
;                                     time has come, the next one (Every) or the timer stopped (After), then the
;                                     method; then TM2 set for the next time, if the timer still runs (the method
;                                     may have stopped it, or started it again)
;
; The timer runs while INTEN's TM2 bit is on; TM2 is the timer event's then, in its period mode, so PWM on TM2's pins
; (pfs154_pwm.c) stops the program meanwhile, and the timer event stops it (the failure stop) while TM2 is a PWM
; output's. Assembly, as SDCC's C keeps every value it works out in RAM of its own: the functions' parameter is on the
; stack (pdk14_runtime.h); they copy it to chipsharp_pdk14_arguments with interrupts off and work in its 8 bytes, where
; the clock's time goes too (pfs154_clock_now). RAM of their own: the next time and the period, 8 bytes.

    .module pfs154_timer
    .globl _ChipSharp_Runtime_TimerEveryMilliseconds
    .globl _ChipSharp_Runtime_TimerEveryMicroseconds
    .globl _ChipSharp_Runtime_TimerAfterMilliseconds
    .globl _ChipSharp_Runtime_TimerAfterMicroseconds
    .globl _ChipSharp_Runtime_TimerStop
    .globl _pfs154_timer_interrupt
    .globl _chipsharp_isr_irq6
    .globl _pfs154_clock_now
    .globl _chipsharp_pdk14_arguments
    .globl _chipsharp_pdk14_argument_1
    .globl _chipsharp_pdk14_interrupts_back
    .globl _chipsharp_halt_failure

    .area DATA
_pfs154_timer_next::
    .ds 4
_pfs154_timer_period::
    .ds 4

    .area INTSRC (CODE)
    t0sn.io 0x04, #6
    t1sn.io 0x05, #6
    goto 00001$
    call _pfs154_timer_interrupt
00001$:

    .area CODE
_ChipSharp_Runtime_TimerEveryMilliseconds::
    disgint
    call _chipsharp_pdk14_argument_1
    call milliseconds
    goto every
_ChipSharp_Runtime_TimerEveryMicroseconds::
    disgint
    call _chipsharp_pdk14_argument_1
    call positive
; the arguments' 4 bytes: the period; 0 stops the timer
every:
    mov a, _chipsharp_pdk14_arguments+0
    or a, _chipsharp_pdk14_arguments+1
    or a, _chipsharp_pdk14_arguments+2
    or a, _chipsharp_pdk14_arguments+3
    cneqsn a, #0
    goto stop_back
    mov a, _chipsharp_pdk14_arguments+0
    mov _pfs154_timer_period+0, a
    mov a, _chipsharp_pdk14_arguments+1
    mov _pfs154_timer_period+1, a
    mov a, _chipsharp_pdk14_arguments+2
    mov _pfs154_timer_period+2, a
    mov a, _chipsharp_pdk14_arguments+3
    mov _pfs154_timer_period+3, a
    goto start
_ChipSharp_Runtime_TimerAfterMilliseconds::
    disgint
    call _chipsharp_pdk14_argument_1
    call milliseconds
    goto after
_ChipSharp_Runtime_TimerAfterMicroseconds::
    disgint
    call _chipsharp_pdk14_argument_1
    call positive
after:
    clear _pfs154_timer_period+0
    clear _pfs154_timer_period+1
    clear _pfs154_timer_period+2
    clear _pfs154_timer_period+3
; the arguments' 4 bytes: the microseconds until the first time
start:
    ; System.InvalidOperationException: The timer event (Timing.Every..., After...) runs on TM2, which a PWM output on
    ; PA3, PB2 or PB4 uses: give the PWM output a pin of TM3 (PB5, PB6, PB7), or stop it first (Pwm.Stop).
    mov.io a, 0x1c
    and a, #0x02
    ceqsn a, #0
    goto _chipsharp_halt_failure
    mov a, _chipsharp_pdk14_arguments+0
    mov _pfs154_timer_next+0, a
    mov a, _chipsharp_pdk14_arguments+1
    mov _pfs154_timer_next+1, a
    mov a, _chipsharp_pdk14_arguments+2
    mov _pfs154_timer_next+2, a
    mov a, _chipsharp_pdk14_arguments+3
    mov _pfs154_timer_next+3, a
    call _pfs154_clock_now
    mov a, _chipsharp_pdk14_arguments+0
    add _pfs154_timer_next+0, a
    mov a, _chipsharp_pdk14_arguments+1
    addc _pfs154_timer_next+1, a
    mov a, _chipsharp_pdk14_arguments+2
    addc _pfs154_timer_next+2, a
    mov a, _chipsharp_pdk14_arguments+3
    addc _pfs154_timer_next+3, a
    call arm
    set1.io 0x04, #6
    goto _chipsharp_pdk14_interrupts_back

_ChipSharp_Runtime_TimerStop::
    disgint
stop_back:
    call stop
    goto _chipsharp_pdk14_interrupts_back

; The timer stopped: its interrupt off, TM2 stopped, its request forgotten.
stop:
    set0.io 0x04, #6
    mov a, #0
    mov.io 0x1c, a
    set0.io 0x05, #6
    ret

; The arguments' 4 bytes, a C# int, 0 when below 0.
positive:
    mov a, _chipsharp_pdk14_arguments+3
    and a, #0x80
    ceqsn a, #0
    goto 00010$
    ret
00010$:
    clear _chipsharp_pdk14_arguments+0
    clear _chipsharp_pdk14_arguments+1
    clear _chipsharp_pdk14_arguments+2
    clear _chipsharp_pdk14_arguments+3
    ret

; The arguments' 4 bytes, a C# int of milliseconds (0 when below 0, 2147483 above it), as microseconds: x * 1000 =
; x * 1024 - x * 8 - x * 16, with shifts (the PFS154 has no multiplication), x * 8 and x * 16 in bytes 4-7.
milliseconds:
    call positive
    mov a, _chipsharp_pdk14_arguments+3
    ceqsn a, #0
    goto 00011$
    mov a, #0x9b
    sub a, _chipsharp_pdk14_arguments+0
    mov a, #0xc4
    subc a, _chipsharp_pdk14_arguments+1
    mov a, #0x20
    subc a, _chipsharp_pdk14_arguments+2
    t1sn.io 0x00, #1
    goto 00012$
00011$:
    mov a, #0x9b
    mov _chipsharp_pdk14_arguments+0, a
    mov a, #0xc4
    mov _chipsharp_pdk14_arguments+1, a
    mov a, #0x20
    mov _chipsharp_pdk14_arguments+2, a
    clear _chipsharp_pdk14_arguments+3
00012$:
    ; bytes 4-7: x * 8
    mov a, _chipsharp_pdk14_arguments+0
    mov _chipsharp_pdk14_arguments+4, a
    mov a, _chipsharp_pdk14_arguments+1
    mov _chipsharp_pdk14_arguments+5, a
    mov a, _chipsharp_pdk14_arguments+2
    mov _chipsharp_pdk14_arguments+6, a
    mov a, _chipsharp_pdk14_arguments+3
    mov _chipsharp_pdk14_arguments+7, a
    mov a, #3
    mov p, a
00013$:
    sl _chipsharp_pdk14_arguments+4
    slc _chipsharp_pdk14_arguments+5
    slc _chipsharp_pdk14_arguments+6
    slc _chipsharp_pdk14_arguments+7
    dzsn p
    goto 00013$
    ; bytes 0-3: x * 1024, a byte up and two bits
    mov a, _chipsharp_pdk14_arguments+2
    mov _chipsharp_pdk14_arguments+3, a
    mov a, _chipsharp_pdk14_arguments+1
    mov _chipsharp_pdk14_arguments+2, a
    mov a, _chipsharp_pdk14_arguments+0
    mov _chipsharp_pdk14_arguments+1, a
    clear _chipsharp_pdk14_arguments+0
    sl _chipsharp_pdk14_arguments+0
    slc _chipsharp_pdk14_arguments+1
    slc _chipsharp_pdk14_arguments+2
    slc _chipsharp_pdk14_arguments+3
    sl _chipsharp_pdk14_arguments+0
    slc _chipsharp_pdk14_arguments+1
    slc _chipsharp_pdk14_arguments+2
    slc _chipsharp_pdk14_arguments+3
    ; minus x * 8, then minus x * 16
    mov a, #2
    mov p, a
00014$:
    mov a, _chipsharp_pdk14_arguments+4
    sub _chipsharp_pdk14_arguments+0, a
    mov a, _chipsharp_pdk14_arguments+5
    subc _chipsharp_pdk14_arguments+1, a
    mov a, _chipsharp_pdk14_arguments+6
    subc _chipsharp_pdk14_arguments+2, a
    mov a, _chipsharp_pdk14_arguments+7
    subc _chipsharp_pdk14_arguments+3, a
    sl _chipsharp_pdk14_arguments+4
    slc _chipsharp_pdk14_arguments+5
    slc _chipsharp_pdk14_arguments+6
    slc _chipsharp_pdk14_arguments+7
    dzsn p
    goto 00014$
    ret

; TM2 set to ask when the time has come, or before: left = next - now; at once (one count of 1 us) when it is 0 or
; less, else the fewest counts of 2^k / 16 us that reach it, at most 256, rounded down (k from 4 to 11 in the
; arguments' byte 4, the counts in bytes 0 and 1, the bound, one less, in byte 0); TM2S the prescaler and scaler of 2^k.
arm:
    call _pfs154_clock_now
    mov a, _pfs154_timer_next+0
    sub a, _chipsharp_pdk14_arguments+0
    mov _chipsharp_pdk14_arguments+0, a
    mov a, _pfs154_timer_next+1
    subc a, _chipsharp_pdk14_arguments+1
    mov _chipsharp_pdk14_arguments+1, a
    mov a, _pfs154_timer_next+2
    subc a, _chipsharp_pdk14_arguments+2
    mov _chipsharp_pdk14_arguments+2, a
    mov a, _pfs154_timer_next+3
    subc a, _chipsharp_pdk14_arguments+3
    mov _chipsharp_pdk14_arguments+3, a
    mov a, #4
    mov _chipsharp_pdk14_arguments+4, a
    ; 0 or less: at once
    mov a, _chipsharp_pdk14_arguments+3
    and a, #0x80
    ceqsn a, #0
    goto 00020$
    mov a, _chipsharp_pdk14_arguments+0
    or a, _chipsharp_pdk14_arguments+1
    or a, _chipsharp_pdk14_arguments+2
    or a, _chipsharp_pdk14_arguments+3
    cneqsn a, #0
    goto 00020$
    ; above 32768 (0x8000): the most, 256 counts of 128 us
    mov a, _chipsharp_pdk14_arguments+3
    or a, _chipsharp_pdk14_arguments+2
    ceqsn a, #0
    goto 00021$
    mov a, #0x00
    sub a, _chipsharp_pdk14_arguments+0
    mov a, #0x80
    subc a, _chipsharp_pdk14_arguments+1
    t0sn.io 0x00, #1
    goto 00021$
    ; 1 to 32768: halved until 256 or fewer, k one more each time
00022$:
    mov a, _chipsharp_pdk14_arguments+1
    ceqsn a, #0
    goto 00023$
    goto 00024$
00023$:
    ceqsn a, #1
    goto 00025$
    mov a, _chipsharp_pdk14_arguments+0
    ceqsn a, #0
    goto 00025$
    goto 00024$
00025$:
    sr _chipsharp_pdk14_arguments+1
    src _chipsharp_pdk14_arguments+0
    inc _chipsharp_pdk14_arguments+4
    goto 00022$
00024$:
    dec _chipsharp_pdk14_arguments+0
    goto 00026$
00020$:
    clear _chipsharp_pdk14_arguments+0
    goto 00026$
00021$:
    mov a, #0xff
    mov _chipsharp_pdk14_arguments+0, a
    mov a, #11
    mov _chipsharp_pdk14_arguments+4, a
00026$:
    ; TM2C (0x1c) stopped, TM2CT (0x1d) 0, TM2S (0x17), TM2B (0x09), then TM2C: the IHRC, period mode, no output
    mov a, #0
    mov.io 0x1c, a
    mov.io 0x1d, a
    mov a, _chipsharp_pdk14_arguments+4
    add a, #-3
    call 00030$
    mov.io 0x17, a
    mov a, _chipsharp_pdk14_arguments+0
    mov.io 0x09, a
    mov a, #0x20
    mov.io 0x1c, a
    set0.io 0x05, #6
    ret
; TM2S for 2^k (A = k - 3): the prescaler 16 or 64, the scaler 1 to 32
00030$:
    pcadd a
    ret #0x40
    ret #0x41
    ret #0x60
    ret #0x61
    ret #0x63
    ret #0x67
    ret #0x6f
    ret #0x7f

_pfs154_timer_interrupt::
    set0.io 0x05, #6
    call _pfs154_clock_now
    ; now - next below 0: not yet
    mov a, _chipsharp_pdk14_arguments+0
    sub a, _pfs154_timer_next+0
    mov a, _chipsharp_pdk14_arguments+1
    subc a, _pfs154_timer_next+1
    mov a, _chipsharp_pdk14_arguments+2
    subc a, _pfs154_timer_next+2
    mov a, _chipsharp_pdk14_arguments+3
    subc a, _pfs154_timer_next+3
    and a, #0x80
    ceqsn a, #0
    goto 00041$
    ; Every: the next time; After: the timer stops
    mov a, _pfs154_timer_period+0
    or a, _pfs154_timer_period+1
    or a, _pfs154_timer_period+2
    or a, _pfs154_timer_period+3
    cneqsn a, #0
    goto 00040$
    mov a, _pfs154_timer_period+0
    add _pfs154_timer_next+0, a
    mov a, _pfs154_timer_period+1
    addc _pfs154_timer_next+1, a
    mov a, _pfs154_timer_period+2
    addc _pfs154_timer_next+2, a
    mov a, _pfs154_timer_period+3
    addc _pfs154_timer_next+3, a
    goto 00042$
00040$:
    call stop
00042$:
    call _chipsharp_isr_irq6
00041$:
    t0sn.io 0x04, #6
    goto arm
    ret
