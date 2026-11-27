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
//
// Text (C#'s string) is a string object (see chipsharp_string.c), or null, which writes nothing, as in .NET. Its chars
// are UTF-16 code units, written as UTF-8, as .NET's UTF-8 console writes them: a lone surrogate, which UTF-8 cannot
// hold, is U+FFFD (EF BF BD), and a high surrogate at the end of one write waits for the low one at the start of the
// next (Console.Write('\uD83D'); Console.Write('\uDE00'); writes one emoji, as in .NET); anything else written first
// makes the waiting half U+FFFD, and one still waiting when the program ends is not written, as .NET does neither.

// In the runtime: writes count bytes where the program's output goes.
void chipsharp_console_write(const char *bytes, unsigned int count);

// In chipsharp_float_text.c: a double or a float, given by its bits, as .NET writes it; returns the length.
int chipsharp_float64_text(unsigned long long bits, char *text);
int chipsharp_float32_text(unsigned int bits, char *text);

// How a string object starts (see chipsharp_string.c): its header, the address of its type, whose second word says
// how many bytes each char takes, then its length; its chars from DATA on.
typedef struct
{
    const unsigned int *type;
    int length;
} string;

#define DATA ((unsigned int)((sizeof(void *) + 4 + 7) / 8 * 8))

// The high surrogate at the end of the text written last, waiting for its low surrogate; 0 when none waits.
static unsigned int pending;

// Writes bytes that are no text, after U+FFFD for a high surrogate that waits in vain.
static void output(const char *bytes, unsigned int count)
{
    if (pending != 0)
    {
        pending = 0;
        chipsharp_console_write("\xEF\xBF\xBD", 3);
    }

    chipsharp_console_write(bytes, count);
}

// UTF-8 on its way out, a buffer at a time.
typedef struct
{
    char bytes[64];
    unsigned int count;
} utf8;

static void put(utf8 *out, unsigned int c)
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

// Writes UTF-16 code units (1 byte each from narrow, else 2 from wide) as UTF-8, with the surrogates as above.
static void write_units(const unsigned char *narrow, const unsigned short *wide, int length)
{
    utf8 out;
    out.count = 0;
    for (int i = 0; i < length; i++)
    {
        unsigned int c = narrow != 0 ? narrow[i] : wide[i];
        if (pending != 0)
        {
            unsigned int high = pending;
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
            pending = c;
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
    if (s->type[1] == 2)
    {
        write_units(0, (const unsigned short *)data, s->length);
        return;
    }

    // 1 byte per char: a run of ASCII goes out as it is
    const unsigned char *chars = (const unsigned char *)data;
    int start = 0;
    for (int i = 0; i <= s->length; i++)
    {
        if (i == s->length || chars[i] >= 0x80)
        {
            if (i > start)
            {
                output((const char *)chars + start, (unsigned int)(i - start));
            }

            if (i < s->length)
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

    do
    {
        text[--start] = (char)('0' + (int)(magnitude % 10));
        magnitude /= 10;
    } while (magnitude != 0);

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

// A bool arrives as an int; like .NET, anything but 0 is True.
static void write_bool(int value, int line)
{
    if (value != 0)
    {
        output("True\r\n", line ? 6u : 4u);
    }
    else
    {
        output("False\r\n", line ? 7u : 5u);
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
        unsigned int bits;
    } number = {value};
    char text[17]; // at most 15 characters, "\r\n"
    write_text(text, chipsharp_float32_text(number.bits, text), line);
}

void ChipSharp_Runtime_WriteLine(void)
{
    output("\r\n", 2);
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
void ChipSharp_Runtime_WriteChar(int value)
{
    unsigned short unit = (unsigned short)value;
    write_units(0, &unit, 1);
}

void ChipSharp_Runtime_WriteLineChar(int value)
{
    ChipSharp_Runtime_WriteChar(value);
    ChipSharp_Runtime_WriteLine();
}

void ChipSharp_Runtime_WriteHexDigits(unsigned long long bits, int count, int upper)
{
    const char *digits = upper ? "0123456789ABCDEF" : "0123456789abcdef";
    char text[16];
    for (int i = 0; i < count && i < 16; i++)
    {
        text[i] = digits[(bits >> (4 * (count - 1 - i))) & 15];
    }

    output(text, (unsigned int)(count < 16 ? count : 16));
}

void ChipSharp_Runtime_WriteInt32(int value)
{
    write_number(magnitude_of(value), value < 0, 0);
}

void ChipSharp_Runtime_WriteLineInt32(int value)
{
    write_number(magnitude_of(value), value < 0, 1);
}

void ChipSharp_Runtime_WriteUInt32(unsigned int value)
{
    write_number(value, 0, 0);
}

void ChipSharp_Runtime_WriteLineUInt32(unsigned int value)
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

void ChipSharp_Runtime_WriteBool(int value)
{
    write_bool(value, 0);
}

void ChipSharp_Runtime_WriteLineBool(int value)
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
