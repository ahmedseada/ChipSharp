using System.Diagnostics.CodeAnalysis;

namespace ChipSharp.Library;

/// <summary>The runtime functions of the methods of System.Array that take a delegate (see ArrayDelegates.cs), and of delegates.</summary>
internal static partial class Runtime
{
    private const string IndexAtMost =
        "Index was out of range. Must be non-negative and less than or equal to the size of the collection.";

    private const string IndexBelow = "Index was out of range. Must be non-negative and less than the size of the collection.";

    private const string CountInside =
        "Count must be positive and count must refer to a location within the string/array/collection.";

    /// <summary>ArgumentNullException: Array.Exists, Find, FindAll, FindIndex, ... are given null for their <c>match</c>.</summary>
    [DoesNotReturn]
    public static void MatchNull() => throw new ArgumentNullException("match");

    /// <summary>ArgumentNullException: Array.ForEach is given null for its <c>action</c>.</summary>
    [DoesNotReturn]
    public static void ActionNull() => throw new ArgumentNullException("action");

    /// <summary>ArgumentNullException: Array.ConvertAll is given null for its <c>converter</c>.</summary>
    [DoesNotReturn]
    public static void ConverterNull() => throw new ArgumentNullException("converter");

    /// <summary>ArgumentNullException: Array.Sort is given null for its <c>comparison</c>.</summary>
    [DoesNotReturn]
    public static void ComparisonNull() => throw new ArgumentNullException("comparison");

    /// <summary>ArgumentOutOfRangeException: Array.FindIndex's <c>startIndex</c> is negative or past the array's end.</summary>
    public static void StartIndexPastEnd() => throw new ArgumentOutOfRangeException("startIndex", IndexAtMost);

    /// <summary>ArgumentOutOfRangeException: Array.FindLastIndex's <c>startIndex</c> is not an index of the array.</summary>
    public static void StartIndexAtEnd() => throw new ArgumentOutOfRangeException("startIndex", IndexBelow);

    /// <summary>ArgumentOutOfRangeException: Array.FindIndex's or FindLastIndex's <c>count</c> is negative or too big.</summary>
    public static void CountOutOfRange() => throw new ArgumentOutOfRangeException("count", CountInside);

    /// <summary>
    /// ArgumentException: Delegate.Combine or Remove of delegates of two types (which C# allows only through variance,
    /// <c>Action&lt;object&gt;</c> as an <c>Action&lt;string&gt;</c>). ChipSharp's Combine and Remove call it themselves.
    /// </summary>
    public static void DelegateTypes() => throw new ArgumentException("Delegates must be of the same type.");
}
