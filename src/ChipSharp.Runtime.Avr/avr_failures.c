// ChipSharp runtime for AVR chips: what happens when a program does something .NET throws an exception for.
//
// ChipSharp has no exceptions yet, so the program says what happened, the way .NET reports an exception that nothing
// catches ("Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds of the array."), and
// stops as a failure (chipsharp_halt_failure, avr_runtime.c): BREAK, so that a debugger or the simulator stops there,
// then interrupts off and SLEEP forever. Where the message goes is the chip runtime's:
//
//   chipsharp_failure_write   the hook: a piece of the message, count bytes of text in RAM. Empty here; a chip runtime
//                             with a console defines it and sends the bytes before it returns, polling (interrupts may
//                             be off, in an interrupt handler), and its chipsharp_before_halt waits for the last to go
//
// The hook always gets text in RAM. The messages are .NET's (the rp2040 runtime's, rp2040_failures.c, word for word),
// each in flash in a section of its own, so that a program has only those of the failures it can have: RAM is 2 KB on
// the ATmega328P, and a constant in RAM costs RAM (.rodata is copied there with .data); they reach the hook through a
// small buffer on the stack. So does the text the program gives (a type's name, a message): the code generator keeps
// its constants in flash and gives them as addresses with bit 15 set (0x8000 + the flash address; RAM's never have
// it), which this file reads with LPM. Every function is weak, so a chip runtime can also say a failure its own way.
//
//   ChipSharp_Runtime_IndexOutOfRange    an array index outside the array (IndexOutOfRangeException)
//   ChipSharp_Runtime_DivideByZero       a whole number divided by zero, x / 0 or x % 0 (DivideByZeroException)
//   ChipSharp_Runtime_TimeSpan...        a TimeSpan too long, or NaN given to one (ChipSharp.Library's System.TimeSpan)
//   ChipSharp_Runtime_NegateOverflow     the negative of the smallest value, e.g. -TimeSpan.MinValue
//   ChipSharp_Runtime_NullReference      a field, element or method of null (NullReferenceException)
//   ChipSharp_Runtime_OutOfMemory        an object that does not fit in the heap (OutOfMemoryException)
//   ChipSharp_Runtime_Overflow           an array of a negative length, int.MinValue / -1 (OverflowException)
//   ChipSharp_Runtime_HeapInInterrupt    an interrupt handler that makes an object, which it never may
//   ChipSharp_Runtime_ArgumentOutOfRange, LengthOutOfRange, DestinationTooShort, DataMisaligned
//                                        a span's start or length outside it, too few bytes for MemoryMarshal.Read or
//                                        Write, a CopyTo a shorter span, a MemoryMarshal.Cast this chip cannot read
//   ChipSharp_Runtime_Unhandled          C#'s throw: an exception nothing catches, with its ToString() (UTF-8, from
//                                        chipsharp_exceptions.c when the program has it)
//   ChipSharp_Runtime_InvalidCast        a cast to a type the object is not of (InvalidCastException), with the names
//                                        of both types
//   ChipSharp_Runtime_EnumCompareType    IComparable's CompareTo of a boxed enum and a box of another type
//   ChipSharp_Runtime_ArrayNull, ...     the arguments of the methods of System.Array (Array.Copy, Fill, IndexOf, ...):
//                                        null, an index or a length outside the array, a destination too short; the
//                                        ...BelowZero ones say the value too, as .NET does
//   ChipSharp_Runtime_ArithmeticNaN, ...MinMax..., RoundingDigits, InvalidMidpointRounding
//                                        the methods of System.Math: Sign of NaN, Clamp given a min above its max (the
//                                        floats as text when the program has chipsharp_float_text.c), Round given too
//                                        many digits or a MidpointRounding that is none of its five
//   chipsharp_fail                       any other failure, its exception and message given
//   ChipSharp_Runtime_StackOverflow, chipsharp_stack_overflow, chipsharp_fatal   below
//
// A C# int is 32 bits: a long in AVR's C, whose int has 16. A double is 64 bits: this file, like the whole runtime, is
// compiled with -mdouble=64.

typedef long chipsharp_int;
typedef unsigned int count_t;

__attribute__((noreturn)) void chipsharp_halt_failure(void);

// In chipsharp_exceptions.c and chipsharp_float_text.c, when the program has them (weak: 0 when it does not)
__attribute__((weak)) const char *chipsharp_unhandled_text(const void *text);
__attribute__((weak)) int chipsharp_float64_text(unsigned long long bits, char *text);
__attribute__((weak)) int chipsharp_float32_text(unsigned long bits, char *text);

__attribute__((weak)) void chipsharp_failure_write(const char *text, count_t count)
{
    (void)text;
    (void)count;
}

// Text in flash, in a section of its own (LLVM puts every __flash constant of a file in one .progmem.data, which the
// linker would keep whole for any one of them)
#define TEXT_OF(name) #name
#define SECTION_NAME(number) ".progmem.data.chipsharp_failure" TEXT_OF(number)
#define FLASH(text)                                                                                                    \
    (__extension__({                                                                                                   \
        static const __flash char flash_text[] __attribute__((section(SECTION_NAME(__COUNTER__)))) = text;             \
        flash_text;                                                                                                    \
    }))

typedef const __flash char flash_char;

static void write_flash(flash_char *text)
{
    char buffer[16];
    count_t count = 0;
    for (char c = *text; c != 0; c = *++text)
    {
        buffer[count++] = c;
        if (count == sizeof buffer)
        {
            chipsharp_failure_write(buffer, count);
            count = 0;
        }
    }

    if (count > 0)
    {
        chipsharp_failure_write(buffer, count);
    }
}

// A byte of text the program gives: in flash when its address has bit 15 set, else in RAM.
static char text_char(const char *text)
{
    unsigned int address = (unsigned int)text;
    return (address & 0x8000u) != 0 ? *(flash_char *)(address & 0x7FFFu) : *text;
}

// Text the program gives, which ends with a 0 byte, in RAM or in flash.
static void write_text(const char *text)
{
    char buffer[16];
    count_t count = 0;
    for (char c = text_char(text); c != 0; c = text_char(++text))
    {
        buffer[count++] = c;
        if (count == sizeof buffer)
        {
            chipsharp_failure_write(buffer, count);
            count = 0;
        }
    }

    if (count > 0)
    {
        chipsharp_failure_write(buffer, count);
    }
}

// A whole number as .NET writes it: a '-' first when negative, then its digits.
static void write_int(chipsharp_int value)
{
    char digits[11];
    count_t n = sizeof digits;
    unsigned long magnitude = value < 0 ? 0ul - (unsigned long)value : (unsigned long)value;
    do
    {
        digits[--n] = (char)('0' + magnitude % 10u);
        magnitude /= 10u;
    } while (magnitude != 0);

    if (value < 0)
    {
        digits[--n] = '-';
    }

    chipsharp_failure_write(digits + n, sizeof digits - n);
}

static void write_int64(unsigned long long magnitude, int negative)
{
    char digits[21];
    count_t n = sizeof digits;
    do
    {
        digits[--n] = (char)('0' + magnitude % 10u);
        magnitude /= 10u;
    } while (magnitude != 0);

    if (negative)
    {
        digits[--n] = '-';
    }

    chipsharp_failure_write(digits + n, sizeof digits - n);
}

#define UNHANDLED "Unhandled exception. "

static void write_unhandled(void) { write_flash(FLASH(UNHANDLED)); }

static void write_line_end(void) { write_flash(FLASH("\r\n")); }

__attribute__((noreturn)) static void stop(void)
{
    write_line_end();
    chipsharp_halt_failure();
}

// "Unhandled exception. ", the exception and its message, the line's end, and the failure stop.
__attribute__((noreturn)) static void fail(flash_char *exception)
{
    write_unhandled();
    write_flash(exception);
    stop();
}

#define FAILURE(name, text)                                                                                            \
    __attribute__((weak, noreturn)) void name(void) { fail(FLASH(text)); }

#define TIMESPAN_TOO_LONG "TimeSpan overflowed because the duration is too long."
#define TIMESPAN_NAN "TimeSpan does not accept floating point Not-a-Number values."

FAILURE(ChipSharp_Runtime_IndexOutOfRange, "System.IndexOutOfRangeException: Index was outside the bounds of the array.")
FAILURE(ChipSharp_Runtime_DivideByZero, "System.DivideByZeroException: Attempted to divide by zero.")
FAILURE(ChipSharp_Runtime_TimeSpanOverflow, "System.OverflowException: " TIMESPAN_TOO_LONG)
FAILURE(ChipSharp_Runtime_TimeSpanOutOfRange, "System.ArgumentOutOfRangeException: " TIMESPAN_TOO_LONG)
FAILURE(ChipSharp_Runtime_TimeSpanNaN, "System.ArgumentException: " TIMESPAN_NAN)
FAILURE(ChipSharp_Runtime_TimeSpanNaNFactor, "System.ArgumentException: " TIMESPAN_NAN " (Parameter 'factor')")
FAILURE(ChipSharp_Runtime_TimeSpanNaNDivisor, "System.ArgumentException: " TIMESPAN_NAN " (Parameter 'divisor')")
FAILURE(ChipSharp_Runtime_TimeSpanDurationOverflow, "System.OverflowException: The duration cannot be returned for "
    "TimeSpan.MinValue because the absolute value of TimeSpan.MinValue exceeds the value of TimeSpan.MaxValue.")
FAILURE(ChipSharp_Runtime_NegateOverflow,
    "System.OverflowException: Negating the minimum value of a twos complement number is invalid.")
FAILURE(ChipSharp_Runtime_NullReference,
    "System.NullReferenceException: Object reference not set to an instance of an object.")
FAILURE(ChipSharp_Runtime_OutOfMemory,
    "System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.")
FAILURE(ChipSharp_Runtime_Overflow, "System.OverflowException: Arithmetic operation resulted in an overflow.")
FAILURE(ChipSharp_Runtime_ArgumentOutOfRange,
    "System.ArgumentOutOfRangeException: Specified argument was out of the range of valid values.")
FAILURE(ChipSharp_Runtime_LengthOutOfRange, "System.ArgumentOutOfRangeException: Specified argument was out of the "
    "range of valid values. (Parameter 'length')")
FAILURE(ChipSharp_Runtime_DestinationTooShort,
    "System.ArgumentException: Destination is too short. (Parameter 'destination')")
FAILURE(ChipSharp_Runtime_DataMisaligned,
    "System.DataMisalignedException: A datatype misalignment was detected in a load or store instruction.")
// .NET has no interrupt handlers, so no message of its own for this: it is said the way .NET says that something was
// done at a time it cannot be (InvalidOperationException)
FAILURE(ChipSharp_Runtime_HeapInInterrupt, "System.InvalidOperationException: An interrupt handler cannot create "
    "objects (new ...): the heap may be in the middle of an allocation or a collection when the interrupt comes. "
    "Create what the handler needs before, e.g. in a static field.")

// A type's name as .NET's InvalidCastException message writes it: a nested type by its own name (Program+Node is
// Node), any other by its full name; a '+' between the brackets of a generic type's arguments stays.
static const char *cast_name(const char *name)
{
    const char *start = name;
    int depth = 0;
    for (const char *c = name; text_char(c) != 0; c++)
    {
        if (text_char(c) == '[')
        {
            depth++;
        }
        else if (text_char(c) == ']')
        {
            depth--;
        }
        else if (text_char(c) == '+' && depth == 0)
        {
            start = c + 1;
        }
    }

    return start;
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_InvalidCast(const char *from, const char *to)
{
    write_unhandled();
    write_flash(FLASH("System.InvalidCastException: Unable to cast object of type '"));
    write_text(cast_name(from));
    write_flash(FLASH("' to type '"));
    write_text(cast_name(to));
    write_flash(FLASH("'."));
    stop();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_EnumCompareType(const char *passed, const char *type)
{
    write_unhandled();
    write_flash(FLASH("System.ArgumentException: Object must be the same type as the enum. The type passed in was '"));
    write_text(passed);
    write_flash(FLASH("'; the enum type was '"));
    write_text(type);
    write_flash(FLASH("'."));
    stop();
}

// Any other failure, its exception and message given (from chipsharp_string.c and chipsharp_exceptions.c), e.g.
// "System.FormatException: The input string 'x' was not in a correct format.".
__attribute__((weak, noreturn)) void chipsharp_fail(const char *message)
{
    write_unhandled();
    write_text(message);
    stop();
}

// ---- The methods of System.Array ----------------------------------------------------------------------------------

#define ARGUMENT_NULL(name) "System.ArgumentNullException: Value cannot be null. (Parameter '" name "')"
#define OUT_OF_RANGE(text) "System.ArgumentOutOfRangeException: " text
#define HUGE_ARRAY(name) OUT_OF_RANGE("Arrays larger than 2GB are not supported. (Parameter '" name "')")
#define NEGATIVE(name) OUT_OF_RANGE("Non-negative number required. (Parameter '" name "')")

FAILURE(ChipSharp_Runtime_ArrayNull, ARGUMENT_NULL("array"))
FAILURE(ChipSharp_Runtime_SourceArrayNull, ARGUMENT_NULL("sourceArray"))
FAILURE(ChipSharp_Runtime_DestinationArrayNull, ARGUMENT_NULL("destinationArray"))
// the delegates the methods of System.Array take, and Delegate.Combine and Remove of two types
FAILURE(ChipSharp_Runtime_MatchNull, ARGUMENT_NULL("match"))
FAILURE(ChipSharp_Runtime_ActionNull, ARGUMENT_NULL("action"))
FAILURE(ChipSharp_Runtime_ConverterNull, ARGUMENT_NULL("converter"))
FAILURE(ChipSharp_Runtime_ComparisonNull, ARGUMENT_NULL("comparison"))
FAILURE(ChipSharp_Runtime_DelegateTypes, "System.ArgumentException: Delegates must be of the same type.")
FAILURE(ChipSharp_Runtime_SourceArrayTooShort, "System.ArgumentException: Source array was not long enough. Check the "
    "source index, length, and the array's lower bounds. (Parameter 'sourceArray')")
FAILURE(ChipSharp_Runtime_DestinationArrayTooShort, "System.ArgumentException: Destination array was not long enough. "
    "Check the destination index, length, and the array's lower bounds. (Parameter 'destinationArray')")
FAILURE(ChipSharp_Runtime_HugeSourceIndex, HUGE_ARRAY("sourceIndex"))
FAILURE(ChipSharp_Runtime_HugeDestinationIndex, HUGE_ARRAY("destinationIndex"))
FAILURE(ChipSharp_Runtime_HugeLength, HUGE_ARRAY("length"))
FAILURE(ChipSharp_Runtime_HugeIndex, HUGE_ARRAY("index"))
FAILURE(ChipSharp_Runtime_StartIndexPastEnd, OUT_OF_RANGE("Index was out of range. Must be non-negative and less than "
    "or equal to the size of the collection. (Parameter 'startIndex')"))
FAILURE(ChipSharp_Runtime_StartIndexAtEnd, OUT_OF_RANGE("Index was out of range. Must be non-negative and less than "
    "the size of the collection. (Parameter 'startIndex')"))
FAILURE(ChipSharp_Runtime_CountOutOfRange, OUT_OF_RANGE("Count must be positive and count must refer to a location "
    "within the string/array/collection. (Parameter 'count')"))
FAILURE(ChipSharp_Runtime_NegativeIndex, NEGATIVE("index"))
FAILURE(ChipSharp_Runtime_NegativeLength, NEGATIVE("length"))
FAILURE(ChipSharp_Runtime_NegativeNewSize, NEGATIVE("newSize"))
FAILURE(ChipSharp_Runtime_InvalidOffsetLength, "System.ArgumentException: Offset and length were out of bounds for the "
    "array or count is greater than the number of elements from index to the end of the source collection.")

// The message of ArgumentOutOfRangeException.ThrowIfNegative and ThrowIfLessThan: "<name> ('<value>') <rule> (Parameter
// '<name>')", then on a line of its own "Actual value was <value>."
__attribute__((noreturn)) static void fail_below_zero(flash_char *name, flash_char *rule, chipsharp_int value)
{
    write_unhandled();
    write_flash(FLASH("System.ArgumentOutOfRangeException: "));
    write_flash(name);
    write_flash(FLASH(" ('"));
    write_int(value);
    write_flash(FLASH("') "));
    write_flash(rule);
    write_flash(FLASH(" (Parameter '"));
    write_flash(name);
    write_flash(FLASH("')\r\nActual value was "));
    write_int(value);
    write_flash(FLASH("."));
    stop();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_LengthBelowZero(chipsharp_int value)
{
    fail_below_zero(FLASH("length"), FLASH("must be a non-negative value."), value);
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_SourceIndexBelowZero(chipsharp_int value)
{
    fail_below_zero(FLASH("sourceIndex"), FLASH("must be greater than or equal to '0'."), value);
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_DestinationIndexBelowZero(chipsharp_int value)
{
    fail_below_zero(FLASH("destinationIndex"), FLASH("must be greater than or equal to '0'."), value);
}

// ---- The methods of System.Math -----------------------------------------------------------------------------------

FAILURE(ChipSharp_Runtime_ArithmeticNaN,
    "System.ArithmeticException: Function does not accept floating point Not-a-Number values.")

// Math.Clamp given a min above its max (ArgumentException): "'<min>' cannot be greater than <max>.", the two numbers
// as text, which write_min_max_start and write_min_max_end go around.
static void write_min_max_start(void)
{
    write_unhandled();
    write_flash(FLASH("System.ArgumentException: '"));
}

static void write_min_max_middle(void) { write_flash(FLASH("' cannot be greater than ")); }

__attribute__((noreturn)) static void min_max_end(void)
{
    write_flash(FLASH("."));
    stop();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_MinMaxInt64(long long min, long long max)
{
    write_min_max_start();
    write_int64(min < 0 ? 0ull - (unsigned long long)min : (unsigned long long)min, min < 0);
    write_min_max_middle();
    write_int64(max < 0 ? 0ull - (unsigned long long)max : (unsigned long long)max, max < 0);
    min_max_end();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_MinMaxUInt64(unsigned long long min, unsigned long long max)
{
    write_min_max_start();
    write_int64(min, 0);
    write_min_max_middle();
    write_int64(max, 0);
    min_max_end();
}

// A float as .NET writes it, when the program has chipsharp_float_text.c; else nothing
static void write_float64(double value)
{
    union { double value; unsigned long long bits; } number = {value};
    char text[40];
    if (chipsharp_float64_text)
    {
        chipsharp_failure_write(text, (count_t)chipsharp_float64_text(number.bits, text));
    }
}

static void write_float32(float value)
{
    union { float value; unsigned long bits; } number = {value};
    char text[40];
    if (chipsharp_float32_text)
    {
        chipsharp_failure_write(text, (count_t)chipsharp_float32_text(number.bits, text));
    }
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_MinMaxFloat64(double min, double max)
{
    write_min_max_start();
    write_float64(min);
    write_min_max_middle();
    write_float64(max);
    min_max_end();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_MinMaxFloat32(float min, float max)
{
    write_min_max_start();
    write_float32(min);
    write_min_max_middle();
    write_float32(max);
    min_max_end();
}

// Math.Round (maximum 15) and MathF.Round (6) given more digits (ArgumentOutOfRangeException).
__attribute__((weak, noreturn)) void ChipSharp_Runtime_RoundingDigits(chipsharp_int maximum)
{
    write_unhandled();
    write_flash(FLASH("System.ArgumentOutOfRangeException: Rounding digits must be between 0 and "));
    write_int(maximum);
    write_flash(FLASH(", inclusive. (Parameter 'digits')"));
    stop();
}

// Math.Round given a MidpointRounding that is none of its five (ArgumentException), by its number.
__attribute__((weak, noreturn)) void ChipSharp_Runtime_InvalidMidpointRounding(chipsharp_int mode)
{
    write_unhandled();
    write_flash(FLASH("System.ArgumentException: The value '"));
    write_int(mode);
    write_flash(FLASH("' is not valid for this usage of the type MidpointRounding. (Parameter 'mode')"));
    stop();
}

// ---- C#'s throw ---------------------------------------------------------------------------------------------------

// An exception nothing catches (ChipSharp.Library's Exceptions.Thrown): "Unhandled exception. " and its ToString(),
// which chipsharp_exceptions.c makes (a program with exceptions has it); without it, the first words alone.
__attribute__((weak, noreturn)) void ChipSharp_Runtime_Unhandled(const void *text)
{
    if (chipsharp_unhandled_text)
    {
        chipsharp_fail(chipsharp_unhandled_text(text));
    }

    write_flash(FLASH("Unhandled exception."));
    stop();
}

// ---- The stack, and fatal errors ----------------------------------------------------------------------------------
//
//   chipsharp_stack_overflow           a function that checks the stack found it below __chipsharp_stack_limit (the
//                                      code generator's check, LlvmStackChecks): it would soon run into the variables
//                                      or the heap. The stack pointer may be below the stack already, so this turns
//                                      interrupts off (nothing of the program runs again), puts the stack pointer back
//                                      at the end of RAM, and calls ChipSharp_Runtime_StackOverflow there
//   ChipSharp_Runtime_StackOverflow    says what .NET writes on standard error, its first line ("Stack overflow.";
//                                      .NET goes on with the methods that were running), and stops
//   chipsharp_fatal                    a fatal error, the whole line given (the heap verifier's, chipsharp_heap.c)

#if defined(__AVR_HAVE_JMP_CALL__)
#define XJMP "jmp"
#else
#define XJMP "rjmp"
#endif

__attribute__((naked, noreturn)) void chipsharp_stack_overflow(void)
{
    __asm__ volatile(
        "cli\n\t"
        "ldi r28, lo8(__chipsharp_stack_top - 1)\n\t"
        "ldi r29, hi8(__chipsharp_stack_top - 1)\n\t"
        "out 0x3e, r29\n\t"
        "out 0x3d, r28\n\t"
        "clr r1\n\t" XJMP " ChipSharp_Runtime_StackOverflow");
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_StackOverflow(void)
{
    write_flash(FLASH("Stack overflow."));
    stop();
}

__attribute__((weak, noreturn)) void chipsharp_fatal(const char *message)
{
    write_text(message);
    stop();
}
