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

const char *chipsharp_unhandled_text(const void *object)
{
    const string *message = (const string *)object;
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
    return text;
}

// ---- The exceptions of ChipSharp.Library's substitutes (Runtime.Throw, ThrowValue, ThrowText) ----------------------
//
// An exception given as fixed text, its type, message and parameter, which needs no heap to say: a DateTime that does
// not exist, a bad format. The message is put together on the stack and stopped with through chipsharp_fail (each
// runtime's failures file), which prints "Unhandled exception. " before it.

__attribute__((noreturn)) void chipsharp_fail(const char *message);

#define MESSAGE_SIZE 600u

typedef struct
{
    char text[MESSAGE_SIZE + 1];
    unsigned int count;
} message;

// Appends one UTF-16 code unit as UTF-8 (a surrogate as U+FFFD: the messages are ASCII), when it fits.
static void append_unit(message *m, unsigned int c)
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

static void append_ascii(message *m, const char *text)
{
    while (*text != 0)
    {
        append_unit(m, (unsigned char)*text++);
    }
}

// Appends a string object's chars (nothing for null).
static void append_string(message *m, const string *s)
{
    if (s == 0)
    {
        return;
    }

    const unsigned char *narrow = (const unsigned char *)s + DATA;
    const unsigned short *wide = (const unsigned short *)(const void *)narrow;
    int is_wide = s->type->element_size == 2;
    for (int i = 0; i < s->length; i++)
    {
        append_unit(m, is_wide ? wide[i] : narrow[i]);
    }
}

static void append_int(message *m, int value)
{
    char digits[12];
    unsigned int magnitude = value < 0 ? 0u - (unsigned int)value : (unsigned int)value;
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

static void append_parameter(message *m, const string *parameter)
{
    if (parameter != 0)
    {
        append_ascii(m, " (Parameter '");
        append_string(m, parameter);
        append_ascii(m, "')");
    }
}

__attribute__((noreturn)) static void stop(message *m)
{
    m->text[m->count] = 0;
    chipsharp_fail(m->text);
}

// "System.ArgumentOutOfRangeException: Ticks must be ... (Parameter 'ticks')": the type, its message, the parameter.
__attribute__((noreturn)) void ChipSharp_Runtime_Throw(const string *exception, const string *text, const string *parameter)
{
    message m;
    m.count = 0;
    append_string(&m, exception);
    append_ascii(&m, ": ");
    append_string(&m, text);
    append_parameter(&m, parameter);
    stop(&m);
}

// An ArgumentOutOfRangeException that says the value too, on a line of its own: "Actual value was 13.".
__attribute__((noreturn)) void ChipSharp_Runtime_ThrowValue(const string *text, const string *parameter, int value)
{
    message m;
    m.count = 0;
    append_ascii(&m, "System.ArgumentOutOfRangeException: ");
    append_string(&m, text);
    append_parameter(&m, parameter);
    append_ascii(&m, "\r\nActual value was ");
    append_int(&m, value);
    append_ascii(&m, ".");
    stop(&m);
}

// The program's text in the message: before, count UTF-16 chars from chars on, after.
__attribute__((noreturn)) void ChipSharp_Runtime_ThrowText(const string *exception, const string *before,
                                                           const unsigned short *chars, int count, const string *after)
{
    message m;
    m.count = 0;
    append_string(&m, exception);
    append_ascii(&m, ": ");
    append_string(&m, before);
    for (int i = 0; i < count; i++)
    {
        append_unit(&m, chars[i]);
    }

    append_string(&m, after);
    stop(&m);
}
