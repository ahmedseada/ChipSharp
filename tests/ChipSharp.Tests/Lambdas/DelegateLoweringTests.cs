using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Lambdas;

/// <summary>
/// The TIR delegates become (see <see cref="Delegates"/>): their layout and descriptors, an invocation as one indirect
/// call, the thunk of a static method, the address of a virtual method devirtualized or read from the tables, and the
/// multicast invoker in each delegate type's virtual method table.
/// </summary>
public sealed class DelegateLoweringTests
{
    private static TirModule Lower(string source, bool debug = false) =>
        IlToTir.Lower(WholeProgramPass.Analyze(Frontend.Il.IlAssembly.Load(CSharpCompiler.Compile([source], [], "Test",
            OutputKind.ConsoleApplication, debug ? OptimizationLevel.Debug : OptimizationLevel.Release), "Test.dll")));

    private static string Function(TirModule module, string name) =>
        TirPrinter.Print(Assert.Single(module.Functions, function => function.Name == name));

    /// <summary>A delegate is its header, target, method and invocations: 16 bytes with 4-byte pointers, 32 with 8.</summary>
    [Theory]
    [InlineData(4, 16, new long[] { 4, 12 })]
    [InlineData(8, 32, new long[] { 8, 24 })]
    public void A_delegate_is_a_header_a_target_a_method_and_its_invocations(int pointerSize, long size, long[] references)
    {
        var layout = new TirLayout(pointerSize);

        Assert.Equal(["<type>", "target", "method", "invocations"], Delegates.ObjectType.Fields.Select(field => field.Name));
        Assert.Equal(size, layout.SizeOf(Delegates.ObjectType));
        Assert.Equal(references, layout.ReferenceOffsets(Delegates.ObjectType));
    }

    [Fact]
    public void An_invocation_is_the_null_check_two_loads_and_one_indirect_call()
    {
        var module = Lower("""
            using System;

            static class Program
            {
                static int Run(Func<int, int> f, int x) => f(x);

                static void Main() => Console.WriteLine(Run(x => x + 1, 2));
            }
            """);

        Assert.Equal("""
            function i32 Program.Run(ref %f, i32 %x)
            IL_0000:
              nullcheck %f
              %0 = fieldaddr System.Delegate %f, target
              %1 = load ref [%0]
              %2 = fieldaddr System.Delegate %f, method
              %3 = load ptr [%2]
              %4 = call indirect i32 %3(%1, %x)
              return %4

            """.ReplaceLineEndings("\n"), Function(module, "Program.Run"));
    }

    /// <summary>
    /// A delegate of a static method holds its thunk, which leaves the target alone; one of an instance method (a lambda
    /// on C#'s cached object) the method itself; each is a new object of its type's descriptor.
    /// </summary>
    [Fact]
    public void A_static_method_is_called_through_its_thunk_and_an_instance_method_directly()
    {
        var module = Lower("""
            using System;

            static class Program
            {
                static int Add(int a, int b) => a + b;

                static void Main()
                {
                    Func<int, int, int> add = Add;
                    Console.WriteLine(add(1, 2));
                }
            }
            """);

        Assert.Equal("""
            function i32 <delegate> Program.Add(int32, int32)(ref %<target>, i32 %a, i32 %b)
            entry:
              %0 = call i32 Program.Add(%a, %b)
              return %0

            """.ReplaceLineEndings("\n"), Function(module, "<delegate> Program.Add(int32, int32)"));
        var main = Function(module, "Program.Main");
        Assert.Contains("funcaddr <delegate> Program.Add(int32, int32)", main);
        Assert.Contains("newobject System.Func<int, int, int>", main);
        var type = Assert.Single(module.TypeDescriptors, descriptor => descriptor.Name == "System.Func<int, int, int>");
        Assert.Equal(("System.Func`3[System.Int32,System.Int32,System.Int32]", "System.MulticastDelegate", "System.Delegate"),
            (type.RuntimeName, type.Base!.Name, type.Base.Base!.Name));
    }

    /// <summary>
    /// The address of a virtual method is the one override when only one can run on the objects the program makes, else
    /// read from the object's virtual method table.
    /// </summary>
    [Theory]
    [InlineData("new Square()", "funcaddr Square.Sides")]
    [InlineData("Pick() ? new Square() : new Circle()", "fieldaddr <type descriptor>")]
    public void A_virtual_methods_address_is_devirtualized_or_read_from_the_table(string made, string expected)
    {
        var module = Lower($$"""
            using System;

            abstract class Shape
            {
                public abstract int Sides();
            }

            class Square : Shape
            {
                public override int Sides() => 4;
            }

            class Circle : Shape
            {
                public override int Sides() => 1;
            }

            static class Program
            {
                static bool Pick() => true;

                static void Main()
                {
                    new Circle();
                    Shape shape = {{made}};
                    Func<int> sides = shape.Sides;
                    Console.WriteLine(sides());
                }
            }
            """);

        Assert.Contains(expected, Function(module, "Program.Main"));
    }

    /// <summary>
    /// A program that combines delegates gives each delegate type's descriptor the invoker of its signature, which calls
    /// each delegate of the list, in order, with its target.
    /// </summary>
    [Fact]
    public void Each_delegate_type_has_the_invoker_of_its_signature_when_the_program_combines_delegates()
    {
        var module = Lower("""
            using System;

            static class Program
            {
                static void Main()
                {
                    Action<int> both = x => Console.WriteLine(x);
                    both += x => Console.WriteLine(x + 1);
                    both(1);
                }
            }
            """);

        var type = Assert.Single(module.TypeDescriptors, descriptor => descriptor.Name == "System.Action<int>");
        var invoker = Assert.Single(type.VirtualMethods);
        Assert.Equal("ChipSharp.Delegate.InvokeAll void(ref, i32)", invoker!.Name);
        Assert.Contains("call indirect void", TirPrinter.Print(invoker));
        Assert.Contains(module.Functions, function => function.Name == "ChipSharp.Delegate.Combine");
    }
}
