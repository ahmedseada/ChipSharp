// ChipSharp runtime for the WCH CH32V003: what happens when a program does something .NET throws an exception for.
//
// ChipSharp has no exceptions yet, so the program says what happened on the console (USART1 on PD5,
// chipsharp_failure_write in ch32v003_usart.c: the same place as Console.WriteLine, so one terminal shows both), the
// way .NET reports an exception that nothing catches, and stops the failure stop (chipsharp_halt_failure,
// riscv32_failures.c: what the console still holds is sent, then EBREAK, where a debugger or the simulator stops; on the
// chip alone the trap entry answers it by sleeping for good). The messages are the ESP32-C5's (esp32c5_failures.c),
// word for word: they are .NET's. They are written in pieces, as they go, with no buffer of a whole message: the chip's
// stack has 512 bytes. A program has only the messages of the failures it can have (the linker drops the others).
//
//   ChipSharp_Runtime_IndexOutOfRange    an array index outside the array (IndexOutOfRangeException)
//   ChipSharp_Runtime_DivideByZero       a whole number divided by zero, x / 0 or x % 0 (DivideByZeroException)
//   ChipSharp_Runtime_TimeSpan...        a TimeSpan too long, or NaN given to one (ChipSharp.Library's System.TimeSpan:
//                                        OverflowException, ArgumentOutOfRangeException, ArgumentException)
//   ChipSharp_Runtime_NegateOverflow     the negative of the smallest value, e.g. -TimeSpan.MinValue (OverflowException)
//   ChipSharp_Runtime_NullReference      a field, element or method of null (NullReferenceException)
//   ChipSharp_Runtime_OutOfMemory        an object that does not fit in the heap, even after a collection
//                                        (OutOfMemoryException)
//   ChipSharp_Runtime_Overflow           an array of a negative length, a span of more bytes than an int counts, the
//                                        smallest value divided by -1 (int.MinValue / -1)
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
//   ChipSharp_Runtime_Unhandled          C#'s throw: an exception nothing catches, with its ToString() (as UTF-8, from
//                                        chipsharp_exceptions.c), as .NET prints it
//   ChipSharp_Runtime_InvalidCast        a cast to a type the object is not of (InvalidCastException), with the names of
//                                        both types as their descriptors hold them (.NET's, e.g. Program+Node)
//   ChipSharp_Runtime_EnumCompareType    IComparable's CompareTo of a boxed enum and a box of another type
//                                        (ArgumentException), with the names of both types
//   ChipSharp_Runtime_ArrayNull, ...     the arguments of the methods of System.Array (Array.Copy, Fill, IndexOf, ...):
//                                        null, an index or a length outside the array, a destination too short
//                                        (ArgumentNullException, ArgumentOutOfRangeException, ArgumentException); the
//                                        ...BelowZero ones say the value too, as .NET does
//   ChipSharp_Runtime_ArithmeticNaN,    the methods of System.Math: Sign of NaN (ArithmeticException), Clamp given a
//   ...MinMax..., RoundingDigits,       min above its max, with both (ArgumentException), Round given too many digits
//   InvalidMidpointRounding             (ArgumentOutOfRangeException) or a MidpointRounding that is none of its five
//   chipsharp_hardware_failure          a serial port, an analog input or a PWM output used the wrong way: a pin it
//                                        cannot use, one not set up first (ArgumentException,
//                                        ArgumentOutOfRangeException, InvalidOperationException, with what to do)
//
// IndexOutOfRange, NullReference, OutOfMemory, Overflow, HeapInInterrupt, the span ones and the array ones are the code
// generator's and the heap's (chipsharp_heap.c, riscv32_heap.c), InvalidCast the program's casts' (ChipSharp.Object.
// CastClass and CastInterface); the others are the methods of ChipSharp.Library.Runtime, which says when each happens
// (Array.Sort and Array.BinarySearch, ChipSharp.Library's, call four of the array ones that way too).

#include "chipsharp_hardware.h"

#define UNHANDLED "Unhandled exception. "
#define TIMESPAN_TOO_LONG "TimeSpan overflowed because the duration is too long."
#define TIMESPAN_NAN "TimeSpan does not accept floating point Not-a-Number values."

__attribute__((noreturn)) static void fail(const char *message, unsigned int count)
{
    chipsharp_failure_write(message, count);
    chipsharp_halt_failure();
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

__attribute__((noreturn)) void ChipSharp_Runtime_DivideByZero(void)
{
    static const char message[] = UNHANDLED "System.DivideByZeroException: Attempted to divide by zero.\r\n";
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

// The CH32V003 (RV32EC) may not read a word at an address that is not a multiple of its size, and MemoryMarshal.Cast
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
    chipsharp_failure_write(start, sizeof start - 1);
    chipsharp_failure_write(from, length(from));
    chipsharp_failure_write(middle, sizeof middle - 1);
    chipsharp_failure_write(to, length(to));
    fail(end, sizeof end - 1);
}

// IComparable's CompareTo of a boxed enum given a box of another type (ArgumentException), with both types' names.
__attribute__((noreturn)) void ChipSharp_Runtime_EnumCompareType(const char *passed, const char *type)
{
    static const char start[] = UNHANDLED "System.ArgumentException: Object must be the same type as the enum. The type "
        "passed in was '";
    static const char middle[] = "'; the enum type was '";
    static const char end[] = "'.\r\n";
    chipsharp_failure_write(start, sizeof start - 1);
    chipsharp_failure_write(passed, length(passed));
    chipsharp_failure_write(middle, sizeof middle - 1);
    chipsharp_failure_write(type, length(type));
    fail(end, sizeof end - 1);
}

// Any other failure, its exception and message given (from chipsharp_string.c and chipsharp_string_heap.c), e.g.
// "System.FormatException: The input string 'x' was not in a correct format.".
__attribute__((noreturn)) void chipsharp_fail(const char *message)
{
    chipsharp_failure_write(UNHANDLED, sizeof UNHANDLED - 1);
    chipsharp_failure_write(message, length(message));
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

// Writes text that ends with a 0 byte where the failures' messages go.
static void write(const char *text)
{
    chipsharp_failure_write(text, length(text));
}

// Writes an int as .NET writes it (digits, a '-' first when negative), with no division.
static void write_int(int value)
{
    static const unsigned int powers[] = {1000000000u, 100000000u, 10000000u, 1000000u, 100000u, 10000u, 1000u, 100u,
                                          10u, 1u};
    char digits[11];
    COUNT count = 0;
    unsigned int magnitude = value < 0 ? 0u - (unsigned int)value : (unsigned int)value;
    int started = 0;
    if (value < 0)
    {
        digits[count++] = '-';
    }

    for (int i = 0; i < 10; i++)
    {
        char digit = '0';
        while (magnitude >= powers[i])
        {
            magnitude -= powers[i];
            digit++;
        }

        if (digit != '0' || started || i == 9)
        {
            digits[count++] = digit;
            started = 1;
        }
    }

    chipsharp_failure_write(digits, count);
}

// The message of ArgumentOutOfRangeException.ThrowIfNegative and ThrowIfLessThan: "<name> ('<value>') <rule> (Parameter
// '<name>')", then on a line of its own "Actual value was <value>."
__attribute__((noreturn)) static void fail_below_zero(const char *name, const char *rule, int value)
{
    write(UNHANDLED "System.ArgumentOutOfRangeException: ");
    write(name);
    write(" ('");
    write_int(value);
    write("') ");
    write(rule);
    write(" (Parameter '");
    write(name);
    write("')\r\nActual value was ");
    write_int(value);
    fail(".\r\n", 3);
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


// ---- The methods of System.Math -----------------------------------------------------------------------------------

// In chipsharp_float_text.c: a double and a float as .NET writes them, the shortest text that reads back as the value.
int chipsharp_float64_text(unsigned long long bits, char *text);
int chipsharp_float32_text(unsigned int bits, char *text);

// Writes a whole number of 64 bits as .NET writes it (a '-' first when negative), with no division.
static void write_int64(unsigned long long magnitude, int negative)
{
    static const unsigned long long powers[] = {10000000000000000000ull, 1000000000000000000ull,
        100000000000000000ull, 10000000000000000ull, 1000000000000000ull, 100000000000000ull, 10000000000000ull,
        1000000000000ull, 100000000000ull, 10000000000ull, 1000000000ull, 100000000ull, 10000000ull, 1000000ull,
        100000ull, 10000ull, 1000ull, 100ull, 10ull, 1ull};
    char digits[21];
    COUNT count = 0;
    int started = 0;
    if (negative)
    {
        digits[count++] = '-';
    }

    for (int i = 0; i < 20; i++)
    {
        char digit = '0';
        while (magnitude >= powers[i])
        {
            magnitude -= powers[i];
            digit++;
        }

        if (digit != '0' || started || i == 19)
        {
            digits[count++] = digit;
            started = 1;
        }
    }

    chipsharp_failure_write(digits, count);
}

// Math.Clamp given a min above its max (ArgumentException): "'<min>' cannot be greater than <max>.": its start, then
// min, its middle, max and its end.
#define MIN_MAX_START UNHANDLED "System.ArgumentException: '"
#define MIN_MAX_MIDDLE "' cannot be greater than "
#define MIN_MAX_END ".\r\n"

__attribute__((noreturn)) void ChipSharp_Runtime_MinMaxInt64(long long min, long long max)
{
    write(MIN_MAX_START);
    write_int64(min < 0 ? 0ull - (unsigned long long)min : (unsigned long long)min, min < 0);
    write(MIN_MAX_MIDDLE);
    write_int64(max < 0 ? 0ull - (unsigned long long)max : (unsigned long long)max, max < 0);
    fail(MIN_MAX_END, sizeof MIN_MAX_END - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_MinMaxUInt64(unsigned long long min, unsigned long long max)
{
    write(MIN_MAX_START);
    write_int64(min, 0);
    write(MIN_MAX_MIDDLE);
    write_int64(max, 0);
    fail(MIN_MAX_END, sizeof MIN_MAX_END - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_MinMaxFloat64(double min, double max)
{
    union { double value; unsigned long long bits; } low = {min}, high = {max};
    char text[40];
    write(MIN_MAX_START);
    chipsharp_failure_write(text, (COUNT)chipsharp_float64_text(low.bits, text));
    write(MIN_MAX_MIDDLE);
    chipsharp_failure_write(text, (COUNT)chipsharp_float64_text(high.bits, text));
    fail(MIN_MAX_END, sizeof MIN_MAX_END - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_MinMaxFloat32(float min, float max)
{
    union { float value; unsigned int bits; } low = {min}, high = {max};
    char text[40];
    write(MIN_MAX_START);
    chipsharp_failure_write(text, (COUNT)chipsharp_float32_text(low.bits, text));
    write(MIN_MAX_MIDDLE);
    chipsharp_failure_write(text, (COUNT)chipsharp_float32_text(high.bits, text));
    fail(MIN_MAX_END, sizeof MIN_MAX_END - 1);
}

__attribute__((noreturn)) void ChipSharp_Runtime_ArithmeticNaN(void)
{
    FAIL_WITH(UNHANDLED "System.ArithmeticException: Function does not accept floating point Not-a-Number values.\r\n");
}

// Math.Round (maximum 15) and MathF.Round (6) given more digits (ArgumentOutOfRangeException).
__attribute__((noreturn)) void ChipSharp_Runtime_RoundingDigits(int maximum)
{
    static const char end[] = ", inclusive. (Parameter 'digits')\r\n";
    write(UNHANDLED "System.ArgumentOutOfRangeException: Rounding digits must be between 0 and ");
    write_int(maximum);
    fail(end, sizeof end - 1);
}

// Math.Round given a MidpointRounding that is none of its five (ArgumentException), by its number.
__attribute__((noreturn)) void ChipSharp_Runtime_InvalidMidpointRounding(int mode)
{
    static const char end[] = "' is not valid for this usage of the type MidpointRounding. (Parameter 'mode')\r\n";
    write(UNHANDLED "System.ArgumentException: The value '");
    write_int(mode);
    fail(end, sizeof end - 1);
}

// ---- C#'s throw ---------------------------------------------------------------------------------------------------

// In chipsharp_exceptions.c: a string object as UTF-8 ending with a 0 byte.
const char *chipsharp_unhandled_text(const void *text);

// An exception nothing catches (ChipSharp.Library's Exceptions.Thrown): "Unhandled exception. " and its ToString().
__attribute__((noreturn)) void ChipSharp_Runtime_Unhandled(const void *text)
{
    chipsharp_fail(chipsharp_unhandled_text(text));
}

// ---- What .NET does not throw an exception for --------------------------------------------------------------------

// The stack ran low (chipsharp_stack_overflow, riscv32_failures.c, calls this on a stack of its own, with interrupts
// off): what .NET writes on standard error, its first line (.NET goes on with the methods that were running).
__attribute__((noreturn)) void ChipSharp_Runtime_StackOverflow(void)
{
    static const char message[] = "Stack overflow.\r\n";
    fail(message, sizeof message - 1);
}

// A fatal error, the whole line given: the heap verifier's (chipsharp build --verify-heap, chipsharp_heap.c), e.g.
// "Fatal error. The heap is corrupted: ...".
__attribute__((noreturn)) void chipsharp_fatal(const char *message)
{
    chipsharp_failure_write(message, length(message));
    fail("\r\n", 2);
}

// ---- The chip's peripherals ---------------------------------------------------------------------------------------

// A pin, a serial port or a peripheral used the wrong way (ch32v003_pins.c, ch32v003_usart.c):
// "Unhandled exception. " and text, e.g. "System.ArgumentException: Pin {0} cannot be serial port 0's TX: ...", with the
// numbers n0 to n3 in place of {0} to {3}, as string.Format writes them.
__attribute__((noreturn)) void chipsharp_hardware_failure(const char *text, int n0, int n1, int n2, int n3)
{
    const int numbers[] = {n0, n1, n2, n3};
    chipsharp_failure_write(UNHANDLED, sizeof UNHANDLED - 1);
    while (*text != 0)
    {
        unsigned int plain = 0;
        while (text[plain] != 0 && !(text[plain] == '{' && text[plain + 1] >= '0' && text[plain + 1] <= '3'
            && text[plain + 2] == '}'))
        {
            plain++;
        }

        chipsharp_failure_write(text, plain);
        text += plain;
        if (*text == '{')
        {
            write_int(numbers[text[1] - '0']);
            text += 3;
        }
    }

    fail("\r\n", 2);
}
