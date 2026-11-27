namespace ChipSharp.Library;

/// <summary>
/// The functions of ChipSharp's runtimes that the substitutes call. In a program ChipSharp compiles, a call to one of
/// these methods is a call to the runtime function of the same name in ChipSharp.Runtime, e.g.
/// <c>ChipSharp.Runtime.TimeSpanOverflow</c>, and the method's own body is left out; on .NET the body runs, so that the
/// substitutes do there what .NET's own types do.
/// </summary>
/// <remarks>
/// ChipSharp has no exceptions yet. Where .NET throws one, these stop the program the way an array index out of range
/// does: they print what .NET prints for an exception nothing catches ("Unhandled exception. System.OverflowException:
/// ..."), on standard error on a PC, which then exits with exit code 1, and on UART0 on a chip, which then stops at a
/// breakpoint. On .NET they throw that very exception. Each needs a function in every runtime's failures file.
/// </remarks>
internal static class Runtime
{
    private const string TimeSpanTooLong = "TimeSpan overflowed because the duration is too long.";
    private const string TimeSpanNotANumber = "TimeSpan does not accept floating point Not-a-Number values.";

    /// <summary>OverflowException: TimeSpan arithmetic (or a double factory) gives more ticks than a long holds.</summary>
    public static void TimeSpanOverflow() => throw new OverflowException(TimeSpanTooLong);

    /// <summary>ArgumentOutOfRangeException: a whole-number factory or a constructor gives too many ticks.</summary>
    public static void TimeSpanOutOfRange() => throw new ArgumentOutOfRangeException(null, TimeSpanTooLong);

    /// <summary>ArgumentException: a double factory is given NaN.</summary>
    public static void TimeSpanNaN() => throw new ArgumentException(TimeSpanNotANumber);

    /// <summary>ArgumentException: a TimeSpan is multiplied by NaN.</summary>
    public static void TimeSpanNaNFactor() => throw new ArgumentException(TimeSpanNotANumber, "factor");

    /// <summary>ArgumentException: a TimeSpan is divided by NaN.</summary>
    public static void TimeSpanNaNDivisor() => throw new ArgumentException(TimeSpanNotANumber, "divisor");

    /// <summary>OverflowException: <c>TimeSpan.MinValue.Duration()</c>.</summary>
    public static void TimeSpanDurationOverflow() => throw new OverflowException(
        "The duration cannot be returned for TimeSpan.MinValue because the absolute value of TimeSpan.MinValue exceeds " +
        "the value of TimeSpan.MaxValue.");

    /// <summary>OverflowException: the negative of the smallest value, e.g. <c>-TimeSpan.MinValue</c>.</summary>
    public static void NegateOverflow() => throw new OverflowException(
        "Negating the minimum value of a twos complement number is invalid.");

    /// <summary>ArgumentNullException: a method of System.Array is given null for its <c>array</c>.</summary>
    public static void ArrayNull() => throw new ArgumentNullException("array");

    /// <summary>ArgumentOutOfRangeException: a method of System.Array is given a negative <c>index</c>.</summary>
    public static void NegativeIndex() => throw new ArgumentOutOfRangeException("index", NonNegative);

    /// <summary>ArgumentOutOfRangeException: a method of System.Array is given a negative <c>length</c>.</summary>
    public static void NegativeLength() => throw new ArgumentOutOfRangeException("length", NonNegative);

    /// <summary>ArgumentException: an index and a length of a method of System.Array that pass the array's end.</summary>
    public static void InvalidOffsetLength() => throw new ArgumentException(
        "Offset and length were out of bounds for the array or count is greater than the number of elements from " +
        "index to the end of the source collection.");

    private const string NonNegative = "Non-negative number required.";
}
