// ChipSharp runtime for AVR chips (ATmega, ATtiny, AVR Dx): the reset handler.
//
// The first code a program runs, from the reset vector (entry 0 of the vector table, avr_vectors.S). Before any C can
// run it sets up what compiled code counts on, then jumps to chipsharp_start (avr_runtime.c), which never returns:
//
//   r1 = 0        the zero register: compiled code expects r1 to hold 0 (avr-gcc's convention, which LLVM keeps)
//   SREG = 0      interrupts off, until chipsharp_start turns them on
//   SP            the end of RAM: __chipsharp_stack_top - 1, as AVR's push stores at SP and then decrements it
//   .data         its starting values copied from flash to RAM, byte by byte (LPM). The linker script puts .rodata
//                 in it too: on AVR, ordinary constants are read from RAM; only the data LLVM puts in addrspace(1)
//                 (section .progmem.data) is read from flash
//   .bss          zeroed
//
// The symbols of the linker script it uses (addresses, not variables; a RAM address may carry the 0x800000 that tells
// AVR's data space from its flash in an ELF file: only its low 16 bits are used):
//
//   __chipsharp_data_start, __chipsharp_data_end   .data in RAM: its first byte, and the byte after its last
//   __chipsharp_data_load                          where .data's starting values are in flash (a byte address)
//   __chipsharp_bss_start, __chipsharp_bss_end     .bss in RAM
//   __chipsharp_stack_top                          the byte after the last of RAM (0x900 on the ATmega328P)
//
// .data's starting values must be in the first 64 KB of flash (LPM reaches no further; the ATmega2560's ELPM is not
// used yet).

#if defined(__AVR_HAVE_JMP_CALL__)
#define XJMP jmp
#else
#define XJMP rjmp
#endif

#define SREG 0x3f
#define SPH 0x3e
#define SPL 0x3d

    .section .text.chipsharp_reset, "ax", @progbits
    .global chipsharp_reset
    .type chipsharp_reset, @function
chipsharp_reset:
    clr r1
    out SREG, r1
    ldi r28, lo8(__chipsharp_stack_top - 1)
    ldi r29, hi8(__chipsharp_stack_top - 1)
    out SPH, r29
    out SPL, r28

    // .data: X walks RAM, Z flash
    ldi r26, lo8(__chipsharp_data_start)
    ldi r27, hi8(__chipsharp_data_start)
    ldi r30, lo8(__chipsharp_data_load)
    ldi r31, hi8(__chipsharp_data_load)
    ldi r24, lo8(__chipsharp_data_end)
    ldi r25, hi8(__chipsharp_data_end)
    rjmp 2f
1:
#if defined(__AVR_HAVE_LPMX__)
    lpm r0, Z+
#else
    lpm
    adiw r30, 1
#endif
    st X+, r0
2:  cp r26, r24
    cpc r27, r25
    brne 1b

    // .bss
    ldi r26, lo8(__chipsharp_bss_start)
    ldi r27, hi8(__chipsharp_bss_start)
    ldi r24, lo8(__chipsharp_bss_end)
    ldi r25, hi8(__chipsharp_bss_end)
    rjmp 4f
3:  st X+, r1
4:  cp r26, r24
    cpc r27, r25
    brne 3b

    XJMP chipsharp_start
    .size chipsharp_reset, . - chipsharp_reset
