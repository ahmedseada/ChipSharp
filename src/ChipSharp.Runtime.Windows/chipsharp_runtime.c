// ChipSharp runtime for Windows x64 programs (target "host").
//
// Freestanding: no headers and no C runtime. The program talks to Windows through three kernel32 functions;
// the toolchain makes their import library from the target's "link.imports".
//
//   chipsharp_start                      the program's entry point: runs chipsharp_entry, exits with its result
//   ChipSharp_Runtime_WriteLine...       Console.WriteLine for each type, written like .NET on Windows ("\r\n")

typedef void *HANDLE;
typedef unsigned long DWORD;
typedef int BOOL;

#define STD_OUTPUT_HANDLE ((DWORD)-11)

__declspec(dllimport) HANDLE __stdcall GetStdHandle(DWORD nStdHandle);
__declspec(dllimport) BOOL __stdcall WriteFile(HANDLE hFile, const void *lpBuffer, DWORD nNumberOfBytesToWrite,
                                               DWORD *lpNumberOfBytesWritten, void *lpOverlapped);
__declspec(dllimport) __declspec(noreturn) void __stdcall ExitProcess(unsigned int uExitCode);

// Written by the code generator: calls the program's Main and returns its result (0 for a void Main).
int chipsharp_entry(void);

// The linker looks for this symbol when a program uses floating point; nothing reads it.
int _fltused = 0;

__declspec(noreturn) void chipsharp_start(void)
{
    ExitProcess((unsigned int)chipsharp_entry());
}

static void write_bytes(const char *bytes, DWORD count)
{
    HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
    while (count > 0)
    {
        DWORD written = 0;
        if (!WriteFile(output, bytes, count, &written, 0) || written == 0)
        {
            return; // nowhere to write (no console, closed pipe): the rest of the line is dropped
        }

        bytes += written;
        count -= written;
    }
}

// Writes the number in decimal, with a minus sign in front when negative is set, and ends the line.
static void write_line_number(unsigned long long magnitude, int negative)
{
    char text[24]; // "-", up to 20 digits, "\r\n"
    int start = (int)sizeof text;
    text[--start] = '\n';
    text[--start] = '\r';
    do
    {
        text[--start] = (char)('0' + (int)(magnitude % 10));
        magnitude /= 10;
    } while (magnitude != 0);

    if (negative)
    {
        text[--start] = '-';
    }

    write_bytes(text + start, (DWORD)((int)sizeof text - start));
}

// The magnitude of a negative number, computed in unsigned arithmetic so that the most negative value
// (int.MinValue, long.MinValue) has one too: 0 - (unsigned)value never overflows.
static unsigned long long magnitude_of(long long value)
{
    return value < 0 ? 0ULL - (unsigned long long)value : (unsigned long long)value;
}

void ChipSharp_Runtime_WriteLine(void)
{
    write_bytes("\r\n", 2);
}

void ChipSharp_Runtime_WriteLineInt32(int value)
{
    write_line_number(magnitude_of(value), value < 0);
}

void ChipSharp_Runtime_WriteLineUInt32(unsigned int value)
{
    write_line_number(value, 0);
}

void ChipSharp_Runtime_WriteLineInt64(long long value)
{
    write_line_number(magnitude_of(value), value < 0);
}

void ChipSharp_Runtime_WriteLineUInt64(unsigned long long value)
{
    write_line_number(value, 0);
}

// A bool arrives as an int; like .NET, anything but 0 is True.
void ChipSharp_Runtime_WriteLineBool(int value)
{
    if (value != 0)
    {
        write_bytes("True\r\n", 6);
    }
    else
    {
        write_bytes("False\r\n", 7);
    }
}
