// ChipSharp runtime, shared by every runtime with a heap: C#'s throw (see ChipSharp.Library's Exceptions.Thrown).
//
// Freestanding: no headers and no C library.
//
// ChipSharp has no exceptions to catch yet, so a thrown exception is one nothing catches, and the program stops the way
// .NET stops it then: "Unhandled exception. " and the exception's ToString(), through chipsharp_fail (each runtime's
// failures file: standard error and exit code 1 on a PC, UART0 and a breakpoint on a chip). The text is a string object
// (see chipsharp_string.c), written as UTF-8, as the console writes text; at most TEXT_SIZE bytes of it.
//
//   ChipSharp_Runtime_Unhandled          throw: the exception's ToString() as text

#define DATA ((unsigned int)((sizeof(void *) + 4 + 7) / 8 * 8))
#define TEXT_SIZE 900u

typedef struct chipsharp_type
{
    unsigned int size;
    unsigned int element_size;
} chipsharp_type;

typedef struct string
{
    const chipsharp_type *type;
    int length;
} string;

__attribute__((noreturn)) void chipsharp_fail(const char *message);

static char text[TEXT_SIZE + 1];

// Appends one code point as UTF-8 when it fits; false when it does not.
static int put(unsigned int *count, unsigned int c)
{
    unsigned int need = c < 0x80u ? 1u : c < 0x800u ? 2u : c < 0x10000u ? 3u : 4u;
    if (*count + need > TEXT_SIZE)
    {
        return 0;
    }

    char *at = text + *count;
    if (need == 1)
    {
        at[0] = (char)c;
    }
    else if (need == 2)
    {
        at[0] = (char)(0xC0u | (c >> 6));
        at[1] = (char)(0x80u | (c & 0x3Fu));
    }
    else if (need == 3)
    {
        at[0] = (char)(0xE0u | (c >> 12));
        at[1] = (char)(0x80u | ((c >> 6) & 0x3Fu));
        at[2] = (char)(0x80u | (c & 0x3Fu));
    }
    else
    {
        at[0] = (char)(0xF0u | (c >> 18));
        at[1] = (char)(0x80u | ((c >> 12) & 0x3Fu));
        at[2] = (char)(0x80u | ((c >> 6) & 0x3Fu));
        at[3] = (char)(0x80u | (c & 0x3Fu));
    }

    *count += need;
    return 1;
}

__attribute__((noreturn)) void ChipSharp_Runtime_Unhandled(const string *message)
{
    unsigned int count = 0;
    if (message != 0)
    {
        const unsigned char *narrow = (const unsigned char *)message + DATA;
        const unsigned short *wide = (const unsigned short *)(const void *)narrow;
        int is_wide = message->type->element_size == 2;
        for (int i = 0; i < message->length; i++)
        {
            unsigned int c = is_wide ? wide[i] : narrow[i];
            if (c >= 0xD800u && c < 0xDC00u && i + 1 < message->length && is_wide && wide[i + 1] >= 0xDC00u
                && wide[i + 1] < 0xE000u)
            {
                c = 0x10000u + ((c - 0xD800u) << 10) + (wide[i + 1] - 0xDC00u);
                i++;
            }
            else if (c >= 0xD800u && c < 0xE000u)
            {
                c = 0xFFFDu; // a lone surrogate, which UTF-8 cannot hold
            }

            if (!put(&count, c))
            {
                break;
            }
        }
    }

    text[count] = 0;
    chipsharp_fail(text);
}
