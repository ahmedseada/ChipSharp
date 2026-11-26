namespace ChipSharp.Ir;

/// <summary>
/// TIR that is not well formed, found while building it or by <see cref="TirVerifier"/>.
/// The message says what is wrong and where (function, block, instruction).
/// </summary>
public sealed class TirException(string message) : Exception(message);
