// ChipSharp runtime, shared by every target: the text of a float or a double, exactly as .NET writes it.
//
// Console.WriteLine(double) and Console.WriteLine(float) print value.ToString(); in the invariant culture that is:
//   - the fewest significant digits that read back as the same value (.NET Core 3.0 and later); of those, the
//     ones closest to the value, and on a tie the ones that end in an even digit
//   - plain notation, "123.45" or "0.0001", while the value is below 10^17 (a float: 10^9) and at least 0.0001;
//     otherwise one digit before the point and an exponent of at least two digits: "1E+17", "1.5E-05", "5E-324"
//   - "0" and "-0", "NaN" (whatever its sign), "Infinity" and "-Infinity"
//
//   chipsharp_float64_text   a double, given by its 64 bits, as text (not ended); returns the length, at most 24
//   chipsharp_float32_text   a float, given by its 32 bits, the same way; at most 15 characters
//
// The digits come from the value's bits by exact integer arithmetic: Steele and White's free-format algorithm
// (Dragon4, as .NET's own fallback does it), on big integers of 32-bit words. It uses no float instruction (the
// Cortex-M0+ has no float hardware), no division and no table of powers of ten, and its multiplications are 16 x 16
// bits, which the Cortex-M0+ does in one instruction. Freestanding: no headers, no C library, nothing it calls.
//
// Every function is built for size (minsize), whatever the target's flags: printing a number is not where a
// program spends its time, and at -O2 the inlined and unrolled loops would make the code four times bigger.
//
// With SDCC (the STM8's runtime), whose double is a float, there is no double to write: only chipsharp_float32_text,
// with big integers the size a float's need, on 1 KB of RAM.

#if defined(__clang__)
#pragma clang attribute push(__attribute__((minsize)), apply_to = function)
#endif

// 32 bits on every target: an int or a long (AVR's and SDCC's int has 16), as the compiler's own fixed-width types say
#if defined(__SDCC)
typedef unsigned long u32;
#define DOUBLES 0
#else
typedef __UINT32_TYPE__ u32;
#define DOUBLES 1
#endif
typedef unsigned long long u64;

// The most 32-bit words a number needs, and then some. The largest number is 10 times a value below the scale, and
// the largest scale, the smallest double's, is at most 4 * 2^1074 * 10: below 2^1084, 34 words. big_set writes up
// to two words above the highest one, the 34th of 2^1076. A float's: the smallest's scale is 4 * 2^149, whose
// word 4 big_set writes up to word 6, and nothing grows past 2^180, 6 words.
#if DOUBLES
#define BIG_WORDS 36
#else
#define BIG_WORDS 8
#endif

// An unsigned integer of any size up to BIG_WORDS words: word[0] holds the lowest 32 bits, and word[length - 1],
// the highest word used, is not 0 (0 has length 0). The words from length up hold nothing.
typedef struct
{
    int length;
    u32 word[BIG_WORDS];
} big;

static void trim(big *x)
{
    while (x->length > 0 && x->word[x->length - 1] == 0)
    {
        x->length--;
    }
}

static u32 word_at(const big *x, int index)
{
    return index < x->length ? x->word[index] : 0u;
}

// x = (high * 2^32 + low) * 2^shift. One loop writes the zeros below the value too, so that the compiler does not
// make it a call to memset, which a program with no C library does not have.
static void big_set(big *x, u32 low, u32 high, int shift)
{
    int words = shift >> 5;
    int bits = shift & 31;
    u32 parts[3] = {low << bits, bits == 0 ? high : (high << bits) | (low >> (32 - bits)), bits == 0 ? 0u : high >> (32 - bits)};
    for (int i = 0; i < words + 3; i++)
    {
        x->word[i] = i < words ? 0u : parts[i - words];
    }

    x->length = words + 3;
    trim(x);
}

// x = x * factor, for a factor of at most 0xFFFF: two 16 x 16 bit products per word, which never overflow 32 bits.
static void big_multiply(big *x, u32 factor)
{
    u32 carry = 0;
    for (int i = 0; i < x->length; i++)
    {
        u32 w = x->word[i];
        u32 low = (w & 0xFFFFu) * factor + carry;
        u32 high = (w >> 16) * factor + (low >> 16);
        x->word[i] = (high << 16) | (low & 0xFFFFu);
        carry = high >> 16;
    }

    if (carry != 0)
    {
        x->word[x->length++] = carry;
    }
}

// x = x * 10^count.
static void big_multiply_pow10(big *x, int count)
{
    for (; count >= 4; count -= 4)
    {
        big_multiply(x, 10000);
    }

    for (; count > 0; count--)
    {
        big_multiply(x, 10);
    }
}

// The sign of a - b: -1, 0 or 1.
static int big_compare(const big *a, const big *b)
{
    if (a->length != b->length)
    {
        return a->length < b->length ? -1 : 1;
    }

    for (int i = a->length - 1; i >= 0; i--)
    {
        if (a->word[i] != b->word[i])
        {
            return a->word[i] < b->word[i] ? -1 : 1;
        }
    }

    return 0;
}

// The sign of a + b - c, worked out without making a + b: c - a - b word by word, the borrow up to 2.
static int big_compare_sum(const big *a, const big *b, const big *c)
{
    int length = a->length > b->length ? a->length : b->length;
    length = c->length > length ? c->length : length;
    u32 borrow = 0;
    u32 rest = 0;
    for (int i = 0; i < length; i++)
    {
        u64 d = (u64)word_at(c, i) - word_at(a, i) - word_at(b, i) - borrow;
        rest |= (u32)d;
        borrow = 0u - (u32)(d >> 32);
    }

    return borrow != 0 ? 1 : rest != 0 ? -1 : 0;
}

// x = x - y, where y is not above x.
static void big_subtract(big *x, const big *y)
{
    u32 borrow = 0;
    for (int i = 0; i < x->length; i++)
    {
        u64 d = (u64)x->word[i] - word_at(y, i) - borrow;
        x->word[i] = (u32)d;
        borrow = (u32)(d >> 32) & 1u;
    }

    trim(x);
}

// The number of bits of x without its leading zeros (0 for 0), in five steps: a loop of one-bit shifts would be
// turned into a call to __clzsi2, which nothing provides on the Cortex-M0+.
static int bit_length(u32 x)
{
    int count = 0;
    if (x >= (u32)1 << 16)
    {
        count += 16;
        x >>= 16;
    }

    if (x >= (u32)1 << 8)
    {
        count += 8;
        x >>= 8;
    }

    if (x >= (u32)1 << 4)
    {
        count += 4;
        x >>= 4;
    }

    if (x >= (u32)1 << 2)
    {
        count += 2;
        x >>= 2;
    }

    if (x >= (u32)1 << 1)
    {
        count += 1;
        x >>= 1;
    }

    return count + (int)x;
}

// The shortest digits of the positive value m * 2^exponent (m = high * 2^32 + low) that read back as it, the way
// .NET picks them. Writes them to digits (each 0-9; the first and the last are not 0), returns how many, and sets
// *point to where the decimal point goes: the value is 0.d1d2d3... * 10^point. lower_closer: the next value below
// is half as far as the next one above (m is a power of 2).
static int shortest_digits(u32 low, u32 high, int exponent, int lower_closer, unsigned char *digits, int *point)
{
    // value / scale is the value; above / scale and below / scale are half the distances to the next values up and
    // down, so anything strictly between value - below and value + above reads back as the value (and the ends
    // too when m is even, as reading rounds a tie to the even m). All times 4, which makes them whole numbers.
    // below is above, or half_above when the value below is closer; half_above is kept up to date only then.
    big value;
    big scale;
    big above;
    big half_above;
    big *below = lower_closer ? &half_above : &above;
    int up_shift = exponent > 0 ? exponent : 0;
    int scale_shift = exponent < 0 ? -exponent : 0;
    big_set(&value, low, high, up_shift + 2);
    big_set(&scale, 1, 0, scale_shift + 2);
    big_set(&above, 1, 0, up_shift + 1);
    big_set(&half_above, 1, 0, up_shift);

    // Divide by 10^k, where k is the number of digits before the point: 10^(k-1) <= value < 10^k. The estimate,
    // floor(log10(2^floor(log2(value)))) + 1, is right or one too small; 78913 / 2^18 is log10(2) closely enough
    // for every exponent a double has.
    int bits = high != 0 ? 32 + bit_length(high) : bit_length(low);
    int k = (((bits - 1 + exponent) * 78913) >> 18) + 1;
    if (k >= 0)
    {
        big_multiply_pow10(&scale, k);
    }
    else
    {
        big_multiply_pow10(&value, -k);
        big_multiply_pow10(&above, -k);
        if (lower_closer)
        {
            big_multiply_pow10(&half_above, -k);
        }
    }

    if (big_compare(&value, &scale) >= 0)
    {
        big_multiply(&scale, 10);
        k++;
    }

    // One digit at a time, until the digits so far, or the digits so far with the last one more, are within the
    // range that reads back as the value.
    int even = (low & 1u) == 0;
    int count = 0;
    for (;;)
    {
        big_multiply(&value, 10);
        big_multiply(&above, 10);
        if (lower_closer)
        {
            big_multiply(&half_above, 10);
        }

        int digit = 0;
        while (big_compare(&value, &scale) >= 0)
        {
            big_subtract(&value, &scale);
            digit++;
        }

        int low_end = big_compare(&value, below);
        int high_end = big_compare_sum(&value, &above, &scale);
        int down = even ? low_end <= 0 : low_end < 0;
        int up = even ? high_end >= 0 : high_end > 0;
        if (!down && !up)
        {
            digits[count++] = (unsigned char)digit;
            continue;
        }

        // When both are within range, the closer one; on a tie, the even digit.
        if (down && up)
        {
            int half = big_compare_sum(&value, &value, &scale);
            up = half > 0 || (half == 0 && (digit & 1) != 0);
        }

        if (up)
        {
            digit++;
        }

        // A 9 rounded up carries into the digits before it; 9s all the way up make the value 1 * 10^k.
        while (digit > 9)
        {
            if (count == 0)
            {
                digit = 1;
                k++;
                break;
            }

            digit = digits[--count] + 1;
        }

        digits[count++] = (unsigned char)digit;
        break;
    }

    while (digits[count - 1] == 0)
    {
        count--;
    }

    *point = k;
    return count;
}

// The words a float may be written as, in flash on an AVR (a constant in RAM would take RAM there: start-up copies
// .rodata to it)
#if defined(__AVR__)
typedef const __flash char constant_char;
#else
typedef const char constant_char;
#endif

static constant_char nan_word[] = "NaN";
static constant_char infinity_word[] = "Infinity";

static int write_word(char *text, constant_char *word)
{
    int length = 0;
    while (word[length] != 0)
    {
        text[length] = word[length];
        length++;
    }

    return length;
}

// The text of a float or double from the fields of its bits: the sign, the biased exponent (all ones for NaN and
// infinity), the fraction (high * 2^32 + low, without the implicit bit); is_double says its format: the size of the
// fraction in bits, the exponent's all ones, and the largest number of digits written before the point without an
// exponent (.NET's precision: 17 for double, 9 for float). Its arguments fit in an AVR's registers (r8-r25): there a
// call that passes some on the stack sets the stack up through r31:r30, which LLVM 22 does not take as changed.
static int float_text(int is_double, int negative, int biased, u32 low, u32 high, char *text)
{
    int fraction_bits = is_double ? 52 : 23;
    int all_ones = is_double ? 0x7FF : 0xFF;
    int precision = is_double ? 17 : 9;
    int n = 0;
    if (biased == all_ones && (low | high) != 0)
    {
        return write_word(text, nan_word);
    }

    if (negative != 0)
    {
        text[n++] = '-';
    }

    if (biased == all_ones)
    {
        return n + write_word(text + n, infinity_word);
    }

    if (biased == 0 && (low | high) == 0)
    {
        text[n++] = '0';
        return n;
    }

    // A normal number has the implicit bit above its fraction. One with only that bit (a power of 2) has its next
    // value below half as far as the next one above, except the smallest normal number: the largest subnormal is
    // as far. (.NET takes it as closer there too; for those two values, a double's and a float's, it makes no
    // difference to the digits.)
    int lower_closer = biased > 1 && (low | high) == 0;
    if (biased != 0)
    {
        if (fraction_bits >= 32)
        {
            high |= (u32)1 << (fraction_bits - 32);
        }
        else
        {
            low |= (u32)1 << fraction_bits;
        }
    }

    int exponent = (biased != 0 ? biased : 1) - (all_ones >> 1) - fraction_bits;
    unsigned char digits[20];
    int point;
    int count = shortest_digits(low, high, exponent, lower_closer, digits, &point);

    // .NET's general format: an exponent when the point is more than precision digits in, or 4 or more zeros
    // would follow it; then one digit before the point.
    int scientific = point > precision || point < -3;
    int before = scientific ? 1 : point;
    int next = 0;
    if (before > 0)
    {
        for (; before > 0; before--)
        {
            text[n++] = next < count ? (char)('0' + digits[next++]) : '0';
        }
    }
    else
    {
        text[n++] = '0';
    }

    if (next < count || before < 0)
    {
        text[n++] = '.';
        for (; before < 0; before++)
        {
            text[n++] = '0';
        }

        while (next < count)
        {
            text[n++] = (char)('0' + digits[next++]);
        }
    }

    // The exponent's digits (it is at most 324) by multiplying: x * 41 >> 12 is x / 100 for x below 1000, and
    // x * 205 >> 11 is x / 10 for x below 1000, with no division, which the Cortex-M0+ would call a routine for.
    if (scientific)
    {
        int power = point - 1;
        text[n++] = 'E';
        text[n++] = power < 0 ? '-' : '+';
        power = power < 0 ? -power : power;
        int hundreds = (power * 41) >> 12;
        if (hundreds != 0)
        {
            text[n++] = (char)('0' + hundreds);
        }

        power -= hundreds * 100;
        int tens = (power * 205) >> 11;
        text[n++] = (char)('0' + tens);
        text[n++] = (char)('0' + power - tens * 10);
    }

    return n;
}

#if DOUBLES
int chipsharp_float64_text(u64 bits, char *text)
{
    u32 high = (u32)(bits >> 32);
    return float_text(1, (int)(high >> 31), (int)((high >> 20) & 0x7FFu), (u32)bits, high & 0xFFFFFu, text);
}
#endif

int chipsharp_float32_text(u32 bits, char *text)
{
    return float_text(0, (int)(bits >> 31), (int)((bits >> 23) & 0xFFu), bits & 0x7FFFFFu, 0, text);
}

#if defined(__clang__)
#pragma clang attribute pop
#endif
