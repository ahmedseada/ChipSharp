using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ChipSharp.Library;

/// <summary>
/// The runtime functions of <see cref="Math"/> and <see cref="MathF"/>: where .NET's Math throws, with .NET's messages.
/// In every runtime's failures file (chipsharp_failures.c, rp2040_failures.c, armv6m_failures.c); they need no heap,
/// and write the numbers of a message as .NET's invariant culture does.
/// </summary>
internal static partial class Runtime
{
    /// <summary>ArithmeticException: Math.Sign of NaN.</summary>
    [DoesNotReturn]
    public static void ArithmeticNaN() =>
        throw new ArithmeticException("Function does not accept floating point Not-a-Number values.");

    /// <summary>ArgumentException: Math.Clamp of a signed whole number given a <paramref name="min"/> above its <paramref name="max"/>.</summary>
    [DoesNotReturn]
    public static void MinMaxInt64(long min, long max) =>
        throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"'{min}' cannot be greater than {max}."));

    /// <summary>ArgumentException: Math.Clamp of an unsigned whole number given a min above its max.</summary>
    [DoesNotReturn]
    public static void MinMaxUInt64(ulong min, ulong max) =>
        throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"'{min}' cannot be greater than {max}."));

    /// <summary>ArgumentException: Math.Clamp of a double given a min above its max.</summary>
    [DoesNotReturn]
    public static void MinMaxFloat64(double min, double max) =>
        throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"'{min}' cannot be greater than {max}."));

    /// <summary>ArgumentException: Math.Clamp of a float given a min above its max.</summary>
    [DoesNotReturn]
    public static void MinMaxFloat32(float min, float max) =>
        throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"'{min}' cannot be greater than {max}."));

    /// <summary>ArgumentOutOfRangeException: Math.Round (<paramref name="maximum"/> 15) or MathF.Round (6) given too many digits.</summary>
    [DoesNotReturn]
    public static void RoundingDigits(int maximum) => throw new ArgumentOutOfRangeException("digits",
        string.Create(CultureInfo.InvariantCulture, $"Rounding digits must be between 0 and {maximum}, inclusive."));

    /// <summary>ArgumentException: Math.Round given a MidpointRounding that is none of its five.</summary>
    [DoesNotReturn]
    public static void InvalidMidpointRounding(int mode) => throw new ArgumentException(string.Create(
        CultureInfo.InvariantCulture, $"The value '{mode}' is not valid for this usage of the type MidpointRounding."), "mode");
}
