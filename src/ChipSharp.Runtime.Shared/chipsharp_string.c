// ChipSharp runtime, shared by every runtime: strings (C#'s string, see src/ChipSharp.Frontend/Strings.cs) and chars,
// what reads them without making new ones. What makes strings is in chipsharp_string_heap.c, for a target with a heap.
//
// Freestanding: no headers and no C library.
//
// A string is an object laid out as an array object: its header (the address of its type), its length (an int, in
// UTF-16 code units, as .NET counts them), then from DATA on its chars, 1 byte each (chipsharp_string_type: every char
// is below U+0100) or 2 bytes each (chipsharp_wide_string_type, UTF-16 code units). The form is canonical: a string is
// 1 byte per char exactly when every char is below U+0100, so two equal strings have the same form. A literal is such
// an object in read-only data (flash on a chip), outside the heap; built text is one on the heap.
//
//   chipsharp_string_type, chipsharp_wide_string_type   the two descriptors, laid out as TirTypeDescriptor says
//   ChipSharp_Runtime_StringChar          s[i], with .NET's IndexOutOfRangeException outside it
//   ChipSharp_Runtime_StringEquals        ==, Equals, string.Equals: char by char; null equals null only
//   ChipSharp_Runtime_StringNotEquals     !=
//   ChipSharp_Runtime_StringEqualsObject  Equals(object): another string with the same chars
//   ChipSharp_Runtime_StringCompareOrdinal  string.CompareOrdinal: the difference of the first chars that differ, else
//                                        of the lengths (null first)
//   ChipSharp_Runtime_StringCompare       CompareTo and string.Compare: the same order, as -1, 0 or 1 (ordinal: .NET's
//                                        own compare by the current culture, which puts "a" before "B")
//   ChipSharp_Runtime_StringHash          GetHashCode: FNV-1a of the UTF-16 code units, the same in every run (.NET's
//                                        is different in each process)
//   ChipSharp_Runtime_StringIsNullOrEmpty, StringIsNullOrWhiteSpace
//   ChipSharp_Runtime_StringIndexOf...    IndexOf, LastIndexOf, Contains, StartsWith, EndsWith of a char or a string,
//                                        ordinal (.NET compares strings by culture in IndexOf, LastIndexOf, StartsWith
//                                        and EndsWith of a string; the same for text without characters the culture
//                                        ignores, such as a soft hyphen or a zero)
//   ChipSharp_Runtime_Char...             char.IsDigit, IsLetter, IsWhiteSpace, IsUpper, IsLower, IsLetterOrDigit,
//                                        ToUpper, ToLower (and their Invariant forms), as .NET has them for the chars
//                                        below U+0100 (Latin-1); from U+0100 on, IsWhiteSpace knows .NET's spaces, and a
//                                        char is no letter, digit, upper or lower case letter, and changes case not
//                                        (but for U+0178 and U+039C, the upper case of U+00FF and U+00B5)
//   ChipSharp_Runtime_Parse..., TryParse...  int.Parse and int.TryParse, and those of the other whole numbers: spaces
//                                        before and after, a sign, decimal digits, as .NET's invariant culture reads
//                                        them; FormatException and OverflowException with .NET's messages
//
// Failures say what .NET says through chipsharp_fail (each runtime's failures file): "System.ArgumentOutOfRangeException:
// ..." after "Unhandled exception. ".

typedef __UINTPTR_TYPE__ uintptr_t;

// A type descriptor, as the code generator writes it (see TirTypeDescriptor): a string's has no virtual methods.
typedef struct chipsharp_type
{
    unsigned int size;
    unsigned int element_size;
    unsigned int flags;
    unsigned int reference_count;
    const unsigned int *references;
    const struct chipsharp_type *base;
    const char *name;
    const void *interfaces;
    unsigned int interface_count;
    unsigned int virtual_count;
} chipsharp_type;

typedef struct string
{
    const chipsharp_type *type;
    int length;
} string;

#define DATA ((unsigned int)((sizeof(void *) + 4 + 7) / 8 * 8))
#define TYPE_ARRAY 1u

const chipsharp_type chipsharp_string_type = {DATA, 1, TYPE_ARRAY, 0, 0, 0, "System.String", 0, 0, 0};
const chipsharp_type chipsharp_wide_string_type = {DATA, 2, TYPE_ARRAY, 0, 0, 0, "System.String", 0, 0, 0};

// In each runtime's failures file: stops the program with "Unhandled exception. " and the message.
__attribute__((noreturn)) void chipsharp_fail(const char *message);
__attribute__((noreturn)) void ChipSharp_Runtime_IndexOutOfRange(void);

static int is_wide(const string *s)
{
    return s->type->element_size == 2;
}

static unsigned int at(const string *s, int i)
{
    const char *data = (const char *)s + DATA;
    return is_wide(s) ? ((const unsigned short *)data)[i] : ((const unsigned char *)data)[i];
}

int ChipSharp_Runtime_StringChar(const string *s, int index)
{
    if ((unsigned int)index >= (unsigned int)s->length)
    {
        ChipSharp_Runtime_IndexOutOfRange();
    }

    return (int)at(s, index);
}

// True when the chars of a from start, count of them, are those of b from 0.
static int same_chars(const string *a, int start, const string *b, int count)
{
    for (int i = 0; i < count; i++)
    {
        if (at(a, start + i) != at(b, i))
        {
            return 0;
        }
    }

    return 1;
}

int ChipSharp_Runtime_StringEquals(const string *a, const string *b)
{
    if (a == b)
    {
        return 1;
    }

    if (a == 0 || b == 0 || a->length != b->length || is_wide(a) != is_wide(b))
    {
        return 0;
    }

    return same_chars(a, 0, b, a->length);
}

int ChipSharp_Runtime_StringNotEquals(const string *a, const string *b)
{
    return !ChipSharp_Runtime_StringEquals(a, b);
}

// True for a string object: its header is one of the two descriptors.
static int is_string(const void *object)
{
    const chipsharp_type *type = *(const chipsharp_type *const *)object;
    return type == &chipsharp_string_type || type == &chipsharp_wide_string_type;
}

int ChipSharp_Runtime_StringEqualsObject(const string *s, const void *other)
{
    return other != 0 && is_string(other) && ChipSharp_Runtime_StringEquals(s, (const string *)other);
}

int ChipSharp_Runtime_StringCompareOrdinal(const string *a, const string *b)
{
    if (a == b)
    {
        return 0;
    }

    if (a == 0 || b == 0)
    {
        return a == 0 ? -1 : 1;
    }

    int count = a->length < b->length ? a->length : b->length;
    for (int i = 0; i < count; i++)
    {
        int difference = (int)at(a, i) - (int)at(b, i);
        if (difference != 0)
        {
            return difference;
        }
    }

    return a->length - b->length;
}

int ChipSharp_Runtime_StringCompare(const string *a, const string *b)
{
    int order = ChipSharp_Runtime_StringCompareOrdinal(a, b);
    return order < 0 ? -1 : order > 0 ? 1 : 0;
}

int ChipSharp_Runtime_StringHash(const string *s)
{
    unsigned int hash = 2166136261u;
    for (int i = 0; i < s->length; i++)
    {
        hash = (hash ^ at(s, i)) * 16777619u;
    }

    return (int)hash;
}

// ---- Chars -----------------------------------------------------------------------------------------------------------

int ChipSharp_Runtime_CharIsWhiteSpace(int c)
{
    return (c >= 0x09 && c <= 0x0D) || c == 0x20 || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A)
        || c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000;
}

int ChipSharp_Runtime_CharIsDigit(int c)
{
    return c >= '0' && c <= '9';
}

int ChipSharp_Runtime_CharIsUpper(int c)
{
    return (c >= 'A' && c <= 'Z') || (c >= 0xC0 && c <= 0xDE && c != 0xD7);
}

int ChipSharp_Runtime_CharIsLower(int c)
{
    return (c >= 'a' && c <= 'z') || c == 0xB5 || (c >= 0xDF && c <= 0xFF && c != 0xF7);
}

int ChipSharp_Runtime_CharIsLetter(int c)
{
    return ChipSharp_Runtime_CharIsUpper(c) || ChipSharp_Runtime_CharIsLower(c) || c == 0xAA || c == 0xBA;
}

int ChipSharp_Runtime_CharIsLetterOrDigit(int c)
{
    return ChipSharp_Runtime_CharIsLetter(c) || ChipSharp_Runtime_CharIsDigit(c);
}

int ChipSharp_Runtime_CharToUpper(int c)
{
    if ((c >= 'a' && c <= 'z') || (c >= 0xE0 && c <= 0xFE && c != 0xF7))
    {
        return c - 0x20;
    }

    return c == 0xFF ? 0x178 : c == 0xB5 ? 0x39C : c;
}

int ChipSharp_Runtime_CharToLower(int c)
{
    return (c >= 'A' && c <= 'Z') || (c >= 0xC0 && c <= 0xDE && c != 0xD7) ? c + 0x20 : c;
}

// ---- Searching -------------------------------------------------------------------------------------------------------

#define OUT_OF_RANGE "System.ArgumentOutOfRangeException: "
#define START_INDEX_LE "Index was out of range. Must be non-negative and less than or equal to the size of the collection. " \
    "(Parameter 'startIndex')"
#define START_INDEX_LT "Index was out of range. Must be non-negative and less than the size of the collection. " \
    "(Parameter 'startIndex')"
#define COUNT_RANGE "Count must be positive and count must refer to a location within the string/array/collection. " \
    "(Parameter 'count')"

__attribute__((noreturn)) static void null_value(void)
{
    chipsharp_fail("System.ArgumentNullException: Value cannot be null. (Parameter 'value')");
}

// Where c is first in s from start, count chars at most; -1 when it is not there.
static int find_char(const string *s, unsigned int c, int start, int count)
{
    for (int i = start; i < start + count; i++)
    {
        if (at(s, i) == c)
        {
            return i;
        }
    }

    return -1;
}

// Where value is first in s from start in count chars at most (the whole value inside them); -1 when it is not there.
static int find(const string *s, const string *value, int start, int count)
{
    for (int i = start; i + value->length <= start + count; i++)
    {
        if (same_chars(s, i, value, value->length))
        {
            return i;
        }
    }

    return -1;
}

int ChipSharp_Runtime_StringIndexOfChar(const string *s, int c)
{
    return find_char(s, (unsigned int)c & 0xFFFF, 0, s->length);
}

int ChipSharp_Runtime_StringIndexOfCharFrom(const string *s, int c, int start)
{
    if ((unsigned int)start > (unsigned int)s->length)
    {
        chipsharp_fail(OUT_OF_RANGE START_INDEX_LE);
    }

    return find_char(s, (unsigned int)c & 0xFFFF, start, s->length - start);
}

int ChipSharp_Runtime_StringIndexOfCharIn(const string *s, int c, int start, int count)
{
    if ((unsigned int)start > (unsigned int)s->length)
    {
        chipsharp_fail(OUT_OF_RANGE START_INDEX_LE);
    }

    if ((unsigned int)count > (unsigned int)(s->length - start))
    {
        chipsharp_fail(OUT_OF_RANGE COUNT_RANGE);
    }

    return find_char(s, (unsigned int)c & 0xFFFF, start, count);
}

int ChipSharp_Runtime_StringIndexOf(const string *s, const string *value)
{
    if (value == 0)
    {
        null_value();
    }

    return find(s, value, 0, s->length);
}

int ChipSharp_Runtime_StringIndexOfFrom(const string *s, const string *value, int start)
{
    if (value == 0)
    {
        null_value();
    }

    if ((unsigned int)start > (unsigned int)s->length)
    {
        chipsharp_fail(OUT_OF_RANGE START_INDEX_LE);
    }

    return find(s, value, start, s->length - start);
}

int ChipSharp_Runtime_StringLastIndexOfChar(const string *s, int c)
{
    for (int i = s->length - 1; i >= 0; i--)
    {
        if (at(s, i) == ((unsigned int)c & 0xFFFF))
        {
            return i;
        }
    }

    return -1;
}

int ChipSharp_Runtime_StringLastIndexOfCharFrom(const string *s, int c, int start)
{
    if (s->length == 0)
    {
        return -1;
    }

    if ((unsigned int)start >= (unsigned int)s->length)
    {
        chipsharp_fail(OUT_OF_RANGE START_INDEX_LT);
    }

    for (int i = start; i >= 0; i--)
    {
        if (at(s, i) == ((unsigned int)c & 0xFFFF))
        {
            return i;
        }
    }

    return -1;
}

// .NET finds the empty string at the end, at the string's length.
int ChipSharp_Runtime_StringLastIndexOf(const string *s, const string *value)
{
    if (value == 0)
    {
        null_value();
    }

    for (int i = s->length - value->length; i >= 0; i--)
    {
        if (same_chars(s, i, value, value->length))
        {
            return i;
        }
    }

    return -1;
}

int ChipSharp_Runtime_StringContains(const string *s, const string *value)
{
    return ChipSharp_Runtime_StringIndexOf(s, value) >= 0;
}

int ChipSharp_Runtime_StringContainsChar(const string *s, int c)
{
    return ChipSharp_Runtime_StringIndexOfChar(s, c) >= 0;
}

int ChipSharp_Runtime_StringStartsWith(const string *s, const string *value)
{
    if (value == 0)
    {
        null_value();
    }

    return value->length <= s->length && same_chars(s, 0, value, value->length);
}

int ChipSharp_Runtime_StringEndsWith(const string *s, const string *value)
{
    if (value == 0)
    {
        null_value();
    }

    return value->length <= s->length && same_chars(s, s->length - value->length, value, value->length);
}

int ChipSharp_Runtime_StringStartsWithChar(const string *s, int c)
{
    return s->length != 0 && at(s, 0) == ((unsigned int)c & 0xFFFF);
}

int ChipSharp_Runtime_StringEndsWithChar(const string *s, int c)
{
    return s->length != 0 && at(s, s->length - 1) == ((unsigned int)c & 0xFFFF);
}

int ChipSharp_Runtime_StringIsNullOrEmpty(const string *s)
{
    return s == 0 || s->length == 0;
}

int ChipSharp_Runtime_StringIsNullOrWhiteSpace(const string *s)
{
    if (s != 0)
    {
        for (int i = 0; i < s->length; i++)
        {
            if (!ChipSharp_Runtime_CharIsWhiteSpace((int)at(s, i)))
            {
                return 0;
            }
        }
    }

    return 1;
}

// ---- Parsing ---------------------------------------------------------------------------------------------------------

// The text of a failure: a prefix, the string's chars as UTF-8 (the first 200 of them), a suffix.
__attribute__((noreturn)) static void fail_with(const char *prefix, const string *s, const char *suffix)
{
    char message[96 + 200 * 3 + 64];
    unsigned int n = 0;
    while (*prefix != 0)
    {
        message[n++] = *prefix++;
    }

    for (int i = 0; i < s->length && i < 200; i++)
    {
        unsigned int c = at(s, i);
        if (c < 0x80)
        {
            message[n++] = (char)c;
        }
        else if (c < 0x800)
        {
            message[n++] = (char)(0xC0 | (c >> 6));
            message[n++] = (char)(0x80 | (c & 0x3F));
        }
        else
        {
            // a surrogate is as UTF-8 cannot hold it on its own: U+FFFD (the pair is seldom in a number)
            if (c >= 0xD800 && c <= 0xDFFF)
            {
                c = 0xFFFD;
            }

            message[n++] = (char)(0xE0 | (c >> 12));
            message[n++] = (char)(0x80 | ((c >> 6) & 0x3F));
            message[n++] = (char)(0x80 | (c & 0x3F));
        }
    }

    while (*suffix != 0)
    {
        message[n++] = *suffix++;
    }

    message[n] = 0;
    chipsharp_fail(message);
}

static int is_parse_space(unsigned int c)
{
    return (c >= 0x09 && c <= 0x0D) || c == 0x20;
}

// Reads a whole number from s as .NET's NumberStyles.Integer does: spaces, a sign, digits, spaces (and zeros, the char
// '\0', at the very end). 1 with *magnitude and *negative set; 0 when it is not one; -1 when it does not fit in 64 bits.
static int read_integer(const string *s, unsigned long long *magnitude, int *negative)
{
    int i = 0;
    int end = s->length;
    while (end > 0 && at(s, end - 1) == 0)
    {
        end--;
    }

    while (i < end && is_parse_space(at(s, i)))
    {
        i++;
    }

    while (end > i && is_parse_space(at(s, end - 1)))
    {
        end--;
    }

    *negative = 0;
    if (i < end && (at(s, i) == '-' || at(s, i) == '+'))
    {
        *negative = at(s, i) == '-';
        i++;
    }

    if (i == end)
    {
        return 0;
    }

    unsigned long long value = 0;
    int overflow = 0;
    for (; i < end; i++)
    {
        unsigned int c = at(s, i);
        if (c < '0' || c > '9')
        {
            return 0;
        }

        unsigned long long digit = c - '0';
        if (value > (18446744073709551615ull - digit) / 10)
        {
            overflow = 1;
        }
        else
        {
            value = value * 10 + digit;
        }
    }

    *magnitude = value;
    return overflow ? -1 : 1;
}

// The kinds of whole numbers, by how parse checks the range: the C# name .NET's overflow message gives it.
enum
{
    INT8,
    UINT8,
    INT16,
    UINT16,
    INT32,
    UINT32,
    INT64,
    UINT64,
};

static const char *const overflow_messages[] = {
    "System.OverflowException: Value was either too large or too small for a signed byte.",
    "System.OverflowException: Value was either too large or too small for an unsigned byte.",
    "System.OverflowException: Value was either too large or too small for an Int16.",
    "System.OverflowException: Value was either too large or too small for a UInt16.",
    "System.OverflowException: Value was either too large or too small for an Int32.",
    "System.OverflowException: Value was either too large or too small for a UInt32.",
    "System.OverflowException: Value was either too large or too small for an Int64.",
    "System.OverflowException: Value was either too large or too small for a UInt64.",
};

static const unsigned long long largest[] = {
    0x7Full, 0xFFull, 0x7FFFull, 0xFFFFull, 0x7FFFFFFFull, 0xFFFFFFFFull, 0x7FFFFFFFFFFFFFFFull, 0xFFFFFFFFFFFFFFFFull,
};

// 1 with *result set (as the bits of the number), 0 when s is no number, -1 when the number is out of the kind's range.
static int parse(const string *s, int kind, unsigned long long *result)
{
    unsigned long long magnitude;
    int negative;
    int read = read_integer(s, &magnitude, &negative);
    if (read <= 0)
    {
        return read;
    }

    int is_signed = kind % 2 == 0;
    if (negative)
    {
        // a whole number below zero: -0 is 0 for the unsigned kinds too
        if (magnitude == 0)
        {
            *result = 0;
            return 1;
        }

        if (!is_signed || magnitude > largest[kind] + 1)
        {
            return -1;
        }

        *result = 0ull - magnitude;
        return 1;
    }

    if (magnitude > largest[kind])
    {
        return -1;
    }

    *result = magnitude;
    return 1;
}

static unsigned long long parse_or_fail(const string *s, int kind)
{
    if (s == 0)
    {
        chipsharp_fail("System.ArgumentNullException: Value cannot be null. (Parameter 's')");
    }

    unsigned long long result = 0;
    int parsed = parse(s, kind, &result);
    if (parsed == 0)
    {
        fail_with("System.FormatException: The input string '", s, "' was not in a correct format.");
    }

    if (parsed < 0)
    {
        chipsharp_fail(overflow_messages[kind]);
    }

    return result;
}

static int try_parse(const string *s, int kind, unsigned long long *result)
{
    *result = 0;
    if (s == 0 || parse(s, kind, result) <= 0)
    {
        *result = 0;
        return 0;
    }

    return 1;
}

int ChipSharp_Runtime_ParseSByte(const string *s) { return (signed char)parse_or_fail(s, INT8); }
int ChipSharp_Runtime_ParseByte(const string *s) { return (unsigned char)parse_or_fail(s, UINT8); }
int ChipSharp_Runtime_ParseInt16(const string *s) { return (short)parse_or_fail(s, INT16); }
int ChipSharp_Runtime_ParseUInt16(const string *s) { return (unsigned short)parse_or_fail(s, UINT16); }
int ChipSharp_Runtime_ParseInt32(const string *s) { return (int)parse_or_fail(s, INT32); }
int ChipSharp_Runtime_ParseUInt32(const string *s) { return (int)(unsigned int)parse_or_fail(s, UINT32); }
long long ChipSharp_Runtime_ParseInt64(const string *s) { return (long long)parse_or_fail(s, INT64); }
long long ChipSharp_Runtime_ParseUInt64(const string *s) { return (long long)parse_or_fail(s, UINT64); }

int ChipSharp_Runtime_TryParseSByte(const string *s, signed char *value)
{
    unsigned long long result;
    int parsed = try_parse(s, INT8, &result);
    *value = (signed char)result;
    return parsed;
}

int ChipSharp_Runtime_TryParseByte(const string *s, unsigned char *value)
{
    unsigned long long result;
    int parsed = try_parse(s, UINT8, &result);
    *value = (unsigned char)result;
    return parsed;
}

int ChipSharp_Runtime_TryParseInt16(const string *s, short *value)
{
    unsigned long long result;
    int parsed = try_parse(s, INT16, &result);
    *value = (short)result;
    return parsed;
}

int ChipSharp_Runtime_TryParseUInt16(const string *s, unsigned short *value)
{
    unsigned long long result;
    int parsed = try_parse(s, UINT16, &result);
    *value = (unsigned short)result;
    return parsed;
}

int ChipSharp_Runtime_TryParseInt32(const string *s, int *value)
{
    unsigned long long result;
    int parsed = try_parse(s, INT32, &result);
    *value = (int)result;
    return parsed;
}

int ChipSharp_Runtime_TryParseUInt32(const string *s, unsigned int *value)
{
    unsigned long long result;
    int parsed = try_parse(s, UINT32, &result);
    *value = (unsigned int)result;
    return parsed;
}

int ChipSharp_Runtime_TryParseInt64(const string *s, long long *value)
{
    unsigned long long result;
    int parsed = try_parse(s, INT64, &result);
    *value = (long long)result;
    return parsed;
}

int ChipSharp_Runtime_TryParseUInt64(const string *s, unsigned long long *value)
{
    unsigned long long result;
    int parsed = try_parse(s, UINT64, &result);
    *value = result;
    return parsed;
}
