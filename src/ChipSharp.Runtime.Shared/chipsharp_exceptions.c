// ChipSharp runtime, shared by every runtime: the text of C#'s throw (see ChipSharp.Library's Exceptions.Thrown).
//
// Freestanding: no headers and no C library.
//
// ChipSharp has no exceptions to catch yet, so a thrown exception is one nothing catches, and the program stops the way
// .NET stops it then: "Unhandled exception. " and the exception's ToString(), through each runtime's failures file
// (ChipSharp_Runtime_Unhandled: standard error and exit code 1 on a PC, UART0 and a breakpoint on a chip). The text is a
// string object (see chipsharp_string.c), which this writes as UTF-8, as the console writes text; at most TEXT_SIZE
// bytes of it.
//
//   chipsharp_unhandled_text             the exception's ToString() as UTF-8 ending with a 0 byte
//
// With SDCC (the STM8's runtime), whose linker takes a file's functions all or none, the file is compiled in modules, one
// for each "//@module" part below, each with the text before the first part and CHIPSHARP_MODULES defined (see
// src/ChipSharp.Runtime.Stm8's Stm8Runtime.Modules): a program that stops with a substitute's exception has no 900 bytes
// of RAM for an exception's text, nor the other exceptions' code. The helpers that parts share (SHARED) are then
// functions of their own part, instead of static ones.

#define DATA ((unsigned int)((sizeof(void *) + 4 + 7) / 8 * 8))

// 32 bits on every target: an int or a long (AVR's and SDCC's int has 16), as the compiler's own fixed-width types say
#if defined(__SDCC)
typedef long s32;
typedef unsigned long u32;
#define NORETURN _Noreturn
// 1 KB of RAM (the STM8S103's): a substitute's message is made on the stack, and is cut at fewer bytes
#define MESSAGE_SIZE 128u
#else
typedef __INT32_TYPE__ s32;
typedef __UINT32_TYPE__ u32;
#define NORETURN __attribute__((noreturn))
#define MESSAGE_SIZE 600u
#endif

// What the program gives (a string, a span's chars) may be its constant data, which on an AVR stays in flash: there an
// address with bit 15 set is 0x8000 plus a flash address, read with LPM (see src/ChipSharp.Backend.Llvm/LlvmFlash.cs);
// RAM's addresses never have it. READ(type, address) reads a value of the type at such an address, from RAM or flash;
// on every other target it is a plain read. This file's own text stays in flash there too (constant_char): a constant
// in RAM would take RAM on an AVR, where start-up copies .rodata to it.
#if defined(__AVR__)
#define READ(type, address)                                                                                            \
    (((unsigned int)(address) & 0x8000u) != 0 ? *(const __flash type *)((unsigned int)(address) & 0x7FFFu)            \
                                              : *(const type *)(address))
typedef const __flash char constant_char;
#else
#define READ(type, address) (*(const type *)(address))
typedef const char constant_char;
#endif

typedef struct chipsharp_type
{
    u32 size;
    u32 element_size;
} chipsharp_type;

typedef struct string
{
    const chipsharp_type *type;
    s32 length;
} string;

typedef const chipsharp_type *type_pointer;

#if defined(CHIPSHARP_MODULES)
#define SHARED
#else
#define SHARED static
#endif

// ---- What the parts of the substitutes' exceptions share (below) -------------------------------------------------

NORETURN void chipsharp_fail(const char *message);

typedef struct
{
    char text[MESSAGE_SIZE + 1];
    unsigned int count;
} message;

// In the part "message", the helpers the others share (SHARED: static unless the file is cut into modules)
SHARED s32 string_length(const string *s);
SHARED int string_wide(const string *s);
SHARED u32 string_unit(const string *s, int wide, s32 i);
SHARED void append_unit(message *m, u32 c);
SHARED void append_ascii(message *m, constant_char *text);
SHARED void append_string(message *m, const string *s);
SHARED void append_parameter(message *m, const string *parameter);
SHARED void append_type(message *m, const string *exception);
SHARED NORETURN void stop(message *m);

//@module unhandled
#define TEXT_SIZE 900u

static char text[TEXT_SIZE + 1];

// Appends one code point as UTF-8 when it fits; false when it does not.
static int put(unsigned int *count, u32 c)
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

const char *chipsharp_unhandled_text(const void *object)
{
    const string *message = (const string *)object;
    unsigned int count = 0;
    if (message != 0)
    {
        int wide = string_wide(message);
        s32 chars = string_length(message);
        for (s32 i = 0; i < chars; i++)
        {
            u32 c = string_unit(message, wide, i);
            u32 next = wide && i + 1 < chars ? string_unit(message, wide, i + 1) : 0;
            if (c >= 0xD800u && c < 0xDC00u && next >= 0xDC00u && next < 0xE000u)
            {
                c = 0x10000u + ((c - 0xD800u) << 10) + (next - 0xDC00u);
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
    return text;
}

// ---- The exceptions of ChipSharp.Library's substitutes (Runtime.Throw, ThrowValue, ThrowText) ----------------------
//
// An exception given as fixed text, its type, message and parameter, which needs no heap to say: a DateTime that does
// not exist, a bad format. The message is put together on the stack and stopped with through chipsharp_fail (each
// runtime's failures file), which prints "Unhandled exception. " before it.

//@module message
SHARED s32 string_length(const string *s)
{
    return READ(s32, &s->length);
}

SHARED int string_wide(const string *s)
{
    return READ(u32, &READ(type_pointer, &s->type)->element_size) == 2;
}

// The UTF-16 code unit at i of a string's chars, narrow (1 byte each) or wide.
SHARED u32 string_unit(const string *s, int wide, s32 i)
{
    const unsigned char *narrow = (const unsigned char *)s + DATA;
    return wide ? READ(unsigned short, (const unsigned short *)(const void *)narrow + i) : READ(unsigned char, narrow + i);
}

// Appends one UTF-16 code unit as UTF-8 (a surrogate as U+FFFD: the messages are ASCII), when it fits.
SHARED void append_unit(message *m, u32 c)
{
    unsigned int need = c < 0x80u ? 1u : c < 0x800u ? 2u : 3u;
    if (m->count + need > MESSAGE_SIZE)
    {
        return;
    }

    if (c >= 0xD800u && c < 0xE000u)
    {
        c = 0xFFFDu;
    }

    char *at = m->text + m->count;
    if (need == 1)
    {
        at[0] = (char)c;
    }
    else if (need == 2)
    {
        at[0] = (char)(0xC0u | (c >> 6));
        at[1] = (char)(0x80u | (c & 0x3Fu));
    }
    else
    {
        at[0] = (char)(0xE0u | (c >> 12));
        at[1] = (char)(0x80u | ((c >> 6) & 0x3Fu));
        at[2] = (char)(0x80u | (c & 0x3Fu));
    }

    m->count += need;
}

SHARED void append_ascii(message *m, constant_char *text)
{
    while (*text != 0)
    {
        append_unit(m, (unsigned char)*text++);
    }
}

// Appends a string object's chars (nothing for null).
SHARED void append_string(message *m, const string *s)
{
    if (s == 0)
    {
        return;
    }

    int wide = string_wide(s);
    s32 chars = string_length(s);
    for (s32 i = 0; i < chars; i++)
    {
        append_unit(m, string_unit(s, wide, i));
    }
}

static constant_char parameter_start[] = " (Parameter '";
static constant_char parameter_end[] = "')";
static constant_char colon[] = ": ";

SHARED void append_parameter(message *m, const string *parameter)
{
    if (parameter != 0)
    {
        append_ascii(m, parameter_start);
        append_string(m, parameter);
        append_ascii(m, parameter_end);
    }
}

// The exception's type and ": ".
SHARED void append_type(message *m, const string *exception)
{
    append_string(m, exception);
    append_ascii(m, colon);
}

SHARED void stop(message *m)
{
    m->text[m->count] = 0;
    chipsharp_fail(m->text);
}

//@module throw
// "System.ArgumentOutOfRangeException: Ticks must be ... (Parameter 'ticks')": the type, its message, the parameter.
NORETURN void ChipSharp_Runtime_Throw(const string *exception, const string *text, const string *parameter)
{
    message m;
    m.count = 0;
    append_type(&m, exception);
    append_string(&m, text);
    append_parameter(&m, parameter);
    stop(&m);
}

//@module throw_value
static void append_int(message *m, s32 value)
{
    char digits[12];
    u32 magnitude = value < 0 ? 0u - (u32)value : (u32)value;
    int n = 0;
    do
    {
        digits[n++] = (char)('0' + magnitude % 10u);
        magnitude /= 10u;
    } while (magnitude != 0);

    if (value < 0)
    {
        append_unit(m, '-');
    }

    while (n > 0)
    {
        append_unit(m, (unsigned char)digits[--n]);
    }
}

static constant_char out_of_range[] = "System.ArgumentOutOfRangeException: ";
static constant_char actual_value[] = "\r\nActual value was ";
static constant_char full_stop[] = ".";

// An ArgumentOutOfRangeException that says the value too, on a line of its own: "Actual value was 13.".
NORETURN void ChipSharp_Runtime_ThrowValue(const string *text, const string *parameter, s32 value)
{
    message m;
    m.count = 0;
    append_ascii(&m, out_of_range);
    append_string(&m, text);
    append_parameter(&m, parameter);
    append_ascii(&m, actual_value);
    append_int(&m, value);
    append_ascii(&m, full_stop);
    stop(&m);
}

//@module throw_text
// The program's text in the message: before, count UTF-16 chars from chars on, after.
NORETURN void ChipSharp_Runtime_ThrowText(const string *exception, const string *before,
                                                           const unsigned short *chars, s32 count, const string *after)
{
    message m;
    m.count = 0;
    append_type(&m, exception);
    append_string(&m, before);
    for (s32 i = 0; i < count; i++)
    {
        append_unit(&m, READ(unsigned short, chars + i));
    }

    append_string(&m, after);
    stop(&m);
}
