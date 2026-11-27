// ChipSharp runtime for AVR chips: the memory functions LLVM calls (to copy and clear large structs and arrays), and
// memcmp, which C may call. Byte by byte: AVR reads and writes a byte at a time anyway. The usual AVR calling
// convention: the first pointer in r25:r24, the second argument in r23:r22, the count (16 bits) in r21:r20; the
// result in r25:r24. Besides that they change only r0, r18, r20, r21 and r26-r31.
//
//   void *memcpy(void *target, const void *source, size_t count)
//   void *memmove(void *target, const void *source, size_t count)    the blocks may overlap
//   void *memset(void *target, int value, size_t count)
//   int memcmp(const void *a, const void *b, size_t count)           the first byte that differs, as unsigned values
//   void *chipsharp_memcpy_flash(void *target, const __flash void *source, size_t count)
//                                                                    memcpy from flash (LPM) to RAM
//
// A target is in RAM. What they read may be in flash: the code generator keeps its constants there and gives them as
// addresses with bit 15 set (0x8000 + the flash address; RAM's never have it), and these read such an address with
// LPM. A plain flash address (addrspace(1): LLVM 22 turns llvm.memcpy.p0.p1 into a call to memcpy all the same,
// which would read RAM there) must go to chipsharp_memcpy_flash, which takes either. Flash is read in its first 32 KB
// (LPM; ELPM, beyond 64 KB, is not used yet).

#if defined(__AVR_HAVE_JMP_CALL__)
#define XJMP jmp
#else
#define XJMP rjmp
#endif

.macro function name
    .section .text.\name, "ax", @progbits
    .global \name
    .type \name, @function
\name:
.endm

.macro end name
    .size \name, . - \name
.endm

// The count in r21:r20 down by one, and the carry set when it was zero already: the end of a loop
.macro count_down
    subi r20, 1
    sbci r21, 0
.endm

// r0 = the byte of flash at Z, Z + 1
.macro lpm_next
#if defined(__AVR_HAVE_LPMX__)
    lpm r0, Z+
#else
    lpm
    adiw r30, 1
#endif
.endm

function memcpy
    sbrc r23, 7             // a source in flash
    XJMP chipsharp_memcpy_flash
    movw r26, r24           // X: the target
    movw r30, r22           // Z: the source
    rjmp 2f
1:  ld r0, Z+
    st X+, r0
2:  count_down
    brcc 1b
    ret
end memcpy

// A target below the source copies forward; one above it copies from the end back, so that neither overwrites a
// byte it has yet to read. A source in flash overlaps nothing.
function memmove
    sbrc r23, 7
    XJMP chipsharp_memcpy_flash
    movw r26, r24
    movw r30, r22
    cp r24, r22
    cpc r25, r23
    brcc 3f
    rjmp 2f
1:  ld r0, Z+
    st X+, r0
2:  count_down
    brcc 1b
    ret
3:  add r26, r20
    adc r27, r21
    add r30, r20
    adc r31, r21
    rjmp 5f
4:  ld r0, -Z
    st -X, r0
5:  count_down
    brcc 4b
    ret
end memmove

function memset
    movw r26, r24
    rjmp 2f
1:  st X+, r22
2:  count_down
    brcc 1b
    ret
end memset

// The difference of the first bytes that differ, a - b, as a negative int when a's is the lower; zero when none does.
// Either may be in flash: each byte is read through Z, a's then b's.
function memcmp
    rjmp 3f
1:  movw r30, r24
    rcall 5f
    mov r18, r0             // a's byte
    adiw r24, 1
    movw r30, r22
    rcall 5f
    subi r22, 0xff          // b + 1
    sbci r23, 0xff
    sub r18, r0
    breq 3f
    sbc r25, r25            // 0xff when the subtraction borrowed: a's byte was the lower
    mov r24, r18
    ret
3:  count_down
    brcc 1b
    clr r24
    clr r25
    ret

// r0 = the byte at Z: in flash when bit 15 is set
5:  sbrs r31, 7
    rjmp 6f
    andi r31, 0x7f
    lpm
    ret
6:  ld r0, Z
    ret
end memcmp

function chipsharp_memcpy_flash
    movw r26, r24           // X: the target, in RAM
    movw r30, r22           // Z: the source, in flash
    andi r31, 0x7f          // with bit 15 or without
    rjmp 2f
1:  lpm_next
    st X+, r0
2:  count_down
    brcc 1b
    ret
end chipsharp_memcpy_flash
