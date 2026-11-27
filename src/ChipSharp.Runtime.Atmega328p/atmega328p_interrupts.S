// ChipSharp runtime for the ATmega328P: the entries of the interrupts the runtime's events use, which the AVR runtime's
// vector table (avr_vectors.S) names: avr-libc's __vector_<n>, in place of its weak ones.
//
//   __vector_1    INT0: D2                 source 0   the pin event (atmega328p_events.c)
//   __vector_2    INT1: D3                 source 1
//   __vector_3    PCINT0: D8-D13 (port B)  source 2
//   __vector_4    PCINT1: A0-A5 (port C)   source 3
//   __vector_5    PCINT2: D0-D7 (port D)   source 4
//   __vector_12   TIMER1_COMPB             source 5   the timer event (atmega328p_events.c)
//   __vector_18   USART_RX                 source 6   a byte received (atmega328p_uart.c)
//
// Each entry saves r24, puts its source in it and goes to one common part, which saves what a C function may change
// (r0, SREG, r1, r18-r27, r30, r31), clears r1 (compiled code takes it to be 0), calls chipsharp_interrupt(source)
// (atmega328p_runtime.c), which calls the handler the runtime put in for that kind of interrupt, if any, and puts it all
// back (RETI). The handler finds out whether the event happened (the right edge of a watched pin, the time come) and
// then calls the program's [Interrupt] method: its entry, chipsharp_isr_irq<n> by the number the target's "interrupts"
// table gives the event (atmega328p.json: 26 pin, 27 timer, 28 uart, past the chip's own vectors), an ordinary function.
//
// The entries are in every program, as the vector table names them; together they take a few words, with the common
// part a hundred bytes, against some seventy for each entry in C (__attribute__((signal)) saving all of that itself).
// Interrupts stay off while a handler runs (the AVR turns them off to enter one, RETI on again): one never interrupts
// another, as on the Pico.

#define SREG 0x3f

    .section .text.chipsharp_interrupt_entries, "ax", @progbits

.macro entry vector, source
    .global __vector_\vector
    .type __vector_\vector, @function
__vector_\vector:
    push r24
    ldi r24, \source
    rjmp common
    .size __vector_\vector, . - __vector_\vector
.endm

    entry 1, 0
    entry 2, 1
    entry 3, 2
    entry 4, 3
    entry 5, 4
    entry 12, 5
    entry 18, 6

common:
    push r0
    in r0, SREG
    push r0
    push r1
    clr r1
    push r18
    push r19
    push r20
    push r21
    push r22
    push r23
    push r25
    push r26
    push r27
    push r30
    push r31
    call chipsharp_interrupt
    pop r31
    pop r30
    pop r27
    pop r26
    pop r25
    pop r23
    pop r22
    pop r21
    pop r20
    pop r19
    pop r18
    pop r1
    pop r0
    out SREG, r0
    pop r0
    pop r24
    reti
