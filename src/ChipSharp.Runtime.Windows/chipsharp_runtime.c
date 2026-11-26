// ChipSharp runtime for Windows x64 programs (target "host").
//
// Freestanding: no headers and no C runtime. The program talks to Windows through three kernel32 functions;
// the toolchain makes their import library from the target's "link.imports".
//
//   chipsharp_start                      the program's entry point: runs chipsharp_entry, exits with its result
//   chipsharp_console_write              where Console.Write and Console.WriteLine write: standard output (the
//                                        text they write, with lines that end with "\r\n" like .NET's on Windows,
//                                        comes from chipsharp_console.c)
// memcpy, memmove and memset (which LLVM calls to copy and clear large structs and arrays) are in chipsharp_memory.c.

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

// Console.Write and Console.WriteLine (chipsharp_console.c) write here: text as the UTF-8 bytes of its literal,
// whatever the console's code page.
void chipsharp_console_write(const char *bytes, unsigned int count)
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
