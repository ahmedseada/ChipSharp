using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The members of <c>Nullable&lt;T&gt;</c> that need more than a field of its struct (see <see cref="Nullables"/>), as
/// TIR functions on the nullable's address, made once per T:
/// <code>
/// function T    ChipSharp.Nullable.Value&lt;T&gt;(ptr %nullable)              the value, or InvalidOperationException
/// function T    ChipSharp.Nullable.ValueOr&lt;T&gt;(ptr %nullable, T %other)   GetValueOrDefault(other)
/// function ref  ChipSharp.Nullable.ToString&lt;T&gt;(ptr %nullable)           the value's text, "" with no value
/// function i32  ChipSharp.Nullable.GetHashCode&lt;T&gt;(ptr %nullable)        the value's hash code, 0 with no value
/// function bool ChipSharp.Nullable.Equals&lt;T&gt;(ptr %nullable, ref %other) with no value, other is null; else the value's Equals
/// </code>
/// </summary>
internal sealed partial class BoxingFunctions
{
    /// <summary>.NET's message for the Value of a nullable with no value.</summary>
    public const string NoValueMessage = "System.InvalidOperationException: Nullable object must have a value.";

    /// <summary><c>Value</c> of a nullable of struct <paramref name="nullable"/>.</summary>
    public TirFunction NullableValue(TirStructType nullable)
    {
        var valueType = nullable.Fields[Nullables.ValueField].Type;
        return Define($"ChipSharp.Nullable.Value<{nullable.Name}>", valueType, [("nullable", TirType.Ptr)], (function, ir) =>
        {
            var address = function.Parameters[0];
            var (some, none) = (function.AddBlock("some"), function.AddBlock("none"));
            ir.Branch(ir.Load(TirType.Bool, ir.FieldAddress(address, nullable, Nullables.HasValueField)), some, none);

            ir.Block = some;
            ir.Return(ir.Load(valueType, ir.FieldAddress(address, nullable, Nullables.ValueField)));

            ir.Block = none;
            Fail(ir, NoValueMessage);
            ir.Return(ir.Zero(valueType));
        });
    }

    /// <summary><c>GetValueOrDefault(other)</c> of a nullable of struct <paramref name="nullable"/>.</summary>
    public TirFunction NullableValueOr(TirStructType nullable)
    {
        var valueType = nullable.Fields[Nullables.ValueField].Type;
        return Define($"ChipSharp.Nullable.ValueOr<{nullable.Name}>", valueType, [("nullable", TirType.Ptr), ("other", valueType)],
            (function, ir) =>
            {
                var address = function.Parameters[0];
                var (some, none) = (function.AddBlock("some"), function.AddBlock("none"));
                ir.Branch(ir.Load(TirType.Bool, ir.FieldAddress(address, nullable, Nullables.HasValueField)), some, none);

                ir.Block = some;
                ir.Return(ir.Load(valueType, ir.FieldAddress(address, nullable, Nullables.ValueField)));

                ir.Block = none;
                ir.Return(function.Parameters[1]);
            });
    }

    /// <summary>ToString of a nullable of <paramref name="boxed"/>: its value's text, "" with no value.</summary>
    public TirFunction NullableToString(BoxedType boxed) => OnValue(boxed, "ToString", TirType.Ref, [],
        (ir, value, _) => ir.Call(ToStringOf(boxed), value), ir => ir.AddressOf(program.Text("")));

    /// <summary>GetHashCode of a nullable of <paramref name="boxed"/>: its value's, 0 with no value.</summary>
    public TirFunction NullableHashCode(BoxedType boxed) => OnValue(boxed, "GetHashCode", TirType.I32, [],
        (ir, value, _) => ir.Call(HashCodeOf(boxed), value), ir => ir.Const(TirType.I32, 0));

    /// <summary>Equals(object) of a nullable of <paramref name="boxed"/>: with no value, whether other is null; else the value's Equals.</summary>
    public TirFunction NullableEquals(BoxedType boxed) => OnValue(boxed, "Equals", TirType.Bool, [("other", TirType.Ref)],
        (ir, value, function) =>
        {
            // .NET's: other == null is false at once, else the value's Equals(other)
            var other = function.Parameters[1];
            var (some, none) = (function.AddBlock("other"), function.AddBlock("other_null"));
            ir.Branch(ir.Compare(TirCompareOp.Eq, other, ir.Zero(TirType.Ref)), none, some);
            ir.Block = none;
            ir.Return(ir.Const(false));
            ir.Block = some;
            var nullable = NullableStruct(boxed);
            return ir.Call(EqualsOf(boxed), ir.FieldAddress(function.Parameters[0], nullable, Nullables.ValueField), other);
        },
        ir => ir.Compare(TirCompareOp.Eq, ir.Function.Parameters[1], ir.Zero(TirType.Ref)));

    /// <summary>
    /// A member of a nullable of <paramref name="boxed"/> that is <paramref name="withValue"/> on the value's address,
    /// and <paramref name="without"/> when there is none.
    /// </summary>
    private TirFunction OnValue(BoxedType boxed, string name, TirType returns, (string Name, TirType Type)[] parameters,
        Func<TirBuilder, TirValue, TirFunction, TirValue> withValue, Func<TirBuilder, TirValue> without)
    {
        var nullable = NullableStruct(boxed);
        return Define($"ChipSharp.Nullable.{name}<{nullable.Name}>", returns, [("nullable", TirType.Ptr), .. parameters],
            (function, ir) =>
            {
                var address = function.Parameters[0];
                var (some, none) = (function.AddBlock("some"), function.AddBlock("none"));
                ir.Branch(ir.Load(TirType.Bool, ir.FieldAddress(address, nullable, Nullables.HasValueField)), some, none);

                ir.Block = some;
                ir.Return(withValue(ir, ir.FieldAddress(address, nullable, Nullables.ValueField), function));

                ir.Block = none;
                ir.Return(without(ir));
            });
    }
}
