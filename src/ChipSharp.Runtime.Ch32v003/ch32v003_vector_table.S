// ChipSharp runtime for the WCH CH32V003: the vector table, the first 156 bytes of flash (the target's "link.head",
// at 0x08000000).
//
// The core starts at 0x00000000 (RM Table 6-1, entry 0), the alias of the flash's first bytes, and with mtvec = this
// table | 3 (ch32v003_boot.c) the PFIC takes an interrupt's or exception's entry from the word at 4 * its number: 2
// NMI, 3 HardFault (every exception), 12 SysTick, 14 SW, 16-38 the peripherals (RM 6.3, QKV2 2.2 and 3). So
//
//   entries 0-1  the instructions the core starts with, at the alias: lui and jalr, an absolute jump to chipsharp_reset
//                (riscv32_startup.S) at the address it is linked at, in the flash itself, 0x08000000 and up, from
//                where the program runs (its PC-relative addresses, gp's and sp's among them, are right only there).
//                Neither compressed nor relaxed; no interrupt has the number 1, so the second word is free
//   entries 2-38 chipsharp_trap's address (riscv32_trap.S), the reserved numbers' too: every interrupt and exception
//                goes to the trap entry, which dispatches by mcause (riscv32_vectors.c: chipsharp_isr_irq<n>, the
//                failure stop for one nothing handles)
//
// 39 words: the linker script checks the section is exactly the size "link.head" says.

    .section .ch32v003_vectors, "ax", @progbits
    .global ch32v003_vector_table
    .type ch32v003_vector_table, @object
    .balign 4
ch32v003_vector_table:
    .option push
    .option norvc
    .option norelax
    lui t0, %hi(chipsharp_reset)
    jalr zero, %lo(chipsharp_reset)(t0)
    .option pop
    .rept 37
    .word chipsharp_trap
    .endr
    .size ch32v003_vector_table, . - ch32v003_vector_table
