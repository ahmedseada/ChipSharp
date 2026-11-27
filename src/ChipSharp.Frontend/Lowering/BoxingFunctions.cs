using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Boxes (see <see cref="Boxing"/>): their descriptors and the TIR functions that make, test, unbox and run them, each
/// added to the module when first needed.
/// <list type="bullet">
/// <item>A box of T is an object of the class <c>T (boxed)</c>: its header, then its one field, <c>value</c>, of T's TIR
/// type, so the value is at the same place in a box of every type (on the Pico a boxed int takes 8 bytes of the heap,
/// a boxed long 16). The descriptor's base is System.Object's, its name .NET's (<c>System.Int32</c>, <c>Program+Point</c>),
/// and each box the program makes has a virtual method table and an interface map (ProgramFunctions.Boxing.cs), whose
/// methods take the box (<see cref="OnBox"/>) and call what the value's type runs on the value in it.</item>
/// <item>The methods of values themselves take the value's address, as a struct's methods take their <c>this</c>, so a
/// constrained call (<c>point.ToString()</c>, generic code) runs them with no box:
/// <code>
/// function ref  ChipSharp.Value.ToString&lt;T&gt;(ptr %value)              its own, or a number's text, an enum's name, a struct's type name
/// function bool ChipSharp.Value.Equals&lt;T&gt;(ptr %value, ref %other)    its own, or a box of exactly T with an equal value
/// function i32  ChipSharp.Value.GetHashCode&lt;T&gt;(ptr %value)           its own, or a number's hash, the fields' combined
/// function i32  ChipSharp.Value.CompareTo&lt;T&gt;(ptr %value, ref %other)  IComparable's of a number or an enum
/// </code></item>
/// <item>Unboxing and type tests:
/// <code>
/// function ptr  ChipSharp.Box.Unbox&lt;T&gt;(ref %object)          the value's address in the box, or it stops (null, another type)
/// function T?   ChipSharp.Box.UnboxNullable&lt;T&gt;(ref %object)  null as no value, a box of exactly T as its value
/// function ref  ChipSharp.Box.As&lt;T&gt;(ref %object)             the box when it is one of exactly T, else null
/// function ref  ChipSharp.Box.Nullable&lt;T&gt;(ptr %nullable)      the box of a nullable's value, or null
/// </code></item>
/// </list>
/// Where .NET throws, the program stops with .NET's message: unboxing null with NullReferenceException's, a box of another
/// type with InvalidCastException's (the runtime's <c>InvalidCast</c>, which writes both names); a nullable's Value with
/// no value, and IComparable's CompareTo of another type, with InvalidOperationException's and ArgumentException's (the
/// runtime's <c>chipsharp_fail</c>, and its <c>EnumCompareType</c> for an enum, whose message names both types).
/// </summary>
internal sealed partial class BoxingFunctions(TirModule module, ProgramFunctions program)
{
    /// <summary>The runtime's function that stops the program with a message (an exception's type and its text).</summary>
    public const string FailName = "chipsharp_fail";

    private readonly Dictionary<BoxedType, TirTypeDescriptor> _descriptors = [];
    private readonly List<BoxedType> _made = [];
    private readonly Dictionary<string, TirFunction> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<TirFunction, TirFunction> _onBox = [];
    private readonly Dictionary<string, TirGlobal> _texts = new(StringComparer.Ordinal);

    /// <summary>The boxes the program makes so far, with their descriptors, in the order first made.</summary>
    public IReadOnlyList<(BoxedType Boxed, TirTypeDescriptor Descriptor)> Made => [.. _made.Select(boxed => (boxed, _descriptors[boxed]))];

    /// <summary>The descriptor of the boxes of <paramref name="boxed"/>, the same one every time.</summary>
    public TirTypeDescriptor Descriptor(BoxedType boxed)
    {
        if (!_descriptors.TryGetValue(boxed, out var descriptor))
        {
            var name = $"{boxed.Name} (boxed)";
            descriptor = module.AddClass(name, TirTypeDescriptor.ObjectStruct(name, ("value", boxed.ValueType)),
                program.ObjectDescriptor);
            descriptor.RuntimeName = boxed.RuntimeName;
            _descriptors.Add(boxed, descriptor);
        }

        return descriptor;
    }

    /// <summary>A new box of <paramref name="boxed"/> with <paramref name="value"/> (of its TIR type) in it: known not to be null.</summary>
    public TirValue Box(TirBuilder ir, BoxedType boxed, TirValue value)
    {
        var descriptor = Descriptor(boxed);
        if (!_made.Contains(boxed))
        {
            _made.Add(boxed);
        }

        var made = ir.NewObject(descriptor);
        ir.Store(ir.FieldAddress(made, descriptor.ObjectType!, 1), value);
        return made;
    }

    /// <summary>The address of the value in <paramref name="box"/>, a box of <paramref name="boxed"/> (or of a type laid out as it is).</summary>
    public TirValue ValueAddress(TirBuilder ir, TirValue box, BoxedType boxed) =>
        ir.FieldAddress(box, Descriptor(boxed).ObjectType!, 1);

    /// <summary>The box of a nullable's value, or null for one with no value: <c>box T?</c>.</summary>
    public TirFunction BoxNullable(BoxedType boxed) => Define($"ChipSharp.Box.Nullable<{boxed.Name}>", TirType.Ref,
        [("nullable", TirType.Ptr)], (function, ir) =>
        {
            var nullable = NullableStruct(boxed);
            var address = function.Parameters[0];
            var (some, none) = (function.AddBlock("some"), function.AddBlock("none"));
            ir.Branch(ir.Load(TirType.Bool, ir.FieldAddress(address, nullable, Nullables.HasValueField)), some, none);

            ir.Block = some;
            ir.Return(Box(ir, boxed, ir.Load(boxed.ValueType, ir.FieldAddress(address, nullable, Nullables.ValueField))));

            ir.Block = none;
            ir.Return(ir.Zero(TirType.Ref));
        });

    /// <summary>
    /// <c>unbox</c> (and <c>unbox.any</c>, which loads from it) to <paramref name="target"/>: the address of the value in
    /// a box of a type that unboxes to it (see <see cref="Boxing.Unboxes"/>), after the null check; any other object
    /// stops the program with InvalidCastException's message.
    /// </summary>
    public TirFunction Unbox(BoxedType target) => Define($"ChipSharp.Box.Unbox<{target.Name}>", TirType.Ptr,
        [("object", TirType.Ref)], (function, ir) =>
        {
            var value = function.Parameters[0];
            var yes = function.AddBlock("yes");
            ir.NullCheck(value);
            var accepted = program.VirtualTables.Dispatch.Boxed.Append(target).Distinct()
                .Where(boxed => Boxing.Unboxes(target, boxed)).ToList();
            for (var i = 0; i < accepted.Count; i++)
            {
                var next = function.AddBlock($"not_{i}");
                ir.Branch(ir.Compare(TirCompareOp.Eq, ir.TypeOf(value), ir.AddressOf(Descriptor(accepted[i]))), yes, next);
                ir.Block = next;
            }

            ir.Call(CastFailed, value, ir.AddressOf(Text($"<name {target.RuntimeName}>", target.RuntimeName)));
            ir.Return(ir.Zero(TirType.Ptr));

            ir.Block = yes;
            ir.Return(ValueAddress(ir, value, target));
        });

    /// <summary>
    /// <c>unbox.any T?</c>: no value for null, the value of a box of exactly T; any other object stops the program with
    /// InvalidCastException's message, which names <paramref name="nullableType"/> (.NET's <c>System.Nullable`1[...]</c>).
    /// </summary>
    public TirFunction UnboxNullable(BoxedType target, IlType nullableType) => Define(
        $"ChipSharp.Box.UnboxNullable<{target.Name}>", NullableStruct(target), [("object", TirType.Ref)], (function, ir) =>
        {
            var nullable = NullableStruct(target);
            var value = function.Parameters[0];
            var result = function.AddSlot("result", nullable);
            var (some, none, yes, no) = (function.AddBlock("some"), function.AddBlock("none"), function.AddBlock("yes"),
                function.AddBlock("no"));
            ir.Branch(ir.Compare(TirCompareOp.Eq, value, ir.Zero(TirType.Ref)), none, some);

            ir.Block = none;
            ir.Return(ir.Zero(nullable));

            ir.Block = some;
            ir.Branch(ir.Compare(TirCompareOp.Eq, ir.TypeOf(value), ir.AddressOf(Descriptor(target))), yes, no);

            ir.Block = yes;
            var address = ir.AddressOf(result);
            ir.Store(ir.FieldAddress(address, nullable, Nullables.HasValueField), ir.Const(true));
            ir.Store(ir.FieldAddress(address, nullable, Nullables.ValueField),
                ir.Load(target.ValueType, ValueAddress(ir, value, target)));
            ir.Return(ir.Load(result));

            ir.Block = no;
            var name = VirtualMethods.RuntimeName(nullableType);
            ir.Call(CastFailed, value, ir.AddressOf(Text($"<name {name}>", name)));
            ir.Return(ir.Zero(nullable));
        });

    /// <summary><c>isinst T</c> of a value type T (a nullable's too): the object when it is a box of exactly T, else null.</summary>
    public TirFunction As(BoxedType target) => Define($"ChipSharp.Box.As<{target.Name}>", TirType.Ref,
        [("object", TirType.Ref)], (function, ir) =>
        {
            var value = function.Parameters[0];
            var (some, yes, no) = (function.AddBlock("some"), function.AddBlock("yes"), function.AddBlock("no"));
            ir.Branch(ir.Compare(TirCompareOp.Eq, value, ir.Zero(TirType.Ref)), no, some);

            ir.Block = some;
            ir.Branch(ir.Compare(TirCompareOp.Eq, ir.TypeOf(value), ir.AddressOf(Descriptor(target))), yes, no);

            ir.Block = yes;
            ir.Return(value);

            ir.Block = no;
            ir.Return(ir.Zero(TirType.Ref));
        });

    /// <summary>
    /// What <paramref name="target"/> runs on a box (one of <see cref="Boxing.Resolve"/>'s, for a virtual method table or
    /// an interface map, or a call with one target): a function that takes the box first, then the method's other
    /// parameters, and calls the method on the address of the value in the box.
    /// </summary>
    public TirFunction OnBox(DispatchTarget target)
    {
        var boxed = target.Boxed ?? throw new ArgumentException($"{target} is not run on a box.", nameof(target));
        var callee = target.Method is { } method
            ? program.Function(DispatchTarget.Of(method))
            : target.ObjectMethod switch
            {
                "ToString" => ToStringOf(boxed),
                "Equals" => EqualsOf(boxed),
                "GetHashCode" => HashCodeOf(boxed),
                "CompareTo" => CompareToOf(boxed),
                var other => throw new ArgumentException($"A box has no {other} of its own.", nameof(target)),
            };
        if (!_onBox.TryGetValue(callee, out var function))
        {
            function = module.AddFunction($"{callee.Name} (on a box)", callee.ReturnType,
                [("this", TirType.Ref), .. callee.Parameters.Skip(1).Select(parameter => (parameter.Name, parameter.Type))]);
            _onBox.Add(callee, function);
            var ir = new TirBuilder(function.AddBlock("entry"));
            var call = ir.Call(callee, [ValueAddress(ir, function.Parameters[0], boxed), .. function.Parameters.Skip(1)]);
            ir.Return(callee.ReturnType == TirType.Void ? null : call);
        }

        return function;
    }

    /// <summary><c>object.Equals(a, b)</c>: true for the same object, false when one is null, else <c>a.Equals(b)</c> through its slot.</summary>
    public TirFunction ObjectEquals => Define("ChipSharp.Object.EqualsOfTwo", TirType.Bool,
        [("a", TirType.Ref), ("b", TirType.Ref)], (function, ir) =>
        {
            var (a, b) = (function.Parameters[0], function.Parameters[1]);
            var (same, other, both, no) = (function.AddBlock("same"), function.AddBlock("other"), function.AddBlock("both"),
                function.AddBlock("no"));
            ir.Branch(ir.Compare(TirCompareOp.Eq, a, b), same, other);

            ir.Block = same;
            ir.Return(ir.Const(true));

            ir.Block = other;
            var either = ir.Binary(TirBinaryOp.Or, ir.Compare(TirCompareOp.Eq, a, ir.Zero(TirType.Ref)),
                ir.Compare(TirCompareOp.Eq, b, ir.Zero(TirType.Ref)));
            ir.Branch(either, no, both);

            ir.Block = both;
            ir.Return(ObjectCall(ir, "Equals", IlType.Object, [a, b]));

            ir.Block = no;
            ir.Return(ir.Const(false));
        });

    /// <summary>
    /// The text of an object as text with it writes it: what <paramref name="toString"/> (its ToString as a call of it
    /// runs it, <c>ref (ref)</c>) gives, "" for null.
    /// </summary>
    public TirFunction TextOrEmpty(TirFunction toString) => Define($"ChipSharp.Object.TextOf<{toString.Name}>", TirType.Ref,
        [("object", TirType.Ref)], (function, ir) =>
        {
            var value = function.Parameters[0];
            var (none, some) = (function.AddBlock("none"), function.AddBlock("some"));
            ir.Branch(ir.Compare(TirCompareOp.Eq, value, ir.Zero(TirType.Ref)), none, some);

            ir.Block = none;
            ir.Return(ir.AddressOf(program.Text("")));

            ir.Block = some;
            ir.Return(ir.Call(toString, value));
        });

    /// <summary>The TIR struct of <c>T?</c> for the boxed type T.</summary>
    public static TirStructType NullableStruct(BoxedType boxed) => Nullables.StructOf(boxed.ValueType, boxed.Name);

    /// <summary>
    /// A call of object's <paramref name="name"/> on <paramref name="arguments"/>[0], an object (not null) of a variable of
    /// <paramref name="receiver"/>, as a <c>callvirt</c> of it does it: the one method there is, the virtual method
    /// table's, a string's.
    /// </summary>
    private TirValue ObjectCall(TirBuilder ir, string name, IlType receiver, TirValue[] arguments)
    {
        var slot = Boxing.ObjectSlot(name);
        var tables = program.VirtualTables;
        var text = FixedText.IsText(StackArrays.Primitive(ValueTypes.WithoutModifiers(receiver)));
        var targets = text ? [] : tables.Dispatch.Targets(slot, receiver);
        var called = targets.Count switch
        {
            0 => null,
            1 => program.Function(targets[0]),
            _ => program.ObjectFunctions.VirtualObjectCall(name, tables.IndexOf(slot)
                ?? throw new InvalidOperationException($"object's {name} has no place in the method tables")),
        };
        if (text || StringMethods.MayBeString(receiver))
        {
            called = program.StringObjects.OrString(name, called);
        }

        return called is not null ? ir.Call(called, arguments)
            : name == "Equals" ? ir.Const(false)
            : name == "GetHashCode" ? ir.Const(TirType.I32, 0)
            : ir.Zero(TirType.Ref);
    }

    /// <summary>Stops the program as a failed cast of <c>%object</c> to the type named <c>%to</c> does (the empty array named as one).</summary>
    private TirFunction CastFailed => Define("ChipSharp.Box.CastFailed", TirType.Void,
        [("object", TirType.Ref), ("to", TirType.Ptr)], (function, ir) =>
        {
            var (value, to) = (function.Parameters[0], function.Parameters[1]);
            var (named, empty) = (function.AddBlock("named"), function.AddBlock("empty"));
            ir.Branch(ir.Compare(TirCompareOp.Eq, ir.TypeOf(value), ir.Zero(TirType.Ptr)), empty, named);

            ir.Block = named;
            var from = ir.Load(TirType.Ptr, ir.FieldAddress(ir.TypeOf(value), TirTypeDescriptor.Layout, TirTypeDescriptor.NameField));
            ir.Call(program.ObjectFunctions.InvalidCast, from, to);
            ir.Return();

            ir.Block = empty;
            ir.Call(program.ObjectFunctions.InvalidCast, ir.AddressOf(program.ObjectFunctions.EmptyArrayName), to);
            ir.Return();
        });

    /// <summary>Stops the program with <paramref name="message"/> (an exception's type, then its text), as .NET would throw it.</summary>
    private void Fail(TirBuilder ir, string message)
    {
        var fail = module.Functions.FirstOrDefault(f => f.Kind == TirFunctionKind.External && f.Name == FailName)
            ?? module.AddExternalFunction(FailName, TirType.Void, ("message", TirType.Ptr));
        ir.Call(fail, ir.AddressOf(Text($"<message {message}>", message)));
    }

    /// <summary>A constant of <paramref name="text"/> as UTF-8 ending with a 0 byte, named <paramref name="name"/>, once.</summary>
    private TirGlobal Text(string name, string text)
    {
        if (!_texts.TryGetValue(name, out var global))
        {
            global = module.AddConstant(name, [.. System.Text.Encoding.UTF8.GetBytes(text), 0]);
            _texts.Add(name, global);
        }

        return global;
    }

    /// <summary>
    /// The function named <paramref name="name"/>, made the first time with <paramref name="build"/>, which gets it and a
    /// builder at its entry; it is known by its name before its body is built, so a function may call itself.
    /// </summary>
    private TirFunction Define(string name, TirType returns, (string Name, TirType Type)[] parameters,
        Action<TirFunction, TirBuilder> build)
    {
        if (!_functions.TryGetValue(name, out var function))
        {
            function = module.AddFunction(name, returns, parameters);
            _functions.Add(name, function);
            build(function, new TirBuilder(function.AddBlock("entry")));
        }

        return function;
    }
}
