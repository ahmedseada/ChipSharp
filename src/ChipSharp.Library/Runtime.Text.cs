using System.Diagnostics.CodeAnalysis;

namespace ChipSharp.Library;

/// <summary>
/// The runtime functions of the substitutes that build text (StringBuilder.cs, Type.cs): .NET's argument exceptions,
/// their parameter's name and message given as strings, so that one function says each kind of message. In
/// src/ChipSharp.Runtime.Shared/chipsharp_string_heap.c.
/// </summary>
internal static partial class Runtime
{
    /// <summary>ArgumentOutOfRangeException: "<paramref name="message"/> (Parameter '<paramref name="parameter"/>')".</summary>
    [DoesNotReturn]
    public static void ArgumentOutOfRangeOf(string parameter, string message) =>
        throw new ArgumentOutOfRangeException(parameter, message);

    /// <summary>
    /// ArgumentOutOfRangeException of <c>ArgumentOutOfRangeException.ThrowIfNegative</c>: "count ('-1') must be a
    /// non-negative value. (Parameter 'count')", then "Actual value was -1." on a line of its own.
    /// </summary>
    [DoesNotReturn]
    public static void ArgumentNegative(string parameter, int value) =>
        throw new ArgumentOutOfRangeException(parameter, value, $"{parameter} ('{value}') must be a non-negative value.");

    /// <summary>ArgumentOutOfRangeException of <c>ArgumentOutOfRangeException.ThrowIfNegativeOrZero</c>.</summary>
    [DoesNotReturn]
    public static void ArgumentNotPositive(string parameter, int value) =>
        throw new ArgumentOutOfRangeException(parameter, value,
            $"{parameter} ('{value}') must be a non-negative and non-zero value.");

    /// <summary>ArgumentNullException: "Value cannot be null. (Parameter '<paramref name="parameter"/>')".</summary>
    [DoesNotReturn]
    public static void ArgumentNullOf(string parameter) => throw new ArgumentNullException(parameter);

    /// <summary>ArgumentException: <paramref name="message"/>, and " (Parameter '...')" when there is a parameter.</summary>
    [DoesNotReturn]
    public static void ArgumentInvalid(string message, string? parameter) => throw new ArgumentException(message, parameter);

    /// <summary>IndexOutOfRangeException: "Index was outside the bounds of the array.", as an array's index says it.</summary>
    [DoesNotReturn]
    public static void IndexOutOfRange() => throw new IndexOutOfRangeException();

    /// <summary>OutOfMemoryException, as the heap says it.</summary>
    [DoesNotReturn]
    public static void OutOfMemory() => throw new OutOfMemoryException();
}
