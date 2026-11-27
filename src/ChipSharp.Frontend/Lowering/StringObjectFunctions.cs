using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Strings among the other objects (see <see cref="Strings"/> and <see cref="VirtualMethods"/>): the TIR functions that
/// virtual calls of object's methods, object's own ToString and type tests against string call, each added to the
/// module when first needed.
/// <code>
/// function ref ChipSharp.Object.ToString(ref %this)                      object's own: the type's .NET name
/// function bool ChipSharp.Object.EqualsOrString&lt;f&gt;(ref %this, ref %other)  a string's Equals, else f's (GetHashCode
///                                                                       and ToString too)
/// function ref ChipSharp.Object.AsString(ref %object)                    C#'s "as string": the string, or null
/// function ref ChipSharp.Object.CastString(ref %object)                  (string)x: the string (null too), or it stops
/// </code>
/// A string's descriptor is one of the runtime's two (it has no virtual method table: the program's tables are its own),
/// so a call of object's Equals, GetHashCode or ToString on a value that may be a string asks first whether it is one,
/// and then runs a string's (its chars compared, their hash, the string itself), else what the program would run.
/// </summary>
internal sealed class StringObjectFunctions(TirModule module, ProgramFunctions program)
{
    private readonly Dictionary<string, TirFunction> _orString = new(StringComparer.Ordinal);
    private readonly Dictionary<bool, TirFunction> _tests = [];
    private TirFunction? _toString;
    private TirGlobal? _stringName;

    /// <summary>Object's own ToString: the name of the object's type as .NET writes it, e.g. "Program+Node".</summary>
    public TirFunction ObjectToString => _toString ??= DefineToString();

    /// <summary>
    /// Object's <paramref name="name"/> (Equals, GetHashCode or ToString) on a value that may be a string: a string's,
    /// else <paramref name="fallback"/> (what the program runs on its other objects; null for object's own).
    /// </summary>
    public TirFunction OrString(string name, TirFunction? fallback)
    {
        var key = $"{name}<{fallback?.Name}>";
        if (!_orString.TryGetValue(key, out var function))
        {
            function = DefineOrString(name, fallback);
            _orString.Add(key, function);
        }

        return function;
    }

    /// <summary>
    /// Writes an object with Console.Write: nothing for null, else the text <paramref name="toString"/> (a function
    /// <c>ref (ref)</c>, its ToString as the call would run it) gives:
    /// <c>function void ChipSharp.Object.Write&lt;f&gt;(ref %object)</c>.
    /// </summary>
    public TirFunction Write(TirFunction toString)
    {
        var key = $"Write<{toString.Name}>";
        if (!_orString.TryGetValue(key, out var function))
        {
            function = module.AddFunction($"ChipSharp.Object.{key}", TirType.Void, ("object", TirType.Ref));
            var value = function.Parameters[0];
            var ir = new TirBuilder(function.AddBlock("entry"));
            var (none, write) = (function.AddBlock("null"), function.AddBlock("write"));
            ir.Branch(ir.Compare(TirCompareOp.Eq, value, ir.Zero(TirType.Ref)), none, write);

            ir.Block = none;
            ir.Return();

            ir.Block = write;
            ir.Call(program.Runtime("WriteString"), ir.Call(toString, value));
            ir.Return();
            _orString.Add(key, function);
        }

        return function;
    }

    /// <summary><c>isinst string</c> (<paramref name="cast"/> false) or <c>castclass string</c> (true).</summary>
    public TirFunction TypeTest(bool cast)
    {
        if (!_tests.TryGetValue(cast, out var function))
        {
            function = DefineTypeTest(cast);
            _tests.Add(cast, function);
        }

        return function;
    }

    /// <summary>True when the object (not null) is a string: its header is one of the two string descriptors.</summary>
    private void IsString(TirBuilder ir, TirValue reference, TirBlock yes, TirBlock no)
    {
        var wide = ir.Function.AddBlock("wide");
        var type = ir.TypeOf(reference);
        ir.Branch(ir.Compare(TirCompareOp.Eq, type, ir.AddressOf(program.NarrowString)), yes, wide);
        ir.Block = wide;
        ir.Branch(ir.Compare(TirCompareOp.Eq, ir.TypeOf(reference), ir.AddressOf(program.WideString)), yes, no);
    }

    private TirFunction DefineToString()
    {
        var function = module.AddFunction("ChipSharp.Object.ToString", TirType.Ref, ("this", TirType.Ref));
        var self = function.Parameters[0];
        var ir = new TirBuilder(function.AddBlock("entry"));
        var (named, unnamed) = (function.AddBlock("named"), function.AddBlock("unnamed"));

        // the empty array of Array.Empty<T>() has no descriptor: it is an array
        var type = ir.TypeOf(self);
        ir.Branch(ir.Compare(TirCompareOp.Eq, type, ir.Zero(TirType.Ptr)), unnamed, named);

        ir.Block = unnamed;
        ir.Return(ir.AddressOf(program.Text("System.Array")));

        ir.Block = named;
        var name = ir.Load(TirType.Ptr, ir.FieldAddress(ir.TypeOf(self), TirTypeDescriptor.Layout, TirTypeDescriptor.NameField));
        ir.Return(ir.Call(program.RuntimeFunction("StringOfName", TirType.Ref, TirType.Ptr), name));
        return function;
    }

    private TirFunction DefineOrString(string name, TirFunction? fallback)
    {
        var (returns, parameters) = name switch
        {
            "Equals" => (TirType.Bool, new[] { ("this", TirType.Ref), ("other", TirType.Ref) }),
            "GetHashCode" => (TirType.I32, new[] { ("this", TirType.Ref) }),
            _ => (TirType.Ref, new[] { ("this", TirType.Ref) }),
        };
        var function = module.AddFunction($"ChipSharp.Object.{name}OrString<{fallback?.Name ?? "System.Object"}>", returns,
            parameters);
        TirValue[] arguments = [.. function.Parameters];
        var ir = new TirBuilder(function.AddBlock("entry"));
        var (text, other) = (function.AddBlock("string"), function.AddBlock("other"));
        IsString(ir, arguments[0], text, other);

        ir.Block = text;
        switch (name)
        {
            case "Equals":
                var equal = ir.Call(program.RuntimeFunction("StringEqualsObject", TirType.I32, TirType.Ref, TirType.Ref),
                    arguments);
                ir.Return(ir.Compare(TirCompareOp.Ne, equal, ir.Const(TirType.I32, 0)));
                break;

            case "GetHashCode":
                ir.Return(ir.Call(program.RuntimeFunction("StringHash", TirType.I32, TirType.Ref), arguments));
                break;

            default:
                ir.Return(arguments[0]);
                break;
        }

        ir.Block = other;
        var own = fallback ?? name switch
        {
            "Equals" => program.ObjectFunctions.ObjectEquals,
            "GetHashCode" => program.ObjectFunctions.ObjectHashCode,
            _ => ObjectToString,
        };
        ir.Return(ir.Call(own, arguments));
        return function;
    }

    private TirFunction DefineTypeTest(bool cast)
    {
        var function = module.AddFunction(cast ? "ChipSharp.Object.CastString" : "ChipSharp.Object.AsString", TirType.Ref,
            ("object", TirType.Ref));
        var value = function.Parameters[0];
        var ir = new TirBuilder(function.AddBlock("entry"));
        var (some, yes, no) = (function.AddBlock("some"), function.AddBlock("yes"), function.AddBlock("no"));
        ir.Branch(ir.Compare(TirCompareOp.Eq, value, ir.Zero(TirType.Ref)), yes, some);

        ir.Block = some;
        IsString(ir, value, yes, no);

        ir.Block = yes;
        ir.Return(value);

        ir.Block = no;
        if (cast)
        {
            var type = ir.TypeOf(value);
            var known = function.AddBlock("known");
            var empty = function.AddBlock("empty");
            ir.Branch(ir.Compare(TirCompareOp.Eq, type, ir.Zero(TirType.Ptr)), empty, known);

            ir.Block = known;
            var from = ir.Load(TirType.Ptr, ir.FieldAddress(ir.TypeOf(value), TirTypeDescriptor.Layout,
                TirTypeDescriptor.NameField));
            ir.Call(program.ObjectFunctions.InvalidCast, from, ir.AddressOf(StringName));
            ir.Return(ir.Zero(TirType.Ref));

            ir.Block = empty;
            ir.Call(program.ObjectFunctions.InvalidCast, ir.AddressOf(program.ObjectFunctions.EmptyArrayName),
                ir.AddressOf(StringName));
        }

        ir.Return(ir.Zero(TirType.Ref));
        return function;
    }

    /// <summary>"System.String" as UTF-8 ending with a 0 byte, for a failed cast's message.</summary>
    private TirGlobal StringName => _stringName ??=
        module.AddConstant("<System.String>", [.. System.Text.Encoding.UTF8.GetBytes("System.String"), 0]);
}
