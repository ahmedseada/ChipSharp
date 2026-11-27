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
//   ChipSharp_Runtime_EnumCompareType    IComparable's CompareTo of a boxed enum and a box of another type
//                                        (ArgumentException), with the names of both types
//   ChipSharp_Runtime_ArrayNull, ...     the arguments of the methods of System.Array (Array.Copy, Fill, IndexOf, ...):
//                                        null, an index or a length outside the array, a destination too short
//                                        (ArgumentNullException, ArgumentOutOfRangeException, ArgumentException); the
//                                        ...BelowZero ones say the value too, as .NET does
//
// IndexOutOfRange, NullReference, OutOfMemory, Overflow, HeapInInterrupt, the span ones and the array ones are the code
// generator's and the heap's (chipsharp_heap.c, armv6m_heap.c), InvalidCast the program's casts' (ChipSharp.Object.
// CastClass and CastInterface); the others are the methods of ChipSharp.Library.Runtime, which says when each happens
// (Array.Sort and Array.BinarySearch, ChipSharp.Library's, call four of the array ones that way too).

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

// IComparable's CompareTo of a boxed enum given a box of another type (ArgumentException), with both types' names.
__attribute__((noreturn)) void ChipSharp_Runtime_EnumCompareType(const char *passed, const char *type)
{
    static const char start[] = UNHANDLED "System.ArgumentException: Object must be the same type as the enum. The type "
        "passed in was '";
    static const char middle[] = "'; the enum type was '";
    static const char end[] = "'.\r\n";
    chipsharp_uart0_write(start, sizeof start - 1);
    chipsharp_uart0_write(passed, length(passed));
    chipsharp_uart0_write(middle, sizeof middle - 1);
    chipsharp_uart0_write(type, length(type));
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

typedef unsigned int COUNT;

// ---- The methods of System.Array ----------------------------------------------------------------------------------

#define ARGUMENT_NULL(name) UNHANDLED "System.ArgumentNullException: Value cannot be null. (Parameter '" name "')\r\n"
#define OUT_OF_RANGE(text) UNHANDLED "System.ArgumentOutOfRangeException: " text "\r\n"
#define HUGE_ARRAY(name) OUT_OF_RANGE("Arrays larger than 2GB are not supported. (Parameter '" name "')")
#define NEGATIVE(name) OUT_OF_RANGE("Non-negative number required. (Parameter '" name "')")
#define FAIL_WITH(text)                                                                                                \
    do                                                                                                                 \
    {                                                                                                                  \
        static const char message[] = text;                                                                            \
        fail(message, (COUNT)(sizeof message - 1));                                                                    \
    } while (0)

__attribute__((noreturn)) void ChipSharp_Runtime_ArrayNull(void) { FAIL_WITH(ARGUMENT_NULL("array")); }
__attribute__((noreturn)) void ChipSharp_Runtime_SourceArrayNull(void) { FAIL_WITH(ARGUMENT_NULL("sourceArray")); }

// the delegates the methods of System.Array take, and Delegate.Combine and Remove of two types
__attribute__((noreturn)) void ChipSharp_Runtime_MatchNull(void) { FAIL_WITH(ARGUMENT_NULL("match")); }
__attribute__((noreturn)) void ChipSharp_Runtime_ActionNull(void) { FAIL_WITH(ARGUMENT_NULL("action")); }
__attribute__((noreturn)) void ChipSharp_Runtime_ConverterNull(void) { FAIL_WITH(ARGUMENT_NULL("converter")); }
__attribute__((noreturn)) void ChipSharp_Runtime_ComparisonNull(void) { FAIL_WITH(ARGUMENT_NULL("comparison")); }
__attribute__((noreturn)) void ChipSharp_Runtime_DelegateTypes(void)
{
    FAIL_WITH(UNHANDLED "System.ArgumentException: Delegates must be of the same type.\r\n");
}

__attribute__((noreturn)) void ChipSharp_Runtime_DestinationArrayNull(void)
{
    FAIL_WITH(ARGUMENT_NULL("destinationArray"));
}

__attribute__((noreturn)) void ChipSharp_Runtime_SourceArrayTooShort(void)
{
    FAIL_WITH(UNHANDLED "System.ArgumentException: Source array was not long enough. Check the source index, length, "
        "and the array's lower bounds. (Parameter 'sourceArray')\r\n");
}

__attribute__((noreturn)) void ChipSharp_Runtime_DestinationArrayTooShort(void)
{
    FAIL_WITH(UNHANDLED "System.ArgumentException: Destination array was not long enough. Check the destination index, "
        "length, and the array's lower bounds. (Parameter 'destinationArray')\r\n");
}

__attribute__((noreturn)) void ChipSharp_Runtime_HugeSourceIndex(void) { FAIL_WITH(HUGE_ARRAY("sourceIndex")); }
__attribute__((noreturn)) void ChipSharp_Runtime_HugeDestinationIndex(void)
{
    FAIL_WITH(HUGE_ARRAY("destinationIndex"));
}

__attribute__((noreturn)) void ChipSharp_Runtime_HugeLength(void) { FAIL_WITH(HUGE_ARRAY("length")); }
__attribute__((noreturn)) void ChipSharp_Runtime_HugeIndex(void) { FAIL_WITH(HUGE_ARRAY("index")); }

__attribute__((noreturn)) void ChipSharp_Runtime_StartIndexPastEnd(void)
{
    FAIL_WITH(OUT_OF_RANGE("Index was out of range. Must be non-negative and less than or equal to the size of the "
        "collection. (Parameter 'startIndex')"));
}

__attribute__((noreturn)) void ChipSharp_Runtime_StartIndexAtEnd(void)
{
    FAIL_WITH(OUT_OF_RANGE("Index was out of range. Must be non-negative and less than the size of the collection. "
        "(Parameter 'startIndex')"));
}

__attribute__((noreturn)) void ChipSharp_Runtime_CountOutOfRange(void)
{
    FAIL_WITH(OUT_OF_RANGE("Count must be positive and count must refer to a location within the string/array/"
        "collection. (Parameter 'count')"));
}

__attribute__((noreturn)) void ChipSharp_Runtime_NegativeIndex(void) { FAIL_WITH(NEGATIVE("index")); }
__attribute__((noreturn)) void ChipSharp_Runtime_NegativeLength(void) { FAIL_WITH(NEGATIVE("length")); }
__attribute__((noreturn)) void ChipSharp_Runtime_NegativeNewSize(void) { FAIL_WITH(NEGATIVE("newSize")); }

__attribute__((noreturn)) void ChipSharp_Runtime_InvalidOffsetLength(void)
{
    FAIL_WITH(UNHANDLED "System.ArgumentException: Offset and length were out of bounds for the array or count is "
        "greater than the number of elements from index to the end of the source collection.\r\n");
}

// A message put together in a buffer of MESSAGE_SIZE bytes: appends text to it.
#define MESSAGE_SIZE 256
static void append(char *message, COUNT *count, const char *text)
{
    while (*text != 0 && *count < MESSAGE_SIZE)
    {
        message[(*count)++] = *text++;
    }
}

// Appends an int as .NET writes it (digits, a '-' first when negative), with no division.
static void append_int(char *message, COUNT *count, int value)
{
    static const unsigned int powers[] = {1000000000u, 100000000u, 10000000u, 1000000u, 100000u, 10000u, 1000u, 100u,
                                          10u, 1u};
    unsigned int magnitude = value < 0 ? 0u - (unsigned int)value : (unsigned int)value;
    int started = 0;
    if (value < 0)
    {
        append(message, count, "-");
    }

    for (int i = 0; i < 10; i++)
    {
        char digit[2] = {'0', 0};
        while (magnitude >= powers[i])
        {
            magnitude -= powers[i];
            digit[0]++;
        }

        if (digit[0] != '0' || started || i == 9)
        {
            append(message, count, digit);
            started = 1;
        }
    }
}

// The message of ArgumentOutOfRangeException.ThrowIfNegative and ThrowIfLessThan: "<name> ('<value>') <rule> (Parameter
// '<name>')", then on a line of its own "Actual value was <value>."
__attribute__((noreturn)) static void fail_below_zero(const char *name, const char *rule, int value)
{
    char message[MESSAGE_SIZE];
    COUNT count = 0;
    append(message, &count, UNHANDLED "System.ArgumentOutOfRangeException: ");
    append(message, &count, name);
    append(message, &count, " ('");
    append_int(message, &count, value);
    append(message, &count, "') ");
    append(message, &count, rule);
    append(message, &count, " (Parameter '");
    append(message, &count, name);
    append(message, &count, "')\r\nActual value was ");
    append_int(message, &count, value);
    append(message, &count, ".\r\n");
    fail(message, count);
}

__attribute__((noreturn)) void ChipSharp_Runtime_LengthBelowZero(int value)
{
    fail_below_zero("length", "must be a non-negative value.", value);
}

__attribute__((noreturn)) void ChipSharp_Runtime_SourceIndexBelowZero(int value)
{
    fail_below_zero("sourceIndex", "must be greater than or equal to '0'.", value);
}

__attribute__((noreturn)) void ChipSharp_Runtime_DestinationIndexBelowZero(int value)
{
    fail_below_zero("destinationIndex", "must be greater than or equal to '0'.", value);
}
