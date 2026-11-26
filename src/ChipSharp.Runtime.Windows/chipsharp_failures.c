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
//
// The ones besides IndexOutOfRange are the methods of ChipSharp.Library.Runtime, which says when each happens.

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

// Writes the message to standard error (as much of it as can be written), then ends the program.
__declspec(noreturn) static void fail(const char *message, DWORD count)
{
    HANDLE error = GetStdHandle(STD_ERROR_HANDLE);
    while (count > 0)
    {
        DWORD written = 0;
        if (!WriteFile(error, message, count, &written, 0) || written == 0)
        {
            break;
        }

        message += written;
        count -= written;
    }

    ExitProcess(1);
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
