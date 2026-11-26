using ChipSharp.Abstractions;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Targets;
using ChipSharp.Tests.Host;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Heap;

/// <summary>
/// The translation's helpers for objects (MethodLowering.Objects.cs) and the mapping of classes (<see cref="ObjectTypes"/>),
/// through what they translate today: <c>new object()</c>, objects in locals, parameters and return values, null, and
/// comparisons by reference, translated straight from their IL (<see cref="Lowered"/>). The program's own classes are
/// the tests in Classes/.
/// </summary>
public sealed class ObjectLoweringTests : IDisposable
{
    /// <summary>Makes 300000 objects (each garbage at once) and compares objects by reference.</summary>
    private const string Objects = """
        using System;

        static class Program
        {
            static object Keep(object o) => o;

            static bool Same(object a, object b) => a == b;

            static void Main()
            {
                object kept = new object();
                object none = null;
                int made = 0;
                for (int i = 0; i < 300000; i++)
                {
                    object o = new object();
                    if (o != null)
                    {
                        made++;
                    }
                }

                Console.WriteLine(made);
                Console.WriteLine(kept != null);
                Console.WriteLine(none == null);
                Console.WriteLine(Same(kept, Keep(kept)));
                Console.WriteLine(Same(kept, new object()));
                Console.WriteLine(Same(none, null));
            }
        }
        """;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void New_object_is_a_newobject_of_system_objects_descriptor_and_an_object_is_a_ref_compared_with_a_null_ref()
    {
        const string Source = """
            static class C
            {
                static object Make() => new object();
                static bool IsSet(object o) => o != null;
                static object Nothing() => null;
                static bool Same(object a, object b) => a == b;
            }
            """;

        Assert.Equal("""
            function ref C.Make()
            IL_0000:
              %0 = newobject System.Object
              return %0

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.Make"));
        Assert.Equal("""
            function bool C.IsSet(ref %o)
            IL_0000:
              %0 = zero ref
              %1 = ugt ref %o, %0
              return %1

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.IsSet"));
        Assert.Equal("""
            function ref C.Nothing()
            IL_0000:
              %0 = zero ref
              return %0

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.Nothing"));
        Assert.Equal("""
            function bool C.Same(ref %a, ref %b)
            IL_0000:
              %0 = eq ref %a, %b
              return %0

            """.ReplaceLineEndings("\n"), Lowered.Function(Source, "C.Same"));

        var module = Lowered.Module(Source, "C.Make");
        Assert.Equal("type System.Object = class System.Object",
            TirPrinter.Print(module).Split('\n').Single(line => line.StartsWith("type ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Null_is_still_a_null_ptr_where_text_is_wanted()
    {
        const string Source = """
            static class C
            {
                static string Pick(bool b) => b ? "x" : null;
            }
            """;

        Assert.Contains("%0 = zero ptr\n  return %0", Lowered.Function(Source, "C.Pick"));
    }

    [Fact]
    public void A_class_is_laid_out_as_its_header_its_base_classes_fields_then_its_own_with_one_descriptor_each()
    {
        var assembly = Lowered.Compile("""
            class Animal { public int Legs; public Animal Friend; private int count; }
            class Dog : Animal { public long Born; private int count; public string Name; }
            struct Spot { public int X; }
            interface IPet { }
            static class C { static void Main() { } }
            """);
        var animal = assembly.FindType("Animal")!;
        var dog = assembly.FindType("Dog")!;

        Assert.True(ObjectTypes.IsClass(dog));
        Assert.False(ObjectTypes.IsClass(assembly.FindType("Spot")!));
        Assert.False(ObjectTypes.IsClass(assembly.FindType("IPet")!));
        Assert.Equal("struct Animal { ptr <type>, i32 Legs, ref Friend, i32 count }", StructText(ObjectTypes.ObjectType(animal)));
        Assert.Equal("struct Dog { ptr <type>, i32 Legs, ref Friend, i32 count, i64 Born, i32 Dog.count, ptr Name }",
            StructText(ObjectTypes.ObjectType(dog)));
        Assert.Same(ObjectTypes.ObjectType(dog), ObjectTypes.ObjectType(dog));
        Assert.Equal((ObjectTypes.ObjectType(animal), 2), ObjectTypes.FieldOf(animal.Fields.Single(f => f.Name == "Friend")));
        Assert.Equal((ObjectTypes.ObjectType(dog), 5), ObjectTypes.FieldOf(dog.Fields.Single(f => f.Name == "count")));
        Assert.True(ObjectTypes.TryMapType(dog.Type, assembly, out var type) && type == TirType.Ref);
        Assert.True(ObjectTypes.TryMapType(IlType.Object, assembly, out type) && type == TirType.Ref);
        Assert.False(ObjectTypes.TryMapType(IlType.Int32, assembly, out _));

        // one descriptor per class and module, its base's first; on 4-byte pointers an Animal takes 16 bytes, a Dog 32
        var module = new TirModule("Classes");
        var functions = new ProgramFunctions(module);
        var dogType = functions.ClassDescriptor(dog);
        Assert.Same(dogType, functions.ClassDescriptor(dog));
        Assert.Equal(["System.Object", "Animal", "Dog"], module.TypeDescriptors.Select(t => t.Name));
        Assert.Equal("Animal", dogType.Base!.Name);
        Assert.Same(functions.ObjectDescriptor, dogType.Base.Base);
        var layout = new TirLayout(4);
        Assert.Equal((16, 32), (layout.SizeOf(dogType.Base.ObjectType!), layout.SizeOf(dogType.ObjectType!)));
        Assert.Equal([8L], layout.ReferenceOffsets(dogType.ObjectType!));
        Assert.Same(functions.ArrayDescriptor("Dog[]", TirType.Ref, dogType), functions.ArrayDescriptor("Dog[]", TirType.Ref));
    }

    [HostFact]
    public async Task New_object_runs_on_the_PC_as_on_dotnet_through_hundreds_of_collections()
    {
        var targets = HeapRun.Targets(Path.Combine(_scratch, "targets"), "16K");
        var bytes = CSharpCompiler.Compile(Objects, "Objects");
        var module = Lowered.Lower(Lowered.Program(IlAssembly.Load(bytes, "Objects.dll")));

        var result = await HeapRun.OnHostAsync(module, Path.Combine(_scratch, "host"), targets.Load("host-16K"));
        var dotnet = await HostRun.RunOnDotnetAsync(bytes, "Objects", Path.Combine(_scratch, "dotnet"));

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(dotnet.Text, result.Raw);
    }

    [SimulatorFact]
    public async Task New_object_runs_on_the_Pico_as_on_dotnet()
    {
        var targets = HeapRun.Targets(Path.Combine(_scratch, "targets"), "4K");
        var bytes = CSharpCompiler.Compile(Objects.Replace("300000", "30000", StringComparison.Ordinal), "Objects");
        var module = Lowered.Lower(Lowered.Program(IlAssembly.Load(bytes, "Objects.dll")));

        var (result, run, _) = await HeapRun.OnPicoAsync(module, Path.Combine(_scratch, "pico"), targets.Load("pico-4K"));

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["30000", "True", "True", "True", "False", "True"], result.Lines);
    }

    private static string StructText(TirStructType type) => $"struct {type.Name} {{ {string.Join(", ", type.Fields)} }}";
}
