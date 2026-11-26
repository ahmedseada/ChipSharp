// ChipSharp runtime for the Raspberry Pi RP2040: what happens when a program does something .NET throws an
// exception for.
//
// ChipSharp has no exceptions yet, so the program says what happened on UART0, the way .NET reports an exception
// that nothing catches, and stops the way a division by zero does (chipsharp_divide_by_zero): at a breakpoint, so
// that a debugger, or the simulator, stops there. Without a debugger the breakpoint becomes a HardFault, whose
// handler sleeps forever. UART0 sends what is still in its FIFO on its own.
//
//   ChipSharp_Runtime_IndexOutOfRange    an array index outside the array (IndexOutOfRangeException)

// In rp2040_runtime.c: Console.WriteLine's output, UART0.
void chipsharp_uart0_write(const char *bytes, unsigned int count);

__attribute__((noreturn)) static void fail(const char *message, unsigned int count)
{
    chipsharp_uart0_write(message, count);
    for (;;)
    {
        __asm__ volatile("bkpt #0\n\twfi");
    }
}

__attribute__((noreturn)) void ChipSharp_Runtime_IndexOutOfRange(void)
{
    static const char message[] =
        "Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds of the array.\r\n";
    fail(message, sizeof message - 1);
}
