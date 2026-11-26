// ChipSharp runtime for the Raspberry Pi RP2040: what happens when a program does something .NET throws an
// exception for.
//
// ChipSharp has no exceptions yet, so the program says what happened on UART0, the way .NET reports an exception
// that nothing catches, and stops the way a division by zero does (chipsharp_divide_by_zero): at a breakpoint, so
// that a debugger, or the simulator, stops there. Without a debugger the breakpoint becomes a HardFault, whose
// handler sleeps forever. UART0 sends what is still in its FIFO on its own.
//
//   ChipSharp_Runtime_IndexOutOfRange    an array index outside the array (IndexOutOfRangeException)
//   ChipSharp_Runtime_TimeSpan...        a TimeSpan too long, or NaN given to one (ChipSharp.Library's System.TimeSpan:
//                                        OverflowException, ArgumentOutOfRangeException, ArgumentException)
//   ChipSharp_Runtime_NegateOverflow     the negative of the smallest value, e.g. -TimeSpan.MinValue (OverflowException)
//   ChipSharp_Runtime_NullReference      a field, element or method of null (NullReferenceException)
//   ChipSharp_Runtime_OutOfMemory        an object that does not fit in the heap, even after a collection
//                                        (OutOfMemoryException)
//   ChipSharp_Runtime_Overflow           an array of a negative length (OverflowException)
//   ChipSharp_Runtime_HeapInInterrupt    an interrupt handler that makes an object, which it never may: the heap may be
//                                        in the middle of an allocation or a collection when the interrupt comes
//
// IndexOutOfRange, NullReference, OutOfMemory, Overflow and HeapInInterrupt are the code generator's and the heap's
// (chipsharp_heap.c, armv6m_heap.c); the others are the methods of ChipSharp.Library.Runtime, which says when each
// happens.

#define UNHANDLED "Unhandled exception. "
#define TIMESPAN_TOO_LONG "TimeSpan overflowed because the duration is too long."
#define TIMESPAN_NAN "TimeSpan does not accept floating point Not-a-Number values."

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

__attribute__((noreturn)) void ChipSharp_Runtime_TimeSpanOverflow(void)
{
    static const char message[] = UNHANDLED "System.OverflowException: " TIMESPAN_TOO_LONG "\r\n";
    fail(message, sizeof message - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_TimeSpanOutOfRange(void)
{
    static const char message[] = UNHANDLED "System.ArgumentOutOfRangeException: " TIMESPAN_TOO_LONG "\r\n";
    fail(message, sizeof message - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_TimeSpanNaN(void)
{
    static const char message[] = UNHANDLED "System.ArgumentException: " TIMESPAN_NAN "\r\n";
    fail(message, sizeof message - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_TimeSpanNaNFactor(void)
{
    static const char message[] = UNHANDLED "System.ArgumentException: " TIMESPAN_NAN " (Parameter 'factor')\r\n";
    fail(message, sizeof message - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_TimeSpanNaNDivisor(void)
{
    static const char message[] = UNHANDLED "System.ArgumentException: " TIMESPAN_NAN " (Parameter 'divisor')\r\n";
    fail(message, sizeof message - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_TimeSpanDurationOverflow(void)
{
    static const char message[] = UNHANDLED "System.OverflowException: The duration cannot be returned for "
        "TimeSpan.MinValue because the absolute value of TimeSpan.MinValue exceeds the value of TimeSpan.MaxValue.\r\n";
    fail(message, sizeof message - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_NegateOverflow(void)
{
    static const char message[] =
        UNHANDLED "System.OverflowException: Negating the minimum value of a twos complement number is invalid.\r\n";
    fail(message, sizeof message - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_NullReference(void)
{
    static const char message[] =
        UNHANDLED "System.NullReferenceException: Object reference not set to an instance of an object.\r\n";
    fail(message, sizeof message - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_OutOfMemory(void)
{
    static const char message[] =
        UNHANDLED "System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.\r\n";
    fail(message, sizeof message - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_Overflow(void)
{
    static const char message[] = UNHANDLED "System.OverflowException: Arithmetic operation resulted in an overflow.\r\n";
    fail(message, sizeof message - 1);
}

// .NET has no interrupt handlers, so no message of its own for this: it is said the way .NET says that something was
// done at a time it cannot be (InvalidOperationException).
__attribute__((noreturn)) void ChipSharp_Runtime_HeapInInterrupt(void)
{
    static const char message[] = UNHANDLED "System.InvalidOperationException: An interrupt handler cannot create "
        "objects (new ...): the heap may be in the middle of an allocation or a collection when the interrupt comes. "
        "Create what the handler needs before, e.g. in a static field.\r\n";
    fail(message, sizeof message - 1);
}
