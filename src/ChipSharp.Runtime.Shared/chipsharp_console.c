// Console.Write and Console.WriteLine, shared by every runtime: the text of text, numbers and bools as .NET writes
// them, each written with one call to chipsharp_console_write, which each runtime has (a PC's standard output, a
// chip's UART). Lines end with "\r\n", as .NET ends them on Windows, where the samples' expected outputs come from.
//
// Freestanding: no headers and no C library.
//
//   ChipSharp_Runtime_Write<Type>        Console.Write(value): String, Int32, UInt32, Int64, UInt64, Bool, Float32,
//                                        Float64; floats and doubles as text come from chipsharp_float_text.c
//   ChipSharp_Runtime_WriteLine<Type>    Console.WriteLine(value): the same, then "\r\n"
//   ChipSharp_Runtime_WriteLine          Console.WriteLine(): "\r\n"
//
// Text (C#'s string) is the address of a literal's record (src/ChipSharp.Frontend/FixedText.cs), or null:
//   offset 0  u32 little-endian  the length in UTF-16 code units, as .NET counts it (not needed here)
//   offset 4  u32 little-endian  how many bytes of UTF-8 follow
//   offset 8                     the text in UTF-8, written as it is
// Null writes nothing, as in .NET.

// In the runtime: writes count bytes where the program's output goes.
void chipsharp_console_write(const char *bytes, unsigned int count);

// In chipsharp_float_text.c: a double or a float, given by its bits, as .NET writes it; returns the length.
int chipsharp_float64_text(unsigned long long bits, char *text);
int chipsharp_float32_text(unsigned int bits, char *text);

#define TEXT_BYTE_COUNT 4
#define TEXT_BYTES 8

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

    chipsharp_console_write(text + start, (unsigned int)((int)sizeof text - start));
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
        chipsharp_console_write("True\r\n", line ? 6u : 4u);
    }
    else
    {
        chipsharp_console_write("False\r\n", line ? 7u : 5u);
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

    chipsharp_console_write(text, (unsigned int)length);
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

// The UTF-8 of a literal, its byte count read a byte at a time (a record needs no alignment); null is no text.
static void write_string(const unsigned char *record)
{
    if (record == 0)
    {
        return;
    }

    const unsigned char *count = record + TEXT_BYTE_COUNT;
    unsigned int bytes = (unsigned int)count[0] | (unsigned int)count[1] << 8 | (unsigned int)count[2] << 16
        | (unsigned int)count[3] << 24;
    chipsharp_console_write((const char *)(record + TEXT_BYTES), bytes);
}

void ChipSharp_Runtime_WriteLine(void)
{
    chipsharp_console_write("\r\n", 2);
}

void ChipSharp_Runtime_WriteString(const unsigned char *value)
{
    write_string(value);
}

// The literal's bytes as they are, then the end of the line: the one WriteLine that writes twice.
void ChipSharp_Runtime_WriteLineString(const unsigned char *value)
{
    write_string(value);
    ChipSharp_Runtime_WriteLine();
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
