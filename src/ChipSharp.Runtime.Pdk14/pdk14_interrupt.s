; ChipSharp runtime for Padauk PDK14 chips: the interrupt handler, where the one interrupt vector (word 0x10, which
; SDCC writes in pdk14_startup.c's file) goes for every interrupt; linked whole, after the start-up.
;
; It saves A, FLAG and both bytes of SDCC's pseudo-register p, notes that it runs (chipsharp_pdk14_in_interrupt), then
; runs what the area INTSRC holds: each runtime file that handles an interrupt adds there the few instructions that call
; its handler when the source's request (INTRQ) and enable (INTEN) bits are both set, and the linker puts the INTSRC of
; every file it links between this file's INTHEAD and INTTAIL, as it does SDCC's GSINIT, so a program has the handlers
; of the files it uses and no others (the PDK14 has one vector, and SDCC's linker no weak symbols). Then it puts back
; what it saved and returns with interrupts on again (reti). A handler clears its request bit with set0, which changes
; no other.
;
;   chipsharp_pdk14_interrupt         the handler
;   chipsharp_pdk14_in_interrupt      1 while it runs, else 0
;   chipsharp_pdk14_interrupts_back   what a function of the runtime ends a disgint with: interrupts on again (engint),
;                                     unless the handler runs (it called the function): they stay off until its reti

    .module pdk14_interrupt
    .globl _chipsharp_pdk14_interrupt
    .globl _chipsharp_pdk14_in_interrupt
    .globl _chipsharp_pdk14_interrupts_back

    .area DATA
_chipsharp_pdk14_in_interrupt::
    .ds 1

    .area INTHEAD (CODE)
_chipsharp_pdk14_interrupt::
    push af
    mov a, p
    push af
    mov a, p+1
    push af
    mov a, #1
    mov _chipsharp_pdk14_in_interrupt, a

    .area INTSRC (CODE)

    .area INTTAIL (CODE)
    clear _chipsharp_pdk14_in_interrupt
    pop af
    mov p+1, a
    pop af
    mov p, a
    pop af
    reti

    .area CODE
_chipsharp_pdk14_interrupts_back::
    mov a, _chipsharp_pdk14_in_interrupt
    ceqsn a, #0
    ret
    engint
    ret
