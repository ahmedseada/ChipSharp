namespace ChipSharp.Abstractions;

/// <summary>An error ChipSharp reports to the user. The message says what is wrong and where.</summary>
public sealed class ChipSharpException(string message, Exception? innerException = null)
    : Exception(message, innerException);
