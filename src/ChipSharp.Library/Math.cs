namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.Math"/>, the members that are more than one runtime function: the whole-number
/// <c>Abs</c>, <c>Min</c>, <c>Max</c>, <c>Clamp</c> and <c>Sign</c>, <c>DivRem</c> and <c>BigMul</c>, <c>Clamp</c> and
/// <c>Sign</c> of floats, <c>Round</c> to a number of digits or by a <see cref="System.MidpointRounding"/>, <c>Log</c>
/// in a base and <c>SinCos</c>, written as .NET 10's System.Private.CoreLib writes them, with its checks and messages.
/// None needs a heap.
/// </summary>
/// <remarks>
/// The members that are one function of floats (<c>Sin</c>, <c>Exp</c>, <c>Pow</c>, <c>Floor</c>, <c>ScaleB</c>,
/// <c>FusedMultiplyAdd</c>, ...; and <c>Abs</c>, <c>Min</c>, <c>Max</c>, <c>Sqrt</c> of floats) are not here: a call
/// to one is a call to its runtime function (see KnownExternals), and so are the calls this class makes to them. Where
/// .NET throws, a <see cref="Runtime"/> function stops the program with .NET's message. The overloads of decimal,
/// Int128 and UInt128 are not here.
/// </remarks>
[Substitute(typeof(System.Math))]
internal static class Math
{
    private const int MaxRoundingDigits = 15;

    // the limit above which a double is a whole number at every number of digits Round takes
    private const double DoubleRoundLimit = 1e16d;

    public static short Abs(short value)
    {
        if (value < 0)
        {
            value = (short)-value;
            if (value < 0)
            {
                Runtime.NegateOverflow();
            }
        }

        return value;
    }

    public static int Abs(int value)
    {
        if (value < 0)
        {
            value = -value;
            if (value < 0)
            {
                Runtime.NegateOverflow();
            }
        }

        return value;
    }

    public static long Abs(long value)
    {
        if (value < 0)
        {
            value = -value;
            if (value < 0)
            {
                Runtime.NegateOverflow();
            }
        }

        return value;
    }

    public static nint Abs(nint value)
    {
        if (value < 0)
        {
            value = -value;
            if (value < 0)
            {
                Runtime.NegateOverflow();
            }
        }

        return value;
    }

    public static sbyte Abs(sbyte value)
    {
        if (value < 0)
        {
            value = (sbyte)-value;
            if (value < 0)
            {
                Runtime.NegateOverflow();
            }
        }

        return value;
    }

    public static ulong BigMul(uint a, uint b) => (ulong)a * b;

    public static long BigMul(int a, int b) => (long)a * b;

    /// <summary>The full product of two ulongs: its high half returned, its low half in <paramref name="low"/>.</summary>
    public static ulong BigMul(ulong a, ulong b, out ulong low)
    {
        // Hacker's Delight, 8: the product of the 32-bit halves, as .NET's software fallback
        uint al = (uint)a;
        uint ah = (uint)(a >> 32);
        uint bl = (uint)b;
        uint bh = (uint)(b >> 32);

        ulong mull = (ulong)al * bl;
        ulong t = (ulong)ah * bl + (mull >> 32);
        ulong tl = (ulong)al * bh + (uint)t;

        low = (tl << 32) | (uint)mull;
        return (ulong)ah * bh + (t >> 32) + (tl >> 32);
    }

    public static long BigMul(long a, long b, out long low)
    {
        ulong high = BigMul((ulong)a, (ulong)b, out ulong ulow);
        low = (long)ulow;
        return (long)high - ((a >> 63) & b) - ((b >> 63) & a);
    }

    public static int DivRem(int a, int b, out int result)
    {
        int div = a / b;
        result = a - (div * b);
        return div;
    }

    public static long DivRem(long a, long b, out long result)
    {
        long div = a / b;
        result = a - (div * b);
        return div;
    }

    public static (sbyte Quotient, sbyte Remainder) DivRem(sbyte left, sbyte right)
    {
        sbyte quotient = (sbyte)(left / right);
        return (quotient, (sbyte)(left - (quotient * right)));
    }

    public static (byte Quotient, byte Remainder) DivRem(byte left, byte right)
    {
        byte quotient = (byte)(left / right);
        return (quotient, (byte)(left - (quotient * right)));
    }

    public static (short Quotient, short Remainder) DivRem(short left, short right)
    {
        short quotient = (short)(left / right);
        return (quotient, (short)(left - (quotient * right)));
    }

    public static (ushort Quotient, ushort Remainder) DivRem(ushort left, ushort right)
    {
        ushort quotient = (ushort)(left / right);
        return (quotient, (ushort)(left - (quotient * right)));
    }

    public static (int Quotient, int Remainder) DivRem(int left, int right)
    {
        int quotient = left / right;
        return (quotient, left - (quotient * right));
    }

    public static (uint Quotient, uint Remainder) DivRem(uint left, uint right)
    {
        uint quotient = left / right;
        return (quotient, left - (quotient * right));
    }

    public static (long Quotient, long Remainder) DivRem(long left, long right)
    {
        long quotient = left / right;
        return (quotient, left - (quotient * right));
    }

    public static (ulong Quotient, ulong Remainder) DivRem(ulong left, ulong right)
    {
        ulong quotient = left / right;
        return (quotient, left - (quotient * right));
    }

    public static (nint Quotient, nint Remainder) DivRem(nint left, nint right)
    {
        nint quotient = left / right;
        return (quotient, left - (quotient * right));
    }

    public static (nuint Quotient, nuint Remainder) DivRem(nuint left, nuint right)
    {
        nuint quotient = left / right;
        return (quotient, left - (quotient * right));
    }

    public static byte Clamp(byte value, byte min, byte max)
    {
        if (min > max)
        {
            Runtime.MinMaxUInt64(min, max);
        }

        return value < min ? min : value > max ? max : value;
    }

    public static double Clamp(double value, double min, double max)
    {
        if (min > max)
        {
            Runtime.MinMaxFloat64(min, max);
        }

        return value < min ? min : value > max ? max : value;
    }

    public static short Clamp(short value, short min, short max)
    {
        if (min > max)
        {
            Runtime.MinMaxInt64(min, max);
        }

        return value < min ? min : value > max ? max : value;
    }

    public static int Clamp(int value, int min, int max)
    {
        if (min > max)
        {
            Runtime.MinMaxInt64(min, max);
        }

        return value < min ? min : value > max ? max : value;
    }

    public static long Clamp(long value, long min, long max)
    {
        if (min > max)
        {
            Runtime.MinMaxInt64(min, max);
        }

        return value < min ? min : value > max ? max : value;
    }

    public static nint Clamp(nint value, nint min, nint max)
    {
        if (min > max)
        {
            Runtime.MinMaxInt64(min, max);
        }

        return value < min ? min : value > max ? max : value;
    }

    public static sbyte Clamp(sbyte value, sbyte min, sbyte max)
    {
        if (min > max)
        {
            Runtime.MinMaxInt64(min, max);
        }

        return value < min ? min : value > max ? max : value;
    }

    public static float Clamp(float value, float min, float max)
    {
        if (min > max)
        {
            Runtime.MinMaxFloat32(min, max);
        }

        return value < min ? min : value > max ? max : value;
    }

    public static ushort Clamp(ushort value, ushort min, ushort max)
    {
        if (min > max)
        {
            Runtime.MinMaxUInt64(min, max);
        }

        return value < min ? min : value > max ? max : value;
    }

    public static uint Clamp(uint value, uint min, uint max)
    {
        if (min > max)
        {
            Runtime.MinMaxUInt64(min, max);
        }

        return value < min ? min : value > max ? max : value;
    }

    public static ulong Clamp(ulong value, ulong min, ulong max)
    {
        if (min > max)
        {
            Runtime.MinMaxUInt64(min, max);
        }

        return value < min ? min : value > max ? max : value;
    }

    public static nuint Clamp(nuint value, nuint min, nuint max)
    {
        if (min > max)
        {
            Runtime.MinMaxUInt64(min, max);
        }

        return value < min ? min : value > max ? max : value;
    }

    /// <summary>The logarithm of <paramref name="a"/> in base <paramref name="newBase"/>: Log(a) / Log(newBase), as .NET.</summary>
    public static double Log(double a, double newBase)
    {
#pragma warning disable CS1718 // x != x is how NaN is told apart here
        if (a != a)
        {
            return a; // IEEE 754-2008: NaN payload must be preserved
        }

        if (newBase != newBase)
        {
            return newBase;
        }
#pragma warning restore CS1718

        if (newBase == 1)
        {
            return double.NaN;
        }

        if (a != 1 && (newBase == 0 || newBase == double.PositiveInfinity))
        {
            return double.NaN;
        }

        return System.Math.Log(a) / System.Math.Log(newBase);
    }

    public static byte Max(byte val1, byte val2) => val1 >= val2 ? val1 : val2;

    public static short Max(short val1, short val2) => val1 >= val2 ? val1 : val2;

    public static int Max(int val1, int val2) => val1 >= val2 ? val1 : val2;

    public static long Max(long val1, long val2) => val1 >= val2 ? val1 : val2;

    public static nint Max(nint val1, nint val2) => val1 >= val2 ? val1 : val2;

    public static sbyte Max(sbyte val1, sbyte val2) => val1 >= val2 ? val1 : val2;

    public static ushort Max(ushort val1, ushort val2) => val1 >= val2 ? val1 : val2;

    public static uint Max(uint val1, uint val2) => val1 >= val2 ? val1 : val2;

    public static ulong Max(ulong val1, ulong val2) => val1 >= val2 ? val1 : val2;

    public static nuint Max(nuint val1, nuint val2) => val1 >= val2 ? val1 : val2;

    public static byte Min(byte val1, byte val2) => val1 <= val2 ? val1 : val2;

    public static short Min(short val1, short val2) => val1 <= val2 ? val1 : val2;

    public static int Min(int val1, int val2) => val1 <= val2 ? val1 : val2;

    public static long Min(long val1, long val2) => val1 <= val2 ? val1 : val2;

    public static nint Min(nint val1, nint val2) => val1 <= val2 ? val1 : val2;

    public static sbyte Min(sbyte val1, sbyte val2) => val1 <= val2 ? val1 : val2;

    public static ushort Min(ushort val1, ushort val2) => val1 <= val2 ? val1 : val2;

    public static uint Min(uint val1, uint val2) => val1 <= val2 ? val1 : val2;

    public static ulong Min(ulong val1, ulong val2) => val1 <= val2 ? val1 : val2;

    public static nuint Min(nuint val1, nuint val2) => val1 <= val2 ? val1 : val2;

    public static double Round(double value, int digits) => Round(value, digits, MidpointRounding.ToEven);

    /// <summary>
    /// The whole number <paramref name="value"/> rounds to by <paramref name="mode"/>; away from zero, as .NET does on
    /// x64: Truncate(value + CopySign(0.49999999999999994, value)).
    /// </summary>
    public static double Round(double value, MidpointRounding mode)
    {
        switch (mode)
        {
            case MidpointRounding.AwayFromZero:
                return System.Math.Truncate(value + System.Math.CopySign(0.49999999999999994, value));
            case MidpointRounding.ToEven:
                return System.Math.Round(value);
            case MidpointRounding.ToZero:
                return System.Math.Truncate(value);
            case MidpointRounding.ToNegativeInfinity:
                return System.Math.Floor(value);
            case MidpointRounding.ToPositiveInfinity:
                return System.Math.Ceiling(value);
            default:
                Runtime.InvalidMidpointRounding((int)mode);
                return default;
        }
    }

    /// <summary>
    /// <paramref name="value"/> rounded to <paramref name="digits"/> decimal digits, as .NET: the value times 10 to the
    /// digits, rounded by <paramref name="mode"/>, divided back; values of 1e16 and more are whole already.
    /// </summary>
    public static double Round(double value, int digits, MidpointRounding mode)
    {
        if ((uint)digits > MaxRoundingDigits)
        {
            Runtime.RoundingDigits(MaxRoundingDigits);
        }

        if (System.Math.Abs(value) < DoubleRoundLimit)
        {
            double power10 = RoundPower10Double[digits];
            value = Round(value * power10, mode) / power10;
        }

        return value;
    }

    public static int Sign(double value)
    {
        if (value < 0)
        {
            return -1;
        }
        else if (value > 0)
        {
            return 1;
        }
        else if (value == 0)
        {
            return 0;
        }

        Runtime.ArithmeticNaN();
        return 0;
    }

    public static int Sign(short value) => Sign((int)value);

    public static int Sign(int value) => (value >> 31) | (int)((uint)-value >> 31);

    public static int Sign(long value) => (int)((value >> 63) | (long)((ulong)-value >> 63));

    public static int Sign(nint value) => value < 0 ? -1 : value > 0 ? 1 : 0;

    public static int Sign(sbyte value) => Sign((int)value);

    public static int Sign(float value)
    {
        if (value < 0)
        {
            return -1;
        }
        else if (value > 0)
        {
            return 1;
        }
        else if (value == 0)
        {
            return 0;
        }

        Runtime.ArithmeticNaN();
        return 0;
    }

    /// <summary>The sine and the cosine of <paramref name="x"/>, as Sin and Cos give them (.NET on Windows calls both).</summary>
    public static (double Sin, double Cos) SinCos(double x) => (System.Math.Sin(x), System.Math.Cos(x));

    /// <summary>10 to the powers Round takes digits for: a constant table in flash.</summary>
    private static ReadOnlySpan<double> RoundPower10Double =>
    [
        1E0, 1E1, 1E2, 1E3, 1E4, 1E5, 1E6, 1E7, 1E8, 1E9, 1E10, 1E11, 1E12, 1E13, 1E14, 1E15,
    ];
}

/// <summary>
/// ChipSharp's <see cref="System.MathF"/>, the members that are more than one runtime function (see <see cref="Math"/>):
/// <c>Sign</c>, <c>Round</c> to a number of digits or by a <see cref="System.MidpointRounding"/>, <c>Log</c> in a base
/// and <c>SinCos</c>, in floats, as .NET 10 writes them.
/// </summary>
[Substitute(typeof(System.MathF))]
internal static class MathF
{
    private const int MaxRoundingDigits = 6;

    // the limit above which a float is a whole number at every number of digits Round takes
    private const float SingleRoundLimit = 1e8f;

    public static float Log(float x, float y)
    {
#pragma warning disable CS1718 // x != x is how NaN is told apart here
        if (x != x)
        {
            return x; // IEEE 754-2008: NaN payload must be preserved
        }

        if (y != y)
        {
            return y;
        }
#pragma warning restore CS1718

        if (y == 1)
        {
            return float.NaN;
        }

        if (x != 1 && (y == 0 || y == float.PositiveInfinity))
        {
            return float.NaN;
        }

        return System.MathF.Log(x) / System.MathF.Log(y);
    }

    public static float Round(float x, int digits) => Round(x, digits, MidpointRounding.ToEven);

    /// <summary>The whole number <paramref name="x"/> rounds to by <paramref name="mode"/> (away from zero: by 0.49999997f).</summary>
    public static float Round(float x, MidpointRounding mode)
    {
        switch (mode)
        {
            case MidpointRounding.AwayFromZero:
                return System.MathF.Truncate(x + System.MathF.CopySign(0.49999997f, x));
            case MidpointRounding.ToEven:
                return System.MathF.Round(x);
            case MidpointRounding.ToZero:
                return System.MathF.Truncate(x);
            case MidpointRounding.ToNegativeInfinity:
                return System.MathF.Floor(x);
            case MidpointRounding.ToPositiveInfinity:
                return System.MathF.Ceiling(x);
            default:
                Runtime.InvalidMidpointRounding((int)mode);
                return default;
        }
    }

    public static float Round(float x, int digits, MidpointRounding mode)
    {
        if ((uint)digits > MaxRoundingDigits)
        {
            Runtime.RoundingDigits(MaxRoundingDigits);
        }

        if (System.MathF.Abs(x) < SingleRoundLimit)
        {
            float power10 = RoundPower10Single[digits];
            x = Round(x * power10, mode) / power10;
        }

        return x;
    }

    public static int Sign(float x) => Math.Sign(x);

    public static (float Sin, float Cos) SinCos(float x) => (System.MathF.Sin(x), System.MathF.Cos(x));

    private static ReadOnlySpan<float> RoundPower10Single => [1e0f, 1e1f, 1e2f, 1e3f, 1e4f, 1e5f, 1e6f];
}

/// <summary>
/// <see cref="System.MidpointRounding"/>, the rules of Math.Round and MathF.Round, with .NET's values: a program's
/// MidpointRounding is this enum.
/// </summary>
[Substitute(typeof(System.MidpointRounding))]
internal enum MidpointRounding
{
    ToEven = 0,
    AwayFromZero = 1,
    ToZero = 2,
    ToNegativeInfinity = 3,
    ToPositiveInfinity = 4,
}
