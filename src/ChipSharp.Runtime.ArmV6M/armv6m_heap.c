// ChipSharp runtime for ARMv6-M chips (Cortex-M0 and M0+): what the heap (chipsharp_heap.c) needs from the chip.
//
//   chipsharp_heap_memory         the heap region of the linker script (__chipsharp_heap_start to _end), the
//                                 target's "heap" bytes between the variables and the stack
//   chipsharp_scan_stack          stores the callee-saved registers (r4-r11) on the stack, then scans from there to the
//                                 top of the stack (__chipsharp_stack_top): the program runs on the main stack
//   chipsharp_heap_lock, _unlock  interrupts off (PRIMASK) while the collector runs, and back as they were, so that no
//                                 handler changes a reference the collector is reading
//   chipsharp_heap_check_thread   stops the program when it runs in an interrupt handler (IPSR is not 0): a handler
//                                 may come in the middle of an allocation or a collection, so it never uses the heap
//
// The failures (ChipSharp_Runtime_OutOfMemory, _Overflow and _HeapInInterrupt) stop at a breakpoint
// (armv6m_failures.c), after the chip runtime says what happened on its console (rp2040_failures.c).

typedef __UINTPTR_TYPE__ uintptr_t;

extern char __chipsharp_heap_start[];
extern char __chipsharp_heap_end[];
extern uintptr_t __chipsharp_stack_top[];

__attribute__((noreturn)) void ChipSharp_Runtime_HeapInInterrupt(void);

void chipsharp_heap_memory(char **start, char **end)
{
    *start = __chipsharp_heap_start;
    *end = __chipsharp_heap_end;
}

void chipsharp_scan_stack(void (*scan)(uintptr_t *from, uintptr_t *to))
{
    uintptr_t registers[8];
    uintptr_t high; // r8-r11 reach memory through a low register
    __asm__ volatile(
        "str r4, [%1, #0]\n\t"
        "str r5, [%1, #4]\n\t"
        "str r6, [%1, #8]\n\t"
        "str r7, [%1, #12]\n\t"
        "mov %0, r8\n\t"
        "str %0, [%1, #16]\n\t"
        "mov %0, r9\n\t"
        "str %0, [%1, #20]\n\t"
        "mov %0, r10\n\t"
        "str %0, [%1, #24]\n\t"
        "mov %0, r11\n\t"
        "str %0, [%1, #28]"
        : "=&l"(high)
        : "l"(registers)
        : "memory");
    scan(registers, __chipsharp_stack_top);

    // the registers stay in this frame until scan is done: no tail call may pop it first
    __asm__ volatile("" : : "r"(registers) : "memory");
}

unsigned int chipsharp_heap_lock(void)
{
    unsigned int primask;
    __asm__ volatile("mrs %0, primask\n\tcpsid i" : "=r"(primask) : : "memory");
    return primask;
}

void chipsharp_heap_unlock(unsigned int primask)
{
    __asm__ volatile("msr primask, %0" : : "r"(primask) : "memory");
}

void chipsharp_heap_check_thread(void)
{
    unsigned int ipsr;
    __asm__ volatile("mrs %0, ipsr" : "=r"(ipsr));
    if (ipsr != 0)
    {
        ChipSharp_Runtime_HeapInInterrupt();
    }
}
