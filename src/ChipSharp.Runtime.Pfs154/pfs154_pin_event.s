; ChipSharp runtime for the Padauk PFS154: the pin event (Pin.Watch, WatchRising, WatchFalling, Unwatch) of the
; program's [Interrupt(Event.Pin)] method, its entry chipsharp_isr_irq0 (the target's "interrupts.pin.irq": PA0's
; request bit). The PFS154 has an interrupt on two pins, PA0 (INTRQ bit 0) and PB0 (bit 1), each for the edges INTEGS
; gives it (both, rising, falling), which the chip itself picks: those two pins are all the pin event watches
; (chipsharp check holds a pin given as a constant to "pinFunctions.pinEvent"; any other pin of the chip stops the
; program, a number no pin has is ignored). One method handles both: it runs once for an interrupt, whichever of the
; watched pins asked.
;
;   ChipSharp_Runtime_PinWatch, ...Rising, ...Falling, PinUnwatch   the pin's edges watched from now on (Unwatch:
;                                     none); changes from before are forgotten (its request bit cleared)
;
; and in the interrupt handler (INTSRC), for PA0's request with its enable bit on, or PB0's: the requests of the watched
; pins clear, then the method runs. INTEGS is write-only, so the edges of both pins are kept (pin_edges: RAM, 1 byte),
; T16's bit (falling, which chipsharp_chip_init sets: the clock's rounds) written with them. The functions' parameter is
; on the stack (pdk14_runtime.h), copied with interrupts off.

    .module pfs154_pin_event
    .globl _ChipSharp_Runtime_PinWatch
    .globl _ChipSharp_Runtime_PinWatchRising
    .globl _ChipSharp_Runtime_PinWatchFalling
    .globl _ChipSharp_Runtime_PinUnwatch
    .globl _chipsharp_isr_irq0
    .globl _chipsharp_pdk14_arguments
    .globl _chipsharp_pdk14_argument_1
    .globl _chipsharp_pdk14_interrupts_back
    .globl _chipsharp_halt_failure

    .area DATA
pin_edges:
    .ds 1

    .area INTSRC (CODE)
    t0sn.io 0x04, #0
    t1sn.io 0x05, #0
    goto 00001$
    goto 00002$
00001$:
    t0sn.io 0x04, #1
    t1sn.io 0x05, #1
    goto 00003$
00002$:
    t0sn.io 0x04, #0
    set0.io 0x05, #0
    t0sn.io 0x04, #1
    set0.io 0x05, #1
    call _chipsharp_isr_irq0
00003$:

    .area CODE
_ChipSharp_Runtime_PinWatch::
    disgint
    call _chipsharp_pdk14_argument_1
    mov a, #0
    goto watch
_ChipSharp_Runtime_PinWatchRising::
    disgint
    call _chipsharp_pdk14_argument_1
    mov a, #1
    goto watch
_ChipSharp_Runtime_PinWatchFalling::
    disgint
    call _chipsharp_pdk14_argument_1
    mov a, #2
    goto watch
_ChipSharp_Runtime_PinUnwatch::
    disgint
    call _chipsharp_pdk14_argument_1
    mov a, #3
; A: the edges, as INTEGS has them (0 both, 1 rising, 2 falling), or 3: none; the pin in the arguments' bytes 0-3
watch:
    mov p, a
    mov a, _chipsharp_pdk14_arguments+1
    or a, _chipsharp_pdk14_arguments+2
    or a, _chipsharp_pdk14_arguments+3
    ceqsn a, #0
    goto 00019$
    mov a, _chipsharp_pdk14_arguments+0
    ceqsn a, #0
    goto 00011$
    ; PA0: INTEGS bits 0-1, INTRQ and INTEN bit 0
    set0.io 0x04, #0
    mov a, p
    cneqsn a, #3
    goto 00019$
    mov a, pin_edges
    and a, #0xfc
    or a, p
    mov pin_edges, a
    or a, #0x10
    mov.io 0x0c, a
    set0.io 0x05, #0
    set1.io 0x04, #0
    goto 00019$
00011$:
    ceqsn a, #8
    goto 00012$
    ; PB0: INTEGS bits 2-3, INTRQ and INTEN bit 1
    set0.io 0x04, #1
    mov a, p
    cneqsn a, #3
    goto 00019$
    sl p
    sl p
    mov a, pin_edges
    and a, #0xf3
    or a, p
    mov pin_edges, a
    or a, #0x10
    mov.io 0x0c, a
    set0.io 0x05, #1
    set1.io 0x04, #1
    goto 00019$
    ; another number: 1, 2 and 16 and up are no pin, ignored; the others are pins the event cannot watch
00012$:
    ceqsn a, #1
    goto 00013$
    goto 00019$
00013$:
    ceqsn a, #2
    goto 00014$
    goto 00019$
00014$:
    sub a, #16
    t1sn.io 0x00, #1
    goto 00019$
    ; System.ArgumentException: Pin {0} cannot be watched: the PFS154's pin event watches pin 0 (PA0) and pin 8
    ; (PB0), the two pins with an interrupt.
    goto _chipsharp_halt_failure
00019$:
    goto _chipsharp_pdk14_interrupts_back
