using ChipSharp.Ir;

namespace ChipSharp.Tests.Tir;

/// <summary>
/// Objects in TIR, for the printer, verifier and code generator tests: classes with a base, virtual methods and an
/// interface, array types, and a function that uses every instruction for objects.
/// </summary>
internal static class TirObjectSamples
{
    public static readonly TirStructType Point = new("Program.Corner", ("X", TirType.I32), ("Y", TirType.I32));

    /// <summary>The object type of Program.Shape: { ptr &lt;type&gt;, i32 Sides, ref Next }.</summary>
    public static readonly TirStructType ShapeType =
        TirTypeDescriptor.ObjectStruct("Program.Shape", ("Sides", TirType.I32), ("Next", TirType.Ref));

    /// <summary>The object type of Program.Square, a Shape with a corner and two more references.</summary>
    public static readonly TirStructType SquareType = TirTypeDescriptor.ObjectStruct("Program.Square",
        ("Sides", TirType.I32), ("Next", TirType.Ref), ("Corner", Point), ("Pair", new TirArrayType(TirType.Ref, 2)));

    /// <summary>
    /// Adds the descriptors (System.Object, Program.Shape, Program.Square with a virtual method and Program.IArea,
    /// Program.IArea, int[], Program.Shape[]) and <c>i32 Program.Use(ref %shape)</c>, which makes a Square and two
    /// arrays and reads them back. Returns the module.
    /// </summary>
    public static TirModule Add(TirModule module)
    {
        var root = module.AddClass("System.Object", TirTypeDescriptor.ObjectStruct("System.Object"));
        var shape = module.AddClass("Program.Shape", ShapeType, root);
        var square = module.AddClass("Program.Square", SquareType, shape);
        var area = module.AddInterface("Program.IArea");
        var ints = module.AddArrayType("int[]", TirType.I32);
        var shapes = module.AddArrayType("Program.Shape[]", TirType.Ref);
        shapes.ElementDescriptor = shape;

        var sides = module.AddFunction("Program.Square.Sides", TirType.I32, ("this", TirType.Ref));
        var body = new TirBuilder(sides.AddBlock("entry"));
        body.Return(body.Load(TirType.I32, body.FieldAddress(sides.Parameters[0], ShapeType, 1)));
        square.VirtualMethods.Add(sides);
        square.VirtualMethods.Add(null);
        square.Interfaces.Add(new TirInterfaceMethods(area, [sides]));
        module.AddGlobal("Program.last", TirType.Ref);

        var use = module.AddFunction("Program.Use", TirType.I32, ("shape", TirType.Ref));
        var ir = new TirBuilder(use.AddBlock("entry"));
        var made = ir.NewObject(square);
        ir.Store(ir.FieldAddress(made, ShapeType, 2), use.Parameters[0]);
        ir.NullCheck(use.Parameters[0]);
        var count = ir.Load(TirType.I32, ir.FieldAddress(use.Parameters[0], ShapeType, 1));
        var numbers = ir.NewArray(ints, count);
        var first = ir.ElementAddress(ir.ArrayData(numbers, TirType.I32), TirType.I32, ir.Const(TirType.I32, 0));
        ir.Store(first, ir.ArrayLength(numbers));
        var list = ir.NewArray(shapes, ir.Const(TirType.I32, 2));
        ir.Store(ir.ElementAddress(ir.ArrayData(list, TirType.Ref), TirType.Ref, ir.Const(TirType.I32, 1)), made);
        var exact = ir.Compare(TirCompareOp.Eq, ir.TypeOf(made), ir.AddressOf(square));
        var isNull = ir.Compare(TirCompareOp.Eq, made, ir.Zero(TirType.Ref));
        ir.Return(ir.Binary(TirBinaryOp.Add, ir.Convert(exact, TirType.I32), ir.Convert(isNull, TirType.I32)));
        return module;
    }
}
