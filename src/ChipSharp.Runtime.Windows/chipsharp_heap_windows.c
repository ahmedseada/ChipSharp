// ChipSharp runtime for Windows x64 programs (target "host"): what the heap (chipsharp_heap.c) needs from Windows.
//
//   chipsharp_heap_memory         the heap: the target's "heap" bytes (chipsharp_heap_size, which chipsharp_heap_size.c
//                                 holds), taken from Windows once, at the start, with VirtualAlloc; Windows gives the
//                                 pages zeroed, and only when the program first touches them
//   chipsharp_scan_stack          stores the callee-saved registers (rbx, rbp, rdi, rsi, r12-r15) on the stack, then
//                                 scans from there to the top of the thread's stack (its StackBase, from the TEB)
//   chipsharp_heap_lock, _unlock  nothing to do: a PC program has no interrupt handlers
//   chipsharp_heap_check_thread   nothing to check, for the same reason

typedef __UINTPTR_TYPE__ uintptr_t;
typedef unsigned long DWORD;

#define MEM_COMMIT 0x00001000u
#define MEM_RESERVE 0x00002000u
#define PAGE_READWRITE 0x04u

__declspec(dllimport) void *__stdcall VirtualAlloc(void *lpAddress, uintptr_t dwSize, DWORD flAllocationType,
                                                   DWORD flProtect);
__declspec(noreturn) void ChipSharp_Runtime_OutOfMemory(void);

// In chipsharp_heap_size.c, which the runtime writes for the target.
extern const unsigned long long chipsharp_heap_size;

void chipsharp_heap_memory(char **start, char **end)
{
    char *memory = (char *)VirtualAlloc(0, (uintptr_t)chipsharp_heap_size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (memory == 0)
    {
        ChipSharp_Runtime_OutOfMemory(); // Windows has not that much memory to give
    }

    *start = memory;
    *end = memory + chipsharp_heap_size;
}

void chipsharp_scan_stack(void (*scan)(uintptr_t *from, uintptr_t *to))
{
    uintptr_t registers[8];
    uintptr_t *top;
    __asm__ volatile(
        "movq %%rbx, 0(%0)\n\t"
        "movq %%rbp, 8(%0)\n\t"
        "movq %%rdi, 16(%0)\n\t"
        "movq %%rsi, 24(%0)\n\t"
        "movq %%r12, 32(%0)\n\t"
        "movq %%r13, 40(%0)\n\t"
        "movq %%r14, 48(%0)\n\t"
        "movq %%r15, 56(%0)"
        :
        : "r"(registers)
        : "memory");
    __asm__ volatile("movq %%gs:8, %0" : "=r"(top)); // NT_TIB.StackBase: the top of this thread's stack
    scan(registers, top);

    // the registers stay in this frame until scan is done: no tail call may pop it first
    __asm__ volatile("" : : "r"(registers) : "memory");
}

unsigned int chipsharp_heap_lock(void)
{
    return 0;
}

void chipsharp_heap_unlock(unsigned int state)
{
    (void)state;
}

void chipsharp_heap_check_thread(void)
{
}
