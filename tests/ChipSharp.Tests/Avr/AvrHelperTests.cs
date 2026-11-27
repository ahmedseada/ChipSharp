using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Runtime.Avr;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// The avr runtime's multiply, divide and memory functions, run on the ATmega328P (<see cref="AvrCpu"/>) and checked
/// against .NET's arithmetic for many values. They are called the way LLVM calls them: from functions in LLVM IR, as
/// ChipSharp's code generator writes it (<c>sdiv i8</c> calls __divmodqi4 with LLVM's AVR_BUILTIN registers, <c>mul i64</c>
/// __muldi3, ...), linked with the runtime and nothing else; and each such call keeps what AVR's calling convention
/// says it must (<see cref="AvrAbi"/>). The two multiplications only a chip without MUL calls, and the memory
/// functions, are called directly.
/// </summary>
public sealed class AvrHelperTests
{
    private static readonly string[] Operations = ["sdiv", "srem", "udiv", "urem", "mul"];
    private static readonly int[] Widths = [8, 16, 32, 64];

    private static readonly Lazy<Task<AvrCoreProgram>> Built = new(() => AvrCoreProgram.BuildAsync(
        [.. AvrCoreProgram.RuntimeFiles, new GeneratedFile("helpers_test.ll", HarnessIr())],
        [.. Widths.SelectMany(bits => Operations.Select(op => Name(op, bits))), "__mulqi3", "__mulhi3", "memcpy",
            "memmove", "memset", "memcmp", "chipsharp_memcpy_flash"]));

    [AvrFact]
    public async Task Eight_bit_division_and_multiplication_match_dotnet_for_every_pair()
    {
        var program = await Built.Value;
        Check(program, 8, [.. Enumerable.Range(0, 256).SelectMany(a => Enumerable.Range(0, 256).Select(b => ((ulong)a, (ulong)b)))]);
    }

    [AvrFact]
    public async Task Sixteen_bit_division_and_multiplication_match_dotnet()
    {
        Check(await Built.Value, 16, Pairs(16, 20_000));
    }

    [AvrFact]
    public async Task Thirty_two_bit_division_and_multiplication_match_dotnet()
    {
        Check(await Built.Value, 32, Pairs(32, 20_000));
    }

    [AvrFact]
    public async Task Sixty_four_bit_division_and_multiplication_match_dotnet()
    {
        Check(await Built.Value, 64, Pairs(64, 10_000));
    }

    /// <summary>__mulqi3 and __mulhi3: LLVM calls them only on a chip without MUL (the ATtiny85), so they are called here.</summary>
    [AvrFact]
    public async Task The_multiplications_of_a_chip_without_MUL_match_dotnet()
    {
        var program = await Built.Value;
        var cpu = program.Load();
        var random = new Random(85);
        for (var a = 0; a < 256; a++)
        {
            for (var b = 0; b < 256; b++)
            {
                Assert.Equal((ulong)(byte)(a * b),
                    AvrAbi.Call(cpu, program.Address("__mulqi3"), 1, random, ((ulong)a, 1), ((ulong)b, 1)));
            }
        }

        foreach (var (a, b) in Pairs(16, 20_000))
        {
            Assert.Equal((ulong)(ushort)(a * b), AvrAbi.Call(cpu, program.Address("__mulhi3"), 2, random, (a, 2), (b, 2)));
        }
    }

    [AvrFact]
    public async Task Memcpy_memmove_memset_and_memcmp_do_what_C_says()
    {
        var program = await Built.Value;
        var cpu = program.Load();
        var random = new Random(328);
        const int Buffer = 0x200; // RAM the program does not use: 0x200-0x5FF
        for (var test = 0; test < 400; test++)
        {
            var count = random.Next(5) == 0 ? random.Next(3) : random.Next(1, 300);
            var source = Buffer + random.Next(0, 400);
            var target = random.Next(2) == 0 ? source + random.Next(-count, count + 1) : Buffer + random.Next(0, 400);
            target = Math.Clamp(target, Buffer, Buffer + 400);
            var before = new byte[0x400];
            random.NextBytes(before);
            before.CopyTo(cpu.Data, Buffer);

            switch (test % 4)
            {
                case 0 when Math.Abs(target - source) >= count: // memcpy: blocks that do not overlap
                case 1: // memmove: any
                    var expected = (byte[])before.Clone();
                    Array.Copy(before, source - Buffer, expected, target - Buffer, count);
                    var name = test % 4 == 0 ? "memcpy" : "memmove";
                    Assert.Equal((ulong)target, AvrAbi.Call(cpu, program.Address(name), 2, random,
                        ((ulong)target, 2), ((ulong)source, 2), ((ulong)count, 2)));
                    Assert.Equal(expected, cpu.Data[Buffer..(Buffer + 0x400)]);
                    break;
                case 2: // memset
                    var value = random.Next(256);
                    var set = (byte[])before.Clone();
                    Array.Fill(set, (byte)value, target - Buffer, count);
                    Assert.Equal((ulong)target, AvrAbi.Call(cpu, program.Address("memset"), 2, random,
                        ((ulong)target, 2), ((ulong)(value | 0x5A00), 2), ((ulong)count, 2)));
                    Assert.Equal(set, cpu.Data[Buffer..(Buffer + 0x400)]);
                    break;
                case 3: // memcmp: equal blocks, then one byte changed
                    Array.Copy(before, source - Buffer, cpu.Data, target, count);
                    var differ = count > 0 && random.Next(4) != 0;
                    if (differ)
                    {
                        cpu.Data[target + random.Next(count)] = (byte)random.Next(256);
                    }

                    var a = cpu.Data[target..(target + count)];
                    var b = cpu.Data[source..(source + count)];
                    var sign = Math.Sign(a.AsSpan().SequenceCompareTo(b));
                    var result = (short)AvrAbi.Call(cpu, program.Address("memcmp"), 2, random,
                        ((ulong)target, 2), ((ulong)source, 2), ((ulong)count, 2));
                    Assert.Equal(sign, Math.Sign(result));
                    break;
            }
        }
    }

    /// <summary>A copy from flash reads flash (LPM), wherever it starts and whatever its length.</summary>
    [AvrFact]
    public async Task Chipsharp_memcpy_flash_copies_from_flash_to_RAM()
    {
        var program = await Built.Value;
        var cpu = program.Load();
        var random = new Random(1);
        for (var test = 0; test < 300; test++)
        {
            var count = random.Next(5) == 0 ? random.Next(3) : random.Next(1, 600);
            var source = random.Next(0, program.Flash.Length - count);
            var target = 0x200 + random.Next(0, 0x100);
            Assert.Equal((ulong)target, AvrAbi.Call(cpu, program.Address("chipsharp_memcpy_flash"), 2, random,
                ((ulong)target, 2), ((ulong)source, 2), ((ulong)count, 2)));
            Assert.Equal(program.Flash[source..(source + count)], cpu.Data[target..(target + count)]);
        }
    }

    /// <summary>
    /// Every helper is small, and a program has only those it calls: none of these is in a program that only divides
    /// 32-bit values but __divmodsi4 and __udivmodsi4.
    /// </summary>
    [AvrFact]
    public async Task The_helpers_are_small_and_a_program_has_only_those_it_uses()
    {
        var program = await Built.Value;
        string[] multiply = ["__mulsi3", "__muldi3"];
        string[] divide =
        [
            "__udivmodqi4", "__divmodqi4", "__udivmodhi4", "__divmodhi4", "__udivmodsi4", "__divmodsi4", "__udivdi3",
            "__umoddi3", "__divdi3", "__moddi3", "__chipsharp_udivmod64", "__chipsharp_divmod64",
        ];
        string[] memory = ["memcpy", "memmove", "memset", "memcmp", "chipsharp_memcpy_flash"];
        Assert.InRange(multiply.Sum(program.SizeOf), 150, 256);
        Assert.InRange(divide.Sum(program.SizeOf), 400, 768);
        Assert.InRange(memory.Sum(program.SizeOf), 80, 160);

        var divides = await AvrCoreProgram.BuildAsync(
        [
            .. AvrCoreProgram.RuntimeFiles,
            new GeneratedFile("divides.c", "long chipsharp_entry(void) { static volatile long a = 1000, b = 7; return a / b + a % b; }\n"),
        ]);
        Assert.True(divides.Symbols.ContainsKey("__divmodsi4"));
        Assert.All(multiply.Concat(divide).Concat(memory).Except(["__divmodsi4", "__udivmodsi4"]).Append("__adddf3"),
            name => Assert.False(divides.Symbols.ContainsKey(name), $"{name} is in a program that does not call it."));
        Assert.InRange(divides.Flash.Length, 200, 512);
    }

    /// <summary>Every operation of every width, as <paramref name="pairs"/> give them, against .NET's.</summary>
    private static void Check(AvrCoreProgram program, int bits, IReadOnlyList<(ulong A, ulong B)> pairs)
    {
        var mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        var bytes = bits / 8;
        Parallel.ForEach(Operations, operation =>
        {
            var cpu = program.Load();
            var random = new Random(bits * 31 + operation.Length);
            var address = program.Address(Name(operation, bits));
            foreach (var (a, b) in pairs)
            {
                if (operation != "mul" && b == 0)
                {
                    continue; // C# throws DivideByZeroException before the division
                }

                var expected = Dotnet(operation, a, b, bits) & mask;
                var actual = AvrAbi.Call(cpu, address, bytes, random, (a, bytes), (b, bytes));
                Assert.True(expected == actual,
                    $"{operation} i{bits} 0x{a:X} 0x{b:X}: .NET 0x{expected:X}, the runtime 0x{actual:X}");
            }
        });
    }

    /// <summary>What C# computes; the smallest value divided by -1 wraps (C# throws OverflowException before it).</summary>
    private static ulong Dotnet(string operation, ulong a, ulong b, int bits)
    {
        var shift = 64 - bits;
        var (x, y) = ((long)(a << shift) >> shift, (long)(b << shift) >> shift);
        return operation switch
        {
            "sdiv" => x == long.MinValue && y == -1 ? a : (ulong)(x / y),
            "srem" => y == -1 ? 0 : (ulong)(x % y),
            "udiv" => a / b,
            "urem" => a % b,
            _ => unchecked(a * b),
        };
    }

    /// <summary>The edges of a width with each other, then random values: any bits, small ones, and divisors from 1 bit up.</summary>
    private static List<(ulong, ulong)> Pairs(int bits, int count)
    {
        var mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        var top = 1UL << (bits - 1);
        ulong[] edges = [0, 1, 2, 3, 7, 10, 0x7F, 0x80, 0xFF, 0x100, top - 1, top, top + 1, mask - 1, mask, mask / 3, 12345];
        var pairs = edges.SelectMany(a => edges.Select(b => (a & mask, b & mask))).ToList();
        var random = new Random(bits);
        for (var i = 0; i < count; i++)
        {
            var a = (ulong)random.NextInt64() * 2 + (ulong)random.Next(2);
            var b = (ulong)random.NextInt64() * 2 + (ulong)random.Next(2);
            pairs.Add(((a >> random.Next(0, 64)) & mask, (b >> random.Next(0, 64)) & mask));
        }

        return pairs;
    }

    private static string Name(string operation, int bits) => $"test_{operation}{bits}";

    /// <summary>A function of LLVM IR per operation and width: <c>define i32 @test_sdiv32(i32 %a, i32 %b)</c> ...</summary>
    private static string HarnessIr()
    {
        var text = new StringBuilder("target triple = \"avr\"\n\ndefine i32 @chipsharp_entry() {\n  ret i32 0\n}\n\n");
        foreach (var bits in Widths)
        {
            foreach (var operation in Operations)
            {
                text.Append($"define i{bits} @{Name(operation, bits)}(i{bits} %a, i{bits} %b) {{\n")
                    .Append($"  %r = {operation} i{bits} %a, %b\n  ret i{bits} %r\n}}\n\n");
            }
        }

        return text.ToString();
    }
}
