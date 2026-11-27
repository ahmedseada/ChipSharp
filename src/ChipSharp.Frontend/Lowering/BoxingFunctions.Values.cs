using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The methods of values, on their address (see <see cref="BoxingFunctions"/>): what a box runs, and a constrained call
/// on the value, as .NET gives them.
/// <list type="bullet">
/// <item>ToString: a struct's own; else a number's text as Console.Write writes it (the runtime's StringOf functions), a
/// bool's "True" or "False", an enum's name (Lowering/EnumNameFunctions.cs), a struct's type name (.NET's, a literal).</item>
/// <item>Equals(object): a struct's own; else true only for a box of exactly the same type with an equal value: numbers
/// as their Equals compares them (a float NaN equals NaN, -0.0 equals 0.0), a struct as ValueType's Equals does, field
/// by field (a field of an object with that object's Equals through its slot, null equal only to null; a struct with its
/// own Equals, given the other value boxed, as .NET boxes it; a nullable with no value equal only to one with none).</item>
/// <item>GetHashCode: a struct's own; else a number's as .NET's gives it (an int its value, a long its halves XORed, a
/// float or double its bits, with every NaN and both zeros alike), an enum its number's, a struct the fields' hash codes
/// combined (h * 31 + the field's, from 0; an object's through its slot, 0 for null): the same for equal values, and in
/// every run, where .NET's ValueType.GetHashCode mixes in a number that changes from one process to the next.</item>
/// <item>IComparable's CompareTo(object) of a number or an enum: 1 for null, the number's CompareTo for a box of the same
/// type (a difference for the types narrower than int, as .NET's), and .NET's ArgumentException for any other.</item>
/// </list>
/// </summary>
internal sealed partial class BoxingFunctions
{
    /// <summary>ToString of a value of <paramref name="boxed"/>: <c>ref (ptr %value)</c>.</summary>
    public TirFunction ToStringOf(BoxedType boxed) => Define($"ChipSharp.Value.ToString<{boxed.Name}>", TirType.Ref,
        [("value", TirType.Ptr)], (function, ir) => ir.Return(Text(ir, boxed, function.Parameters[0])));

    /// <summary>Equals(object) of a value of <paramref name="boxed"/>: <c>bool (ptr %value, ref %other)</c>.</summary>
    public TirFunction EqualsOf(BoxedType boxed) => Define($"ChipSharp.Value.Equals<{boxed.Name}>", TirType.Bool,
        [("value", TirType.Ptr), ("other", TirType.Ref)], (function, ir) =>
        {
            var (value, other) = (function.Parameters[0], function.Parameters[1]);
            if (boxed.Kind == BoxedKind.Struct && Boxing.Override(boxed.Definition!, "Equals") is { } own)
            {
                ir.Return(ir.Call(program.Function(DispatchTarget.Of(own)), value, other));
                return;
            }

            var (some, same, no) = (function.AddBlock("some"), function.AddBlock("same"), function.AddBlock("no"));
            ir.Branch(ir.Compare(TirCompareOp.Eq, other, ir.Zero(TirType.Ref)), no, some);

            ir.Block = some;
            ir.Branch(ir.Compare(TirCompareOp.Eq, ir.TypeOf(other), ir.AddressOf(Descriptor(boxed))), same, no);

            ir.Block = same;
            ir.Return(ValuesEqual(ir, boxed.Type, boxed.Definition?.Assembly, value, ValueAddress(ir, other, boxed)));

            ir.Block = no;
            ir.Return(ir.Const(false));
        });

    /// <summary>GetHashCode of a value of <paramref name="boxed"/>: <c>i32 (ptr %value)</c>.</summary>
    public TirFunction HashCodeOf(BoxedType boxed) => Define($"ChipSharp.Value.GetHashCode<{boxed.Name}>", TirType.I32,
        [("value", TirType.Ptr)], (function, ir) =>
        {
            var value = function.Parameters[0];
            ir.Return(boxed.Kind == BoxedKind.Struct && Boxing.Override(boxed.Definition!, "GetHashCode") is { } own
                ? ir.Call(program.Function(DispatchTarget.Of(own)), value)
                : ValueHash(ir, boxed.Type, boxed.Definition?.Assembly, value));
        });

    /// <summary>IComparable's CompareTo(object) of a number or an enum: <c>i32 (ptr %value, ref %other)</c>.</summary>
    public TirFunction CompareToOf(BoxedType boxed) => Define($"ChipSharp.Value.CompareTo<{boxed.Name}>", TirType.I32,
        [("value", TirType.Ptr), ("other", TirType.Ref)], (function, ir) =>
        {
            var (value, other) = (function.Parameters[0], function.Parameters[1]);
            var (some, same, wrong, one) = (function.AddBlock("some"), function.AddBlock("same"), function.AddBlock("wrong"),
                function.AddBlock("one"));
            ir.Branch(ir.Compare(TirCompareOp.Eq, other, ir.Zero(TirType.Ref)), one, some);

            ir.Block = one;
            ir.Return(ir.Const(TirType.I32, 1));

            ir.Block = some;
            ir.Branch(ir.Compare(TirCompareOp.Eq, ir.TypeOf(other), ir.AddressOf(Descriptor(boxed))), same, wrong);

            ir.Block = same;
            var kind = boxed.Primitive!.Kind;
            ir.Return(Compare(ir, kind, ir.Load(boxed.ValueType, value), ir.Load(boxed.ValueType, ValueAddress(ir, other, boxed))));

            ir.Block = wrong;
            if (boxed.Kind == BoxedKind.Enum)
            {
                var mismatch = module.Functions.FirstOrDefault(f => f.Kind == TirFunctionKind.External
                        && f.Name == "ChipSharp.Runtime.EnumCompareType")
                    ?? module.AddExternalFunction("ChipSharp.Runtime.EnumCompareType", TirType.Void, ("passed", TirType.Ptr),
                        ("enum", TirType.Ptr));
                var (named, empty) = (function.AddBlock("named"), function.AddBlock("empty"));
                ir.Branch(ir.Compare(TirCompareOp.Eq, ir.TypeOf(other), ir.Zero(TirType.Ptr)), empty, named);

                ir.Block = named;
                ir.Call(mismatch, ir.Load(TirType.Ptr, ir.FieldAddress(ir.TypeOf(other), TirTypeDescriptor.Layout,
                    TirTypeDescriptor.NameField)), ir.AddressOf(Text($"<name {boxed.RuntimeName}>", boxed.RuntimeName)));
                ir.Return(ir.Const(TirType.I32, 0));

                ir.Block = empty;
                ir.Call(mismatch, ir.AddressOf(program.ObjectFunctions.EmptyArrayName),
                    ir.AddressOf(Text($"<name {boxed.RuntimeName}>", boxed.RuntimeName)));
                ir.Return(ir.Const(TirType.I32, 0));
                return;
            }

            Fail(ir, $"System.ArgumentException: Object must be of type {boxed.RuntimeName["System.".Length..]}.");
            ir.Return(ir.Const(TirType.I32, 0));
        });

    /// <summary>The text of the value at <paramref name="address"/>, of <paramref name="boxed"/> (see the summary).</summary>
    private TirValue Text(TirBuilder ir, BoxedType boxed, TirValue address)
    {
        if (boxed.Kind == BoxedKind.Struct)
        {
            return Boxing.Override(boxed.Definition!, "ToString") is { } own
                ? ir.Call(program.Function(DispatchTarget.Of(own)), address)
                : ir.AddressOf(program.Text(boxed.RuntimeName));
        }

        var kind = boxed.Primitive!.Kind;
        var value = ir.Load(boxed.ValueType, address);
        if (boxed.Kind == BoxedKind.Enum)
        {
            return ir.Call(program.EnumNames.Name(boxed.Definition!), Widen(ir, value, kind));
        }

        TirFunction StringOf(string what, params TirType[] parameters) =>
            program.RuntimeFunction($"StringOf{what}", TirType.Ref, parameters);

        return kind switch
        {
            IlPrimitiveKind.Bool => ir.Call(StringOf("Bool", TirType.I32), ir.Convert(value, TirType.I32)),
            IlPrimitiveKind.Char => ir.Call(StringOf("Char", TirType.I32), ir.Convert(value, TirType.I32, unsigned: true)),
            IlPrimitiveKind.Float32 => ir.Call(StringOf("Float32", TirType.F32), value),
            IlPrimitiveKind.Float64 => ir.Call(StringOf("Float64", TirType.F64), value),
            _ when IsSigned(kind) => ir.Call(StringOf("Int64", TirType.I64, TirType.I32), Widen(ir, value, kind),
                ir.Const(TirType.I32, 0)),
            _ => ir.Call(StringOf("UInt64", TirType.I64, TirType.I32), Widen(ir, value, kind), ir.Const(TirType.I32, 0)),
        };
    }

    /// <summary>
    /// Whether the values of <paramref name="type"/> at <paramref name="a"/> and <paramref name="b"/> are equal, as
    /// ValueType's Equals compares a field of the type (see the summary).
    /// </summary>
    private TirValue ValuesEqual(TirBuilder ir, IlType type, IlAssembly? assembly, TirValue a, TirValue b)
    {
        if (Nullables.ValueOf(type) is not null)
        {
            return ir.Call(NullablesEqual(type, assembly!), a, b);
        }

        if (BoxOf(type, assembly) is { } boxed)
        {
            return boxed.Kind == BoxedKind.Struct
                ? ir.Call(StructsEqual(boxed), a, b)
                : Same(ir, boxed.Primitive!.Kind, ir.Load(boxed.ValueType, a), ir.Load(boxed.ValueType, b));
        }

        return ir.Call(ReferencesEqual(type), ir.Load(TirType.Ref, a), ir.Load(TirType.Ref, b));
    }

    /// <summary>ValueType's Equals of two values of a struct, field by field: <c>bool (ptr %a, ptr %b)</c>.</summary>
    private TirFunction StructsEqual(BoxedType boxed) => Define($"ChipSharp.Value.FieldsEqual<{boxed.Name}>", TirType.Bool,
        [("a", TirType.Ptr), ("b", TirType.Ptr)], (function, ir) =>
        {
            var (a, b) = (function.Parameters[0], function.Parameters[1]);
            var definition = boxed.Definition!;
            if (Boxing.Override(definition, "Equals") is { } own)
            {
                // a struct with an Equals of its own takes the other value as an object: a box of it, as .NET makes
                ir.Return(ir.Call(program.Function(DispatchTarget.Of(own)), a, Box(ir, boxed, ir.Load(boxed.ValueType, b))));
                return;
            }

            var structType = (TirStructType)boxed.ValueType;
            var no = function.AddBlock("no");
            var fields = ValueTypes.InstanceFields(definition).ToList();
            for (var i = 0; i < fields.Count; i++)
            {
                var equal = ValuesEqual(ir, Normalized(fields[i].Type), definition.Assembly,
                    ir.FieldAddress(a, structType, i), ir.FieldAddress(b, structType, i));
                var next = function.AddBlock($"field_{i + 1}");
                ir.Branch(equal, next, no);
                ir.Block = next;
            }

            ir.Return(ir.Const(true));

            ir.Block = no;
            ir.Return(ir.Const(false));
        });

    /// <summary>Two nullables of <paramref name="type"/> (<c>T?</c>): both without a value, or both with equal values.</summary>
    private TirFunction NullablesEqual(IlType type, IlAssembly assembly)
    {
        var nullable = NullableStructOf(type, assembly);
        return Define($"ChipSharp.Value.NullablesEqual<{nullable.Name}>", TirType.Bool, [("a", TirType.Ptr), ("b", TirType.Ptr)],
            (function, ir) =>
            {
                var (a, b) = (function.Parameters[0], function.Parameters[1]);
                var (alike, values, no, yes) = (function.AddBlock("alike"), function.AddBlock("values"), function.AddBlock("no"),
                    function.AddBlock("yes"));
                var hasA = ir.Load(TirType.Bool, ir.FieldAddress(a, nullable, Nullables.HasValueField));
                var hasB = ir.Load(TirType.Bool, ir.FieldAddress(b, nullable, Nullables.HasValueField));
                ir.Branch(ir.Compare(TirCompareOp.Eq, ir.Convert(hasA, TirType.I32), ir.Convert(hasB, TirType.I32)), alike, no);

                ir.Block = alike;
                ir.Branch(ir.Load(TirType.Bool, ir.FieldAddress(a, nullable, Nullables.HasValueField)), values, yes);

                ir.Block = values;
                ir.Return(ValuesEqual(ir, Nullables.ValueOf(type)!, assembly, ir.FieldAddress(a, nullable, Nullables.ValueField),
                    ir.FieldAddress(b, nullable, Nullables.ValueField)));

                ir.Block = yes;
                ir.Return(ir.Const(true));

                ir.Block = no;
                ir.Return(ir.Const(false));
            });
    }

    /// <summary>Two objects in fields of <paramref name="type"/>: null equals only null, else the first's Equals of the second.</summary>
    private TirFunction ReferencesEqual(IlType type) => Define($"ChipSharp.Value.ObjectsEqual<{CSharpName(type)}>", TirType.Bool,
        [("a", TirType.Ref), ("b", TirType.Ref)], (function, ir) =>
        {
            var (a, b) = (function.Parameters[0], function.Parameters[1]);
            var (none, some) = (function.AddBlock("none"), function.AddBlock("some"));
            ir.Branch(ir.Compare(TirCompareOp.Eq, a, ir.Zero(TirType.Ref)), none, some);

            ir.Block = none;
            ir.Return(ir.Compare(TirCompareOp.Eq, b, ir.Zero(TirType.Ref)));

            ir.Block = some;
            ir.Return(ObjectCall(ir, "Equals", type, [a, b]));
        });

    /// <summary>The hash code of the value of <paramref name="type"/> at <paramref name="address"/>, as a field of a struct's counts it.</summary>
    private TirValue ValueHash(TirBuilder ir, IlType type, IlAssembly? assembly, TirValue address)
    {
        if (Nullables.ValueOf(type) is { } value)
        {
            return ir.Call(NullableHashOf(type, assembly!), address);
        }

        if (BoxOf(type, assembly) is { } boxed)
        {
            return boxed.Kind == BoxedKind.Struct
                ? Override(boxed, "GetHashCode") is { } own ? ir.Call(own, address) : ir.Call(StructHash(boxed), address)
                : NumberHash(ir, boxed.Primitive!.Kind, address);
        }

        return ir.Call(ReferenceHash(type), ir.Load(TirType.Ref, address));
    }

    /// <summary>The fields' hash codes of a struct without one of its own, combined: <c>i32 (ptr %value)</c>.</summary>
    private TirFunction StructHash(BoxedType boxed) => Define($"ChipSharp.Value.FieldsHash<{boxed.Name}>", TirType.I32,
        [("value", TirType.Ptr)], (function, ir) =>
        {
            var structType = (TirStructType)boxed.ValueType;
            var definition = boxed.Definition!;
            TirValue hash = ir.Const(TirType.I32, 0);
            var i = 0;
            foreach (var field in ValueTypes.InstanceFields(definition))
            {
                var own = ValueHash(ir, Normalized(field.Type), definition.Assembly,
                    ir.FieldAddress(function.Parameters[0], structType, i++));
                hash = ir.Binary(TirBinaryOp.Add, ir.Binary(TirBinaryOp.Mul, hash, ir.Const(TirType.I32, 31)), own);
            }

            ir.Return(hash);
        });

    /// <summary>The hash code of a nullable of <paramref name="type"/>: its value's, 0 with no value.</summary>
    private TirFunction NullableHashOf(IlType type, IlAssembly assembly)
    {
        var nullable = NullableStructOf(type, assembly);
        return Define($"ChipSharp.Value.NullableHash<{nullable.Name}>", TirType.I32, [("value", TirType.Ptr)], (function, ir) =>
        {
            var address = function.Parameters[0];
            var (some, none) = (function.AddBlock("some"), function.AddBlock("none"));
            ir.Branch(ir.Load(TirType.Bool, ir.FieldAddress(address, nullable, Nullables.HasValueField)), some, none);

            ir.Block = some;
            ir.Return(ValueHash(ir, Nullables.ValueOf(type)!, assembly, ir.FieldAddress(address, nullable, Nullables.ValueField)));

            ir.Block = none;
            ir.Return(ir.Const(TirType.I32, 0));
        });
    }

    /// <summary>The hash code of an object in a field of <paramref name="type"/>: its GetHashCode through its slot, 0 for null.</summary>
    private TirFunction ReferenceHash(IlType type) => Define($"ChipSharp.Value.ObjectHash<{CSharpName(type)}>", TirType.I32,
        [("value", TirType.Ref)], (function, ir) =>
        {
            var value = function.Parameters[0];
            var (none, some) = (function.AddBlock("none"), function.AddBlock("some"));
            ir.Branch(ir.Compare(TirCompareOp.Eq, value, ir.Zero(TirType.Ref)), none, some);

            ir.Block = none;
            ir.Return(ir.Const(TirType.I32, 0));

            ir.Block = some;
            ir.Return(ObjectCall(ir, "GetHashCode", type, [value]));
        });

    /// <summary>The struct's own <paramref name="name"/> (ToString, Equals or GetHashCode) as a function on its address; null without one.</summary>
    private TirFunction? Override(BoxedType boxed, string name) =>
        boxed.Kind == BoxedKind.Struct && Boxing.Override(boxed.Definition!, name) is { } own
            ? program.Function(DispatchTarget.Of(own))
            : null;

    /// <summary>
    /// A number's hash code as .NET's GetHashCode gives it, from the number at <paramref name="address"/>: a float's and a
    /// double's from their bits, NaNs and both zeros alike; nint and nuint as the 64-bit .NET has it for 64 bits, and the
    /// 32-bit for 32 (the value zero-extended, then its halves XORed).
    /// </summary>
    private static TirValue NumberHash(TirBuilder ir, IlPrimitiveKind kind, TirValue address)
    {
        switch (kind)
        {
            case IlPrimitiveKind.Bool:
                return ir.Convert(ir.Load(TirType.Bool, address), TirType.I32);

            case IlPrimitiveKind.Char:
                var c = ir.Convert(ir.Load(TirType.I16, address), TirType.I32, unsigned: true);
                return ir.Binary(TirBinaryOp.Or, c, ir.Binary(TirBinaryOp.Shl, c, ir.Const(TirType.I32, 16)));

            case IlPrimitiveKind.Int8 or IlPrimitiveKind.UInt8:
                return ir.Convert(ir.Load(TirType.I8, address), TirType.I32, unsigned: kind == IlPrimitiveKind.UInt8);

            case IlPrimitiveKind.Int16 or IlPrimitiveKind.UInt16:
                return ir.Convert(ir.Load(TirType.I16, address), TirType.I32, unsigned: kind == IlPrimitiveKind.UInt16);

            case IlPrimitiveKind.Int32 or IlPrimitiveKind.UInt32:
                return ir.Load(TirType.I32, address);

            case IlPrimitiveKind.Float32:
                // if (((bits - 1) & 0x7FFFFFFF) >= 0x7F800000) bits &= 0x7F800000: NaN, 0.0 and -0.0
                var bits = ir.Load(TirType.I32, address);
                var special = ir.Compare(TirCompareOp.GeUnsigned, ir.Binary(TirBinaryOp.And,
                    ir.Binary(TirBinaryOp.Sub, bits, ir.Const(TirType.I32, 1)), ir.Const(TirType.I32, 0x7FFFFFFF)),
                    ir.Const(TirType.I32, 0x7F800000));
                return ir.Binary(TirBinaryOp.And, bits, Mask(ir, special, TirType.I32, 0x7F800000));

            case IlPrimitiveKind.Float64:
                var wide = ir.Load(TirType.I64, address);
                var nan = ir.Compare(TirCompareOp.GeUnsigned, ir.Binary(TirBinaryOp.And,
                    ir.Binary(TirBinaryOp.Sub, wide, ir.Const(TirType.I64, 1)), ir.Const(TirType.I64, 0x7FFF_FFFF_FFFF_FFFF)),
                    ir.Const(TirType.I64, 0x7FF0_0000_0000_0000));
                return Halves(ir, ir.Binary(TirBinaryOp.And, wide, Mask(ir, nan, TirType.I64, 0x7FF0_0000_0000_0000)));

            case IlPrimitiveKind.NativeInt or IlPrimitiveKind.NativeUInt:
                return Halves(ir, ir.Convert(ir.Load(TirType.NativeInt, address), TirType.I64, unsigned: true));

            default:
                return Halves(ir, ir.Load(TirType.I64, address));
        }
    }

    /// <summary><paramref name="keep"/> when <paramref name="condition"/>, else all ones: a mask with no branch.</summary>
    private static TirValue Mask(TirBuilder ir, TirValue condition, TirType type, long keep)
    {
        var minusOne = ir.Binary(TirBinaryOp.Sub, ir.Const(type, 0), ir.Convert(condition, type));
        return ir.Binary(TirBinaryOp.Or, ir.Binary(TirBinaryOp.And, minusOne, ir.Const(type, keep)),
            ir.Binary(TirBinaryOp.Xor, minusOne, ir.Const(type, -1)));
    }

    /// <summary>(int)bits ^ (int)(bits >> 32): .NET's hash of a long.</summary>
    private static TirValue Halves(TirBuilder ir, TirValue bits) => ir.Binary(TirBinaryOp.Xor, ir.Convert(bits, TirType.I32),
        ir.Convert(ir.Binary(TirBinaryOp.ShrUnsigned, bits, ir.Const(TirType.I64, 32)), TirType.I32));

    /// <summary>Two numbers equal as their Equals says: a float NaN equals NaN.</summary>
    private static TirValue Same(TirBuilder ir, IlPrimitiveKind kind, TirValue a, TirValue b)
    {
        if (kind == IlPrimitiveKind.Bool)
        {
            return ir.Compare(TirCompareOp.Eq, ir.Convert(a, TirType.I32), ir.Convert(b, TirType.I32));
        }

        var equal = ir.Compare(TirCompareOp.Eq, a, b);
        if (kind is not (IlPrimitiveKind.Float32 or IlPrimitiveKind.Float64))
        {
            return equal;
        }

        var bothNaN = ir.Binary(TirBinaryOp.And, ir.Compare(TirCompareOp.Ne, a, a), ir.Compare(TirCompareOp.Ne, b, b));
        return ir.Binary(TirBinaryOp.Or, equal, bothNaN);
    }

    /// <summary>
    /// A number's CompareTo of another of its type, as .NET's: a difference for bool, char and the types narrower than
    /// int, -1, 0 or 1 for the others, NaN below every other float and equal to NaN.
    /// </summary>
    private static TirValue Compare(TirBuilder ir, IlPrimitiveKind kind, TirValue a, TirValue b)
    {
        switch (kind)
        {
            case IlPrimitiveKind.Bool or IlPrimitiveKind.Char or IlPrimitiveKind.Int8 or IlPrimitiveKind.UInt8
                or IlPrimitiveKind.Int16 or IlPrimitiveKind.UInt16:
                var unsigned = kind is not (IlPrimitiveKind.Int8 or IlPrimitiveKind.Int16);
                return ir.Binary(TirBinaryOp.Sub, ir.Convert(a, TirType.I32, unsigned), ir.Convert(b, TirType.I32, unsigned));

            case IlPrimitiveKind.Float32 or IlPrimitiveKind.Float64:
                // gt - lt, and when either is NaN (neither less nor greater): isNaN(b) - isNaN(a)
                var ordered = ir.Binary(TirBinaryOp.Sub, Flag(ir, ir.Compare(TirCompareOp.Gt, a, b)),
                    Flag(ir, ir.Compare(TirCompareOp.Lt, a, b)));
                var nans = ir.Binary(TirBinaryOp.Sub, Flag(ir, ir.Compare(TirCompareOp.Ne, b, b)),
                    Flag(ir, ir.Compare(TirCompareOp.Ne, a, a)));
                return ir.Binary(TirBinaryOp.Add, ordered, nans);

            default:
                var signed = IsSigned(kind);
                return ir.Binary(TirBinaryOp.Sub,
                    Flag(ir, ir.Compare(signed ? TirCompareOp.Gt : TirCompareOp.GtUnsigned, a, b)),
                    Flag(ir, ir.Compare(signed ? TirCompareOp.Lt : TirCompareOp.LtUnsigned, a, b)));
        }
    }

    private static TirValue Flag(TirBuilder ir, TirValue condition) => ir.Convert(condition, TirType.I32);

    /// <summary>A whole number (an enum's too) as an i64: sign-extended for a signed type, else zero-extended.</summary>
    private static TirValue Widen(TirBuilder ir, TirValue value, IlPrimitiveKind kind) =>
        value.Type == TirType.I64 ? value : ir.Convert(value, TirType.I64, unsigned: !IsSigned(kind));

    private static bool IsSigned(IlPrimitiveKind kind) => kind is IlPrimitiveKind.Int8 or IlPrimitiveKind.Int16
        or IlPrimitiveKind.Int32 or IlPrimitiveKind.Int64 or IlPrimitiveKind.NativeInt;

    /// <summary>A field's type as boxes compare it: no modifiers, a library primitive as the primitive.</summary>
    private static IlType Normalized(IlType type) => StackArrays.Primitive(ValueTypes.WithoutModifiers(type));

    /// <summary>The box of <paramref name="type"/>: of a type of <paramref name="assembly"/>, or of a primitive (with no assembly).</summary>
    private static BoxedType? BoxOf(IlType type, IlAssembly? assembly) =>
        assembly is not null ? Boxing.Of(type, assembly) : PrimitiveBox(type);

    /// <summary>The box of a primitive type, which needs no assembly to know; null for any other type.</summary>
    private static BoxedType? PrimitiveBox(IlType type) =>
        type is IlPrimitiveType { Kind: >= IlPrimitiveKind.Bool and <= IlPrimitiveKind.NativeUInt } primitive
        && SupportedSubset.TryMapType(primitive, out var tir)
            ? new BoxedType(primitive, BoxedKind.Number, tir, primitive, null)
            : null;

    /// <summary>The TIR struct of the nullable <paramref name="type"/> (<c>T?</c>).</summary>
    private static TirStructType NullableStructOf(IlType type, IlAssembly assembly) =>
        Nullables.TryMapType(type, assembly, out var mapped) ? (TirStructType)mapped
            : throw new InvalidOperationException($"{type} is not a nullable ChipSharp compiles.");

    private static string CSharpName(IlType type) => Analysis.CSharpNames.Type(type);
}
