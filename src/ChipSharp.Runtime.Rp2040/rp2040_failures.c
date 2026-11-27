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
//   ChipSharp_Runtime_Overflow           an array of a negative length, a span of more bytes than an int counts
//                                        (OverflowException)
//   ChipSharp_Runtime_HeapInInterrupt    an interrupt handler that makes an object, which it never may: the heap may be
//                                        in the middle of an allocation or a collection when the interrupt comes
//   ChipSharp_Runtime_ArgumentOutOfRange a span's start or length outside it: a slice, new Span<T>(array, start, length)
//                                        (ArgumentOutOfRangeException)
//   ChipSharp_Runtime_LengthOutOfRange   MemoryMarshal.Read and Write (BinaryPrimitives too) on too few bytes
//                                        (ArgumentOutOfRangeException, parameter 'length')
//   ChipSharp_Runtime_DestinationTooShort  a span's CopyTo a shorter one (ArgumentException)
//   ChipSharp_Runtime_DataMisaligned     MemoryMarshal.Cast to elements this chip cannot read where the span starts
//                                        (DataMisalignedException)
//   ChipSharp_Runtime_InvalidCast        a cast to a type the object is not of (InvalidCastException), with the names of
//                                        both types as their descriptors hold them (.NET's, e.g. Program+Node)
//
// IndexOutOfRange, NullReference, OutOfMemory, Overflow, HeapInInterrupt and the span ones are the code generator's
// and the heap's (chipsharp_heap.c, armv6m_heap.c), InvalidCast the program's casts' (ChipSharp.Object.CastClass and
// CastInterface); the others are the methods of ChipSharp.Library.Runtime, which says when each happens.

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

// The number of bytes of text that ends with a 0 byte, without it.
static unsigned int length(const char *text)
{
    unsigned int count = 0;
    while (text[count] != 0)
    {
        count++;
    }

    return count;
}

// A type's name as .NET's InvalidCastException message writes it: a nested type by its own name (Program+Node is
// Node), any other by its full name; a '+' between the brackets of a generic type's arguments stays.
static const char *cast_name(const char *name)
{
    const char *start = name;
    int depth = 0;
    for (const char *c = name; *c != 0; c++)
    {
        if (*c == '[')
        {
            depth++;
        }
        else if (*c == ']')
        {
            depth--;
        }
        else if (*c == '+' && depth == 0)
        {
            start = c + 1;
        }
    }

    return start;
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

__attribute__((noreturn)) void ChipSharp_Runtime_ArgumentOutOfRange(void)
{
    static const char message[] =
        UNHANDLED "System.ArgumentOutOfRangeException: Specified argument was out of the range of valid values.\r\n";
    fail(message, sizeof message - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_LengthOutOfRange(void)
{
    static const char message[] = UNHANDLED "System.ArgumentOutOfRangeException: Specified argument was out of the "
        "range of valid values. (Parameter 'length')\r\n";
    fail(message, sizeof message - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_DestinationTooShort(void)
{
    static const char message[] =
        UNHANDLED "System.ArgumentException: Destination is too short. (Parameter 'destination')\r\n";
    fail(message, sizeof message - 1);
}

// The RP2040's Cortex-M0+ cannot read a word at an address that is not a multiple of its size, and MemoryMarshal.Cast
// made a span whose elements are there: said with the message of .NET's exception for a misaligned read.
__attribute__((noreturn)) void ChipSharp_Runtime_DataMisaligned(void)
{
    static const char message[] =
        UNHANDLED "System.DataMisalignedException: A datatype misalignment was detected in a load or store instruction.\r\n";
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

__attribute__((noreturn)) void ChipSharp_Runtime_InvalidCast(const char *from, const char *to)
{
    static const char start[] = UNHANDLED "System.InvalidCastException: Unable to cast object of type '";
    static const char middle[] = "' to type '";
    static const char end[] = "'.\r\n";
    from = cast_name(from);
    to = cast_name(to);
    chipsharp_uart0_write(start, sizeof start - 1);
    chipsharp_uart0_write(from, length(from));
    chipsharp_uart0_write(middle, sizeof middle - 1);
    chipsharp_uart0_write(to, length(to));
    fail(end, sizeof end - 1);
}

// Any other failure, its exception and message given (from chipsharp_string.c and chipsharp_string_heap.c), e.g.
// "System.FormatException: The input string 'x' was not in a correct format.".
__attribute__((noreturn)) void chipsharp_fail(const char *message)
{
    chipsharp_uart0_write(UNHANDLED, sizeof UNHANDLED - 1);
    chipsharp_uart0_write(message, length(message));
    fail("\r\n", 2);
}
