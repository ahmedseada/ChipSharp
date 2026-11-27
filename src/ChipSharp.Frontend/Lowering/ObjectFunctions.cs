using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The TIR functions that virtual calls, interfaces and type tests call (see <see cref="VirtualMethods"/>), each added
/// to the module when first needed. They read type descriptors through their address, laid out as
/// <see cref="TirTypeDescriptor.Layout"/> says, and loop where a test walks a chain: a function of their own, so that
/// the code that tests an object stays one call.
/// <code>
/// function bool ChipSharp.Object.IsClass(ptr %type, ptr %class)          the class or a base of it is %class
/// function ptr ChipSharp.Object.FindInterface(ptr %type, ptr %interface) its method table for %interface, or null
/// function bool ChipSharp.Object.IsArray(ptr %type, ptr %array)          an array a variable of %array's type holds
/// function ref ChipSharp.Object.AsClass(ref %object, ptr %class)         C#'s "as": the object, or null
/// function ref ChipSharp.Object.AsInterface(ref %object, ptr %interface)
/// function ref ChipSharp.Object.AsArray(ref %object, ptr %array)
/// function ref ChipSharp.Object.CastClass(ref %object, ptr %class)       (T)x: the object (null too), or it stops
/// function ref ChipSharp.Object.CastInterface(ref %object, ptr %interface)
/// function ref ChipSharp.Object.CastArray(ref %object, ptr %array)
/// function bool ChipSharp.Object.Equals(ref %this, ref %other)           object's own Equals: the same object
/// function i32 ChipSharp.Object.GetHashCode(ref %this)                   object's own GetHashCode
/// function bool ChipSharp.Object.CallEquals(ref %this, ref %other)       a virtual call of Equals (GetHashCode too)
/// external function void ChipSharp.Runtime.InvalidCast(ptr %from, ptr %to)
/// </code>
/// <para>
/// An object may be an array (see <see cref="HeapArrays"/>), whose descriptor has no virtual methods and no interfaces,
/// and whose base is the descriptor of its elements' type: a test against a class or an interface fails for it, and a
/// virtual call of object's Equals or GetHashCode, which an array has from object, runs object's own. An array of a
/// class's objects is an array of its base's too, as .NET's arrays are covariant, and an array of references is one of
/// <c>object</c>'s. The empty array <c>Array.Empty&lt;T&gt;()</c> gives, one object for every T, has no descriptor (its
/// header is null): it passes a test against any array type, and fails one against a class or an interface.
/// </para>
/// <para>
/// A cast that fails calls the runtime's InvalidCast with the names of the object's type and of the type it was cast to
/// (.NET's, as the descriptors hold them), which stops the program with .NET's InvalidCastException message. Object's own
/// GetHashCode is the object's address divided by 8 (an object starts at a multiple of 8): the collector never moves an
/// object, so it stays the same for as long as the object lives, and no two live objects share one; .NET's own numbers
/// are another program's, so a program sees only that the same object gives the same number.
/// </para>
/// </summary>
internal sealed class ObjectFunctions(TirModule module, Func<TirTypeDescriptor> systemObject)
{
    /// <summary>The runtime function a cast that fails calls; each runtime's failures file has it.</summary>
    public const string InvalidCastName = "ChipSharp.Runtime.InvalidCast";

    private static readonly TirStructType Descriptor = TirTypeDescriptor.Layout;
    private static readonly TirStructType Entry = TirTypeDescriptor.InterfaceEntry;

    private readonly Dictionary<(TirTypeDescriptorKind Kind, bool Cast), TirFunction> _tests = [];
    private readonly Dictionary<string, TirFunction> _virtualCalls = new(StringComparer.Ordinal);
    private TirFunction? _isClass;
    private TirFunction? _findInterface;
    private TirFunction? _isArray;
    private TirFunction? _equals;
    private TirFunction? _hashCode;
    private TirFunction? _invalidCast;
    private TirGlobal? _emptyArrayName;

    /// <summary>True when the class at <c>%type</c> (a descriptor's address) is <c>%class</c> or derives from it.</summary>
    public TirFunction IsClass => _isClass ??= DefineIsClass();

    /// <summary>The method table of the interface <c>%interface</c> in the interface map of the class at <c>%type</c>; null without one.</summary>
    public TirFunction FindInterface => _findInterface ??= DefineFindInterface();

    /// <summary>True when the type at <c>%type</c> is an array that a variable of the array type <c>%array</c> can hold.</summary>
    public TirFunction IsArray => _isArray ??= DefineIsArray();

    /// <summary>Object's own Equals: true for the same object.</summary>
    public TirFunction ObjectEquals => _equals ??= DefineEquals();

    /// <summary>Object's own GetHashCode: the object's address divided by 8.</summary>
    public TirFunction ObjectHashCode => _hashCode ??= DefineHashCode();

    /// <summary>
    /// <c>isinst</c> (<paramref name="cast"/> false: the object, or null) or <c>castclass</c> (true: the object, or the
    /// program stops) against the descriptor of a class, an interface or an array type (<paramref name="kind"/>).
    /// </summary>
    public TirFunction TypeTest(TirTypeDescriptorKind kind, bool cast)
    {
        if (!_tests.TryGetValue((kind, cast), out var function))
        {
            function = DefineTypeTest(kind, cast);
            _tests.Add((kind, cast), function);
        }

        return function;
    }

    /// <summary>
    /// A virtual call of System.Object's <paramref name="name"/> (Equals or GetHashCode), whose slot is number
    /// <paramref name="index"/> of every class's virtual method table: object's own on an array, else the table's.
    /// </summary>
    public TirFunction VirtualObjectCall(string name, int index)
    {
        if (!_virtualCalls.TryGetValue(name, out var function))
        {
            function = DefineVirtualObjectCall(name, index);
            _virtualCalls.Add(name, function);
        }

        return function;
    }

    /// <summary>
    /// Ends the block with a branch to <paramref name="whenArray"/> when the descriptor at <paramref name="type"/> (a
    /// descriptor's address, made again in each block) is an array type's, or null (the empty array); else goes on in a
    /// new block.
    /// </summary>
    private static void BranchOnArray(TirBuilder ir, Func<TirValue> type, TirBlock whenArray)
    {
        var function = ir.Function;
        var (known, other) = (function.AddBlock("known"), function.AddBlock("not_array"));
        ir.Branch(ir.Compare(TirCompareOp.Eq, type(), ir.Zero(TirType.Ptr)), whenArray, known);

        ir.Block = known;
        ir.Branch(IsArrayType(ir, type()), whenArray, other);
        ir.Block = other;
    }

    /// <summary>True when the descriptor at <paramref name="type"/> (not null) is an array type's: its flags have 1.</summary>
    private static TirValue IsArrayType(TirBuilder ir, TirValue type)
    {
        var flags = ir.Load(TirType.I32, ir.FieldAddress(type, Descriptor, TirTypeDescriptor.FlagsField));
        var array = ir.Binary(TirBinaryOp.And, flags, ir.Const(TirType.I32, 1));
        return ir.Compare(TirCompareOp.Ne, array, ir.Const(TirType.I32, 0));
    }

    private TirFunction DefineIsClass()
    {
        var function = module.AddFunction("ChipSharp.Object.IsClass", TirType.Bool, ("type", TirType.Ptr),
            ("class", TirType.Ptr));
        var current = function.AddSlot("type", TirType.Ptr);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var (loop, up, yes, no) = (function.AddBlock("loop"), function.AddBlock("up"), function.AddBlock("yes"),
            function.AddBlock("no"));

        // an array is of no class: the base in its descriptor is its elements' type
        BranchOnArray(ir, () => function.Parameters[0], no);
        ir.Store(current, function.Parameters[0]);
        ir.Jump(loop);

        ir.Block = loop;
        ir.Branch(ir.Compare(TirCompareOp.Eq, ir.Load(current), function.Parameters[1]), yes, up);

        // on to the base class, until there is none
        ir.Block = up;
        var baseType = ir.Load(TirType.Ptr, ir.FieldAddress(ir.Load(current), Descriptor, TirTypeDescriptor.BaseField));
        ir.Store(current, baseType);
        ir.Branch(ir.Compare(TirCompareOp.Eq, baseType, ir.Zero(TirType.Ptr)), no, loop);

        ir.Block = yes;
        ir.Return(ir.Const(true));

        ir.Block = no;
        ir.Return(ir.Const(false));
        return function;
    }

    private TirFunction DefineFindInterface()
    {
        var function = module.AddFunction("ChipSharp.Object.FindInterface", TirType.Ptr, ("type", TirType.Ptr),
            ("interface", TirType.Ptr));
        var (type, wanted) = (function.Parameters[0], function.Parameters[1]);
        var index = function.AddSlot("index", TirType.I32);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var (loop, test, next, found, none) = (function.AddBlock("loop"), function.AddBlock("test"),
            function.AddBlock("next"), function.AddBlock("found"), function.AddBlock("none"));

        // the empty array has no descriptor (an array's has an empty interface map)
        ir.Branch(ir.Compare(TirCompareOp.Eq, type, ir.Zero(TirType.Ptr)), none, loop);

        ir.Block = loop;
        var count = ir.Load(TirType.I32, ir.FieldAddress(type, Descriptor, TirTypeDescriptor.InterfaceCountField));
        ir.Branch(ir.Compare(TirCompareOp.LtUnsigned, ir.Load(index), count), test, none);

        ir.Block = test;
        var entry = EntryAt(ir, type, ir.Load(index));
        ir.Branch(ir.Compare(TirCompareOp.Eq, ir.Load(TirType.Ptr, ir.FieldAddress(entry, Entry, 0)), wanted), found, next);

        ir.Block = next;
        ir.Store(index, ir.Binary(TirBinaryOp.Add, ir.Load(index), ir.Const(TirType.I32, 1)));
        ir.Jump(loop);

        ir.Block = found;
        ir.Return(ir.Load(TirType.Ptr, ir.FieldAddress(EntryAt(ir, type, ir.Load(index)), Entry, 1)));

        ir.Block = none;
        ir.Return(ir.Zero(TirType.Ptr));
        return function;
    }

    /// <summary>The address of entry <paramref name="index"/> of the interface map of the class at <paramref name="type"/>.</summary>
    private static TirValue EntryAt(TirBuilder ir, TirValue type, TirValue index)
    {
        var map = ir.Load(TirType.Ptr, ir.FieldAddress(type, Descriptor, TirTypeDescriptor.InterfacesField));
        return ir.ElementAddress(map, Entry, index);
    }

    private TirFunction DefineIsArray()
    {
        // the same array type; else an array of references whose elements' type (in the descriptor's base) derives from
        // the wanted one's, or the wanted elements are object's (whose descriptor has no base)
        var function = module.AddFunction("ChipSharp.Object.IsArray", TirType.Bool, ("type", TirType.Ptr),
            ("array", TirType.Ptr));
        var (type, array) = (function.Parameters[0], function.Parameters[1]);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var (known, isArray, other, elements, yes, no) = (function.AddBlock("known"), function.AddBlock("is_array"),
            function.AddBlock("other"), function.AddBlock("elements"), function.AddBlock("yes"), function.AddBlock("no"));

        // the empty array is an array of every type
        ir.Branch(ir.Compare(TirCompareOp.Eq, type, ir.Zero(TirType.Ptr)), yes, known);

        ir.Block = known;
        ir.Branch(IsArrayType(ir, type), isArray, no);

        ir.Block = isArray;
        ir.Branch(ir.Compare(TirCompareOp.Eq, type, array), yes, other);

        ir.Block = other;
        var wanted = ir.Load(TirType.Ptr, ir.FieldAddress(array, Descriptor, TirTypeDescriptor.BaseField));
        var have = ir.Load(TirType.Ptr, ir.FieldAddress(type, Descriptor, TirTypeDescriptor.BaseField));
        var both = ir.Binary(TirBinaryOp.And, ir.Compare(TirCompareOp.Ne, wanted, ir.Zero(TirType.Ptr)),
            ir.Compare(TirCompareOp.Ne, have, ir.Zero(TirType.Ptr)));
        ir.Branch(both, elements, no);

        ir.Block = elements;
        var wantedElements = ir.Load(TirType.Ptr, ir.FieldAddress(array, Descriptor, TirTypeDescriptor.BaseField));
        var haveElements = ir.Load(TirType.Ptr, ir.FieldAddress(type, Descriptor, TirTypeDescriptor.BaseField));
        var ofObjects = ir.Compare(TirCompareOp.Eq,
            ir.Load(TirType.Ptr, ir.FieldAddress(wantedElements, Descriptor, TirTypeDescriptor.BaseField)),
            ir.Zero(TirType.Ptr));
        ir.Return(ir.Binary(TirBinaryOp.Or, ofObjects, ir.Call(IsClass, haveElements, wantedElements)));

        ir.Block = yes;
        ir.Return(ir.Const(true));

        ir.Block = no;
        ir.Return(ir.Const(false));
        return function;
    }

    private TirFunction DefineTypeTest(TirTypeDescriptorKind kind, bool cast)
    {
        var (what, parameter) = kind switch
        {
            TirTypeDescriptorKind.Interface => ("Interface", "interface"),
            TirTypeDescriptorKind.Array => ("Array", "array"),
            _ => ("Class", "class"),
        };
        var function = module.AddFunction($"ChipSharp.Object.{(cast ? "Cast" : "As")}{what}", TirType.Ref,
            ("object", TirType.Ref), (parameter, TirType.Ptr));
        var (value, target) = (function.Parameters[0], function.Parameters[1]);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var (test, yes, no) = (function.AddBlock("test"), function.AddBlock("yes"), function.AddBlock("no"));

        // null is null, for as and for a cast alike
        ir.Branch(ir.Compare(TirCompareOp.Eq, value, ir.Zero(TirType.Ref)), cast ? yes : no, test);

        ir.Block = test;
        var type = ir.TypeOf(value);
        var fits = kind switch
        {
            TirTypeDescriptorKind.Interface =>
                ir.Compare(TirCompareOp.Ne, ir.Call(FindInterface, type, target), ir.Zero(TirType.Ptr)),
            TirTypeDescriptorKind.Array => (TirValue)ir.Call(IsArray, type, target),
            _ => ir.Call(IsClass, type, target),
        };
        ir.Branch(fits, yes, no);

        ir.Block = yes;
        ir.Return(value);

        ir.Block = no;
        if (!cast)
        {
            ir.Return(ir.Zero(TirType.Ref));
            return function;
        }

        // the empty array, which has no descriptor, fails only against a class or an interface: it is named as an array
        var (named, empty) = (function.AddBlock("named"), function.AddBlock("empty"));
        ir.Branch(ir.Compare(TirCompareOp.Eq, ir.TypeOf(value), ir.Zero(TirType.Ptr)), empty, named);

        ir.Block = named;
        var from = ir.Load(TirType.Ptr, ir.FieldAddress(ir.TypeOf(value), Descriptor, TirTypeDescriptor.NameField));
        ir.Call(InvalidCast, from, ir.Load(TirType.Ptr, ir.FieldAddress(target, Descriptor, TirTypeDescriptor.NameField)));
        ir.Return(ir.Zero(TirType.Ref));

        ir.Block = empty;
        ir.Call(InvalidCast, ir.AddressOf(EmptyArrayName), ir.Load(TirType.Ptr,
            ir.FieldAddress(target, Descriptor, TirTypeDescriptor.NameField)));
        ir.Return(ir.Zero(TirType.Ref));
        return function;
    }

    private TirFunction InvalidCast => _invalidCast ??= module.AddExternalFunction(InvalidCastName, TirType.Void,
        ("from", TirType.Ptr), ("to", TirType.Ptr));

    /// <summary>The name a failed cast gives the empty array: "System.Array", as UTF-8 ending with a 0 byte.</summary>
    private TirGlobal EmptyArrayName => _emptyArrayName ??=
        module.AddConstant("<System.Array>", [.. System.Text.Encoding.UTF8.GetBytes("System.Array"), 0]);

    private TirFunction DefineVirtualObjectCall(string name, int index)
    {
        var own = name == "Equals" ? ObjectEquals : ObjectHashCode;
        var function = module.AddFunction($"ChipSharp.Object.Call{name}", own.ReturnType,
            [.. own.Parameters.Select(parameter => (parameter.Name, parameter.Type))]);
        TirValue[] arguments = [.. function.Parameters];
        var ir = new TirBuilder(function.AddBlock("entry"));
        var (array, table) = (function.AddBlock("array"), function.AddBlock("table"));
        BranchOnArray(ir, () => ir.TypeOf(arguments[0]), array);
        ir.Jump(table);

        ir.Block = table;
        var slots = ir.FieldAddress(ir.TypeOf(arguments[0]), Descriptor, TirTypeDescriptor.VirtualsField);
        var method = ir.Load(TirType.Ptr, ir.ElementAddress(slots, TirType.Ptr, ir.Const(TirType.I32, index)));
        ir.Return(ir.CallIndirect(own.ReturnType, method, arguments));

        ir.Block = array;
        ir.Return(ir.Call(own, arguments));
        return function;
    }

    private TirFunction DefineEquals()
    {
        var function = module.AddFunction("ChipSharp.Object.Equals", TirType.Bool, ("this", TirType.Ref),
            ("other", TirType.Ref));
        var ir = new TirBuilder(function.AddBlock("entry"));
        ir.Return(ir.Compare(TirCompareOp.Eq, function.Parameters[0], function.Parameters[1]));
        return function;
    }

    private TirFunction DefineHashCode()
    {
        // the header's address is the object's: a ptr, which is a number, where the ref is not
        var objectType = systemObject().ObjectType!;
        var function = module.AddFunction("ChipSharp.Object.GetHashCode", TirType.I32, ("this", TirType.Ref));
        var ir = new TirBuilder(function.AddBlock("entry"));
        var address = ir.PtrToInt(ir.FieldAddress(function.Parameters[0], objectType, 0), TirType.I64);
        var eighths = ir.Binary(TirBinaryOp.ShrUnsigned, address, ir.Const(TirType.I64, 3));
        ir.Return(ir.Convert(eighths, TirType.I32));
        return function;
    }
}
