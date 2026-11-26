using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Analysis;

/// <summary>Types and methods in messages are written the C# way.</summary>
public sealed class CSharpNamesTests
{
    private static readonly IlNamedType List = new("System.Collections.Generic", "List`1", null, "System.Collections");
    private static readonly IlNamedType Nullable = new("System", "Nullable`1", null, "System.Runtime");
    private static readonly IlNamedType Outer = new("Demo", "Outer", null, null);

    [Fact]
    public void Types_are_written_as_CSharp_writes_them()
    {
        Assert.Equal("int", CSharpNames.Type(IlType.Int32));
        Assert.Equal("ulong", CSharpNames.Type(IlType.UInt64));
        Assert.Equal("nuint", CSharpNames.Type(IlType.NativeUInt));
        Assert.Equal("string[]", CSharpNames.Type(new IlArrayType(IlType.String)));
        Assert.Equal("double[,]", CSharpNames.Type(new IlMultiDimensionalArrayType(IlType.Float64, 2)));
        Assert.Equal("ref int", CSharpNames.Type(new IlByRefType(IlType.Int32)));
        Assert.Equal("byte*", CSharpNames.Type(new IlPointerType(IlType.UInt8)));
        Assert.Equal("int?", CSharpNames.Type(new IlGenericInstanceType(Nullable, [IlType.Int32])));
        Assert.Equal("System.Collections.Generic.List<long>", CSharpNames.Type(new IlGenericInstanceType(List, [IlType.Int64])));
        Assert.Equal("Demo.Outer.Inner", CSharpNames.Type(new IlNamedType("", "Inner", Outer, null)));
        Assert.Equal("string", CSharpNames.Type(new IlNamedType("System", "String", null, "System.Runtime")));
        Assert.Equal("System.Console", CSharpNames.Type(new IlNamedType("System", "Console", null, "System.Console")));
        Assert.Equal("!!0", CSharpNames.Type(new IlGenericParameterType(0, true)));
    }

    [Fact]
    public void Methods_are_written_with_their_parameter_types()
    {
        var assembly = IlAssembly.Load(CSharpCompiler.Compile("""
            using System.Collections.Generic;

            static class C
            {
                static int Square(int n) => n * n;
                static T[] Pair<T>(T first, ref T second) => [first, second];
                static void Use(List<int> list) => list.Add(Square(3));
            }
            """), "Test.dll");

        Assert.Equal("C.Square(int)", CSharpNames.Method(assembly.FindMethods("C.Square").Single()));
        Assert.Equal("C.Pair<T>(T, ref T)", CSharpNames.Method(assembly.FindMethods("C.Pair").Single()));

        var add = assembly.FindMethods("C.Use").Single().Body!.Instructions
            .Select(i => i.Operand).OfType<IlMethodOperand>().Last().Method;
        Assert.Equal("System.Collections.Generic.List<int>.Add(int)", CSharpNames.Method(add));
    }
}
