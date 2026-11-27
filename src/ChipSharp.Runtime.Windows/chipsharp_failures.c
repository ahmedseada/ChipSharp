// ChipSharp runtime for Windows x64 programs (target "host"): what happens when a program does something .NET
// throws an exception for.
//
// ChipSharp has no exceptions yet, so the program says what happened the way .NET reports an exception that
// nothing catches (on standard error), and exits with exit code 1.
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
//   ChipSharp_Runtime_ArgumentOutOfRange a span's start or length outside it (ArgumentOutOfRangeException)
//   ChipSharp_Runtime_LengthOutOfRange   MemoryMarshal.Read and Write on too few bytes (ArgumentOutOfRangeException)
//   ChipSharp_Runtime_DestinationTooShort  a span's CopyTo a shorter one (ArgumentException)
//   ChipSharp_Runtime_DataMisaligned     MemoryMarshal.Cast on a chip without unaligned access (DataMisalignedException)
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
//
// IndexOutOfRange, NullReference, OutOfMemory, Overflow, the span ones and the array ones are the code generator's and
// the heap's (chipsharp_heap.c), InvalidCast the program's casts' (ChipSharp.Object.CastClass and CastInterface); the
// others are the methods of ChipSharp.Library.Runtime, which says when each happens (Array.Sort and Array.BinarySearch,
// ChipSharp.Library's, call four of the array ones that way too).

#define UNHANDLED "Unhandled exception. "
#define TIMESPAN_TOO_LONG "TimeSpan overflowed because the duration is too long."
#define TIMESPAN_NAN "TimeSpan does not accept floating point Not-a-Number values."

typedef void *HANDLE;
typedef unsigned long DWORD;
typedef int BOOL;

#define STD_ERROR_HANDLE ((DWORD)-12)

__declspec(dllimport) HANDLE __stdcall GetStdHandle(DWORD nStdHandle);
__declspec(dllimport) BOOL __stdcall WriteFile(HANDLE hFile, const void *lpBuffer, DWORD nNumberOfBytesToWrite,
                                               DWORD *lpNumberOfBytesWritten, void *lpOverlapped);
__declspec(dllimport) __declspec(noreturn) void __stdcall ExitProcess(unsigned int uExitCode);

// Writes text to standard error, as much of it as can be written.
static void write_error(const char *text, DWORD count)
{
    HANDLE error = GetStdHandle(STD_ERROR_HANDLE);
    while (count > 0)
    {
        DWORD written = 0;
        if (!WriteFile(error, text, count, &written, 0) || written == 0)
        {
            break;
        }

        text += written;
        count -= written;
    }
}

// Writes the message to standard error (as much of it as can be written), then ends the program.
__declspec(noreturn) static void fail(const char *message, DWORD count)
{
    write_error(message, count);
    ExitProcess(1);
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

__declspec(noreturn) void ChipSharp_Runtime_IndexOutOfRange(void)
{
    static const char message[] =
        "Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds of the array.\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_TimeSpanOverflow(void)
{
    static const char message[] = UNHANDLED "System.OverflowException: " TIMESPAN_TOO_LONG "\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_TimeSpanOutOfRange(void)
{
    static const char message[] = UNHANDLED "System.ArgumentOutOfRangeException: " TIMESPAN_TOO_LONG "\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_TimeSpanNaN(void)
{
    static const char message[] = UNHANDLED "System.ArgumentException: " TIMESPAN_NAN "\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_TimeSpanNaNFactor(void)
{
    static const char message[] = UNHANDLED "System.ArgumentException: " TIMESPAN_NAN " (Parameter 'factor')\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_TimeSpanNaNDivisor(void)
{
    static const char message[] = UNHANDLED "System.ArgumentException: " TIMESPAN_NAN " (Parameter 'divisor')\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_TimeSpanDurationOverflow(void)
{
    static const char message[] = UNHANDLED "System.OverflowException: The duration cannot be returned for "
        "TimeSpan.MinValue because the absolute value of TimeSpan.MinValue exceeds the value of TimeSpan.MaxValue.\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_NegateOverflow(void)
{
    static const char message[] =
        UNHANDLED "System.OverflowException: Negating the minimum value of a twos complement number is invalid.\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_NullReference(void)
{
    static const char message[] =
        UNHANDLED "System.NullReferenceException: Object reference not set to an instance of an object.\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_OutOfMemory(void)
{
    static const char message[] =
        UNHANDLED "System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_Overflow(void)
{
    static const char message[] = UNHANDLED "System.OverflowException: Arithmetic operation resulted in an overflow.\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_ArgumentOutOfRange(void)
{
    static const char message[] =
        UNHANDLED "System.ArgumentOutOfRangeException: Specified argument was out of the range of valid values.\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_LengthOutOfRange(void)
{
    static const char message[] = UNHANDLED "System.ArgumentOutOfRangeException: Specified argument was out of the "
        "range of valid values. (Parameter 'length')\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_DestinationTooShort(void)
{
    static const char message[] =
        UNHANDLED "System.ArgumentException: Destination is too short. (Parameter 'destination')\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

// Any other failure, its exception and message given (from chipsharp_string.c and chipsharp_string_heap.c), e.g.
// "System.FormatException: The input string 'x' was not in a correct format."; at most 900 bytes of it are written.
__declspec(noreturn) void chipsharp_fail(const char *message)
{
    static char text[sizeof UNHANDLED + 900 + 2];
    DWORD count = 0;
    for (const char *from = UNHANDLED; *from != 0; from++)
    {
        text[count++] = *from;
    }

    for (; *message != 0 && count < sizeof UNHANDLED - 1 + 900; message++)
    {
        text[count++] = *message;
    }

    text[count++] = '\r';
    text[count++] = '\n';
    fail(text, count);
}

// A PC reads values at any address, so MemoryMarshal.Cast never calls this there ("unalignedAccess": true); it is here
// for every runtime to have each failure function.
__declspec(noreturn) void ChipSharp_Runtime_DataMisaligned(void)
{
    static const char message[] =
        UNHANDLED "System.DataMisalignedException: A datatype misalignment was detected in a load or store instruction.\r\n";
    fail(message, (DWORD)(sizeof message - 1));
}

__declspec(noreturn) void ChipSharp_Runtime_InvalidCast(const char *from, const char *to)
{
    static const char start[] = UNHANDLED "System.InvalidCastException: Unable to cast object of type '";
    static const char middle[] = "' to type '";
    static const char end[] = "'.\r\n";
    from = cast_name(from);
    to = cast_name(to);
    write_error(start, (DWORD)(sizeof start - 1));
    write_error(from, length(from));
    write_error(middle, (DWORD)(sizeof middle - 1));
    write_error(to, length(to));
    fail(end, (DWORD)(sizeof end - 1));
}

// IComparable's CompareTo of a boxed enum given a box of another type (ArgumentException), with both types' names.
__declspec(noreturn) void ChipSharp_Runtime_EnumCompareType(const char *passed, const char *type)
{
    static const char start[] = UNHANDLED "System.ArgumentException: Object must be the same type as the enum. The type "
        "passed in was '";
    static const char middle[] = "'; the enum type was '";
    static const char end[] = "'.\r\n";
    write_error(start, (DWORD)(sizeof start - 1));
    write_error(passed, length(passed));
    write_error(middle, (DWORD)(sizeof middle - 1));
    write_error(type, length(type));
    fail(end, (DWORD)(sizeof end - 1));
}

typedef DWORD COUNT;

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

__declspec(noreturn) void ChipSharp_Runtime_ArrayNull(void) { FAIL_WITH(ARGUMENT_NULL("array")); }
__declspec(noreturn) void ChipSharp_Runtime_SourceArrayNull(void) { FAIL_WITH(ARGUMENT_NULL("sourceArray")); }
__declspec(noreturn) void ChipSharp_Runtime_DestinationArrayNull(void) { FAIL_WITH(ARGUMENT_NULL("destinationArray")); }

// the delegates the methods of System.Array take, and Delegate.Combine and Remove of two types
__declspec(noreturn) void ChipSharp_Runtime_MatchNull(void) { FAIL_WITH(ARGUMENT_NULL("match")); }
__declspec(noreturn) void ChipSharp_Runtime_ActionNull(void) { FAIL_WITH(ARGUMENT_NULL("action")); }
__declspec(noreturn) void ChipSharp_Runtime_ConverterNull(void) { FAIL_WITH(ARGUMENT_NULL("converter")); }
__declspec(noreturn) void ChipSharp_Runtime_ComparisonNull(void) { FAIL_WITH(ARGUMENT_NULL("comparison")); }
__declspec(noreturn) void ChipSharp_Runtime_DelegateTypes(void)
{
    FAIL_WITH(UNHANDLED "System.ArgumentException: Delegates must be of the same type.\r\n");
}

__declspec(noreturn) void ChipSharp_Runtime_SourceArrayTooShort(void)
{
    FAIL_WITH(UNHANDLED "System.ArgumentException: Source array was not long enough. Check the source index, length, "
        "and the array's lower bounds. (Parameter 'sourceArray')\r\n");
}

__declspec(noreturn) void ChipSharp_Runtime_DestinationArrayTooShort(void)
{
    FAIL_WITH(UNHANDLED "System.ArgumentException: Destination array was not long enough. Check the destination index, "
        "length, and the array's lower bounds. (Parameter 'destinationArray')\r\n");
}

__declspec(noreturn) void ChipSharp_Runtime_HugeSourceIndex(void) { FAIL_WITH(HUGE_ARRAY("sourceIndex")); }
__declspec(noreturn) void ChipSharp_Runtime_HugeDestinationIndex(void) { FAIL_WITH(HUGE_ARRAY("destinationIndex")); }
__declspec(noreturn) void ChipSharp_Runtime_HugeLength(void) { FAIL_WITH(HUGE_ARRAY("length")); }
__declspec(noreturn) void ChipSharp_Runtime_HugeIndex(void) { FAIL_WITH(HUGE_ARRAY("index")); }

__declspec(noreturn) void ChipSharp_Runtime_StartIndexPastEnd(void)
{
    FAIL_WITH(OUT_OF_RANGE("Index was out of range. Must be non-negative and less than or equal to the size of the "
        "collection. (Parameter 'startIndex')"));
}

__declspec(noreturn) void ChipSharp_Runtime_StartIndexAtEnd(void)
{
    FAIL_WITH(OUT_OF_RANGE("Index was out of range. Must be non-negative and less than the size of the collection. "
        "(Parameter 'startIndex')"));
}

__declspec(noreturn) void ChipSharp_Runtime_CountOutOfRange(void)
{
    FAIL_WITH(OUT_OF_RANGE("Count must be positive and count must refer to a location within the string/array/"
        "collection. (Parameter 'count')"));
}

__declspec(noreturn) void ChipSharp_Runtime_NegativeIndex(void) { FAIL_WITH(NEGATIVE("index")); }
__declspec(noreturn) void ChipSharp_Runtime_NegativeLength(void) { FAIL_WITH(NEGATIVE("length")); }
__declspec(noreturn) void ChipSharp_Runtime_NegativeNewSize(void) { FAIL_WITH(NEGATIVE("newSize")); }

__declspec(noreturn) void ChipSharp_Runtime_InvalidOffsetLength(void)
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
__declspec(noreturn) static void fail_below_zero(const char *name, const char *rule, int value)
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

__declspec(noreturn) void ChipSharp_Runtime_LengthBelowZero(int value)
{
    fail_below_zero("length", "must be a non-negative value.", value);
}

__declspec(noreturn) void ChipSharp_Runtime_SourceIndexBelowZero(int value)
{
    fail_below_zero("sourceIndex", "must be greater than or equal to '0'.", value);
}

__declspec(noreturn) void ChipSharp_Runtime_DestinationIndexBelowZero(int value)
{
    fail_below_zero("destinationIndex", "must be greater than or equal to '0'.", value);
}

// ---- C#'s throw ---------------------------------------------------------------------------------------------------

// In chipsharp_exceptions.c: a string object as UTF-8 ending with a 0 byte.
const char *chipsharp_unhandled_text(const void *text);

// An exception nothing catches (ChipSharp.Library's Exceptions.Thrown): "Unhandled exception. " and its ToString().
__declspec(noreturn) void ChipSharp_Runtime_Unhandled(const void *text)
{
    chipsharp_fail(chipsharp_unhandled_text(text));
}