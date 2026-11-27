// ChipSharp runtime for AVR chips: the multiplications LLVM calls a function for.
//
// AVR multiplies 8 bits by 8 bits at most (MUL, on the chips that have it: not the ATtiny85's AVR25 core), so LLVM
// calls these, with avr-gcc's names and the usual AVR calling convention: arguments from r25 down (a 16-bit one in
// r25:r24 then r23:r22, a 32-bit one in r25-r22 then r21-r18, a 64-bit one in r25-r18 then r17-r10), the result where
// the first argument came (64-bit in r25-r18), low byte in the lowest register. Each keeps r2-r17, r28 and r29, and
// leaves r1 zero (MUL writes its product to r1:r0):
//
//   __mulqi3    8 x 8 bits, r24 * r22 -> r24          only on a chip without MUL (LLVM multiplies inline with MUL)
//   __mulhi3    16 x 16 bits -> r25:r24               only on a chip without MUL
//   __mulsi3    32 x 32 bits, the product's low 32 bits -> r25-r22: ten 8 x 8 bit products with MUL, a loop without
//   __muldi3    64 x 64 bits, the product's low 64 bits -> r25-r18: a loop over the bits of the second value, a byte
//               at a time (a zero byte costs a shift of a whole byte)
//
// The low bits of a product are the same for signed and unsigned values, so each function is both.

#if !defined(__AVR_HAVE_MOVW__)
#error "ChipSharp's AVR runtime needs MOVW (every AVR core but the oldest, AVR2, has it)"
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

// u8 __mulqi3(u8 a, u8 b): shift and add
function __mulqi3
    clr r23                 // the product
1:  lsr r22                 // the next bit of b
    brcc 2f
    add r23, r24
2:  lsl r24
    tst r22                 // until b has no bit left
    brne 1b
    mov r24, r23
    ret
end __mulqi3

// u16 __mulhi3(u16 a, u16 b): shift and add
function __mulhi3
    clr r26                 // the product, r27:r26
    clr r27
1:  lsr r23                 // the next bit of b
    ror r22
    brcc 2f
    add r26, r24
    adc r27, r25
2:  lsl r24
    rol r25
    cp r22, r1              // until b has no bit left
    cpc r23, r1
    brne 1b
    movw r24, r26
    ret
end __mulhi3

// u32 __mulsi3(u32 a, u32 b): a in r25-r22 (A3-A0), b in r21-r18 (B3-B0), the product P in r31, r30, r27, r26 (P3-P0)
function __mulsi3
#if defined(__AVR_HAVE_MUL__)
    // A(i) * B(j) adds at byte i + j; above byte 3 nothing is kept
    mul r22, r18            // A0 * B0: bytes 0-1
    movw r26, r0
    mul r23, r19            // A1 * B1: bytes 2-3
    movw r30, r0
    mul r22, r20            // A0 * B2: bytes 2-3
    add r30, r0
    adc r31, r1
    mul r24, r18            // A2 * B0: bytes 2-3
    add r30, r0
    adc r31, r1
    mul r22, r21            // A0 * B3, A1 * B2, A2 * B1, A3 * B0: byte 3, the low byte of each
    add r31, r0
    mul r23, r20
    add r31, r0
    mul r24, r19
    add r31, r0
    mul r25, r18
    add r31, r0
    mul r22, r19            // A0 * B1: bytes 1-2, and its carry into byte 3
    add r27, r0
    adc r30, r1
    clr r1                  // (clr leaves the carry as it is)
    adc r31, r1
    mul r23, r18            // A1 * B0: the same
    add r27, r0
    adc r30, r1
    clr r1
    adc r31, r1
#else
    clr r26
    clr r27
    movw r30, r26
1:  lsr r21                 // the next bit of b
    ror r20
    ror r19
    ror r18
    brcc 2f
    add r26, r22
    adc r27, r23
    adc r30, r24
    adc r31, r25
2:  lsl r22
    rol r23
    rol r24
    rol r25
    cp r18, r1              // until b has no bit left
    cpc r19, r1
    cpc r20, r1
    cpc r21, r1
    brne 1b
#endif
    movw r22, r26
    movw r24, r30
    ret
end __mulsi3

// u64 __muldi3(u64 a, u64 b): a in r25-r18 (A7-A0), b in r17-r10 (B7-B0, left as they are), the product P in r5, r4,
// r3, r2, r31, r30, r27, r26 (P7-P0). For each byte of b, lowest first: P += A for each of its set bits, lowest first,
// A shifted left once per bit
function __muldi3
    push r2
    push r3
    push r4
    push r5
    push r28
    clr r26
    clr r27
    movw r30, r26
    movw r2, r26
    movw r4, r26
    mov r0, r10
    rcall 1f
    mov r0, r11
    rcall 1f
    mov r0, r12
    rcall 1f
    mov r0, r13
    rcall 1f
    mov r0, r14
    rcall 1f
    mov r0, r15
    rcall 1f
    mov r0, r16
    rcall 1f
    mov r0, r17
    rcall 1f
    movw r18, r26
    movw r20, r30
    movw r22, r2
    movw r24, r4
    pop r28
    pop r5
    pop r4
    pop r3
    pop r2
    ret

// P += A * r0, A <<= 8
1:  tst r0
    brne 2f
    mov r25, r24            // a zero byte: A shifted a whole byte at once
    mov r24, r23
    mov r23, r22
    mov r22, r21
    mov r21, r20
    mov r20, r19
    mov r19, r18
    clr r18
    ret
2:  ldi r28, 8
3:  lsr r0                  // the next bit of the byte
    brcc 4f
    add r26, r18
    adc r27, r19
    adc r30, r20
    adc r31, r21
    adc r2, r22
    adc r3, r23
    adc r4, r24
    adc r5, r25
4:  lsl r18
    rol r19
    rol r20
    rol r21
    rol r22
    rol r23
    rol r24
    rol r25
    dec r28
    brne 3b
    ret
end __muldi3
