using Xunit.Sdk;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// Calls a function on <see cref="AvrCpu"/> as compiled code calls it, with AVR's calling convention (avr-gcc's, which
/// LLVM keeps): the arguments from r25 down, each in an even number of registers (a 32-bit one in r25-r22, a second in
/// r21-r18; a 64-bit one in r25-r18, a second in r17-r10), the result where the first argument would be (8 bits in
/// r24, 16 in r25:r24, 32 in r25-r22, 64 in r25-r18). And checks what the function must keep: r2-r17, r28 and r29 as they
/// were (also those that held arguments), r1 zero, the stack pointer where it was.
/// </summary>
internal static class AvrAbi
{
    private static readonly int[] Kept = [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 28, 29];

    /// <summary>
    /// Calls the function at byte address <paramref name="address"/> with <paramref name="arguments"/> (each a value and
    /// its size in bytes, 1, 2, 4 or 8) and returns its result of <paramref name="resultSize"/> bytes (0: none). Every
    /// other register holds random bits first, so that a function that counts on one fails.
    /// </summary>
    public static ulong Call(AvrCpu cpu, int address, int resultSize, Random random, params (ulong Value, int Size)[] arguments)
    {
        for (var register = 0; register < 32; register++)
        {
            cpu.Data[register] = (byte)random.Next(256);
        }

        cpu.Data[1] = 0;
        var next = 26;
        foreach (var (value, size) in arguments)
        {
            next -= (size + 1) & ~1;
            if (next < 8)
            {
                throw new ArgumentException("More arguments than the registers hold.", nameof(arguments));
            }

            for (var b = 0; b < size; b++)
            {
                cpu.Data[next + b] = (byte)(value >> (8 * b));
            }
        }

        var kept = Kept.Select(register => cpu.Data[register]).ToArray();
        var stack = cpu.Data.Length - 1;
        cpu.Sp = stack;
        cpu.Call(address);

        for (var i = 0; i < Kept.Length; i++)
        {
            if (cpu.Data[Kept[i]] != kept[i])
            {
                throw new XunitException($"The function at 0x{address:X4} changed r{Kept[i]}, which it must keep.");
            }
        }

        if (cpu.Data[1] != 0)
        {
            throw new XunitException($"The function at 0x{address:X4} left r1 at {cpu.Data[1]}; it must be zero.");
        }

        if (cpu.Sp != stack)
        {
            throw new XunitException($"The function at 0x{address:X4} left the stack pointer at 0x{cpu.Sp:X4}, not 0x{stack:X4}.");
        }

        return Result(cpu, resultSize);
    }

    /// <summary>The result of <paramref name="size"/> bytes where a function leaves it.</summary>
    public static ulong Result(AvrCpu cpu, int size)
    {
        var first = 26 - ((size + 1) & ~1);
        var result = 0UL;
        for (var b = 0; b < size; b++)
        {
            result |= (ulong)cpu.Data[first + b] << (8 * b);
        }

        return result;
    }
}
