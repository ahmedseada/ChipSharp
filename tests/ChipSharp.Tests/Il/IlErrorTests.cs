using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Il;

/// <summary>Bad IL, made by patching the bytes of methods Roslyn compiled, is reported with file, method and offset.</summary>
public sealed class IlErrorTests
{
    // Square's IL is 02 02 5A 2A (ldarg.0 ldarg.0 mul ret); Use's is 19 28 <token> 2A (ldc.i4.3 call Square ret)
    private const string Source = """
        static class C
        {
            static int Square(int n) => n * n;
            static int Use() => Square(3);
        }
        """;

    [Fact]
    public void An_unknown_opcode_is_an_error()
    {
        var error = BodyError("Square", 0x02, 0x02, 0x24, 0x2A);

        Assert.Equal("Test.dll: C.Square(int32) at IL_0002: unknown opcode 0x24.", error.Message);
    }

    [Fact]
    public void An_unknown_two_byte_opcode_is_an_error()
    {
        var error = BodyError("Square", 0x02, 0xFE, 0x08, 0x2A);

        Assert.Equal("Test.dll: C.Square(int32) at IL_0001: unknown opcode 0xFE 0x08.", error.Message);
    }

    [Fact]
    public void IL_that_ends_inside_an_instruction_is_an_error()
    {
        Assert.Equal("Test.dll: C.Square(int32) at IL_0002: the IL ends in the middle of the operand of ldc.i4.",
            BodyError("Square", 0x02, 0x02, 0x20, 0x2A).Message);
        Assert.Contains("at IL_0003: the IL ends after 0xFE", BodyError("Square", 0x02, 0x02, 0x5A, 0xFE).Message);
    }

    [Fact]
    public void A_branch_outside_the_method_is_an_error()
    {
        var error = BodyError("Square", 0x02, 0x2B, 0x05, 0x2A);

        Assert.Equal("Test.dll: C.Square(int32) at IL_0001: br.s jumps to offset 8, outside the method's 4 bytes of IL.",
            error.Message);
    }

    [Fact]
    public void A_branch_into_the_middle_of_an_instruction_is_an_error()
    {
        // br.s +1 lands on the operand of the ldc.i4.s at IL_0002
        var error = BodyError("Square", 0x2B, 0x01, 0x1F, 0x2A);

        Assert.Equal("Test.dll: C.Square(int32) at IL_0000: br.s jumps to offset 3, inside another instruction.", error.Message);
    }

    [Fact]
    public void A_token_that_names_nothing_or_the_wrong_thing_is_an_error()
    {
        Assert.Equal("Test.dll: C.Use() at IL_0001: token 0x06000099 names a row that does not exist.",
            BodyError("Use", 0x19, 0x28, 0x99, 0x00, 0x00, 0x06, 0x2A).Message);
        Assert.Equal("Test.dll: C.Use() at IL_0001: token 0x02000001 is not a method.",
            BodyError("Use", 0x19, 0x28, 0x01, 0x00, 0x00, 0x02, 0x2A).Message);
    }

    [Fact]
    public void The_rest_of_the_assembly_still_reads_when_one_body_is_bad()
    {
        var assembly = Patched("Square", 0x02, 0x02, 0x24, 0x2A);

        Assert.Throws<ChipSharpException>(() => IlReaderTests.Method(assembly, "C.Square").Body);
        Assert.Equal(3, IlReaderTests.Method(assembly, "C.Use").Body!.Instructions.Count);
    }

    [Fact]
    public void A_file_that_is_not_an_assembly_is_an_error()
    {
        var notPe = Assert.Throws<ChipSharpException>(() => IlAssembly.Load([1, 2, 3, 4], "junk.dll"));
        Assert.StartsWith("'junk.dll' is not a valid .NET assembly", notPe.Message);

        var missing = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"), "missing.dll");
        var notFound = Assert.Throws<ChipSharpException>(() => IlAssembly.Load(missing));
        Assert.StartsWith($"Cannot read '{missing}'", notFound.Message);
    }

    private static ChipSharpException BodyError(string method, params byte[] il) =>
        Assert.Throws<ChipSharpException>(() => IlReaderTests.Method(Patched(method, il), $"C.{method}").Body);

    /// <summary>Compiles <see cref="Source"/> and overwrites the IL of <paramref name="method"/> (same length).</summary>
    private static IlAssembly Patched(string method, params byte[] il)
    {
        var image = CSharpCompiler.Compile(Source);
        var code = CodeOffset(image, method, il.Length);
        il.CopyTo(image, code);
        return IlAssembly.Load(image, "Test.dll");
    }

    /// <summary>Where the IL of a method with a tiny header starts in the file.</summary>
    private static int CodeOffset(byte[] image, string methodName, int codeSize)
    {
        using var pe = new PEReader(ImmutableArray.Create(image));
        var reader = pe.GetMetadataReader();
        var handle = reader.MethodDefinitions.Single(h => reader.GetString(reader.GetMethodDefinition(h).Name) == methodName);
        var rva = reader.GetMethodDefinition(handle).RelativeVirtualAddress;
        var section = pe.PEHeaders.SectionHeaders.Single(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + s.VirtualSize);
        var header = rva - section.VirtualAddress + section.PointerToRawData;

        // a tiny header is one byte: the code size in the top six bits, 0b10 in the low two
        Assert.Equal((codeSize << 2) | 2, image[header]);
        return header + 1;
    }
}
