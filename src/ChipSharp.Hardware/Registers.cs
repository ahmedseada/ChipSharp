namespace ChipSharp.Hardware;

/// <summary>
/// The chip's memory-mapped registers, read and written 32 bits at a time by address, for anything the other
/// classes do not cover yet. The address must be a multiple of 4; the chip's datasheet lists the registers.
/// </summary>
/// <remarks>
/// These methods run on a chip, in a program built with ChipSharp. On .NET itself they throw
/// <see cref="PlatformNotSupportedException"/>.
/// </remarks>
public static class Registers
{
    /// <summary>The 32-bit value of the register at <paramref name="address"/>.</summary>
    public static uint Read(uint address) => throw ChipOnly.Method("Registers.Read");

    /// <summary>Writes <paramref name="value"/> to the register at <paramref name="address"/>.</summary>
    public static void Write(uint address, uint value) => throw ChipOnly.Method("Registers.Write");
}
