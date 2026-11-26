// ChipSharp runtime for Windows x64 programs (target "host"): the memory functions LLVM calls.
//
// LLVM zeroes and copies large blocks of memory (a big array on the stack, a struct) with calls to memset and
// memcpy, which the C runtime would provide; a program without one gets them here. They use x64's string
// instructions (rep stosb, rep movsb), which recent x64 processors run fast for any size and alignment, and which
// the compiler cannot turn back into calls to the very functions they implement.
//
//   memset, memcpy, memmove

typedef __SIZE_TYPE__ size_t;

void *memset(void *target, int value, size_t count)
{
    void *to = target;
    __asm__ volatile("rep stosb" : "+D"(to), "+c"(count) : "a"(value) : "memory");
    return target;
}

void *memcpy(void *target, const void *source, size_t count)
{
    void *to = target;
    __asm__ volatile("rep movsb" : "+D"(to), "+S"(source), "+c"(count) : : "memory");
    return target;
}

void *memmove(void *target, const void *source, size_t count)
{
    const unsigned char *to = (const unsigned char *)target;
    const unsigned char *from = (const unsigned char *)source;
    if (to <= from || to >= from + count)
    {
        return memcpy(target, source, count); // copying forward never reads a byte it has already written
    }

    // the target overlaps the end of the source: copy backward, from the last byte, with the direction flag set
    // (and cleared again, as the Windows x64 calling convention requires)
    to += count - 1;
    from += count - 1;
    __asm__ volatile("std\n\trep movsb\n\tcld" : "+D"(to), "+S"(from), "+c"(count) : : "memory");
    return target;
}
