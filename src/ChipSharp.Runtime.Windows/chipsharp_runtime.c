// ChipSharp runtime for Windows x64 programs (target "host").
//
// Freestanding: no headers and no C runtime. The program talks to Windows through a few kernel32 functions; the
// toolchain makes their import library from the target's "link.imports".
//
//   chipsharp_start                      the program's entry point: makes a stack overflow end the program as .NET
//                                        ends it, runs chipsharp_entry, exits with its result
//   chipsharp_console_write              where Console.Write and Console.WriteLine write: standard output (the
//                                        text they write, with lines that end with "\r\n" like .NET's on Windows,
//                                        comes from chipsharp_console.c)
// memcpy, memmove and memset (which LLVM calls to copy and clear large structs and arrays) are in chipsharp_memory.c.
//
// A stack overflow: the program's stack ends in Windows' guard page, which Windows turns into the exception
// STATUS_STACK_OVERFLOW when a function reaches it (LLVM probes a frame larger than a page page by page, with
// __chkstk). A vectored exception handler sees it first, on the stack Windows keeps for it (SetThreadStackGuarantee),
// writes what .NET writes on standard error, "Stack overflow.", and ends the program with that exception's code as its
// exit code, as .NET does (0xC00000FD; .NET also lists the methods that were running).

typedef void *HANDLE;
typedef unsigned long DWORD;
typedef int BOOL;
typedef long LONG;

#define STD_OUTPUT_HANDLE ((DWORD)-11)
#define STD_ERROR_HANDLE ((DWORD)-12)
#define STATUS_STACK_OVERFLOW 0xC00000FDu
#define EXCEPTION_CONTINUE_SEARCH 0

// The stack Windows keeps for the handler of a stack overflow: enough to write the message and exit.
#define STACK_GUARANTEE 16384u

typedef struct
{
    DWORD ExceptionCode;
    DWORD ExceptionFlags;
    void *ExceptionRecord;
    void *ExceptionAddress;
    DWORD NumberParameters;
    unsigned long long ExceptionInformation[15];
} EXCEPTION_RECORD;

typedef struct
{
    EXCEPTION_RECORD *ExceptionRecord;
    void *ContextRecord;
} EXCEPTION_POINTERS;

__declspec(dllimport) HANDLE __stdcall GetStdHandle(DWORD nStdHandle);
__declspec(dllimport) BOOL __stdcall WriteFile(HANDLE hFile, const void *lpBuffer, DWORD nNumberOfBytesToWrite,
                                               DWORD *lpNumberOfBytesWritten, void *lpOverlapped);
__declspec(dllimport) __declspec(noreturn) void __stdcall ExitProcess(unsigned int uExitCode);
__declspec(dllimport) void *__stdcall AddVectoredExceptionHandler(DWORD First,
                                                                  LONG(__stdcall *Handler)(EXCEPTION_POINTERS *));
__declspec(dllimport) BOOL __stdcall SetThreadStackGuarantee(DWORD *StackSizeInBytes);

// Written by the code generator: calls the program's Main and returns its result (0 for a void Main).
int chipsharp_entry(void);

// The linker looks for this symbol when a program uses floating point; nothing reads it.
int _fltused = 0;

// The first to see every exception: a stack overflow ends the program as .NET ends it; any other goes on as before.
static LONG __stdcall on_exception(EXCEPTION_POINTERS *exception)
{
    if (exception->ExceptionRecord->ExceptionCode == STATUS_STACK_OVERFLOW)
    {
        static const char message[] = "Stack overflow.\r\n";
        DWORD written = 0;
        WriteFile(GetStdHandle(STD_ERROR_HANDLE), message, (DWORD)(sizeof message - 1), &written, 0);
        ExitProcess(STATUS_STACK_OVERFLOW);
    }

    return EXCEPTION_CONTINUE_SEARCH;
}

__declspec(noreturn) void chipsharp_start(void)
{
    DWORD guarantee = STACK_GUARANTEE;
    SetThreadStackGuarantee(&guarantee);
    AddVectoredExceptionHandler(1, on_exception);
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
