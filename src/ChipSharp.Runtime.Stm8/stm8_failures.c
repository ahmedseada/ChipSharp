// ChipSharp runtime for STM8 chips: what happens when a program does something .NET throws an exception for.
//
// ChipSharp has no exceptions on a chip without a heap, so the program says what happened, the way .NET reports an
// exception that nothing catches ("Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds
// of the array."), and stops as a failure (chipsharp_halt_failure, stm8_halt.c: break, then halt for good). Where the
// message goes is the chip runtime's: chipsharp_failure_write, which gets it a piece at a time (the STM8 reads flash
// and RAM at the same addresses, so the pieces are written where they are), sends each before it returns, polling
// (interrupts may be off, in a handler), and chipsharp_before_halt waits for the last to go.
//
// The messages are .NET's (the AVR runtime's, avr_failures.c, word for word). SDCC's linker takes a file whole, so each
// failure is a module of its own (Stm8Runtime makes one of each "//@module" part below, with the text before the first):
// a program has only the messages of the failures it can have, in 8 KB of flash. The parts:
//
//   text, number                         what the others write with: chipsharp_fail_with ("Unhandled exception. ", the
//                                        exception and its message, the line's end, the failure stop), and the text and
//                                        the numbers of the messages that say more
//   hardware                             chipsharp_hardware_failure, the hardware contract's: a pin, port, bus,
//                                        frequency or mode the chip does not have, with its numbers in the message
//   ChipSharp_Runtime_IndexOutOfRange,   one each: an array index outside the array, a division by zero, a TimeSpan too
//   ... DivideByZero, TimeSpan..., ...   long, an overflow, a span's start or length outside it, the arguments of
//                                        System.Array's and System.Math's methods (as avr_failures.c lists them)
//   chipsharp_fail                       any other failure, its exception and message given (chipsharp_string.c's)

#include "chipsharp_hardware.h"
#include "stm8_runtime.h"

// In the parts "text" and "number"
CHIPSHARP_NORETURN void chipsharp_fail_with(const char *exception);
void chipsharp_failure_text(const char *text);
void chipsharp_failure_number(i32 value);
void chipsharp_failure_unhandled(void);
CHIPSHARP_NORETURN void chipsharp_failure_stop(void);

// In the part "below_zero"
CHIPSHARP_NORETURN void chipsharp_fail_below_zero(const char *name, const char *rule, i32 value);

// In the chip runtime's chipsharp_float_text.c, when it has a console
int chipsharp_float32_text(u32 bits, char *text);

#define FAILURE(name, text)                                                                                            \
    CHIPSHARP_NORETURN void name(void)                                                                                 \
    {                                                                                                                  \
        chipsharp_fail_with(text);                                                                                     \
    }

#define TIMESPAN_TOO_LONG "TimeSpan overflowed because the duration is too long."
#define TIMESPAN_NAN "TimeSpan does not accept floating point Not-a-Number values."
#define ARGUMENT_NULL(name) "System.ArgumentNullException: Value cannot be null. (Parameter '" name "')"
#define OUT_OF_RANGE(text) "System.ArgumentOutOfRangeException: " text
#define HUGE_ARRAY(name) OUT_OF_RANGE("Arrays larger than 2GB are not supported. (Parameter '" name "')")
#define NEGATIVE(name) OUT_OF_RANGE("Non-negative number required. (Parameter '" name "')")

//@module text
void chipsharp_failure_text(const char *text)
{
    chipsharp_count count = 0;
    while (text[count] != 0)
    {
        count++;
    }

    chipsharp_failure_write(text, count);
}

void chipsharp_failure_unhandled(void)
{
    chipsharp_failure_text("Unhandled exception. ");
}

void chipsharp_failure_stop(void)
{
    chipsharp_failure_write("\r\n", 2);
    chipsharp_halt_failure();
}

void chipsharp_fail_with(const char *exception)
{
    chipsharp_failure_unhandled();
    chipsharp_failure_text(exception);
    chipsharp_failure_stop();
}

//@module number
// A whole number as .NET writes it: a '-' first when negative, then its digits.
void chipsharp_failure_number(i32 value)
{
    char digits[11];
    u8 n = sizeof digits;
    u32 magnitude = value < 0 ? 0ul - (u32)value : (u32)value;
    do
    {
        digits[--n] = (char)('0' + (u8)(magnitude % 10u));
        magnitude /= 10u;
    } while (magnitude != 0);

    if (value < 0)
    {
        digits[--n] = '-';
    }

    chipsharp_failure_write(digits + n, sizeof digits - n);
}

//@module hardware
// "Unhandled exception. " and text, with n0 to n3 in place of {0} to {3}; then the stop.
void chipsharp_hardware_failure(CHIPSHARP_MESSAGE_CHAR *text, chipsharp_i32 n0, chipsharp_i32 n1, chipsharp_i32 n2,
    chipsharp_i32 n3)
{
    chipsharp_failure_unhandled();
    chipsharp_count start = 0;
    chipsharp_count i = 0;
    for (; text[i] != 0; i++)
    {
        if (text[i] == '{' && text[i + 1] >= '0' && text[i + 1] <= '3' && text[i + 2] == '}')
        {
            chipsharp_failure_write(text + start, i - start);
            u8 which = (u8)(text[i + 1] - '0');
            chipsharp_failure_number(which == 0 ? n0 : which == 1 ? n1 : which == 2 ? n2 : n3);
            i += 2;
            start = i + 1;
        }
    }

    chipsharp_failure_write(text + start, i - start);
    chipsharp_failure_stop();
}

//@module index_out_of_range
FAILURE(ChipSharp_Runtime_IndexOutOfRange, "System.IndexOutOfRangeException: Index was outside the bounds of the array.")

//@module divide_by_zero
FAILURE(ChipSharp_Runtime_DivideByZero, "System.DivideByZeroException: Attempted to divide by zero.")

//@module timespan_overflow
FAILURE(ChipSharp_Runtime_TimeSpanOverflow, "System.OverflowException: " TIMESPAN_TOO_LONG)

//@module timespan_out_of_range
FAILURE(ChipSharp_Runtime_TimeSpanOutOfRange, "System.ArgumentOutOfRangeException: " TIMESPAN_TOO_LONG)

//@module timespan_nan
FAILURE(ChipSharp_Runtime_TimeSpanNaN, "System.ArgumentException: " TIMESPAN_NAN)

//@module timespan_nan_factor
FAILURE(ChipSharp_Runtime_TimeSpanNaNFactor, "System.ArgumentException: " TIMESPAN_NAN " (Parameter 'factor')")

//@module timespan_nan_divisor
FAILURE(ChipSharp_Runtime_TimeSpanNaNDivisor, "System.ArgumentException: " TIMESPAN_NAN " (Parameter 'divisor')")

//@module timespan_duration_overflow
FAILURE(ChipSharp_Runtime_TimeSpanDurationOverflow, "System.OverflowException: The duration cannot be returned for "
    "TimeSpan.MinValue because the absolute value of TimeSpan.MinValue exceeds the value of TimeSpan.MaxValue.")

//@module negate_overflow
FAILURE(ChipSharp_Runtime_NegateOverflow,
    "System.OverflowException: Negating the minimum value of a twos complement number is invalid.")

//@module null_reference
FAILURE(ChipSharp_Runtime_NullReference,
    "System.NullReferenceException: Object reference not set to an instance of an object.")

//@module out_of_memory
FAILURE(ChipSharp_Runtime_OutOfMemory,
    "System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.")

//@module overflow
FAILURE(ChipSharp_Runtime_Overflow, "System.OverflowException: Arithmetic operation resulted in an overflow.")

//@module argument_out_of_range
FAILURE(ChipSharp_Runtime_ArgumentOutOfRange,
    "System.ArgumentOutOfRangeException: Specified argument was out of the range of valid values.")

//@module length_out_of_range
FAILURE(ChipSharp_Runtime_LengthOutOfRange, "System.ArgumentOutOfRangeException: Specified argument was out of the "
    "range of valid values. (Parameter 'length')")

//@module destination_too_short
FAILURE(ChipSharp_Runtime_DestinationTooShort,
    "System.ArgumentException: Destination is too short. (Parameter 'destination')")

//@module data_misaligned
FAILURE(ChipSharp_Runtime_DataMisaligned,
    "System.DataMisalignedException: A datatype misalignment was detected in a load or store instruction.")

//@module heap_in_interrupt
FAILURE(ChipSharp_Runtime_HeapInInterrupt, "System.InvalidOperationException: An interrupt handler cannot create "
    "objects (new ...): the heap may be in the middle of an allocation or a collection when the interrupt comes. "
    "Create what the handler needs before, e.g. in a static field.")

//@module array_null
FAILURE(ChipSharp_Runtime_ArrayNull, ARGUMENT_NULL("array"))

//@module source_array_null
FAILURE(ChipSharp_Runtime_SourceArrayNull, ARGUMENT_NULL("sourceArray"))

//@module destination_array_null
FAILURE(ChipSharp_Runtime_DestinationArrayNull, ARGUMENT_NULL("destinationArray"))

//@module match_null
FAILURE(ChipSharp_Runtime_MatchNull, ARGUMENT_NULL("match"))

//@module action_null
FAILURE(ChipSharp_Runtime_ActionNull, ARGUMENT_NULL("action"))

//@module converter_null
FAILURE(ChipSharp_Runtime_ConverterNull, ARGUMENT_NULL("converter"))

//@module comparison_null
FAILURE(ChipSharp_Runtime_ComparisonNull, ARGUMENT_NULL("comparison"))

//@module delegate_types
FAILURE(ChipSharp_Runtime_DelegateTypes, "System.ArgumentException: Delegates must be of the same type.")

//@module source_array_too_short
FAILURE(ChipSharp_Runtime_SourceArrayTooShort, "System.ArgumentException: Source array was not long enough. Check the "
    "source index, length, and the array's lower bounds. (Parameter 'sourceArray')")

//@module destination_array_too_short
FAILURE(ChipSharp_Runtime_DestinationArrayTooShort, "System.ArgumentException: Destination array was not long enough. "
    "Check the destination index, length, and the array's lower bounds. (Parameter 'destinationArray')")

//@module huge_source_index
FAILURE(ChipSharp_Runtime_HugeSourceIndex, HUGE_ARRAY("sourceIndex"))

//@module huge_destination_index
FAILURE(ChipSharp_Runtime_HugeDestinationIndex, HUGE_ARRAY("destinationIndex"))

//@module huge_length
FAILURE(ChipSharp_Runtime_HugeLength, HUGE_ARRAY("length"))

//@module huge_index
FAILURE(ChipSharp_Runtime_HugeIndex, HUGE_ARRAY("index"))

//@module start_index_past_end
FAILURE(ChipSharp_Runtime_StartIndexPastEnd, OUT_OF_RANGE("Index was out of range. Must be non-negative and less than "
    "or equal to the size of the collection. (Parameter 'startIndex')"))

//@module start_index_at_end
FAILURE(ChipSharp_Runtime_StartIndexAtEnd, OUT_OF_RANGE("Index was out of range. Must be non-negative and less than "
    "the size of the collection. (Parameter 'startIndex')"))

//@module count_out_of_range
FAILURE(ChipSharp_Runtime_CountOutOfRange, OUT_OF_RANGE("Count must be positive and count must refer to a location "
    "within the string/array/collection. (Parameter 'count')"))

//@module negative_index
FAILURE(ChipSharp_Runtime_NegativeIndex, NEGATIVE("index"))

//@module negative_length
FAILURE(ChipSharp_Runtime_NegativeLength, NEGATIVE("length"))

//@module negative_new_size
FAILURE(ChipSharp_Runtime_NegativeNewSize, NEGATIVE("newSize"))

//@module invalid_offset_length
FAILURE(ChipSharp_Runtime_InvalidOffsetLength, "System.ArgumentException: Offset and length were out of bounds for the "
    "array or count is greater than the number of elements from index to the end of the source collection.")

//@module arithmetic_nan
FAILURE(ChipSharp_Runtime_ArithmeticNaN,
    "System.ArithmeticException: Function does not accept floating point Not-a-Number values.")

//@module below_zero
// The message of ArgumentOutOfRangeException.ThrowIfNegative and ThrowIfLessThan: "<name> ('<value>') <rule> (Parameter
// '<name>')", then on a line of its own "Actual value was <value>."
CHIPSHARP_NORETURN void chipsharp_fail_below_zero(const char *name, const char *rule, i32 value)
{
    chipsharp_failure_unhandled();
    chipsharp_failure_text("System.ArgumentOutOfRangeException: ");
    chipsharp_failure_text(name);
    chipsharp_failure_text(" ('");
    chipsharp_failure_number(value);
    chipsharp_failure_text("') ");
    chipsharp_failure_text(rule);
    chipsharp_failure_text(" (Parameter '");
    chipsharp_failure_text(name);
    chipsharp_failure_text("')\r\nActual value was ");
    chipsharp_failure_number(value);
    chipsharp_failure_text(".");
    chipsharp_failure_stop();
}

//@module length_below_zero
CHIPSHARP_NORETURN void ChipSharp_Runtime_LengthBelowZero(i32 value)
{
    chipsharp_fail_below_zero("length", "must be a non-negative value.", value);
}

//@module source_index_below_zero
CHIPSHARP_NORETURN void ChipSharp_Runtime_SourceIndexBelowZero(i32 value)
{
    chipsharp_fail_below_zero("sourceIndex", "must be greater than or equal to '0'.", value);
}

//@module destination_index_below_zero
CHIPSHARP_NORETURN void ChipSharp_Runtime_DestinationIndexBelowZero(i32 value)
{
    chipsharp_fail_below_zero("destinationIndex", "must be greater than or equal to '0'.", value);
}

//@module min_max_int64
// Math.Clamp given a min above its max (ArgumentException): "'<min>' cannot be greater than <max>."
static void write_int64(i64 value)
{
    char digits[20];
    u8 n = sizeof digits;
    u64 magnitude = value < 0 ? 0ull - (u64)value : (u64)value;
    do
    {
        digits[--n] = (char)('0' + (u8)(magnitude % 10u));
        magnitude /= 10u;
    } while (magnitude != 0);

    if (value < 0)
    {
        chipsharp_failure_write("-", 1);
    }

    chipsharp_failure_write(digits + n, sizeof digits - n);
}

CHIPSHARP_NORETURN void ChipSharp_Runtime_MinMaxInt64(i64 min, i64 max)
{
    chipsharp_failure_unhandled();
    chipsharp_failure_text("System.ArgumentException: '");
    write_int64(min);
    chipsharp_failure_text("' cannot be greater than ");
    write_int64(max);
    chipsharp_failure_text(".");
    chipsharp_failure_stop();
}

//@module min_max_uint64
static void write_uint64(u64 magnitude)
{
    char digits[20];
    u8 n = sizeof digits;
    do
    {
        digits[--n] = (char)('0' + (u8)(magnitude % 10u));
        magnitude /= 10u;
    } while (magnitude != 0);

    chipsharp_failure_write(digits + n, sizeof digits - n);
}

CHIPSHARP_NORETURN void ChipSharp_Runtime_MinMaxUInt64(u64 min, u64 max)
{
    chipsharp_failure_unhandled();
    chipsharp_failure_text("System.ArgumentException: '");
    write_uint64(min);
    chipsharp_failure_text("' cannot be greater than ");
    write_uint64(max);
    chipsharp_failure_text(".");
    chipsharp_failure_stop();
}

//@module min_max_float32
// The floats as .NET writes them (chipsharp_float_text.c, the chip runtime's with its console). A float's bits are
// taken with a union of the float and a u32: both are the same 4 bytes in the same order, whatever the order is.
static void write_float32(float value)
{
    union
    {
        float value;
        u32 bits;
    } number;
    char text[16];
    number.value = value;
    chipsharp_failure_write(text, (chipsharp_count)chipsharp_float32_text(number.bits, text));
}

CHIPSHARP_NORETURN void ChipSharp_Runtime_MinMaxFloat32(float min, float max)
{
    chipsharp_failure_unhandled();
    chipsharp_failure_text("System.ArgumentException: '");
    write_float32(min);
    chipsharp_failure_text("' cannot be greater than ");
    write_float32(max);
    chipsharp_failure_text(".");
    chipsharp_failure_stop();
}

//@module rounding_digits
// Math.Round (maximum 15) and MathF.Round (6) given more digits (ArgumentOutOfRangeException).
CHIPSHARP_NORETURN void ChipSharp_Runtime_RoundingDigits(i32 maximum)
{
    chipsharp_failure_unhandled();
    chipsharp_failure_text("System.ArgumentOutOfRangeException: Rounding digits must be between 0 and ");
    chipsharp_failure_number(maximum);
    chipsharp_failure_text(", inclusive. (Parameter 'digits')");
    chipsharp_failure_stop();
}

//@module invalid_midpoint_rounding
// Math.Round given a MidpointRounding that is none of its five (ArgumentException), by its number.
CHIPSHARP_NORETURN void ChipSharp_Runtime_InvalidMidpointRounding(i32 mode)
{
    chipsharp_failure_unhandled();
    chipsharp_failure_text("System.ArgumentException: The value '");
    chipsharp_failure_number(mode);
    chipsharp_failure_text("' is not valid for this usage of the type MidpointRounding. (Parameter 'mode')");
    chipsharp_failure_stop();
}

//@module unhandled
// C#'s throw, an exception nothing catches: on a chip without a heap there is no exception object to say more of.
CHIPSHARP_NORETURN void ChipSharp_Runtime_Unhandled(const void *text)
{
    (void)text;
    chipsharp_failure_text("Unhandled exception.");
    chipsharp_failure_stop();
}

//@module fail
// Any other failure, its exception and message given (from chipsharp_string.c), e.g. "System.FormatException: The input
// string 'x' was not in a correct format.".
CHIPSHARP_NORETURN void chipsharp_fail(const char *message)
{
    chipsharp_fail_with(message);
}
