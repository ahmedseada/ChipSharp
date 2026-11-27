// ChipSharp runtime for AVR chips: the memory functions LLVM calls (to copy and clear large structs and arrays), and
// memcmp, which C may call. Byte by byte: AVR reads and writes a byte at a time anyway. The usual AVR calling
// convention: the first pointer in r25:r24, the second argument in r23:r22, the count (16 bits) in r21:r20; the
// result in r25:r24. Besides that they change only r0, r20, r21 and r26-r31.
//
//   void *memcpy(void *target, const void *source, size_t count)
//   void *memmove(void *target, const void *source, size_t count)    the blocks may overlap
//   void *memset(void *target, int value, size_t count)
//   int memcmp(const void *a, const void *b, size_t count)           the first byte that differs, as unsigned values
//
// They work on RAM (AVR's data space). Data in flash (addrspace(1), read with LPM) is not RAM: these cannot read it.

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

function memcpy
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
// byte it has yet to read
function memmove
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

// The difference of the first bytes that differ, a - b, as a negative int when a's is the lower; zero when none does
function memcmp
    movw r26, r24
    movw r30, r22
    rjmp 2f
1:  ld r24, X+
    ld r0, Z+
    sub r24, r0
    brne 3f
2:  count_down
    brcc 1b
    clr r24
    clr r25
    ret
3:  sbc r25, r25            // 0xff when the subtraction borrowed: a's byte was the lower
    ret
end memcmp
