; ChipSharp runtime for the Padauk PFS154: PWM outputs (ChipSharp.Hardware.Pwm) on the outputs of its 8-bit timers,
; each of which drives one pin at a time (TM2C, TM3C: OUTPUT):
;
;   TM2   PB2 (pin 10), PA3 (3), PB4 (12)    while the timer event is off: it runs on TM2 (pfs154_timer.s)
;   TM3   PB5 (pin 13), PB6 (14), PB7 (15)
;
; so two outputs at once ("pwm": 2), each at its own frequency. A timer in PWM mode counts from 0 to 255 (or to 63:
; 6-bit PWM, TMxS bit 7) and round again, its pin high while the count is below the bound (TMxB). It counts the IHRC
; (16 MHz) or the system clock (the IHRC / 2^PFS154_SYSTEM_CLOCK_SHIFT, pfs154_clock.inc: 2 MHz for pfs154-bare)
; divided by a prescaler (1, 4, 16, 64) and a scaler S (1-32): a period is S * 2^n ticks of the IHRC. Pwm.Output takes
; the one nearest the frequency asked for, of 256 counts where two are as near (finer pulses): e.g. 1008 Hz for 1000
; (6-bit, the system clock / 31). From about 4 Hz to 250 kHz.
;
;   ChipSharp_Runtime_PwmOutput   Pwm.Output: the pin's timer at the frequency, the pin an output, low (bound 0)
;   ChipSharp_Runtime_PwmPulse    Pwm.PulseMicroseconds: the nearest number of counts (the whole period at most: 255 of
;                                 256, or 64 of 64), written once the count is where the pin's level is the same with
;                                 the old bound and the new (at most a period's wait, with interrupts off), so that no
;                                 period is cut short or gets a second pulse
;   ChipSharp_Runtime_PwmStop     Pwm.Stop: the timer stopped, the pin an input, as after start-up
;
; A pin that is not a PWM output, a timer that drives another pin (or is the timer event's), a frequency the chip
; cannot make, a pulse below 0 and a pin used before Pwm.Output stop the program (the failure stop, .NET's message in a
; comment). Pwm.Duty takes a double, which SDCC has not: chipsharp check says so.
;
; Assembly, as SDCC's C keeps every value it works out in RAM of its own. The functions' parameters are on the stack
; (pdk14_runtime.h); they copy them with interrupts off to chipsharp_pdk14_arguments, where the work is done. RAM of
; their own: what each timer makes (its scaler, and the shift of its counts, 4 bytes), the pin's (2 bytes), and the
; search's (4 bytes).
;
; Pwm.Output's search: Q = 250000 / frequency, the period in 64ths of the IHRC's ticks (bit by bit: the PFS154 has no
; division), then each of 16 settings i: bit 3 6-bit PWM, bit 2 the system clock, bits 0-1 the prescaler's power of 4;
; its counts' shift s (in Q's units, from the table below) and S = Q / 2^s rounded (1 to 32); the one whose S * 2^s is
; nearest Q, the first of two as near.

    .module pfs154_pwm
    .globl _ChipSharp_Runtime_PwmOutput
    .globl _ChipSharp_Runtime_PwmPulse
    .globl _ChipSharp_Runtime_PwmStop
    .globl _chipsharp_pdk14_arguments
    .globl _chipsharp_pdk14_argument_1
    .globl _chipsharp_pdk14_argument_2
    .globl _chipsharp_pdk14_interrupts_back
    .globl _chipsharp_halt_failure

    .include "pfs154_clock.inc"

    .area DATA
scaler:
    .ds 2
shift:
    .ds 2
code:
    .ds 1
mask:
    .ds 1
best:
    .ds 3
s:
    .ds 1

    .area CODE
_ChipSharp_Runtime_PwmOutput::
    disgint
    call _chipsharp_pdk14_argument_2
    call pin
    call output
    goto _chipsharp_pdk14_interrupts_back
_ChipSharp_Runtime_PwmPulse::
    disgint
    call _chipsharp_pdk14_argument_2
    call pin
    call pulse
    goto _chipsharp_pdk14_interrupts_back
_ChipSharp_Runtime_PwmStop::
    disgint
    call _chipsharp_pdk14_argument_1
    call pin
    call stop
    goto _chipsharp_pdk14_interrupts_back

; The pin in the arguments' bytes 0-3: code (bits 0-1 its output, 1-3; bit 2 TM3, else TM2; bit 3 port B, else A) and
; mask (its bit in the port). System.ArgumentException: Pin {0} is not a PWM pin: on the PFS154 they are PA3, PB2 and
; PB4 (TM2) and PB5, PB6 and PB7 (TM3).
pin:
    mov a, _chipsharp_pdk14_arguments+1
    or a, _chipsharp_pdk14_arguments+2
    or a, _chipsharp_pdk14_arguments+3
    ceqsn a, #0
    goto _chipsharp_halt_failure
    mov a, _chipsharp_pdk14_arguments+0
    sub a, #16
    t1sn.io 0x00, #1
    goto _chipsharp_halt_failure
    mov a, _chipsharp_pdk14_arguments+0
    add a, #1
    call 00002$
    cneqsn a, #0
    goto _chipsharp_halt_failure
    mov code, a
    mov a, _chipsharp_pdk14_arguments+0
    and a, #7
    add a, #1
    mov p, a
    mov a, #1
    goto 00001$
00003$:
    sl a
00001$:
    dzsn p
    goto 00003$
    mov mask, a
    ret
00002$:
    pcadd a
    ret #0x00
    ret #0x00
    ret #0x00
    ret #0x02
    ret #0x00
    ret #0x00
    ret #0x00
    ret #0x00
    ret #0x00
    ret #0x00
    ret #0x09
    ret #0x00
    ret #0x0b
    ret #0x0d
    ret #0x0e
    ret #0x0f

; A: the pin's timer's control register (TM2C 0x1c, TM3C 0x32).
control:
    mov a, code
    and a, #0x04
    ceqsn a, #0
    goto 00010$
    mov.io a, 0x1c
    ret
00010$:
    mov.io a, 0x32
    ret

; A: 1 when the pin's timer is a PWM output on the pin (PWM mode, the pin's output), else 0.
running:
    call control
    and a, #0x0e
    mov p, a
    mov a, code
    and a, #0x03
    sl a
    sl a
    or a, #0x02
    ceqsn a, p
    ret #0
    ret #1

; The pin an output, low (out), or an input (direction), its latch low.
out:
    mov a, mask
    mov p, a
    not p
    mov a, code
    and a, #0x08
    ceqsn a, #0
    goto 00020$
    mov.io a, 0x10
    and a, p
    mov.io 0x10, a
    not p
    mov.io a, 0x11
    or a, p
    mov.io 0x11, a
    ret
00020$:
    mov.io a, 0x14
    and a, p
    mov.io 0x14, a
    not p
    mov.io a, 0x15
    or a, p
    mov.io 0x15, a
    ret
direction:
    mov a, mask
    mov p, a
    not p
    mov a, code
    and a, #0x08
    ceqsn a, #0
    goto 00021$
    mov.io a, 0x10
    and a, p
    mov.io 0x10, a
    mov.io a, 0x11
    and a, p
    mov.io 0x11, a
    ret
00021$:
    mov.io a, 0x14
    and a, p
    mov.io 0x14, a
    mov.io a, 0x15
    and a, p
    mov.io 0x15, a
    ret

stop:
    call running
    cneqsn a, #0
    goto direction
    mov a, code
    and a, #0x04
    ceqsn a, #0
    goto 00022$
    mov a, #0
    mov.io 0x1c, a
    goto direction
00022$:
    mov a, #0
    mov.io 0x32, a
    goto direction

output:
    ; System.InvalidOperationException: Pin {0} is an output of TM2, which the timer event (Timing.Every..., After...)
    ; runs on: give the PWM output a pin of TM3 (PB5, PB6, PB7), or stop the timer first (Timing.Stop).
    mov a, code
    and a, #0x04
    ceqsn a, #0
    goto 00030$
    mov.io a, 0x1c
    and a, #0xf0
    cneqsn a, #0
    goto 00030$
    mov.io a, 0x1c
    and a, #0x02
    cneqsn a, #0
    goto _chipsharp_halt_failure
00030$:
    ; System.InvalidOperationException: Pin {0} shares TM{1} with another PWM output, which it drives: a timer drives
    ; one pin. Stop that one first (Pwm.Stop), or use a pin of the other timer.
    call control
    and a, #0x02
    cneqsn a, #0
    goto 00031$
    call running
    cneqsn a, #0
    goto _chipsharp_halt_failure
00031$:
    ; System.ArgumentOutOfRangeException: The PFS154 cannot make a PWM frequency of {0} Hz: from 4 Hz to 250000 Hz.
    ; (the frequency 1 to 250000, in bytes 4-6, and 7 zero)
    mov a, _chipsharp_pdk14_arguments+7
    ceqsn a, #0
    goto _chipsharp_halt_failure
    mov a, _chipsharp_pdk14_arguments+4
    or a, _chipsharp_pdk14_arguments+5
    or a, _chipsharp_pdk14_arguments+6
    cneqsn a, #0
    goto _chipsharp_halt_failure
    ; Q = 250000 (0x03d090) / frequency, 24 bits in bytes 0-2, the rest in best, the round in byte 3
    mov a, #0x90
    mov _chipsharp_pdk14_arguments+0, a
    mov a, #0xd0
    mov _chipsharp_pdk14_arguments+1, a
    mov a, #0x03
    mov _chipsharp_pdk14_arguments+2, a
    clear best+0
    clear best+1
    clear best+2
    mov a, #24
    mov _chipsharp_pdk14_arguments+3, a
00032$:
    sl _chipsharp_pdk14_arguments+0
    slc _chipsharp_pdk14_arguments+1
    slc _chipsharp_pdk14_arguments+2
    slc best+0
    slc best+1
    slc best+2
    mov a, best+0
    sub a, _chipsharp_pdk14_arguments+4
    mov a, best+1
    subc a, _chipsharp_pdk14_arguments+5
    mov a, best+2
    subc a, _chipsharp_pdk14_arguments+6
    t0sn.io 0x00, #1
    goto 00033$
    mov a, _chipsharp_pdk14_arguments+4
    sub best+0, a
    mov a, _chipsharp_pdk14_arguments+5
    subc best+1, a
    mov a, _chipsharp_pdk14_arguments+6
    subc best+2, a
    mov a, _chipsharp_pdk14_arguments+0
    or a, #1
    mov _chipsharp_pdk14_arguments+0, a
00033$:
    dzsn _chipsharp_pdk14_arguments+3
    goto 00032$
    ; Q 1 to 65535 (the frequency 4 to 250000), into bytes 4-5
    mov a, _chipsharp_pdk14_arguments+2
    ceqsn a, #0
    goto _chipsharp_halt_failure
    mov a, _chipsharp_pdk14_arguments+0
    or a, _chipsharp_pdk14_arguments+1
    cneqsn a, #0
    goto _chipsharp_halt_failure
    mov a, _chipsharp_pdk14_arguments+0
    mov _chipsharp_pdk14_arguments+4, a
    mov a, _chipsharp_pdk14_arguments+1
    mov _chipsharp_pdk14_arguments+5, a
    ; the search: i in byte 3, the best's error in best (24 bits), its i in byte 6, its S in byte 7
    mov a, #0xff
    mov best+0, a
    mov best+1, a
    mov best+2, a
    clear _chipsharp_pdk14_arguments+3
00034$:
    ; s for i, then S = Q / 2^s rounded (the last bit shifted out added), in bytes 0-1
    mov a, _chipsharp_pdk14_arguments+3
    add a, #1
    call 00050$
    mov s, a
    mov a, _chipsharp_pdk14_arguments+4
    mov _chipsharp_pdk14_arguments+0, a
    mov a, _chipsharp_pdk14_arguments+5
    mov _chipsharp_pdk14_arguments+1, a
    clear _chipsharp_pdk14_arguments+2
    mov a, s
    add a, #1
    mov p, a
    goto 00036$
00035$:
    sr _chipsharp_pdk14_arguments+1
    src _chipsharp_pdk14_arguments+0
    mov a, #0
    addc a
    mov _chipsharp_pdk14_arguments+2, a
00036$:
    dzsn p
    goto 00035$
    mov a, _chipsharp_pdk14_arguments+2
    add _chipsharp_pdk14_arguments+0, a
    addc _chipsharp_pdk14_arguments+1
    ; S from 1 to 32 (byte 0; byte 2 the product's third)
    mov a, _chipsharp_pdk14_arguments+1
    ceqsn a, #0
    goto 00037$
    mov a, _chipsharp_pdk14_arguments+0
    cneqsn a, #0
    goto 00038$
    sub a, #33
    t1sn.io 0x00, #1
    goto 00037$
    goto 00039$
00037$:
    mov a, #32
    mov _chipsharp_pdk14_arguments+0, a
    goto 00039$
00038$:
    mov a, #1
    mov _chipsharp_pdk14_arguments+0, a
00039$:
    ; S * 2^s, 24 bits in bytes 0-2; S kept in s (no longer the shift), into byte 7 when it is the best
    clear _chipsharp_pdk14_arguments+1
    clear _chipsharp_pdk14_arguments+2
    mov a, s
    add a, #1
    mov p, a
    mov a, _chipsharp_pdk14_arguments+0
    mov s, a
    goto 00041$
00040$:
    sl _chipsharp_pdk14_arguments+0
    slc _chipsharp_pdk14_arguments+1
    slc _chipsharp_pdk14_arguments+2
00041$:
    dzsn p
    goto 00040$
    ; the error: S * 2^s - Q, negated when below 0
    mov a, _chipsharp_pdk14_arguments+4
    sub _chipsharp_pdk14_arguments+0, a
    mov a, _chipsharp_pdk14_arguments+5
    subc _chipsharp_pdk14_arguments+1, a
    subc _chipsharp_pdk14_arguments+2
    t0sn.io 0x00, #1
    goto 00042$
    goto 00043$
00042$:
    not _chipsharp_pdk14_arguments+0
    not _chipsharp_pdk14_arguments+1
    not _chipsharp_pdk14_arguments+2
    inc _chipsharp_pdk14_arguments+0
    addc _chipsharp_pdk14_arguments+1
    addc _chipsharp_pdk14_arguments+2
00043$:
    ; below the best's: the best now
    mov a, _chipsharp_pdk14_arguments+0
    sub a, best+0
    mov a, _chipsharp_pdk14_arguments+1
    subc a, best+1
    mov a, _chipsharp_pdk14_arguments+2
    subc a, best+2
    t1sn.io 0x00, #1
    goto 00044$
    mov a, s
    mov _chipsharp_pdk14_arguments+7, a
    mov a, _chipsharp_pdk14_arguments+3
    mov _chipsharp_pdk14_arguments+6, a
    mov a, _chipsharp_pdk14_arguments+0
    mov best+0, a
    mov a, _chipsharp_pdk14_arguments+1
    mov best+1, a
    mov a, _chipsharp_pdk14_arguments+2
    mov best+2, a
00044$:
    inc _chipsharp_pdk14_arguments+3
    mov a, _chipsharp_pdk14_arguments+3
    ceqsn a, #16
    goto 00034$
    ; the best i: TMxS = 6-bit (i bit 3) << 7 | prescaler (bits 0-1) << 5 | S - 1, in byte 0; TMxC = the IHRC (0x20)
    ; or the system clock (0x10, i bit 2) | output << 2 | PWM (0x02), in byte 1; and the timer's shift of its counts in
    ; the IHRC's ticks, 2 * prescaler + the system clock's shift, bit 7 for 6-bit, in byte 2
    mov a, _chipsharp_pdk14_arguments+6
    and a, #0x03
    swap a
    sl a
    mov _chipsharp_pdk14_arguments+0, a
    mov a, _chipsharp_pdk14_arguments+7
    add a, #-1
    or _chipsharp_pdk14_arguments+0, a
    mov a, _chipsharp_pdk14_arguments+6
    and a, #0x03
    sl a
    mov _chipsharp_pdk14_arguments+2, a
    mov a, code
    and a, #0x03
    sl a
    sl a
    or a, #0x22
    mov _chipsharp_pdk14_arguments+1, a
    mov a, _chipsharp_pdk14_arguments+6
    and a, #0x04
    cneqsn a, #0
    goto 00045$
    mov a, #0x30
    xor _chipsharp_pdk14_arguments+1, a
    mov a, #PFS154_SYSTEM_CLOCK_SHIFT
    add _chipsharp_pdk14_arguments+2, a
00045$:
    mov a, _chipsharp_pdk14_arguments+6
    and a, #0x08
    cneqsn a, #0
    goto 00046$
    mov a, #0x80
    or _chipsharp_pdk14_arguments+0, a
    or _chipsharp_pdk14_arguments+2, a
00046$:
    call out
    mov a, code
    and a, #0x04
    ceqsn a, #0
    goto 00047$
    mov a, _chipsharp_pdk14_arguments+7
    mov scaler+0, a
    mov a, _chipsharp_pdk14_arguments+2
    mov shift+0, a
    mov a, #0
    mov.io 0x1c, a
    mov.io 0x1d, a
    mov.io 0x09, a
    mov a, _chipsharp_pdk14_arguments+0
    mov.io 0x17, a
    mov a, _chipsharp_pdk14_arguments+1
    mov.io 0x1c, a
    ret
00047$:
    mov a, _chipsharp_pdk14_arguments+7
    mov scaler+1, a
    mov a, _chipsharp_pdk14_arguments+2
    mov shift+1, a
    mov a, #0
    mov.io 0x32, a
    mov.io 0x33, a
    mov.io 0x35, a
    mov a, _chipsharp_pdk14_arguments+0
    mov.io 0x34, a
    mov a, _chipsharp_pdk14_arguments+1
    mov.io 0x32, a
    ret

; s, the shift of setting i's counts in Q's units (A = i + 1): 2 for 256 counts (0 for 64), the system clock's, twice
; the prescaler's power of 4.
00050$:
    pcadd a
    ret #2
    ret #4
    ret #6
    ret #8
    ret #2+PFS154_SYSTEM_CLOCK_SHIFT
    ret #4+PFS154_SYSTEM_CLOCK_SHIFT
    ret #6+PFS154_SYSTEM_CLOCK_SHIFT
    ret #8+PFS154_SYSTEM_CLOCK_SHIFT
    ret #0
    ret #2
    ret #4
    ret #6
    ret #0+PFS154_SYSTEM_CLOCK_SHIFT
    ret #2+PFS154_SYSTEM_CLOCK_SHIFT
    ret #4+PFS154_SYSTEM_CLOCK_SHIFT
    ret #6+PFS154_SYSTEM_CLOCK_SHIFT

pulse:
    ; System.InvalidOperationException: Pin {0} is not a PWM output: call Pwm.Output(pin, frequency) first.
    call running
    cneqsn a, #0
    goto _chipsharp_halt_failure
    ; System.ArgumentOutOfRangeException: A pulse of {0} microseconds is shorter than none.
    mov a, _chipsharp_pdk14_arguments+7
    and a, #0x80
    ceqsn a, #0
    goto _chipsharp_halt_failure
    ; the timer's scaler (s) and shift (p, and bit 7 in byte 3: 6-bit)
    mov a, code
    and a, #0x04
    ceqsn a, #0
    goto 00060$
    mov a, scaler+0
    mov s, a
    mov a, shift+0
    goto 00061$
00060$:
    mov a, scaler+1
    mov s, a
    mov a, shift+1
00061$:
    mov _chipsharp_pdk14_arguments+3, a
    ; 2^24 us and more: the whole period
    mov a, _chipsharp_pdk14_arguments+7
    ceqsn a, #0
    goto 00068$
    ; y = microseconds * 16 / 2^shift, plus S / 2, in bytes 4-7
    sl _chipsharp_pdk14_arguments+4
    slc _chipsharp_pdk14_arguments+5
    slc _chipsharp_pdk14_arguments+6
    slc _chipsharp_pdk14_arguments+7
    sl _chipsharp_pdk14_arguments+4
    slc _chipsharp_pdk14_arguments+5
    slc _chipsharp_pdk14_arguments+6
    slc _chipsharp_pdk14_arguments+7
    sl _chipsharp_pdk14_arguments+4
    slc _chipsharp_pdk14_arguments+5
    slc _chipsharp_pdk14_arguments+6
    slc _chipsharp_pdk14_arguments+7
    sl _chipsharp_pdk14_arguments+4
    slc _chipsharp_pdk14_arguments+5
    slc _chipsharp_pdk14_arguments+6
    slc _chipsharp_pdk14_arguments+7
    mov a, _chipsharp_pdk14_arguments+3
    and a, #0x7f
    add a, #1
    mov p, a
    goto 00063$
00062$:
    sr _chipsharp_pdk14_arguments+7
    src _chipsharp_pdk14_arguments+6
    src _chipsharp_pdk14_arguments+5
    src _chipsharp_pdk14_arguments+4
00063$:
    dzsn p
    goto 00062$
    mov a, s
    sr a
    add _chipsharp_pdk14_arguments+4, a
    addc _chipsharp_pdk14_arguments+5
    addc _chipsharp_pdk14_arguments+6
    addc _chipsharp_pdk14_arguments+7
    ; y / S: 256 or more (y / 256 at least S) is the whole period; else 8 rounds of a division, the quotient in byte 4
    mov a, _chipsharp_pdk14_arguments+7
    or a, _chipsharp_pdk14_arguments+6
    ceqsn a, #0
    goto 00068$
    mov a, _chipsharp_pdk14_arguments+5
    sub a, s
    t1sn.io 0x00, #1
    goto 00068$
    mov a, #9
    mov p, a
    goto 00065$
00064$:
    sl _chipsharp_pdk14_arguments+4
    slc _chipsharp_pdk14_arguments+5
    mov a, _chipsharp_pdk14_arguments+5
    sub a, s
    t0sn.io 0x00, #1
    goto 00065$
    mov _chipsharp_pdk14_arguments+5, a
    mov a, _chipsharp_pdk14_arguments+4
    or a, #1
    mov _chipsharp_pdk14_arguments+4, a
00065$:
    dzsn p
    goto 00064$
    ; at most the whole period: 64 counts of 64 (6-bit), 255 of 256
    mov a, _chipsharp_pdk14_arguments+3
    and a, #0x80
    cneqsn a, #0
    goto 00069$
    mov a, #64
    sub a, _chipsharp_pdk14_arguments+4
    t1sn.io 0x00, #1
    goto 00069$
00068$:
    mov a, #255
    mov _chipsharp_pdk14_arguments+4, a
    mov a, _chipsharp_pdk14_arguments+3
    and a, #0x80
    cneqsn a, #0
    goto 00069$
    mov a, #64
    mov _chipsharp_pdk14_arguments+4, a
00069$:
    ; wait for a count where the pin is high below both bounds or low above both (the bound in byte 4): s 1 while the
    ; count is below the bound, the pin's level 1 when high; they agree then
    mov a, code
    and a, #0x04
    ceqsn a, #0
    goto 00072$
00070$:
    clear s
    mov.io a, 0x1d
    sub a, _chipsharp_pdk14_arguments+4
    t0sn.io 0x00, #1
    inc s
    call level
    xor a, s
    ceqsn a, #0
    goto 00070$
    mov a, _chipsharp_pdk14_arguments+4
    mov.io 0x09, a
    ret
00072$:
    clear s
    mov.io a, 0x33
    sub a, _chipsharp_pdk14_arguments+4
    t0sn.io 0x00, #1
    inc s
    call level
    xor a, s
    ceqsn a, #0
    goto 00072$
    mov a, _chipsharp_pdk14_arguments+4
    mov.io 0x35, a
    ret

; A: 1 when the pin is high, else 0.
level:
    mov a, mask
    mov p, a
    mov a, code
    and a, #0x08
    ceqsn a, #0
    goto 00080$
    mov.io a, 0x10
    goto 00081$
00080$:
    mov.io a, 0x14
00081$:
    and a, p
    ceqsn a, #0
    mov a, #1
    ret
