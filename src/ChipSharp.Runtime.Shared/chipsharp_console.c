// Console.Write and Console.WriteLine, shared by every runtime: the text of text, chars, numbers and bools as .NET writes
// them, written with chipsharp_console_write, which each runtime has (a PC's standard output, a chip's UART). Lines end
// with "\r\n", as .NET ends them on Windows, where the samples' expected outputs come from.
//
// Freestanding: no headers and no C library.
//
//   ChipSharp_Runtime_Write<Type>        Console.Write(value): String, Char, Int32, UInt32, Int64, UInt64, Bool,
//                                        Float32, Float64; floats and doubles as text come from chipsharp_float_text.c
//   ChipSharp_Runtime_WriteLine<Type>    Console.WriteLine(value): the same, then "\r\n"
//   ChipSharp_Runtime_WriteLine          Console.WriteLine(): "\r\n"
//   ChipSharp_Runtime_WriteHexDigits     the last count hex digits of a number, for printed text's X and x formats
//   ChipSharp_Runtime_WriteChars         UTF-16 chars from a span, for text a substitute prints itself (a DateTime's)
//
// Text (C#'s string) is a string object (see chipsharp_string.c), or null, which writes nothing, as in .NET. Its chars
// are UTF-16 code units, written as UTF-8, as .NET's UTF-8 console writes them: a lone surrogate, which UTF-8 cannot
// hold, is U+FFFD (EF BF BD), and a high surrogate at the end of one write waits for the low one at the start of the
// next (Console.Write('\uD83D'); Console.Write('\uDE00'); writes one emoji, as in .NET); anything else written first
// makes the waiting half U+FFFD, and one still waiting when the program ends is not written, as .NET does neither.

// 32 bits on every target: an int or a long (AVR's int has 16), as the compiler's own fixed-width types say. C# passes
// an int, a uint, a char (its UTF-16 code unit) and a bool (0 or 1) as 32 bits.
typedef __INT32_TYPE__ s32;
typedef __UINT32_TYPE__ u32;

// In the runtime: writes count bytes where the program's output goes. They are always in RAM.
void chipsharp_console_write(const char *bytes, unsigned int count);

// In chipsharp_float_text.c: a double or a float, given by its bits, as .NET writes it; returns the length.
int chipsharp_float64_text(unsigned long long bits, char *text);
int chipsharp_float32_text(u32 bits, char *text);

// What the program gives (a string, a span's chars) may be its constant data, which on an AVR stays in flash: there an
// address with bit 15 set is 0x8000 plus a flash address, read with LPM (see src/ChipSharp.Backend.Llvm/LlvmFlash.cs);
// RAM's addresses never have it. READ(type, address) reads a value of the type at such an address, from RAM or flash;
// on every other target it is a plain read. The console's own text (True, False) stays in flash there too, and is
// copied to the stack to be written: on an AVR a constant in RAM takes RAM (start-up copies .rodata there).
#if defined(__AVR__)
#define IN_FLASH(address) (((unsigned int)(address) & 0x8000u) != 0)
#define READ(type, address)                                                                                            \
    (IN_FLASH(address) ? *(const __flash type *)((unsigned int)(address) & 0x7FFFu) : *(const type *)(address))
typedef const __flash char constant_char;
#else
#define IN_FLASH(address) 0
#define READ(type, address) (*(const type *)(address))
typedef const char constant_char;
#endif

// How a string object starts (see chipsharp_string.c): its header, the address of its type, whose second word says
// how many bytes each char takes, then its length (a C# int); its chars from DATA on.
typedef const u32 *type_words;

typedef struct
{
    type_words type;
    s32 length;
} string;

#define DATA ((unsigned int)((sizeof(void *) + 4 + 7) / 8 * 8))

// The high surrogate at the end of the text written last, waiting for its low surrogate; 0 when none waits.
static unsigned int pending;

// Writes the console's own text, at most 8 bytes: through the stack on an AVR.
static void write_constant(constant_char *text, unsigned int count)
{
#if defined(__AVR__)
    char bytes[8];
    for (unsigned int i = 0; i < count; i++)
    {
        bytes[i] = text[i];
    }

    chipsharp_console_write(bytes, count);
#else
    chipsharp_console_write(text, count);
#endif
}

static constant_char replacement[] = "\xEF\xBF\xBD";

// U+FFFD for a high surrogate that waits in vain, as something else is written.
static void end_pending(void)
{
    if (pending != 0)
    {
        pending = 0;
        write_constant(replacement, 3);
    }
}

// Writes bytes that are no text.
static void output(const char *bytes, unsigned int count)
{
    end_pending();
    chipsharp_console_write(bytes, count);
}

// output, of the console's own text.
static void output_constant(constant_char *text, unsigned int count)
{
    end_pending();
    write_constant(text, count);
}

// UTF-8 on its way out, a buffer at a time.
typedef struct
{
    char bytes[64];
    unsigned int count;
} utf8;

static void put(utf8 *out, u32 c)
{
    if (out->count > sizeof out->bytes - 4)
    {
        chipsharp_console_write(out->bytes, out->count);
        out->count = 0;
    }

    char *at = out->bytes + out->count;
    if (c < 0x80)
    {
        at[0] = (char)c;
        out->count += 1;
    }
    else if (c < 0x800)
    {
        at[0] = (char)(0xC0 | (c >> 6));
        at[1] = (char)(0x80 | (c & 0x3F));
        out->count += 2;
    }
    else if (c < 0x10000)
    {
        at[0] = (char)(0xE0 | (c >> 12));
        at[1] = (char)(0x80 | ((c >> 6) & 0x3F));
        at[2] = (char)(0x80 | (c & 0x3F));
        out->count += 3;
    }
    else
    {
        at[0] = (char)(0xF0 | (c >> 18));
        at[1] = (char)(0x80 | ((c >> 12) & 0x3F));
        at[2] = (char)(0x80 | ((c >> 6) & 0x3F));
        at[3] = (char)(0x80 | (c & 0x3F));
        out->count += 4;
    }
}

// Writes length UTF-16 code units from units (2 bytes each when wide, else 1) as UTF-8, with the surrogates as above.
static void write_units(const void *units, int wide, s32 length)
{
    const unsigned char *narrow = (const unsigned char *)units;
    const unsigned short *chars = (const unsigned short *)units;
    utf8 out;
    out.count = 0;
    for (s32 i = 0; i < length; i++)
    {
        u32 c = wide ? READ(unsigned short, chars + i) : READ(unsigned char, narrow + i);
        if (pending != 0)
        {
            u32 high = pending;
            pending = 0;
            if (c >= 0xDC00 && c <= 0xDFFF)
            {
                put(&out, 0x10000 + ((high - 0xD800) << 10) + (c - 0xDC00));
                continue;
            }

            put(&out, 0xFFFD);
        }

        if (c >= 0xD800 && c <= 0xDBFF)
        {
            pending = (unsigned int)c;
        }
        else
        {
            put(&out, c >= 0xDC00 && c <= 0xDFFF ? 0xFFFD : c);
        }
    }

    if (out.count > 0)
    {
        chipsharp_console_write(out.bytes, out.count);
    }
}

static void write_string(const string *s)
{
    if (s == 0)
    {
        return;
    }

    const char *data = (const char *)s + DATA;
    type_words type = READ(type_words, &s->type);
    s32 length = READ(s32, &s->length);
    int wide = READ(u32, type + 1) == 2;
    if (wide || IN_FLASH(data))
    {
        write_units(data, wide, length);
        return;
    }

    // 1 byte per char, in RAM: a run of ASCII goes out as it is
    const unsigned char *chars = (const unsigned char *)data;
    s32 start = 0;
    for (s32 i = 0; i <= length; i++)
    {
        if (i == length || chars[i] >= 0x80)
        {
            if (i > start)
            {
                output((const char *)chars + start, (unsigned int)(i - start));
            }

            if (i < length)
            {
                write_units(chars + i, 0, 1);
            }

            start = i + 1;
        }
    }
}

// Writes the number in decimal, with a minus sign in front when negative is set, then "\r\n" when line is set.
static void write_number(unsigned long long magnitude, int negative, int line)
{
    char text[23]; // "-", up to 20 digits, "\r\n"; filled from the end
    int start = (int)sizeof text;
    if (line)
    {
        text[--start] = '\n';
        text[--start] = '\r';
    }

    // the digits of what is above 32 bits by 64-bit division, the rest by 32-bit division (on a chip without a divide
    // instruction, a helper's loop of 32 steps instead of 64)
    while ((magnitude >> 32) != 0)
    {
        text[--start] = (char)('0' + (int)(magnitude % 10));
        magnitude /= 10;
    }

    u32 low = (u32)magnitude;
    do
    {
        text[--start] = (char)('0' + (int)(low % 10));
        low /= 10;
    } while (low != 0);

    if (negative)
    {
        text[--start] = '-';
    }

    output(text + start, (unsigned int)((int)sizeof text - start));
}

// The magnitude of a negative number, computed in unsigned arithmetic so that the most negative value
// (int.MinValue, long.MinValue) has one too: 0 - (unsigned)value never overflows.
static unsigned long long magnitude_of(long long value)
{
    return value < 0 ? 0ULL - (unsigned long long)value : (unsigned long long)value;
}

static constant_char true_text[] = "True\r\n";
static constant_char false_text[] = "False\r\n";
static constant_char line_end[] = "\r\n";

// A bool arrives as an int; like .NET, anything but 0 is True.
static void write_bool(s32 value, int line)
{
    if (value != 0)
    {
        output_constant(true_text, line ? 6u : 4u);
    }
    else
    {
        output_constant(false_text, line ? 7u : 5u);
    }
}

// Writes text[0..length), and "\r\n" after it when line is set: the buffer has room for two more characters.
static void write_text(char *text, int length, int line)
{
    if (line)
    {
        text[length++] = '\r';
        text[length++] = '\n';
    }

    output(text, (unsigned int)length);
}

// The shortest text that reads back as the same value, as .NET writes it. The bits are read through a union, so
// the number itself is never used in a calculation (a chip may have no float hardware, nor the routines that would
// stand in for it).
static void write_float64(double value, int line)
{
    union
    {
        double value;
        unsigned long long bits;
    } number = {value};
    char text[26]; // at most 24 characters, "\r\n"
    write_text(text, chipsharp_float64_text(number.bits, text), line);
}

static void write_float32(float value, int line)
{
    union
    {
        float value;
        u32 bits;
    } number = {value};
    char text[17]; // at most 15 characters, "\r\n"
    write_text(text, chipsharp_float32_text(number.bits, text), line);
}

void ChipSharp_Runtime_WriteLine(void)
{
    output_constant(line_end, 2);
}

void ChipSharp_Runtime_WriteString(const string *value)
{
    write_string(value);
}

void ChipSharp_Runtime_WriteLineString(const string *value)
{
    write_string(value);
    ChipSharp_Runtime_WriteLine();
}

// A char arrives as an int holding its UTF-16 code unit.
void ChipSharp_Runtime_WriteChar(s32 value)
{
    unsigned short unit = (unsigned short)value;
    write_units(&unit, 1, 1);
}

// ChipSharp.Library's Runtime.WriteChars: count UTF-16 chars from chars on, e.g. a DateTime's text, written a few at a
// time with no string made (DateTimeText).
void ChipSharp_Runtime_WriteChars(const unsigned short *chars, s32 count)
{
    write_units(chars, 1, count);
}

void ChipSharp_Runtime_WriteLineChar(s32 value)
{
    ChipSharp_Runtime_WriteChar(value);
    ChipSharp_Runtime_WriteLine();
}

void ChipSharp_Runtime_WriteHexDigits(unsigned long long bits, s32 count, s32 upper)
{
    char text[16];
    for (s32 i = 0; i < count && i < 16; i++)
    {
        unsigned int digit = (unsigned int)(bits >> (4 * (count - 1 - i))) & 15u;
        text[i] = (char)(digit < 10 ? '0' + digit : (upper ? 'A' : 'a') + digit - 10);
    }

    output(text, (unsigned int)(count < 16 ? count : 16));
}

void ChipSharp_Runtime_WriteInt32(s32 value)
{
    write_number(magnitude_of(value), value < 0, 0);
}

void ChipSharp_Runtime_WriteLineInt32(s32 value)
{
    write_number(magnitude_of(value), value < 0, 1);
}

void ChipSharp_Runtime_WriteUInt32(u32 value)
{
    write_number(value, 0, 0);
}

void ChipSharp_Runtime_WriteLineUInt32(u32 value)
{
    write_number(value, 0, 1);
}

void ChipSharp_Runtime_WriteInt64(long long value)
{
    write_number(magnitude_of(value), value < 0, 0);
}

void ChipSharp_Runtime_WriteLineInt64(long long value)
{
    write_number(magnitude_of(value), value < 0, 1);
}

void ChipSharp_Runtime_WriteUInt64(unsigned long long value)
{
    write_number(value, 0, 0);
}

void ChipSharp_Runtime_WriteLineUInt64(unsigned long long value)
{
    write_number(value, 0, 1);
}

void ChipSharp_Runtime_WriteBool(s32 value)
{
    write_bool(value, 0);
}

void ChipSharp_Runtime_WriteLineBool(s32 value)
{
    write_bool(value, 1);
}

void ChipSharp_Runtime_WriteFloat64(double value)
{
    write_float64(value, 0);
}

void ChipSharp_Runtime_WriteLineFloat64(double value)
{
    write_float64(value, 1);
}

void ChipSharp_Runtime_WriteFloat32(float value)
{
    write_float32(value, 0);
}

void ChipSharp_Runtime_WriteLineFloat32(float value)
{
    write_float32(value, 1);
}
