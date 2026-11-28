// ChipSharp runtime for 32-bit RISC-V chips (RV32, machine mode): the trap entry.
//
// chipsharp_trap is where mtvec points (riscv32_startup.S): every exception and every interrupt comes here. It is in the
// section .vectors, which the linker script aligns to the target's link.vectorAlign (64 bytes on riscv32): direct mode
// needs 4, and a CLIC's mode (mtvec | 3, the ESP32-C5's), whose mtvec keeps only bits 31-6 of the address, 64. A chip
// with a CLIC sets that mode in its chipsharp_chip_boot; its interrupts that are not vectored (SHV clear, all of
// ChipSharp's) come here too, with their id in mcause's bits 11-0, as a CLINT's interrupts have their cause there. A
// chip whose vector table holds the handlers' addresses (the CH32V003's PFIC) has this one's in every entry.
//
// It saves the registers a C function may change (ra, t0-t6, a0-a7: 16 words, the stack 16-byte aligned; on RV32E,
// which has no x16-x31, ra, t0-t2 and a0-a5: 10 words, ilp32e's stack 4-byte aligned), calls chipsharp_trap_dispatch
// (riscv32_vectors.c) with mcause and mepc, puts them back and returns with MRET to where the trap came: an interrupt's
// handler, a C function, returns and the program goes on. Interrupts stay off in a handler (the trap turned
// mstatus.MIE off; MRET puts it back), so handlers do not nest.

#ifdef __riscv_e
#define FRAME 40
#else
#define FRAME 64
#endif

    .section .vectors, "ax", @progbits
    .global chipsharp_trap
    .type chipsharp_trap, @function
    .balign 4
chipsharp_trap:
    addi sp, sp, -FRAME
    sw ra, 0(sp)
    sw t0, 4(sp)
    sw t1, 8(sp)
    sw t2, 12(sp)
    sw a0, 16(sp)
    sw a1, 20(sp)
    sw a2, 24(sp)
    sw a3, 28(sp)
    sw a4, 32(sp)
    sw a5, 36(sp)
#ifndef __riscv_e
    sw a6, 40(sp)
    sw a7, 44(sp)
    sw t3, 48(sp)
    sw t4, 52(sp)
    sw t5, 56(sp)
    sw t6, 60(sp)
#endif
    csrr a0, mcause
    csrr a1, mepc
    call chipsharp_trap_dispatch
    lw ra, 0(sp)
    lw t0, 4(sp)
    lw t1, 8(sp)
    lw t2, 12(sp)
    lw a0, 16(sp)
    lw a1, 20(sp)
    lw a2, 24(sp)
    lw a3, 28(sp)
    lw a4, 32(sp)
    lw a5, 36(sp)
#ifndef __riscv_e
    lw a6, 40(sp)
    lw a7, 44(sp)
    lw t3, 48(sp)
    lw t4, 52(sp)
    lw t5, 56(sp)
    lw t6, 60(sp)
#endif
    addi sp, sp, FRAME
    mret
    .size chipsharp_trap, . - chipsharp_trap
