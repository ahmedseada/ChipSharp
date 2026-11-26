// ChipSharp runtime for ARMv6-M chips (Cortex-M0 and M0+): 64-bit division and remainder.
//
// The RTABI returns both results in registers (quotient in r0:r1, remainder in r2:r3), which C cannot express,
// so these two entry points pass a stack slot for the remainder to the C functions in armv6m_helpers.c and load
// it into r2:r3 afterwards.
//
//   u64 chipsharp_udivmod64(u64 dividend, u64 divisor, u64 *remainder)   dividend in r0:r1, divisor in r2:r3,
//   u64 chipsharp_divmod64(u64 dividend, u64 divisor, u64 *remainder)    remainder pointer on the stack

    .syntax unified
    .cpu cortex-m0plus
    .thumb

    .section .text.__aeabi_uldivmod, "ax", %progbits
    .global __aeabi_uldivmod
    .type __aeabi_uldivmod, %function
    .thumb_func
__aeabi_uldivmod:
    push {r4, lr}
    sub sp, sp, #16           // [sp]: the fifth argument; [sp + 8]: the remainder (8-byte aligned)
    add r4, sp, #8
    str r4, [sp]
    bl chipsharp_udivmod64
    ldr r2, [sp, #8]
    ldr r3, [sp, #12]
    add sp, sp, #16
    pop {r4, pc}
    .size __aeabi_uldivmod, . - __aeabi_uldivmod

    .section .text.__aeabi_ldivmod, "ax", %progbits
    .global __aeabi_ldivmod
    .type __aeabi_ldivmod, %function
    .thumb_func
__aeabi_ldivmod:
    push {r4, lr}
    sub sp, sp, #16
    add r4, sp, #8
    str r4, [sp]
    bl chipsharp_divmod64
    ldr r2, [sp, #8]
    ldr r3, [sp, #12]
    add sp, sp, #16
    pop {r4, pc}
    .size __aeabi_ldivmod, . - __aeabi_ldivmod
