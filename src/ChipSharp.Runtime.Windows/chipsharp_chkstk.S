// ChipSharp runtime for Windows x64 programs (target "host"): __chkstk, the stack probe.
//
// Windows commits a thread's stack one 4 KB page at a time, as it grows into the guard page below the part in use.
// A stack frame larger than a page (a big array on the stack) could skip the guard page and reach memory that is
// not the stack yet, so LLVM calls __chkstk before such a function moves rsp, with the frame's size in rax. It
// touches each page of the new frame from the top down, so the guard page moves down a page at a time.
//
// As Windows x64 defines it, __chkstk changes no register but r10, r11 and the flags, and does not move rsp: the
// caller subtracts rax from rsp itself. The C runtime provides it; a program without one gets it here. This is
// compiler-rt's ___chkstk_ms, the same routine under the name LLVM calls for the MSVC target.

    .text
    .globl __chkstk
    .p2align 4
__chkstk:
    push    %rcx
    push    %rax
    cmp     $0x1000, %rax
    lea     24(%rsp), %rcx      // rsp where the caller called: above the two pushes and the return address
    jb      2f
1:
    sub     $0x1000, %rcx       // one page down: touch it
    test    %rcx, (%rcx)
    sub     $0x1000, %rax
    cmp     $0x1000, %rax
    ja      1b
2:
    sub     %rax, %rcx          // the rest, less than a page: touch the bottom of the frame
    test    %rcx, (%rcx)
    pop     %rax
    pop     %rcx
    ret
