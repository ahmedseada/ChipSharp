// ChipSharp runtime, shared by every runtime of a target with a heap: what makes strings (see chipsharp_string.c for
// how a string is laid out, and src/ChipSharp.Frontend/Strings.cs): the methods of string that give new text, text
// built from parts ($"...", +, string.Concat, string.Join), the text of numbers, bools and chars, and the spans of chars
// of a string.
//
// Freestanding: no headers and no C library. Each new string is an object of chipsharp_new_array (chipsharp_heap.c)
// with one of the two string descriptors: 1 byte per char when every char is below U+0100, else 2, always so. A method
// that changes nothing gives back the string it was given, as .NET's do. The references these functions hold while
// they make a string are on the stack or in registers, where the collector finds them.
//
//   ChipSharp_Runtime_StringConcat        the parts one after another (null as nothing): $"...", +, string.Concat
//   ChipSharp_Runtime_StringJoin          string.Join(separator, string[])
//   ChipSharp_Runtime_StringSubstring..., StringReplace..., StringTrim..., StringPad..., StringToUpper, StringToLower,
//   StringInsert, StringRemove...         the methods of string of those names, with .NET's results and checks
//   ChipSharp_Runtime_StringOf...         the text of a whole number (decimal with at least so many digits, or hex), of
//                                        a float or a double (.NET's shortest text), of a bool and of a char
//   ChipSharp_Runtime_StringAligned       text padded with spaces to an alignment ($"{x,5}", $"{x,-5}")
//   ChipSharp_Runtime_StringOfChars       new string(char[]), new string(ReadOnlySpan<char>), span.ToString()
//   ChipSharp_Runtime_StringRepeat        new string(c, count)
//   ChipSharp_Runtime_StringWideChars     the UTF-16 chars of a string for a span of them: its own when it has 2 bytes
//                                        per char, else those of a copy on the heap
//   ChipSharp_Runtime_StringToCharArray, StringSplit   ToCharArray and Split, given the descriptor of char[] and
//                                        string[] (the program's)

typedef __UINTPTR_TYPE__ uintptr_t;

typedef struct chipsharp_type chipsharp_type;

typedef struct string
{
    const chipsharp_type *type;
    int length;
} string;

#define DATA ((unsigned int)((sizeof(void *) + 4 + 7) / 8 * 8))
#define WORD ((unsigned int)sizeof(void *))
#define OUT_OF_RANGE "System.ArgumentOutOfRangeException: "

extern const chipsharp_type chipsharp_string_type;
extern const chipsharp_type chipsharp_wide_string_type;

void *chipsharp_new_array(const chipsharp_type *type, __INT32_TYPE__ length);
__attribute__((noreturn)) void chipsharp_fail(const char *message);
int ChipSharp_Runtime_CharIsWhiteSpace(int c);
int ChipSharp_Runtime_CharToUpper(int c);
int ChipSharp_Runtime_CharToLower(int c);
int chipsharp_float64_text(unsigned long long bits, char *text);
int chipsharp_float32_text(unsigned int bits, char *text);

static int is_wide(const string *s)
{
    return s->type == &chipsharp_wide_string_type;
}

static unsigned int at(const string *s, int i)
{
    const char *data = (const char *)s + DATA;
    return is_wide(s) ? ((const unsigned short *)data)[i] : ((const unsigned char *)data)[i];
}

static string *new_string(int length, int wide)
{
    return (string *)chipsharp_new_array(wide ? &chipsharp_wide_string_type : &chipsharp_string_type, length);
}

static void set(string *s, int i, unsigned int c)
{
    char *data = (char *)s + DATA;
    if (is_wide(s))
    {
        ((unsigned short *)data)[i] = (unsigned short)c;
    }
    else
    {
        ((unsigned char *)data)[i] = (unsigned char)c;
    }
}

// Copies count chars of source from start to destination at to; destination is wide when source is.
static void copy(string *destination, int to, const string *source, int start, int count)
{
    for (int i = 0; i < count; i++)
    {
        set(destination, to + i, at(source, start + i));
    }
}

static int any_wide(const string *s, int start, int count)
{
    if (!is_wide(s))
    {
        return 0;
    }

    for (int i = start; i < start + count; i++)
    {
        if (at(s, i) >= 0x100)
        {
            return 1;
        }
    }

    return 0;
}

// The chars of s from start, count of them, as a string: s itself when that is all of it.
static const string *slice(const string *s, int start, int count)
{
    if (start == 0 && count == s->length)
    {
        return s;
    }

    string *result = new_string(count, any_wide(s, start, count));
    copy(result, 0, s, start, count);
    return result;
}

// ---- Failures ----------------------------------------------------------------------------------------------------------

// Appends text to message at *n.
static void append(char *message, unsigned int *n, const char *text)
{
    while (*text != 0)
    {
        message[(*n)++] = *text++;
    }
}

static void append_number(char *message, unsigned int *n, long long value)
{
    char digits[24];
    int count = 0;
    unsigned long long magnitude = value < 0 ? 0ull - (unsigned long long)value : (unsigned long long)value;
    do
    {
        digits[count++] = (char)('0' + (int)(magnitude % 10));
        magnitude /= 10;
    } while (magnitude != 0);

    if (value < 0)
    {
        message[(*n)++] = '-';
    }

    while (count > 0)
    {
        message[(*n)++] = digits[--count];
    }
}

// "name ('value') must be a non-negative value. (Parameter 'name')\r\nActual value was value."
__attribute__((noreturn)) static void negative(const char *name, long long value)
{
    char message[256];
    unsigned int n = 0;
    append(message, &n, OUT_OF_RANGE);
    append(message, &n, name);
    append(message, &n, " ('");
    append_number(message, &n, value);
    append(message, &n, "') must be a non-negative value. (Parameter '");
    append(message, &n, name);
    append(message, &n, "')\r\nActual value was ");
    append_number(message, &n, value);
    append(message, &n, ".");
    message[n] = 0;
    chipsharp_fail(message);
}

// "name ('value') must be less than or equal to 'limit'. (Parameter 'name')\r\nActual value was value."
__attribute__((noreturn)) static void above(const char *name, long long value, long long limit)
{
    char message[256];
    unsigned int n = 0;
    append(message, &n, OUT_OF_RANGE);
    append(message, &n, name);
    append(message, &n, " ('");
    append_number(message, &n, value);
    append(message, &n, "') must be less than or equal to '");
    append_number(message, &n, limit);
    append(message, &n, "'. (Parameter '");
    append(message, &n, name);
    append(message, &n, "')\r\nActual value was ");
    append_number(message, &n, value);
    append(message, &n, ".");
    message[n] = 0;
    chipsharp_fail(message);
}

__attribute__((noreturn)) static void null_argument(const char *name)
{
    char message[96];
    unsigned int n = 0;
    append(message, &n, "System.ArgumentNullException: Value cannot be null. (Parameter '");
    append(message, &n, name);
    append(message, &n, "')");
    message[n] = 0;
    chipsharp_fail(message);
}

// ---- Built text --------------------------------------------------------------------------------------------------------

void *ChipSharp_Runtime_StringConcat(const string *const *parts, int count)
{
    int length = 0;
    int wide = 0;
    int filled = 0;
    const string *only = 0;
    for (int i = 0; i < count; i++)
    {
        if (parts[i] != 0 && parts[i]->length != 0)
        {
            length += parts[i]->length;
            wide |= is_wide(parts[i]);
            only = parts[i];
            filled++;
        }
    }

    // one part with text is the text itself
    if (filled == 1)
    {
        return (void *)only;
    }

    string *result = new_string(length, wide);
    int at_char = 0;
    for (int i = 0; i < count; i++)
    {
        if (parts[i] != 0)
        {
            copy(result, at_char, parts[i], 0, parts[i]->length);
            at_char += parts[i]->length;
        }
    }

    return result;
}

void *ChipSharp_Runtime_StringJoin(const string *separator, const string *const *parts, int count)
{
    if (parts == 0)
    {
        null_argument("value");
    }

    int length = 0;
    int wide = 0;
    for (int i = 0; i < count; i++)
    {
        if (parts[i] != 0)
        {
            length += parts[i]->length;
            wide |= is_wide(parts[i]);
        }
    }

    if (separator != 0 && count > 1)
    {
        length += separator->length * (count - 1);
        wide |= is_wide(separator);
    }

    if (count == 1 && parts[0] != 0)
    {
        return (void *)parts[0];
    }

    string *result = new_string(length, wide);
    int to = 0;
    for (int i = 0; i < count; i++)
    {
        if (i > 0 && separator != 0)
        {
            copy(result, to, separator, 0, separator->length);
            to += separator->length;
        }

        if (parts[i] != 0)
        {
            copy(result, to, parts[i], 0, parts[i]->length);
            to += parts[i]->length;
        }
    }

    return result;
}

// ---- The methods of string -----------------------------------------------------------------------------------------

const void *ChipSharp_Runtime_StringSubstring(const string *s, int start)
{
    if (start < 0)
    {
        negative("startIndex", start);
    }

    if (start > s->length)
    {
        chipsharp_fail(OUT_OF_RANGE "startIndex cannot be larger than length of string. (Parameter 'startIndex')");
    }

    return slice(s, start, s->length - start);
}

const void *ChipSharp_Runtime_StringSubstringOf(const string *s, int start, int length)
{
    if (start < 0)
    {
        negative("startIndex", start);
    }

    if (start > s->length)
    {
        chipsharp_fail(OUT_OF_RANGE "startIndex cannot be larger than length of string. (Parameter 'startIndex')");
    }

    if (length < 0)
    {
        negative("length", length);
    }

    if (start > s->length - length)
    {
        chipsharp_fail(OUT_OF_RANGE "Index and length must refer to a location within the string. (Parameter 'length')");
    }

    return slice(s, start, length);
}

const void *ChipSharp_Runtime_StringReplaceChar(const string *s, int old_char, int new_char)
{
    unsigned int from = (unsigned int)old_char & 0xFFFF;
    unsigned int to = (unsigned int)new_char & 0xFFFF;
    int first = -1;
    for (int i = 0; i < s->length && first < 0; i++)
    {
        if (at(s, i) == from)
        {
            first = i;
        }
    }

    if (first < 0)
    {
        return s;
    }

    // the result is wide when it keeps a wide char, or gets one
    int wide = to >= 0x100;
    for (int i = 0; i < s->length && !wide; i++)
    {
        unsigned int c = at(s, i);
        wide = c != from && c >= 0x100;
    }

    string *result = new_string(s->length, wide);
    for (int i = 0; i < s->length; i++)
    {
        unsigned int c = at(s, i);
        set(result, i, c == from ? to : c);
    }

    return result;
}

// True when old_value is in s at i.
static int is_at(const string *s, int i, const string *old_value)
{
    if (i + old_value->length > s->length)
    {
        return 0;
    }

    for (int k = 0; k < old_value->length; k++)
    {
        if (at(s, i + k) != at(old_value, k))
        {
            return 0;
        }
    }

    return 1;
}

const void *ChipSharp_Runtime_StringReplace(const string *s, const string *old_value, const string *new_value)
{
    if (old_value == 0)
    {
        null_argument("oldValue");
    }

    if (old_value->length == 0)
    {
        chipsharp_fail("System.ArgumentException: The value cannot be an empty string. (Parameter 'oldValue')");
    }

    int found = 0;
    for (int i = 0; i + old_value->length <= s->length;)
    {
        if (is_at(s, i, old_value))
        {
            found++;
            i += old_value->length;
        }
        else
        {
            i++;
        }
    }

    if (found == 0)
    {
        return s;
    }

    int new_length = new_value == 0 ? 0 : new_value->length;
    int length = s->length + found * (new_length - old_value->length);

    // wide when a char that stays is wide, or the new value is
    int wide = new_value != 0 && found > 0 && is_wide(new_value);
    for (int i = 0; i < s->length && !wide && is_wide(s);)
    {
        if (is_at(s, i, old_value))
        {
            i += old_value->length;
        }
        else
        {
            wide = at(s, i) >= 0x100;
            i++;
        }
    }

    string *result = new_string(length, wide);
    int to = 0;
    for (int i = 0; i < s->length;)
    {
        if (is_at(s, i, old_value))
        {
            if (new_length > 0)
            {
                copy(result, to, new_value, 0, new_length);
            }

            to += new_length;
            i += old_value->length;
        }
        else
        {
            set(result, to++, at(s, i++));
        }
    }

    return result;
}

// What trimming leaves: start and end (after the last char kept); whitespace when trim is -1, else that char.
static const string *trim(const string *s, int trim_char, int at_start, int at_end)
{
    int start = 0;
    int end = s->length;
    if (at_start)
    {
        while (start < end && (trim_char < 0 ? ChipSharp_Runtime_CharIsWhiteSpace((int)at(s, start))
            : at(s, start) == (unsigned int)trim_char))
        {
            start++;
        }
    }

    if (at_end)
    {
        while (end > start && (trim_char < 0 ? ChipSharp_Runtime_CharIsWhiteSpace((int)at(s, end - 1))
            : at(s, end - 1) == (unsigned int)trim_char))
        {
            end--;
        }
    }

    return slice(s, start, end - start);
}

const void *ChipSharp_Runtime_StringTrim(const string *s) { return trim(s, -1, 1, 1); }
const void *ChipSharp_Runtime_StringTrimStart(const string *s) { return trim(s, -1, 1, 0); }
const void *ChipSharp_Runtime_StringTrimEnd(const string *s) { return trim(s, -1, 0, 1); }
const void *ChipSharp_Runtime_StringTrimChar(const string *s, int c) { return trim(s, c & 0xFFFF, 1, 1); }
const void *ChipSharp_Runtime_StringTrimStartChar(const string *s, int c) { return trim(s, c & 0xFFFF, 1, 0); }
const void *ChipSharp_Runtime_StringTrimEndChar(const string *s, int c) { return trim(s, c & 0xFFFF, 0, 1); }

static const string *pad(const string *s, int width, int c, int left)
{
    if (width < 0)
    {
        negative("totalWidth", width);
    }

    unsigned int padding = (unsigned int)c & 0xFFFF;
    if (width <= s->length)
    {
        return s;
    }

    int count = width - s->length;
    string *result = new_string(width, is_wide(s) || padding >= 0x100);
    for (int i = 0; i < count; i++)
    {
        set(result, left ? i : s->length + i, padding);
    }

    copy(result, left ? count : 0, s, 0, s->length);
    return result;
}

const void *ChipSharp_Runtime_StringPadLeft(const string *s, int width) { return pad(s, width, ' ', 1); }
const void *ChipSharp_Runtime_StringPadLeftWith(const string *s, int width, int c) { return pad(s, width, c, 1); }
const void *ChipSharp_Runtime_StringPadRight(const string *s, int width) { return pad(s, width, ' ', 0); }
const void *ChipSharp_Runtime_StringPadRightWith(const string *s, int width, int c) { return pad(s, width, c, 0); }

static const string *change_case(const string *s, int upper)
{
    int changed = 0;
    int wide = 0;
    for (int i = 0; i < s->length; i++)
    {
        unsigned int c = at(s, i);
        unsigned int to = (unsigned int)(upper ? ChipSharp_Runtime_CharToUpper((int)c) : ChipSharp_Runtime_CharToLower((int)c));
        changed |= to != c;
        wide |= to >= 0x100;
    }

    if (!changed)
    {
        return s;
    }

    string *result = new_string(s->length, wide);
    for (int i = 0; i < s->length; i++)
    {
        unsigned int c = at(s, i);
        set(result, i, (unsigned int)(upper ? ChipSharp_Runtime_CharToUpper((int)c) : ChipSharp_Runtime_CharToLower((int)c)));
    }

    return result;
}

const void *ChipSharp_Runtime_StringToUpper(const string *s) { return change_case(s, 1); }
const void *ChipSharp_Runtime_StringToLower(const string *s) { return change_case(s, 0); }

const void *ChipSharp_Runtime_StringInsert(const string *s, int start, const string *value)
{
    if (value == 0)
    {
        null_argument("value");
    }

    if ((unsigned int)start > (unsigned int)s->length)
    {
        // .NET names the index as an unsigned number
        above("startIndex", (long long)(unsigned int)start, s->length);
    }

    if (value->length == 0)
    {
        return s;
    }

    if (s->length == 0)
    {
        return value;
    }

    string *result = new_string(s->length + value->length, is_wide(s) || is_wide(value));
    copy(result, 0, s, 0, start);
    copy(result, start, value, 0, value->length);
    copy(result, start + value->length, s, start, s->length - start);
    return result;
}

const void *ChipSharp_Runtime_StringRemove(const string *s, int start)
{
    if (start < 0)
    {
        chipsharp_fail(OUT_OF_RANGE "StartIndex cannot be less than zero. (Parameter 'startIndex')");
    }

    if (start > s->length)
    {
        chipsharp_fail(OUT_OF_RANGE "startIndex cannot be larger than length of string. (Parameter 'startIndex')");
    }

    return slice(s, 0, start);
}

const void *ChipSharp_Runtime_StringRemoveCount(const string *s, int start, int count)
{
    if (start < 0)
    {
        negative("startIndex", start);
    }

    if (count < 0)
    {
        negative("count", count);
    }

    if (count > s->length - start)
    {
        above("count", count, s->length - start);
    }

    if (count == 0)
    {
        return s;
    }

    int length = s->length - count;
    int wide = any_wide(s, 0, start) || any_wide(s, start + count, s->length - start - count);
    string *result = new_string(length, wide);
    copy(result, 0, s, 0, start);
    copy(result, start, s, start + count, s->length - start - count);
    return result;
}

// ---- New strings -----------------------------------------------------------------------------------------------------

void *ChipSharp_Runtime_StringRepeat(int c, int count)
{
    if (count < 0)
    {
        negative("count", count);
    }

    unsigned int unit = (unsigned int)c & 0xFFFF;
    string *result = new_string(count, unit >= 0x100);
    for (int i = 0; i < count; i++)
    {
        set(result, i, unit);
    }

    return result;
}

void *ChipSharp_Runtime_StringOfChars(const unsigned short *chars, int count)
{
    int wide = 0;
    for (int i = 0; i < count && !wide; i++)
    {
        wide = chars[i] >= 0x100;
    }

    string *result = new_string(count, wide);
    for (int i = 0; i < count; i++)
    {
        set(result, i, chars[i]);
    }

    return result;
}

void *ChipSharp_Runtime_StringOfChar(int c)
{
    return ChipSharp_Runtime_StringRepeat(c, 1);
}

const void *ChipSharp_Runtime_StringWideChars(const string *s)
{
    if (is_wide(s))
    {
        return (const char *)s + DATA;
    }

    string *copy_of = (string *)chipsharp_new_array(&chipsharp_wide_string_type, s->length);
    copy(copy_of, 0, s, 0, s->length);
    return (const char *)copy_of + DATA;
}

// An array object of the program's (its descriptor given), length elements.
static char *new_array(const chipsharp_type *type, int length)
{
    return (char *)chipsharp_new_array(type, length);
}

void *ChipSharp_Runtime_StringToCharArray(const string *s, const chipsharp_type *char_array)
{
    char *array = new_array(char_array, s->length);
    unsigned short *chars = (unsigned short *)(array + DATA);
    for (int i = 0; i < s->length; i++)
    {
        chars[i] = (unsigned short)at(s, i);
    }

    return array;
}

// Split(separator, options): None 0, RemoveEmptyEntries 1, TrimEntries 2 (both: 3), as .NET splits.
void *ChipSharp_Runtime_StringSplit(const string *s, int separator, int options, const chipsharp_type *string_array)
{
    unsigned int split = (unsigned int)separator & 0xFFFF;
    int remove_empty = options & 1;
    int trim_entries = options & 2;

    // first count the parts, then make each
    int parts = 0;
    for (int pass = 0; pass < 2; pass++)
    {
        char *array = pass == 1 ? new_array(string_array, parts) : 0;
        int count = 0;
        int start = 0;
        for (int i = 0; i <= s->length; i++)
        {
            if (i < s->length && at(s, i) != split)
            {
                continue;
            }

            int from = start;
            int to = i;
            start = i + 1;
            if (trim_entries)
            {
                while (from < to && ChipSharp_Runtime_CharIsWhiteSpace((int)at(s, from)))
                {
                    from++;
                }

                while (to > from && ChipSharp_Runtime_CharIsWhiteSpace((int)at(s, to - 1)))
                {
                    to--;
                }
            }

            if (remove_empty && to == from)
            {
                continue;
            }

            if (array != 0)
            {
                ((const string **)(array + DATA))[count] = slice(s, from, to - from);
            }

            count++;
        }

        if (pass == 1)
        {
            return array;
        }

        parts = count;
    }

    return 0;
}

// ---- The text of values ----------------------------------------------------------------------------------------------

// A string of the ASCII text[0..count).
static void *of_ascii(const char *text, int count)
{
    string *result = new_string(count, 0);
    for (int i = 0; i < count; i++)
    {
        set(result, i, (unsigned char)text[i]);
    }

    return result;
}

// The magnitude in decimal, with at least precision digits (zeros after the minus sign).
static void *of_decimal(unsigned long long magnitude, int negative_number, int precision)
{
    char digits[24];
    int count = 0;
    do
    {
        digits[count++] = (char)('0' + (int)(magnitude % 10));
        magnitude /= 10;
    } while (magnitude != 0);

    int width = count > precision ? count : precision;
    string *result = new_string(width + (negative_number ? 1 : 0), 0);
    int to = 0;
    if (negative_number)
    {
        set(result, to++, '-');
    }

    for (int i = count; i < width; i++)
    {
        set(result, to++, '0');
    }

    while (count > 0)
    {
        set(result, to++, (unsigned char)digits[--count]);
    }

    return result;
}

void *ChipSharp_Runtime_StringOfInt64(long long value, int precision)
{
    return of_decimal(value < 0 ? 0ull - (unsigned long long)value : (unsigned long long)value, value < 0, precision);
}

void *ChipSharp_Runtime_StringOfUInt64(long long value, int precision)
{
    return of_decimal((unsigned long long)value, 0, precision);
}

// The hex digits of bits (as unsigned), at least precision of them.
void *ChipSharp_Runtime_StringOfHex(long long bits, int precision, int upper)
{
    unsigned long long value = (unsigned long long)bits;
    int count = 1;
    while (count < 16 && (value >> (4 * count)) != 0)
    {
        count++;
    }

    int width = count > precision ? count : precision;
    const char *digits = upper ? "0123456789ABCDEF" : "0123456789abcdef";
    string *result = new_string(width, 0);
    for (int i = 0; i < width; i++)
    {
        int shift = 4 * (width - 1 - i);
        set(result, i, shift >= 64 ? '0' : (unsigned char)digits[(value >> shift) & 15]);
    }

    return result;
}

void *ChipSharp_Runtime_StringOfFloat64(double value)
{
    union
    {
        double value;
        unsigned long long bits;
    } number = {value};
    char text[26];
    return of_ascii(text, chipsharp_float64_text(number.bits, text));
}

void *ChipSharp_Runtime_StringOfFloat32(float value)
{
    union
    {
        float value;
        unsigned int bits;
    } number = {value};
    char text[17];
    return of_ascii(text, chipsharp_float32_text(number.bits, text));
}

void *ChipSharp_Runtime_StringOfBool(int value)
{
    return value != 0 ? of_ascii("True", 4) : of_ascii("False", 5);
}

// Text (null as nothing) padded with spaces on the left to alignment chars, or on the right to -alignment.
const void *ChipSharp_Runtime_StringAligned(const string *s, int alignment)
{
    static const string empty = {&chipsharp_string_type, 0};
    const string *text = s == 0 ? &empty : s;
    int width = alignment < 0 ? -alignment : alignment;
    if (width <= text->length)
    {
        return text == &empty ? ChipSharp_Runtime_StringRepeat(' ', 0) : text;
    }

    return pad(text, width, ' ', alignment > 0);
}

// ---- Spans of chars --------------------------------------------------------------------------------------------------

// s.AsSpan(start) (has_length 0) or s.AsSpan(start, length) checked as .NET checks them: null is the empty span when
// start (and length) are 0.
static void check_span(const string *s, int start, int length, int has_length)
{
    if (s == 0)
    {
        if (start != 0 || (has_length && length != 0))
        {
            chipsharp_fail(OUT_OF_RANGE "Specified argument was out of the range of valid values. (Parameter 'start')");
        }

        return;
    }

    unsigned long long end = (unsigned long long)(unsigned int)start + (has_length ? (unsigned int)length : 0u);
    if ((unsigned int)start > (unsigned int)s->length || end > (unsigned long long)(unsigned int)s->length)
    {
        chipsharp_fail(OUT_OF_RANGE "Specified argument was out of the range of valid values. (Parameter 'start')");
    }
}

// The address of char start of the span: in the string itself when it has 2 bytes per char, else in a UTF-16 copy.
const void *ChipSharp_Runtime_StringSpanChars(const string *s, int start, int length, int has_length)
{
    check_span(s, start, length, has_length);
    if (s == 0)
    {
        return 0;
    }

    return (const unsigned short *)ChipSharp_Runtime_StringWideChars(s) + start;
}

int ChipSharp_Runtime_StringSpanLength(const string *s, int start, int length, int has_length)
{
    check_span(s, start, length, has_length);
    return s == 0 ? 0 : has_length ? length : s->length - start;
}

// new string(char[] value, int startIndex, int length), checked as .NET checks it.
void *ChipSharp_Runtime_StringOfCharRange(const unsigned short *chars, int count, int start, int length)
{
    if (start < 0)
    {
        chipsharp_fail(OUT_OF_RANGE "StartIndex cannot be less than zero. (Parameter 'startIndex')");
    }

    if (length < 0)
    {
        chipsharp_fail(OUT_OF_RANGE "Length cannot be less than zero. (Parameter 'length')");
    }

    if (start > count - length)
    {
        chipsharp_fail(OUT_OF_RANGE "Index and length must refer to a location within the string. (Parameter 'startIndex')");
    }

    return ChipSharp_Runtime_StringOfChars(chars + start, length);
}

// A type's name, as its descriptor holds it (UTF-8 ending with a 0 byte), as a string: object's own ToString.
void *ChipSharp_Runtime_StringOfName(const char *name)
{
    // the chars first: a byte that starts a UTF-8 sequence is one (a 4-byte sequence two, a surrogate pair)
    int count = 0;
    int wide = 0;
    for (const unsigned char *c = (const unsigned char *)name; *c != 0; c++)
    {
        if ((*c & 0xC0) != 0x80)
        {
            count += *c >= 0xF0 ? 2 : 1;
            wide |= *c >= 0xC4;
        }
    }

    string *result = new_string(count, wide);
    int to = 0;
    for (const unsigned char *c = (const unsigned char *)name; *c != 0;)
    {
        unsigned int unit = *c++;
        if (unit >= 0xF0)
        {
            unsigned int point = (unit & 0x07) << 18 | (c[0] & 0x3Fu) << 12 | (c[1] & 0x3Fu) << 6 | (c[2] & 0x3Fu);
            c += 3;
            point -= 0x10000;
            set(result, to++, 0xD800 + (point >> 10));
            set(result, to++, 0xDC00 + (point & 0x3FF));
            continue;
        }

        if (unit >= 0xE0)
        {
            unit = (unit & 0x0F) << 12 | (c[0] & 0x3Fu) << 6 | (c[1] & 0x3Fu);
            c += 2;
        }
        else if (unit >= 0xC0)
        {
            unit = (unit & 0x1F) << 6 | (c[0] & 0x3Fu);
            c += 1;
        }

        set(result, to++, unit);
    }

    return result;
}

// ---- Failures of the substitutes that build text (ChipSharp.Library's StringBuilder) ------------------------------------
//
// ChipSharp.Library's Runtime.ArgumentOutOfRangeOf, ArgumentNegative, ArgumentNotPositive, ArgumentNullOf and
// ArgumentInvalid: .NET's argument exceptions, their parameter's name and message given as strings.

#define FAILURE_SIZE 512

// Appends the chars of s (null as nothing) to message at *n as UTF-8, while there is room.
static void append_string(char *message, unsigned int *n, const string *s)
{
    for (int i = 0; s != 0 && i < s->length && *n < FAILURE_SIZE - 8; i++)
    {
        unsigned int c = at(s, i);
        if (c < 0x80)
        {
            message[(*n)++] = (char)c;
        }
        else if (c < 0x800)
        {
            message[(*n)++] = (char)(0xC0 | c >> 6);
            message[(*n)++] = (char)(0x80 | (c & 0x3F));
        }
        else
        {
            message[(*n)++] = (char)(0xE0 | c >> 12);
            message[(*n)++] = (char)(0x80 | (c >> 6 & 0x3F));
            message[(*n)++] = (char)(0x80 | (c & 0x3F));
        }
    }
}

// " (Parameter 'name')"
static void append_parameter(char *message, unsigned int *n, const string *parameter)
{
    append(message, n, " (Parameter '");
    append_string(message, n, parameter);
    append(message, n, "')");
}

__attribute__((noreturn)) void ChipSharp_Runtime_ArgumentOutOfRangeOf(const string *parameter, const string *text)
{
    char message[FAILURE_SIZE];
    unsigned int n = 0;
    append(message, &n, OUT_OF_RANGE);
    append_string(message, &n, text);
    append_parameter(message, &n, parameter);
    message[n] = 0;
    chipsharp_fail(message);
}

// "name ('value') <rule> (Parameter 'name')\r\nActual value was value."
__attribute__((noreturn)) static void argument_rule(const string *parameter, int value, const char *rule)
{
    char message[FAILURE_SIZE];
    unsigned int n = 0;
    append(message, &n, OUT_OF_RANGE);
    append_string(message, &n, parameter);
    append(message, &n, " ('");
    append_number(message, &n, value);
    append(message, &n, "') ");
    append(message, &n, rule);
    append_parameter(message, &n, parameter);
    append(message, &n, "\r\nActual value was ");
    append_number(message, &n, value);
    append(message, &n, ".");
    message[n] = 0;
    chipsharp_fail(message);
}

__attribute__((noreturn)) void ChipSharp_Runtime_ArgumentNegative(const string *parameter, int value)
{
    argument_rule(parameter, value, "must be a non-negative value.");
}

__attribute__((noreturn)) void ChipSharp_Runtime_ArgumentNotPositive(const string *parameter, int value)
{
    argument_rule(parameter, value, "must be a non-negative and non-zero value.");
}

__attribute__((noreturn)) void ChipSharp_Runtime_ArgumentNullOf(const string *parameter)
{
    char message[FAILURE_SIZE];
    unsigned int n = 0;
    append(message, &n, "System.ArgumentNullException: Value cannot be null.");
    append_parameter(message, &n, parameter);
    message[n] = 0;
    chipsharp_fail(message);
}

__attribute__((noreturn)) void ChipSharp_Runtime_ArgumentInvalid(const string *text, const string *parameter)
{
    char message[FAILURE_SIZE];
    unsigned int n = 0;
    append(message, &n, "System.ArgumentException: ");
    append_string(message, &n, text);
    if (parameter != 0)
    {
        append_parameter(message, &n, parameter);
    }

    message[n] = 0;
    chipsharp_fail(message);
}
