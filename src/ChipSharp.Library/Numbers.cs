namespace ChipSharp.Library;

/// <summary>
/// The methods of the numbers, of bool and of char that generic code calls through an interface (<c>IComparable&lt;T&gt;</c>,
/// <c>IEquatable&lt;T&gt;</c>) or object (<c>GetHashCode</c>): in an instance of generic code for a number, ChipSharp
/// calls the one here for the number's own (<c>a.CompareTo(b)</c> with T = int is <c>CompareTo(ref a, b)</c>), which
/// takes the number's address as the number's own takes it as <c>this</c>. Each gives what .NET's gives, written as
/// System.Private.CoreLib writes it: the CompareTo of the types narrower than int is a difference, not -1, 0 or 1; a
/// float NaN equals NaN and is less than every other value.
/// </summary>
/// <remarks>The GetHashCode of float, double, nint and nuint (which depend on bits or on the width of a pointer) are not here yet.</remarks>
internal static class Numbers
{
    public static int CompareTo(ref bool value, bool other) => value == other ? 0 : !value ? -1 : 1;

    public static int CompareTo(ref char value, char other) => value - other;

    public static int CompareTo(ref sbyte value, sbyte other) => value - other;

    public static int CompareTo(ref byte value, byte other) => value - other;

    public static int CompareTo(ref short value, short other) => value - other;

    public static int CompareTo(ref ushort value, ushort other) => value - other;

    public static int CompareTo(ref int value, int other) => value < other ? -1 : value > other ? 1 : 0;

    public static int CompareTo(ref uint value, uint other) => value < other ? -1 : value > other ? 1 : 0;

    public static int CompareTo(ref long value, long other) => value < other ? -1 : value > other ? 1 : 0;

    public static int CompareTo(ref ulong value, ulong other) => value < other ? -1 : value > other ? 1 : 0;

    public static int CompareTo(ref nint value, nint other) => value < other ? -1 : value > other ? 1 : 0;

    public static int CompareTo(ref nuint value, nuint other) => value < other ? -1 : value > other ? 1 : 0;

    // NaN (the only value not equal to itself) is less than any other, and equal to NaN
#pragma warning disable CS1718 // x != x is how NaN is told apart here, without the library's IsNaN
    public static int CompareTo(ref float value, float other) =>
        value < other ? -1 : value > other ? 1 : value == other ? 0 : value != value ? (other != other ? 0 : -1) : 1;

    public static int CompareTo(ref double value, double other) =>
        value < other ? -1 : value > other ? 1 : value == other ? 0 : value != value ? (other != other ? 0 : -1) : 1;

    public static bool Equals(ref float value, float other) => value == other || (value != value && other != other);

    public static bool Equals(ref double value, double other) => value == other || (value != value && other != other);
#pragma warning restore CS1718

    public static bool Equals(ref bool value, bool other) => value == other;

    public static bool Equals(ref char value, char other) => value == other;

    public static bool Equals(ref sbyte value, sbyte other) => value == other;

    public static bool Equals(ref byte value, byte other) => value == other;

    public static bool Equals(ref short value, short other) => value == other;

    public static bool Equals(ref ushort value, ushort other) => value == other;

    public static bool Equals(ref int value, int other) => value == other;

    public static bool Equals(ref uint value, uint other) => value == other;

    public static bool Equals(ref long value, long other) => value == other;

    public static bool Equals(ref ulong value, ulong other) => value == other;

    public static bool Equals(ref nint value, nint other) => value == other;

    public static bool Equals(ref nuint value, nuint other) => value == other;

    public static int GetHashCode(ref bool value) => value ? 1 : 0;

    public static int GetHashCode(ref char value) => value | (value << 16);

    public static int GetHashCode(ref sbyte value) => value;

    public static int GetHashCode(ref byte value) => value;

    public static int GetHashCode(ref short value) => value;

    public static int GetHashCode(ref ushort value) => value;

    public static int GetHashCode(ref int value) => value;

    public static int GetHashCode(ref uint value) => (int)value;

    public static int GetHashCode(ref long value) => (int)value ^ (int)(value >> 32);

    public static int GetHashCode(ref ulong value) => (int)value ^ (int)(value >> 32);
}
