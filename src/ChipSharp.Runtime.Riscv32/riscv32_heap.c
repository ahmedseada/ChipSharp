// ChipSharp runtime for 32-bit RISC-V chips (RV32, machine mode): what the heap (chipsharp_heap.c) needs from the chip,
// for a target with a heap (its "heap" capability).
//
//   chipsharp_heap_memory         the heap region of the linker script (__chipsharp_heap_start to _end), the
//                                 target's "heap" bytes between the variables and the stack
//   chipsharp_scan_stack          stores the callee-saved registers (s0-s11) on the stack, then scans from there to the
//                                 top of the stack (__chipsharp_stack_top): the program runs on that one stack, its
//                                 interrupts' handlers too
//   chipsharp_heap_lock, _unlock  interrupts off (mstatus.MIE), and back as they were: around the compare and store of an
//                                 event's add and remove (Interlocked.CompareExchange, see DelegateFunctions), so that
//                                 no handler stores the field in between. The collector itself runs with interrupts on
//                                 (see chipsharp_heap.c)
//   chipsharp_heap_check_thread   stops the program when it runs in an interrupt's handler (chipsharp_in_interrupt, which
//                                 the trap entry's dispatch counts, riscv32_vectors.c): a handler may come in the middle
//                                 of an allocation or a collection, so it never uses the heap
//
// The failures (ChipSharp_Runtime_OutOfMemory, _Overflow and _HeapInInterrupt) stop at the failure stop
// (riscv32_failures.c), after the chip runtime says what happened on its console.

typedef __UINTPTR_TYPE__ uintptr_t;

extern char __chipsharp_heap_start[];
extern char __chipsharp_heap_end[];
extern uintptr_t __chipsharp_stack_top[];

// riscv32_vectors.c: how many interrupt handlers are running (0 or 1: they do not nest)
extern volatile unsigned int chipsharp_in_interrupt;

__attribute__((noreturn)) void ChipSharp_Runtime_HeapInInterrupt(void);

void chipsharp_heap_memory(char **start, char **end)
{
    *start = __chipsharp_heap_start;
    *end = __chipsharp_heap_end;
}

void chipsharp_scan_stack(void (*scan)(uintptr_t *from, uintptr_t *to))
{
    uintptr_t registers[12];
    __asm__ volatile(
        "sw s0, 0(%0)\n\t"
        "sw s1, 4(%0)\n\t"
        "sw s2, 8(%0)\n\t"
        "sw s3, 12(%0)\n\t"
        "sw s4, 16(%0)\n\t"
        "sw s5, 20(%0)\n\t"
        "sw s6, 24(%0)\n\t"
        "sw s7, 28(%0)\n\t"
        "sw s8, 32(%0)\n\t"
        "sw s9, 36(%0)\n\t"
        "sw s10, 40(%0)\n\t"
        "sw s11, 44(%0)"
        :
        : "r"(registers)
        : "memory");
    scan(registers, __chipsharp_stack_top);

    // the registers stay in this frame until scan is done: no tail call may pop it first
    __asm__ volatile("" : : "r"(registers) : "memory");
}

unsigned int chipsharp_heap_lock(void)
{
    unsigned int mstatus;
    __asm__ volatile("csrrci %0, mstatus, 8" : "=r"(mstatus) : : "memory");
    return mstatus & 8;
}

void chipsharp_heap_unlock(unsigned int enabled)
{
    if (enabled != 0)
    {
        __asm__ volatile("csrsi mstatus, 8" : : : "memory");
    }
}

void chipsharp_heap_check_thread(void)
{
    if (chipsharp_in_interrupt != 0)
    {
        ChipSharp_Runtime_HeapInInterrupt();
    }
}
