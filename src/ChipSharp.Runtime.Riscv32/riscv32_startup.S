// ChipSharp runtime for 32-bit RISC-V chips (RV32, machine mode): the reset handler.
//
// chipsharp_reset is where the program starts: the ELF's entry point, where the chip's boot ROM jumps (the ESP32-C5's,
// once it has loaded the program into SRAM) or a debugger starts it. Nothing is set up: no stack, no global pointer, the
// trap vector the ROM's own. In this order, it
//
//   1. puts the global pointer in gp (__global_pointer$, the linker script's) and the stack pointer at the top of the
//      stack (__chipsharp_stack_top), 16-byte aligned as RISC-V's ABI wants it;
//   2. turns interrupts off (mstatus.MIE) and points mtvec at the runtime's trap entry (chipsharp_trap, riscv32_trap.S),
//      direct mode, so that an exception from here on is the runtime's;
//   3. calls chipsharp_chip_boot, the chip's first steps, before the variables are set up: what cannot wait, such as the
//      watchdogs the ESP32-C5's ROM leaves running (or a CLIC's mode in mtvec). Empty unless the chip's runtime has
//      one; it may use the stack, but no variable;
//   4. jumps to chipsharp_start (riscv32_runtime.c), which sets up .data and .bss and runs the program.
//
// Interrupts stay off until the chip's runtime turns them on (chipsharp_chip_init, once it has set up what may
// interrupt), so that nothing the ROM left enabled comes before the program's handlers are in place.

    .section .text.chipsharp_reset, "ax", @progbits
    .global chipsharp_reset
    .type chipsharp_reset, @function
    .balign 4
chipsharp_reset:
    // gp's own load must not be relaxed into an access relative to gp, which is not set yet
    .option push
    .option norelax
    la gp, __global_pointer$
    .option pop
    la sp, __chipsharp_stack_top
    andi sp, sp, -16
    csrci mstatus, 8            // MIE: interrupts off
    la t0, chipsharp_trap
    csrw mtvec, t0              // direct mode (bits 1-0 zero): every trap to chipsharp_trap
    call chipsharp_chip_boot
    tail chipsharp_start
    .size chipsharp_reset, . - chipsharp_reset

    // The chip's first steps: none, unless the chip's runtime defines chipsharp_chip_boot.
    .section .text.chipsharp_chip_boot, "ax", @progbits
    .weak chipsharp_chip_boot
    .type chipsharp_chip_boot, @function
    .balign 4
chipsharp_chip_boot:
    ret
    .size chipsharp_chip_boot, . - chipsharp_chip_boot
