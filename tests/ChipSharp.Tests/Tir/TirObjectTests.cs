using ChipSharp.Ir;

namespace ChipSharp.Tests.Tir;

/// <summary>Objects in TIR: the ref type, type descriptors, the instructions for objects, how they print and verify.</summary>
public sealed class TirObjectTests
{
    private readonly TirModule _module = TirSamples.WithMain(new TirModule("Test"));

    [Fact]
    public void The_descriptors_print_after_the_globals_and_the_instructions_name_them()
    {
        TirObjectSamples.Add(_module);

        Assert.Empty(TirVerifier.FindErrors(_module));
        var text = TirPrinter.Print(_module);
        Assert.Contains(Text("""
            struct System.Object { ptr <type> }
            struct Program.Shape { ptr <type>, i32 Sides, ref Next }
            struct Program.Corner { i32 X, i32 Y }
            struct Program.Square { ptr <type>, i32 Sides, ref Next, Program.Corner Corner, [2 x ref] Pair }
            global ref @Program.last
            type System.Object = class System.Object
            type Program.Shape = class Program.Shape : System.Object
            type Program.Square = class Program.Square : Program.Shape virtual [Program.Square.Sides, -] Program.IArea [Program.Square.Sides]
            type Program.IArea = interface
            type int[] = array of i32
            type Program.Shape[] = array of ref Program.Shape
            """), text);
        Assert.Contains(Text("""
            function i32 Program.Use(ref %shape)
            entry:
              %0 = newobject Program.Square
              %1 = fieldaddr Program.Shape %0, Next
              store ref [%1], %shape
              nullcheck %shape
              %2 = fieldaddr Program.Shape %shape, Sides
              %3 = load i32 [%2]
              %4 = newarray int[], %3
              %5 = arraydata i32 %4
              %6 = const i32 0
              %7 = elementaddr i32 %5, %6
              %8 = arraylength %4
              store i32 [%7], %8
              %9 = const i32 2
              %10 = newarray Program.Shape[], %9
              %11 = arraydata ref %10
              %12 = const i32 1
              %13 = elementaddr ref %11, %12
              store ref [%13], %0
              %14 = typeof %0
              %15 = typeaddr Program.Square
              %16 = eq ptr %14, %15
              %17 = zero ref
              %18 = eq ref %0, %17
              %19 = convert bool %16 to i32
              %20 = convert bool %18 to i32
              %21 = add i32 %19, %20
              return %21
            """), text);
    }

    [Fact]
    public void A_ref_is_never_a_number_nor_an_address_to_load_through()
    {
        var shape = _module.AddClass("Program.Shape", TirObjectSamples.ShapeType);
        var (f, b) = Function(TirType.Void, ("o", TirType.Ref), ("p", TirType.Ptr));
        var (o, p) = (f.Parameters[0], f.Parameters[1]);
        b.Convert(o, TirType.I64);
        b.PtrToInt(o, TirType.NativeInt);
        b.Load(TirType.I32, o);
        b.Binary(TirBinaryOp.Add, o, o);
        b.Compare(TirCompareOp.Lt, o, o);
        b.Compare(TirCompareOp.GtUnsigned, o, o);
        b.Compare(TirCompareOp.Eq, o, p);
        b.FieldAddress(o, TirMemorySamples.Point, 0);
        b.FieldAddress(p, shape.ObjectType!, 1);
        b.Return();

        AssertError(
            "Function 'Program.F', block 'entry', '%0 = convert ref %o to i64': convert does not take or produce ref: " +
                "a reference to an object is never a number.",
            "Function 'Program.F', block 'entry', '%1 = ptrtoint ref %o to nint': ptrtoint takes a ptr, not ref.",
            "Function 'Program.F', block 'entry', '%2 = load i32 [%o]': the address %o is ref, an object, not a ptr: " +
                "fieldaddr reaches its fields, arraydata the elements of an array.",
            "Function 'Program.F', block 'entry', '%3 = add ref %o, %o': add works on integers and floats, not on ref.",
            "Function 'Program.F', block 'entry', '%4 = lt ref %o, %o': lt works on integers and floats, not on ref.",
            "Function 'Program.F', block 'entry', '%6 = eq ref %o, %p': the operands have different types: ref and ptr.",
            "Function 'Program.F', block 'entry', '%7 = fieldaddr Program.Point %o, X': %o is an object (ref), and " +
                "Program.Point is the object type of no class of this module.");
    }

    [Fact]
    public void The_instructions_for_objects_take_objects_and_descriptors_of_the_right_kind_from_their_module()
    {
        var shape = _module.AddClass("Program.Shape", TirObjectSamples.ShapeType);
        var ints = _module.AddArrayType("int[]", TirType.I32);
        var other = new TirModule("Other").AddClass("Other.Thing", TirTypeDescriptor.ObjectStruct("Other.Thing"));
        var (f, b) = Function(TirType.Void, ("p", TirType.Ptr), ("n", TirType.I64));
        b.NewObject(ints);
        b.NewArray(shape, b.Const(TirType.I32, 1));
        b.NewArray(ints, f.Parameters[1]);
        b.NullCheck(f.Parameters[0]);
        b.ArrayLength(f.Parameters[0]);
        b.TypeOf(f.Parameters[0]);
        b.ArrayData(b.NewArray(ints, b.Const(TirType.I32, 1)), TirType.I64);
        b.NewObject(other);
        b.Return();

        AssertError(
            "Function 'Program.F', block 'entry', '%0 = newobject int[]': newobject needs a class, and int[] is an array type.",
            "Function 'Program.F', block 'entry', '%2 = newarray Program.Shape, %1': newarray needs an array type, and " +
                "Program.Shape is a class.",
            "Function 'Program.F', block 'entry', '%3 = newarray int[], %n': the length is i64; it must be i32.",
            "Function 'Program.F', block 'entry', 'nullcheck %p': nullcheck takes an object (ref), not ptr.",
            "Function 'Program.F', block 'entry', '%4 = arraylength %p': arraylength takes an object (ref), not ptr.",
            "Function 'Program.F', block 'entry', '%5 = typeof %p': typeof takes an object (ref), not ptr.",
            "Function 'Program.F', block 'entry', '%8 = arraydata i64 %7': no array type of this module has elements of i64.",
            "Function 'Program.F', block 'entry', '%9 = newobject Other.Thing': newobject names type Other.Thing of " +
                "module 'Other'.");
    }

    [Fact]
    public void A_method_table_holds_functions_of_the_module_and_an_interface_map_names_interfaces()
    {
        var shape = _module.AddClass("Program.Shape", TirObjectSamples.ShapeType);
        var ints = _module.AddArrayType("int[]", TirType.I32);
        var foreign = new TirModule("Other").AddFunction("Other.Run", TirType.Void);
        shape.VirtualMethods.Add(foreign);
        shape.Interfaces.Add(new TirInterfaceMethods(ints, []));
        ints.VirtualMethods.Add(null);
        ints.ElementDescriptor = shape;

        AssertError(
            "Module 'Test', type Program.Shape: its method table names 'Other.Run', a function of module 'Other'.",
            "Module 'Test', type Program.Shape: it implements int[], which is not an interface of this module.",
            "Module 'Test', type int[]: only a class has virtual methods and interfaces, and this is an array type.",
            "Module 'Test', type int[]: its element type Program.Shape must be a type of this module, and only an array " +
                "of refs has one.");
    }

    [Fact]
    public void A_class_starts_with_the_header_then_its_bases_fields_and_names_are_unique()
    {
        var shape = _module.AddClass("Program.Shape", TirObjectSamples.ShapeType);

        Assert.Equal("Module 'Test': class Program.Bad's object type Program.Point must start with the header, a ptr " +
            "field named <type>.",
            Assert.Throws<TirException>(() => _module.AddClass("Program.Bad", TirMemorySamples.Point)).Message);
        Assert.Equal("Module 'Test': class Program.Odd's object type Program.Odd must start with the fields of its base " +
            "Program.Shape's, Program.Shape, in their order.",
            Assert.Throws<TirException>(() => _module.AddClass("Program.Odd",
                TirTypeDescriptor.ObjectStruct("Program.Odd", ("Next", TirType.Ref), ("Sides", TirType.I32)), shape)).Message);
        Assert.Equal("Module 'Test': the base of class Program.Sub must be a class of this module, not interface " +
            "Program.I.",
            Assert.Throws<TirException>(() => _module.AddClass("Program.Sub", TirObjectSamples.SquareType,
                _module.AddInterface("Program.I"))).Message);
        Assert.Equal("Module 'Test' already has a type descriptor named Program.Shape.",
            Assert.Throws<TirException>(() => _module.AddArrayType("Program.Shape", TirType.I8)).Message);
        Assert.Equal("Module 'Test': array type void[] cannot have elements of type void.",
            Assert.Throws<TirException>(() => _module.AddArrayType("void[]", TirType.Void)).Message);
        Assert.Equal("A constant cannot have type ref; null is 'zero ref', and only newobject and newarray make objects.",
            Assert.Throws<TirException>(() => new TirConst(TirType.Ref, 0)).Message);
        Assert.Equal("Program.Square", _module.AddClass("Program.Square", TirObjectSamples.SquareType, shape).Name);
    }

    [Theory]
    [InlineData(4, 8, new long[] { 8, 20, 24 })]
    [InlineData(8, 16, new long[] { 16, 32, 40 })]
    public void A_ref_is_pointer_sized_and_the_layout_knows_where_the_refs_and_the_elements_are(int pointerSize,
        int arrayData, long[] references)
    {
        var layout = new TirLayout(pointerSize);

        Assert.Equal((pointerSize, pointerSize), (layout.SizeOf(TirType.Ref), layout.AlignmentOf(TirType.Ref)));
        Assert.Equal((pointerSize, arrayData), (layout.ArrayLengthOffset, layout.ArrayDataOffset));
        Assert.Equal(references, layout.ReferenceOffsets(TirObjectSamples.SquareType));
        Assert.Empty(layout.ReferenceOffsets(TirMemorySamples.Point));
        Assert.Equal([0L, pointerSize, 2L * pointerSize], layout.ReferenceOffsets(new TirArrayType(TirType.Ref, 3)));
    }

    private (TirFunction Function, TirBuilder Builder) Function(TirType returnType,
        params (string Name, TirType Type)[] parameters)
    {
        var function = _module.AddFunction("Program.F", returnType, parameters);
        return (function, new TirBuilder(function.AddBlock("entry")));
    }

    private void AssertError(params string[] expected) => Assert.Equal(expected, TirVerifier.FindErrors(_module));

    private static string Text(string text) => text.ReplaceLineEndings("\n");
}
