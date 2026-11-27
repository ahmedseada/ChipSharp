// ChipSharp runtime for AVR chips: the divisions LLVM calls a function for.
//
// AVR has no divide instruction. LLVM computes the quotient and the remainder of 8-, 16- and 32-bit values with one
// call each, with avr-gcc's names and registers; a 64-bit division or remainder is a call of its own. Each keeps
// r2-r17, r28 and r29 and leaves r1 zero, as every AVR function must:
//
//   __udivmodqi4, __divmodqi4   8 bits: dividend r24, divisor r22; quotient r24, remainder r25
//   __udivmodhi4, __divmodhi4   16 bits: dividend r25:r24, divisor r23:r22; quotient r23:r22, remainder r25:r24
//                               (LLVM's AVR_BUILTIN calling convention for these four)
//   __udivmodsi4, __divmodsi4   32 bits: dividend r25-r22, divisor r21-r18; quotient r21-r18, remainder r25-r22
//   __udivdi3, __divdi3         64 bits: dividend r25-r18, divisor r17-r10; quotient r25-r18
//   __umoddi3, __moddi3         64 bits: the same, the remainder in r25-r18
//
// Signed division rounds toward zero and the remainder has the dividend's sign, as in C#. Each is long division, a
// quotient bit per step; the 64-bit ones skip the dividend's leading zero bytes first. A zero divisor gives nothing
// that means anything, but no C# program gets here with one: the code generator checks the divisor first
// (DivideByZeroException, and OverflowException for the smallest value divided by -1, which wraps here).

#if !defined(__AVR_HAVE_MOVW__)
#error "ChipSharp's AVR runtime needs MOVW (every AVR core but the oldest, AVR2, has it)"
#endif

#if defined(__AVR_HAVE_JMP_CALL__)
#define XCALL call
#else
#define XCALL rcall
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

// The negative of a 16-bit value, a 32-bit one and a 64-bit one in registers r16-r31 (two's complement: the bits
// inverted, plus one)
.macro negate16 b0, b1
    com \b1
    neg \b0
    sbci \b1, 0xff
.endm

.macro negate32 b0, b1, b2, b3
    com \b3
    com \b2
    com \b1
    neg \b0
    sbci \b1, 0xff
    sbci \b2, 0xff
    sbci \b3, 0xff
.endm

.macro negate64 b0, b1, b2, b3, b4, b5, b6, b7
    com \b7
    com \b6
    com \b5
    com \b4
    com \b3
    com \b2
    com \b1
    neg \b0
    sbci \b1, 0xff
    sbci \b2, 0xff
    sbci \b3, 0xff
    sbci \b4, 0xff
    sbci \b5, 0xff
    sbci \b6, 0xff
    sbci \b7, 0xff
.endm

// The same for 64 bits in any registers (r0-r15 have no SBCI): COM sets the carry, which the ADCs add as the one
.macro negate64_low b0, b1, b2, b3, b4, b5, b6, b7
    com \b0
    com \b1
    com \b2
    com \b3
    com \b4
    com \b5
    com \b6
    com \b7
    adc \b0, r1
    adc \b1, r1
    adc \b2, r1
    adc \b3, r1
    adc \b4, r1
    adc \b5, r1
    adc \b6, r1
    adc \b7, r1
.endm

// ---- 8 bits ---------------------------------------------------------------------------------------------------------

// r24 / r22: the dividend is shifted out of r24 at the top into the remainder (r25) as the quotient's bits come in at
// the bottom. Before the last step the remainder is below 2^7, so it never needs a ninth bit.
function __udivmodqi4
    clr r25
    ldi r23, 8
1:  lsl r24
    rol r25
    cp r25, r22
    brcs 2f
    sub r25, r22
    inc r24                 // the quotient's bit (the LSL cleared it)
2:  dec r23
    brne 1b
    ret
end __udivmodqi4

// T: the remainder's sign (the dividend's); r0 bit 7: the quotient's
function __divmodqi4
    bst r24, 7
    mov r0, r24
    eor r0, r22
    sbrc r24, 7
    neg r24
    sbrc r22, 7
    neg r22
    XCALL __udivmodqi4      // it changes neither r0 nor T
    sbrc r0, 7
    neg r24
    brtc 1f
    neg r25
1:  ret
end __divmodqi4

// ---- 16 bits --------------------------------------------------------------------------------------------------------

// r25:r24 / r23:r22, the divisor moved to r21:r20, the dividend to r23:r22 (where the quotient comes in), the remainder
// in r25:r24
function __udivmodhi4
    movw r20, r22
    movw r22, r24
    clr r24
    clr r25
    ldi r26, 16
1:  lsl r22
    rol r23
    rol r24
    rol r25
    cp r24, r20
    cpc r25, r21
    brcs 2f
    sub r24, r20
    sbc r25, r21
    inc r22
2:  dec r26
    brne 1b
    ret
end __udivmodhi4

function __divmodhi4
    bst r25, 7
    mov r0, r25
    eor r0, r23
    sbrs r25, 7
    rjmp 1f
    negate16 r24, r25
1:  sbrs r23, 7
    rjmp 2f
    negate16 r22, r23
2:  XCALL __udivmodhi4      // it changes neither r0 nor T
    sbrs r0, 7
    rjmp 3f
    negate16 r22, r23
3:  brtc 4f
    negate16 r24, r25
4:  ret
end __divmodhi4

// ---- 32 bits --------------------------------------------------------------------------------------------------------

// r25-r22 / r21-r18: the divisor moved to r31, r30, r27, r26, the dividend to r21-r18 (where the quotient comes in),
// the remainder in r25-r22, the steps counted in r0
function __udivmodsi4
    movw r26, r18
    movw r30, r20
    movw r18, r22
    movw r20, r24
    ldi r22, 32
    mov r0, r22
    clr r22
    clr r23
    movw r24, r22
1:  lsl r18
    rol r19
    rol r20
    rol r21
    rol r22
    rol r23
    rol r24
    rol r25
    cp r22, r26
    cpc r23, r27
    cpc r24, r30
    cpc r25, r31
    brcs 2f
    sub r22, r26
    sbc r23, r27
    sbc r24, r30
    sbc r25, r31
    inc r18
2:  dec r0
    brne 1b
    ret
end __udivmodsi4

// The quotient's sign is kept on the stack: __udivmodsi4 uses every register a function may change
function __divmodsi4
    mov r0, r25
    eor r0, r21
    push r0
    bst r25, 7
    brtc 1f
    negate32 r22, r23, r24, r25
1:  sbrs r21, 7
    rjmp 2f
    negate32 r18, r19, r20, r21
2:  XCALL __udivmodsi4      // it does not change T
    pop r0
    sbrs r0, 7
    rjmp 3f
    negate32 r18, r19, r20, r21
3:  brtc 4f
    negate32 r22, r23, r24, r25
4:  ret
end __divmodsi4

// ---- 64 bits --------------------------------------------------------------------------------------------------------

// Not called from outside: r25-r18 (A, the dividend) / r17-r10 (B, the divisor, left as it is). The quotient comes in
// where A is shifted out, in r25-r18, and the remainder R is in r5, r4, r3, r2, r31, r30, r27, r26; r28 counts the
// steps. The caller has saved r2-r5 and r28.
function __chipsharp_udivmod64
    clr r26
    clr r27
    movw r30, r26
    movw r2, r26
    movw r4, r26
    ldi r28, 64
1:  tst r25                 // a leading zero byte of A: its 8 steps leave R zero and add 8 zero bits to the quotient
    brne 2f
    mov r25, r24
    mov r24, r23
    mov r23, r22
    mov r22, r21
    mov r21, r20
    mov r20, r19
    mov r19, r18
    clr r18
    subi r28, 8
    brne 1b
    ret                     // A was zero: so are the quotient and the remainder
2:  lsl r18
    rol r19
    rol r20
    rol r21
    rol r22
    rol r23
    rol r24
    rol r25
    rol r26
    rol r27
    rol r30
    rol r31
    rol r2
    rol r3
    rol r4
    rol r5
    cp r26, r10
    cpc r27, r11
    cpc r30, r12
    cpc r31, r13
    cpc r2, r14
    cpc r3, r15
    cpc r4, r16
    cpc r5, r17
    brcs 3f
    sub r26, r10
    sbc r27, r11
    sbc r30, r12
    sbc r31, r13
    sbc r2, r14
    sbc r3, r15
    sbc r4, r16
    sbc r5, r17
    inc r18
3:  dec r28
    brne 2b
    ret
end __chipsharp_udivmod64

// Not called from outside: the signed division of the same registers, the quotient with the sign of A ^ B, the
// remainder with A's. B is made positive for the division and given back as it was. The caller has saved r2-r5, r28
// and r29.
function __chipsharp_divmod64
    mov r29, r25            // bit 7: the quotient's sign
    eor r29, r17
    bst r25, 7              // T: the remainder's sign
    push r17                // bit 7: B's sign
    brtc 1f
    negate64 r18, r19, r20, r21, r22, r23, r24, r25
1:  sbrs r17, 7
    rjmp 2f
    negate64_low r10, r11, r12, r13, r14, r15, r16, r17
2:  XCALL __chipsharp_udivmod64     // it does not change T
    pop r0
    sbrs r0, 7
    rjmp 3f
    negate64_low r10, r11, r12, r13, r14, r15, r16, r17
3:  sbrs r29, 7
    rjmp 4f
    negate64 r18, r19, r20, r21, r22, r23, r24, r25
4:  brtc 5f
    negate64_low r26, r27, r30, r31, r2, r3, r4, r5
5:  ret
end __chipsharp_divmod64

.macro save
    push r2
    push r3
    push r4
    push r5
    push r28
    push r29
.endm

.macro restore
    pop r29
    pop r28
    pop r5
    pop r4
    pop r3
    pop r2
.endm

.macro remainder
    movw r18, r26
    movw r20, r30
    movw r22, r2
    movw r24, r4
.endm

function __udivdi3
    save
    XCALL __chipsharp_udivmod64
    restore
    ret
end __udivdi3

function __umoddi3
    save
    XCALL __chipsharp_udivmod64
    remainder
    restore
    ret
end __umoddi3

function __divdi3
    save
    XCALL __chipsharp_divmod64
    restore
    ret
end __divdi3

function __moddi3
    save
    XCALL __chipsharp_divmod64
    remainder
    restore
    ret
end __moddi3
