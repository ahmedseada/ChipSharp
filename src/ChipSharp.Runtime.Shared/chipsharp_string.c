// ChipSharp runtime, shared by every runtime: strings (C#'s string, see src/ChipSharp.Frontend/Strings.cs) and chars,
// what reads them without making new ones. What makes strings is in chipsharp_string_heap.c, for a target with a heap.
//
// Freestanding: no headers and no C library.
//
// A string is an object laid out as an array object: its header (the address of its type), its length (an s32, in
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
//   ChipSharp_Runtime_StringCompare       CompareTo and string.Compare: the difference of the first chars that differ, else
//                                        of the lengths (null first), ordinal, as .NET in its invariant mode (with a
//                                        culture .NET puts "a" before "B" and gives -1, 0 or 1)
//   ChipSharp_Runtime_StringCompareOrdinal  string.CompareOrdinal: the same, but where one string starts with the other,
//                                        as .NET's ends (see compare_ordinal)
//   ChipSharp_Runtime_String...Using      the methods with a StringComparison (Compare, Equals, StartsWith, EndsWith,
//                                        IndexOf, LastIndexOf, Contains): char by char for every value, as .NET in its
//                                        invariant mode, the IgnoreCase ones comparing the upper case of each char
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
//   ChipSharp_Runtime_Parse..., TryParse...  s32.Parse and s32.TryParse, and those of the other whole numbers: spaces
//                                        before and after, a sign, decimal digits, as .NET's invariant culture reads
//                                        them; FormatException and OverflowException with .NET's messages
//
// Failures say what .NET says through chipsharp_fail (each runtime's failures file): "System.ArgumentOutOfRangeException:
// ..." after "Unhandled exception. ".

typedef __UINTPTR_TYPE__ uintptr_t;

// 32 bits on every target: an int or a long (AVR's int has 16), as the compiler's own fixed-width types say. C# passes
// an int, a char (its UTF-16 code unit) and a bool (0 or 1) as 32 bits, and gets them back so.
typedef __INT32_TYPE__ s32;
typedef __UINT32_TYPE__ u32;

// A string the program gives may be one of its constants, which on an AVR stay in flash: there an address with bit 15
// set is 0x8000 plus a flash address, read with LPM (see src/ChipSharp.Backend.Llvm/LlvmFlash.cs); RAM's addresses
// never have it. READ(type, address) reads a value of the type at such an address, from RAM or flash (it uses the
// address twice: nothing like ++ in it); on every other target it is a plain read. MESSAGE(text) is a failure's
// message, which chipsharp_fail reads so too: on an AVR in flash, a section of its own each (the linker keeps only
// those a program can fail with), with bit 15 set, for a constant in RAM would take RAM there (start-up copies .rodata
// to it).
#if defined(__AVR__)
#define READ(type, address)                                                                                            \
    (((uintptr_t)(address) & 0x8000u) != 0 ? *(const __flash type *)((uintptr_t)(address) & 0x7FFFu)                   \
                                           : *(const type *)(address))
#define TEXT_OF(name) #name
#define SECTION_NAME(number) ".progmem.data.chipsharp_string" TEXT_OF(number)
#define MESSAGE(text)                                                                                                  \
    (__extension__({                                                                                                   \
        static const __flash char flash_text[] __attribute__((section(SECTION_NAME(__COUNTER__)))) = text;             \
        (const char *)((uintptr_t)flash_text | 0x8000u);                                                               \
    }))
#else
#define READ(type, address) (*(const type *)(address))
#define MESSAGE(text) (text)
#endif

// A type descriptor, as the code generator writes it (see TirTypeDescriptor): a string's has no virtual methods.
typedef struct chipsharp_type
{
    u32 size;
    u32 element_size;
    u32 flags;
    u32 reference_count;
    const u32 *references;
    const struct chipsharp_type *base;
    const char *name;
    const void *interfaces;
    u32 interface_count;
    u32 virtual_count;
} chipsharp_type;

typedef struct string
{
    const chipsharp_type *type;
    s32 length;
} string;

#define DATA ((u32)((sizeof(void *) + 4 + 7) / 8 * 8))
#define TYPE_ARRAY 1u

const chipsharp_type chipsharp_string_type = {DATA, 1, TYPE_ARRAY, 0, 0, 0, "System.String", 0, 0, 0};
const chipsharp_type chipsharp_wide_string_type = {DATA, 2, TYPE_ARRAY, 0, 0, 0, "System.String", 0, 0, 0};

// In each runtime's failures file: stops the program with "Unhandled exception. " and the message.
__attribute__((noreturn)) void chipsharp_fail(const char *message);
__attribute__((noreturn)) void ChipSharp_Runtime_IndexOutOfRange(void);

typedef const chipsharp_type *type_pointer;

static s32 length(const string *s)
{
    return READ(s32, &s->length);
}

static s32 is_wide(const string *s)
{
    return READ(u32, &READ(type_pointer, &s->type)->element_size) == 2;
}

static u32 at(const string *s, s32 i)
{
    const char *data = (const char *)s + DATA;
    return is_wide(s) ? READ(unsigned short, (const unsigned short *)data + i) : READ(unsigned char, data + i);
}

s32 ChipSharp_Runtime_StringChar(const string *s, s32 index)
{
    if ((u32)index >= (u32)length(s))
    {
        ChipSharp_Runtime_IndexOutOfRange();
    }

    return (s32)at(s, index);
}

// True when the chars of a from start, count of them, are those of b from 0.
static s32 same_chars(const string *a, s32 start, const string *b, s32 count)
{
    for (s32 i = 0; i < count; i++)
    {
        if (at(a, start + i) != at(b, i))
        {
            return 0;
        }
    }

    return 1;
}

s32 ChipSharp_Runtime_StringEquals(const string *a, const string *b)
{
    if (a == b)
    {
        return 1;
    }

    if (a == 0 || b == 0 || length(a) != length(b) || is_wide(a) != is_wide(b))
    {
        return 0;
    }

    return same_chars(a, 0, b, length(a));
}

s32 ChipSharp_Runtime_StringNotEquals(const string *a, const string *b)
{
    return !ChipSharp_Runtime_StringEquals(a, b);
}

// True for a string object: its header is one of the two descriptors.
static s32 is_string(const void *object)
{
    type_pointer type = READ(type_pointer, object);
    return type == &chipsharp_string_type || type == &chipsharp_wide_string_type;
}

s32 ChipSharp_Runtime_StringEqualsObject(const string *s, const void *other)
{
    return other != 0 && is_string(other) && ChipSharp_Runtime_StringEquals(s, (const string *)other);
}

s32 ChipSharp_Runtime_CharToUpper(s32 c);

// The char at i as a comparison reads it: its upper case when ignore_case is 1.
static u32 char_as(const string *s, s32 i, s32 ignore_case)
{
    u32 c = at(s, i);
    return ignore_case ? (u32)ChipSharp_Runtime_CharToUpper((s32)c) : c;
}

// The difference of the first chars that differ (the upper case of each when ignore_case is 1), else of the lengths:
// how .NET compares in its invariant mode.
static s32 compare_chars(const string *a, const string *b, s32 ignore_case)
{
    s32 count = length(a) < length(b) ? length(a) : length(b);
    for (s32 i = 0; i < count; i++)
    {
        s32 difference = (s32)char_as(a, i, ignore_case) - (s32)char_as(b, i, ignore_case);
        if (difference != 0)
        {
            return difference;
        }
    }

    return length(a) - length(b);
}

// As compare_chars, but where one string starts with the other, as .NET's ordinal comparison ends: it compares two chars
// at a time from the third, so where the shorter has fewer than two chars or an odd number of them it reads the 0 that
// ends a string in .NET and gives the difference of the longer one's next char and 0 ("apple" and "app": 108, the 'l'),
// else the difference of the lengths ("apple" and "ap": 3).
static s32 compare_ordinal(const string *a, const string *b)
{
    s32 count = length(a) < length(b) ? length(a) : length(b);
    for (s32 i = 0; i < count; i++)
    {
        s32 difference = (s32)at(a, i) - (s32)at(b, i);
        if (difference != 0)
        {
            return difference;
        }
    }

    if (length(a) != length(b) && (count <= 1 || (count & 1) == 1))
    {
        return length(a) > count ? (s32)at(a, count) : -(s32)at(b, count);
    }

    return length(a) - length(b);
}

s32 ChipSharp_Runtime_StringCompareOrdinal(const string *a, const string *b)
{
    if (a == b)
    {
        return 0;
    }

    if (a == 0 || b == 0)
    {
        return a == 0 ? -1 : 1;
    }

    return compare_ordinal(a, b);
}

s32 ChipSharp_Runtime_StringCompare(const string *a, const string *b)
{
    if (a == b)
    {
        return 0;
    }

    if (a == 0 || b == 0)
    {
        return a == 0 ? -1 : 1;
    }

    return compare_chars(a, b, 0);
}

s32 ChipSharp_Runtime_StringHash(const string *s)
{
    u32 hash = 2166136261u;
    for (s32 i = 0; i < length(s); i++)
    {
        hash = (hash ^ at(s, i)) * 16777619u;
    }

    return (s32)hash;
}

// ---- Chars -----------------------------------------------------------------------------------------------------------

s32 ChipSharp_Runtime_CharIsWhiteSpace(s32 c)
{
    return (c >= 0x09 && c <= 0x0D) || c == 0x20 || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A)
        || c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000;
}

s32 ChipSharp_Runtime_CharIsDigit(s32 c)
{
    return c >= '0' && c <= '9';
}

s32 ChipSharp_Runtime_CharIsUpper(s32 c)
{
    return (c >= 'A' && c <= 'Z') || (c >= 0xC0 && c <= 0xDE && c != 0xD7);
}

s32 ChipSharp_Runtime_CharIsLower(s32 c)
{
    return (c >= 'a' && c <= 'z') || c == 0xB5 || (c >= 0xDF && c <= 0xFF && c != 0xF7);
}

s32 ChipSharp_Runtime_CharIsLetter(s32 c)
{
    return ChipSharp_Runtime_CharIsUpper(c) || ChipSharp_Runtime_CharIsLower(c) || c == 0xAA || c == 0xBA;
}

s32 ChipSharp_Runtime_CharIsLetterOrDigit(s32 c)
{
    return ChipSharp_Runtime_CharIsLetter(c) || ChipSharp_Runtime_CharIsDigit(c);
}

s32 ChipSharp_Runtime_CharToUpper(s32 c)
{
    if ((c >= 'a' && c <= 'z') || (c >= 0xE0 && c <= 0xFE && c != 0xF7))
    {
        return c - 0x20;
    }

    return c == 0xFF ? 0x178 : c == 0xB5 ? 0x39C : c;
}

s32 ChipSharp_Runtime_CharToLower(s32 c)
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
    chipsharp_fail(MESSAGE("System.ArgumentNullException: Value cannot be null. (Parameter 'value')"));
}

// Where c is first in s from start, count chars at most; -1 when it is not there.
static s32 find_char(const string *s, u32 c, s32 start, s32 count)
{
    for (s32 i = start; i < start + count; i++)
    {
        if (at(s, i) == c)
        {
            return i;
        }
    }

    return -1;
}

// Where value is first in s from start in count chars at most (the whole value inside them); -1 when it is not there.
static s32 find(const string *s, const string *value, s32 start, s32 count)
{
    for (s32 i = start; i + length(value) <= start + count; i++)
    {
        if (same_chars(s, i, value, length(value)))
        {
            return i;
        }
    }

    return -1;
}

s32 ChipSharp_Runtime_StringIndexOfChar(const string *s, s32 c)
{
    return find_char(s, (u32)c & 0xFFFF, 0, length(s));
}

s32 ChipSharp_Runtime_StringIndexOfCharFrom(const string *s, s32 c, s32 start)
{
    if ((u32)start > (u32)length(s))
    {
        chipsharp_fail(MESSAGE(OUT_OF_RANGE START_INDEX_LE));
    }

    return find_char(s, (u32)c & 0xFFFF, start, length(s) - start);
}

s32 ChipSharp_Runtime_StringIndexOfCharIn(const string *s, s32 c, s32 start, s32 count)
{
    if ((u32)start > (u32)length(s))
    {
        chipsharp_fail(MESSAGE(OUT_OF_RANGE START_INDEX_LE));
    }

    if ((u32)count > (u32)(length(s) - start))
    {
        chipsharp_fail(MESSAGE(OUT_OF_RANGE COUNT_RANGE));
    }

    return find_char(s, (u32)c & 0xFFFF, start, count);
}

s32 ChipSharp_Runtime_StringIndexOf(const string *s, const string *value)
{
    if (value == 0)
    {
        null_value();
    }

    return find(s, value, 0, length(s));
}

s32 ChipSharp_Runtime_StringIndexOfFrom(const string *s, const string *value, s32 start)
{
    if (value == 0)
    {
        null_value();
    }

    if ((u32)start > (u32)length(s))
    {
        chipsharp_fail(MESSAGE(OUT_OF_RANGE START_INDEX_LE));
    }

    return find(s, value, start, length(s) - start);
}

s32 ChipSharp_Runtime_StringLastIndexOfChar(const string *s, s32 c)
{
    for (s32 i = length(s) - 1; i >= 0; i--)
    {
        if (at(s, i) == ((u32)c & 0xFFFF))
        {
            return i;
        }
    }

    return -1;
}

s32 ChipSharp_Runtime_StringLastIndexOfCharFrom(const string *s, s32 c, s32 start)
{
    if (length(s) == 0)
    {
        return -1;
    }

    if ((u32)start >= (u32)length(s))
    {
        chipsharp_fail(MESSAGE(OUT_OF_RANGE START_INDEX_LT));
    }

    for (s32 i = start; i >= 0; i--)
    {
        if (at(s, i) == ((u32)c & 0xFFFF))
        {
            return i;
        }
    }

    return -1;
}

// .NET finds the empty string at the end, at the string's length.
s32 ChipSharp_Runtime_StringLastIndexOf(const string *s, const string *value)
{
    if (value == 0)
    {
        null_value();
    }

    for (s32 i = length(s) - length(value); i >= 0; i--)
    {
        if (same_chars(s, i, value, length(value)))
        {
            return i;
        }
    }

    return -1;
}

s32 ChipSharp_Runtime_StringContains(const string *s, const string *value)
{
    return ChipSharp_Runtime_StringIndexOf(s, value) >= 0;
}

s32 ChipSharp_Runtime_StringContainsChar(const string *s, s32 c)
{
    return ChipSharp_Runtime_StringIndexOfChar(s, c) >= 0;
}

s32 ChipSharp_Runtime_StringStartsWith(const string *s, const string *value)
{
    if (value == 0)
    {
        null_value();
    }

    return length(value) <= length(s) && same_chars(s, 0, value, length(value));
}

s32 ChipSharp_Runtime_StringEndsWith(const string *s, const string *value)
{
    if (value == 0)
    {
        null_value();
    }

    return length(value) <= length(s) && same_chars(s, length(s) - length(value), value, length(value));
}

s32 ChipSharp_Runtime_StringStartsWithChar(const string *s, s32 c)
{
    return length(s) != 0 && at(s, 0) == ((u32)c & 0xFFFF);
}

s32 ChipSharp_Runtime_StringEndsWithChar(const string *s, s32 c)
{
    return length(s) != 0 && at(s, length(s) - 1) == ((u32)c & 0xFFFF);
}

// ---- With a StringComparison -----------------------------------------------------------------------------------------

// The overloads with a StringComparison compare char by char for every value, as .NET compares in its invariant mode:
// CurrentCulture (0), InvariantCulture (2) and Ordinal (4) the chars themselves, their IgnoreCase forms (1, 3, 5) the
// upper case of each char (ChipSharp_Runtime_CharToUpper), as OrdinalIgnoreCase does. Another value fails as in .NET.

#define COMPARISON_TYPE "System.ArgumentException: The string comparison type passed in is currently not supported. " \
    "(Parameter 'comparisonType')"

// 1 when the comparison ignores case; fails for a value that is no StringComparison.
static s32 ignores_case(s32 comparison)
{
    if ((u32)comparison > 5)
    {
        chipsharp_fail(MESSAGE(COMPARISON_TYPE));
    }

    return comparison & 1;
}

// Like same_chars, comparing the upper case of each char when ignore_case is 1.
static s32 same_chars_as(const string *a, s32 start, const string *b, s32 count, s32 ignore_case)
{
    for (s32 i = 0; i < count; i++)
    {
        if (char_as(a, start + i, ignore_case) != char_as(b, i, ignore_case))
        {
            return 0;
        }
    }

    return 1;
}

// Like find, comparing the upper case of each char when ignore_case is 1.
static s32 find_as(const string *s, const string *value, s32 start, s32 count, s32 ignore_case)
{
    for (s32 i = start; i + length(value) <= start + count; i++)
    {
        if (same_chars_as(s, i, value, length(value), ignore_case))
        {
            return i;
        }
    }

    return -1;
}

// string.Compare(a, b, comparison): null first; Ordinal as string.CompareOrdinal, the others as CompareTo.
s32 ChipSharp_Runtime_StringCompareUsing(const string *a, const string *b, s32 comparison)
{
    s32 ignore_case = ignores_case(comparison);
    if (a == b)
    {
        return 0;
    }

    if (a == 0 || b == 0)
    {
        return a == 0 ? -1 : 1;
    }

    return comparison == 4 ? compare_ordinal(a, b) : compare_chars(a, b, ignore_case);
}

// string.Equals(a, b, comparison) and a.Equals(b, comparison): null equals null only.
s32 ChipSharp_Runtime_StringEqualsUsing(const string *a, const string *b, s32 comparison)
{
    s32 ignore_case = ignores_case(comparison);
    if (a == b)
    {
        return 1;
    }

    return a != 0 && b != 0 && length(a) == length(b) && same_chars_as(a, 0, b, length(a), ignore_case);
}

s32 ChipSharp_Runtime_StringStartsWithUsing(const string *s, const string *value, s32 comparison)
{
    if (value == 0)
    {
        null_value();
    }

    s32 ignore_case = ignores_case(comparison);
    return length(value) <= length(s) && same_chars_as(s, 0, value, length(value), ignore_case);
}

s32 ChipSharp_Runtime_StringEndsWithUsing(const string *s, const string *value, s32 comparison)
{
    if (value == 0)
    {
        null_value();
    }

    s32 ignore_case = ignores_case(comparison);
    return length(value) <= length(s) && same_chars_as(s, length(s) - length(value), value, length(value), ignore_case);
}

// .NET looks at the comparison first here, and says a null value is null whatever it is.
s32 ChipSharp_Runtime_StringIndexOfFromUsing(const string *s, const string *value, s32 start, s32 comparison)
{
    if (value == 0)
    {
        null_value();
    }

    s32 ignore_case = ignores_case(comparison);
    if ((u32)start > (u32)length(s))
    {
        chipsharp_fail(MESSAGE(OUT_OF_RANGE START_INDEX_LE));
    }

    return find_as(s, value, start, length(s) - start, ignore_case);
}

s32 ChipSharp_Runtime_StringIndexOfUsing(const string *s, const string *value, s32 comparison)
{
    return ChipSharp_Runtime_StringIndexOfFromUsing(s, value, 0, comparison);
}

s32 ChipSharp_Runtime_StringContainsUsing(const string *s, const string *value, s32 comparison)
{
    return ChipSharp_Runtime_StringIndexOfUsing(s, value, comparison) >= 0;
}

// As .NET: an empty string, or an empty value, is found before the comparison is looked at; the empty value at the end.
s32 ChipSharp_Runtime_StringLastIndexOfUsing(const string *s, const string *value, s32 comparison)
{
    if (value == 0)
    {
        null_value();
    }

    if (length(s) == 0 || length(value) == 0)
    {
        return length(value) == 0 ? length(s) : -1;
    }

    s32 ignore_case = ignores_case(comparison);
    for (s32 i = length(s) - length(value); i >= 0; i--)
    {
        if (same_chars_as(s, i, value, length(value), ignore_case))
        {
            return i;
        }
    }

    return -1;
}

s32 ChipSharp_Runtime_StringIsNullOrEmpty(const string *s)
{
    return s == 0 || length(s) == 0;
}

s32 ChipSharp_Runtime_StringIsNullOrWhiteSpace(const string *s)
{
    if (s != 0)
    {
        for (s32 i = 0; i < length(s); i++)
        {
            if (!ChipSharp_Runtime_CharIsWhiteSpace((s32)at(s, i)))
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
    u32 n = 0;
    for (char c = READ(char, prefix); c != 0; prefix++, c = READ(char, prefix))
    {
        message[n++] = c;
    }

    for (s32 i = 0; i < length(s) && i < 200; i++)
    {
        u32 c = at(s, i);
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

    for (char c = READ(char, suffix); c != 0; suffix++, c = READ(char, suffix))
    {
        message[n++] = c;
    }

    message[n] = 0;
    chipsharp_fail(message);
}

static s32 is_parse_space(u32 c)
{
    return (c >= 0x09 && c <= 0x0D) || c == 0x20;
}

// Reads a whole number from s as .NET's NumberStyles.Integer does: spaces, a sign, digits, spaces (and zeros, the char
// '\0', at the very end). 1 with *magnitude and *negative set; 0 when it is not one; -1 when it does not fit in 64 bits.
static s32 read_integer(const string *s, unsigned long long *magnitude, s32 *negative)
{
    s32 i = 0;
    s32 end = length(s);
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
    s32 overflow = 0;
    for (; i < end; i++)
    {
        u32 c = at(s, i);
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

// The message of a whole number too large or too small for its kind.
static const char *overflow_message(s32 kind)
{
    switch (kind)
    {
    case INT8:
        return MESSAGE("System.OverflowException: Value was either too large or too small for a signed byte.");
    case UINT8:
        return MESSAGE("System.OverflowException: Value was either too large or too small for an unsigned byte.");
    case INT16:
        return MESSAGE("System.OverflowException: Value was either too large or too small for an Int16.");
    case UINT16:
        return MESSAGE("System.OverflowException: Value was either too large or too small for a UInt16.");
    case INT32:
        return MESSAGE("System.OverflowException: Value was either too large or too small for an Int32.");
    case UINT32:
        return MESSAGE("System.OverflowException: Value was either too large or too small for a UInt32.");
    case INT64:
        return MESSAGE("System.OverflowException: Value was either too large or too small for an Int64.");
    default:
        return MESSAGE("System.OverflowException: Value was either too large or too small for a UInt64.");
    }
}

// The largest value of the kind: of 8, 16, 32 or 64 bits, signed (the even kinds) or not.
static unsigned long long largest(s32 kind)
{
    s32 bits = 8 << (kind / 2);
    unsigned long long all = bits == 64 ? ~0ull : (1ull << bits) - 1;
    return kind % 2 == 0 ? all >> 1 : all;
}

// 1 with *result set (as the bits of the number), 0 when s is no number, -1 when the number is out of the kind's range.
static s32 parse(const string *s, s32 kind, unsigned long long *result)
{
    unsigned long long magnitude;
    s32 negative;
    s32 read = read_integer(s, &magnitude, &negative);
    if (read <= 0)
    {
        return read;
    }

    s32 is_signed = kind % 2 == 0;
    if (negative)
    {
        // a whole number below zero: -0 is 0 for the unsigned kinds too
        if (magnitude == 0)
        {
            *result = 0;
            return 1;
        }

        if (!is_signed || magnitude > largest(kind) + 1)
        {
            return -1;
        }

        *result = 0ull - magnitude;
        return 1;
    }

    if (magnitude > largest(kind))
    {
        return -1;
    }

    *result = magnitude;
    return 1;
}

static unsigned long long parse_or_fail(const string *s, s32 kind)
{
    if (s == 0)
    {
        chipsharp_fail(MESSAGE("System.ArgumentNullException: Value cannot be null. (Parameter 's')"));
    }

    unsigned long long result = 0;
    s32 parsed = parse(s, kind, &result);
    if (parsed == 0)
    {
        fail_with(MESSAGE("System.FormatException: The input string '"), s, MESSAGE("' was not in a correct format."));
    }

    if (parsed < 0)
    {
        chipsharp_fail(overflow_message(kind));
    }

    return result;
}

static s32 try_parse(const string *s, s32 kind, unsigned long long *result)
{
    *result = 0;
    if (s == 0 || parse(s, kind, result) <= 0)
    {
        *result = 0;
        return 0;
    }

    return 1;
}

s32 ChipSharp_Runtime_ParseSByte(const string *s) { return (signed char)parse_or_fail(s, INT8); }
s32 ChipSharp_Runtime_ParseByte(const string *s) { return (unsigned char)parse_or_fail(s, UINT8); }
s32 ChipSharp_Runtime_ParseInt16(const string *s) { return (short)parse_or_fail(s, INT16); }
s32 ChipSharp_Runtime_ParseUInt16(const string *s) { return (unsigned short)parse_or_fail(s, UINT16); }
s32 ChipSharp_Runtime_ParseInt32(const string *s) { return (s32)parse_or_fail(s, INT32); }
s32 ChipSharp_Runtime_ParseUInt32(const string *s) { return (s32)(u32)parse_or_fail(s, UINT32); }
long long ChipSharp_Runtime_ParseInt64(const string *s) { return (long long)parse_or_fail(s, INT64); }
long long ChipSharp_Runtime_ParseUInt64(const string *s) { return (long long)parse_or_fail(s, UINT64); }

s32 ChipSharp_Runtime_TryParseSByte(const string *s, signed char *value)
{
    unsigned long long result;
    s32 parsed = try_parse(s, INT8, &result);
    *value = (signed char)result;
    return parsed;
}

s32 ChipSharp_Runtime_TryParseByte(const string *s, unsigned char *value)
{
    unsigned long long result;
    s32 parsed = try_parse(s, UINT8, &result);
    *value = (unsigned char)result;
    return parsed;
}

s32 ChipSharp_Runtime_TryParseInt16(const string *s, short *value)
{
    unsigned long long result;
    s32 parsed = try_parse(s, INT16, &result);
    *value = (short)result;
    return parsed;
}

s32 ChipSharp_Runtime_TryParseUInt16(const string *s, unsigned short *value)
{
    unsigned long long result;
    s32 parsed = try_parse(s, UINT16, &result);
    *value = (unsigned short)result;
    return parsed;
}

s32 ChipSharp_Runtime_TryParseInt32(const string *s, s32 *value)
{
    unsigned long long result;
    s32 parsed = try_parse(s, INT32, &result);
    *value = (s32)result;
    return parsed;
}

s32 ChipSharp_Runtime_TryParseUInt32(const string *s, u32 *value)
{
    unsigned long long result;
    s32 parsed = try_parse(s, UINT32, &result);
    *value = (u32)result;
    return parsed;
}

s32 ChipSharp_Runtime_TryParseInt64(const string *s, long long *value)
{
    unsigned long long result;
    s32 parsed = try_parse(s, INT64, &result);
    *value = (long long)result;
    return parsed;
}

s32 ChipSharp_Runtime_TryParseUInt64(const string *s, unsigned long long *value)
{
    unsigned long long result;
    s32 parsed = try_parse(s, UINT64, &result);
    *value = result;
    return parsed;
}
