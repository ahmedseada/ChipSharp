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
/// function ref ChipSharp.Object.AsClass(ref %object, ptr %class)         C#'s "as": the object, or null
/// function ref ChipSharp.Object.AsInterface(ref %object, ptr %interface)
/// function ref ChipSharp.Object.CastClass(ref %object, ptr %class)       (T)x: the object (null too), or it stops
/// function ref ChipSharp.Object.CastInterface(ref %object, ptr %interface)
/// function bool ChipSharp.Object.Equals(ref %this, ref %other)           object's own Equals: the same object
/// function i32 ChipSharp.Object.GetHashCode(ref %this)                   object's own GetHashCode
/// external function void ChipSharp.Runtime.InvalidCast(ptr %from, ptr %to)
/// </code>
/// A cast that fails calls the runtime's InvalidCast with the names of the object's type and of the type it was cast to
/// (.NET's, as the descriptors hold them), which stops the program with .NET's InvalidCastException message. Object's own
/// GetHashCode is the object's address divided by 8 (an object starts at a multiple of 8): the collector never moves an
/// object, so it stays the same for as long as the object lives, and no two live objects share one; .NET's own numbers
/// are another program's, so a program sees only that the same object gives the same number.
/// </summary>
internal sealed class ObjectFunctions(TirModule module, Func<TirTypeDescriptor> systemObject)
{
    /// <summary>The runtime function a cast that fails calls; each runtime's failures file has it.</summary>
    public const string InvalidCastName = "ChipSharp.Runtime.InvalidCast";

    private static readonly TirStructType Descriptor = TirTypeDescriptor.Layout;
    private static readonly TirStructType Entry = TirTypeDescriptor.InterfaceEntry;

    private readonly Dictionary<(bool Interface, bool Cast), TirFunction> _tests = [];
    private TirFunction? _isClass;
    private TirFunction? _findInterface;
    private TirFunction? _equals;
    private TirFunction? _hashCode;
    private TirFunction? _invalidCast;

    /// <summary>True when the class at <c>%type</c> (a descriptor's address) is <c>%class</c> or derives from it.</summary>
    public TirFunction IsClass => _isClass ??= DefineIsClass();

    /// <summary>The method table of the interface <c>%interface</c> in the interface map of the class at <c>%type</c>; null without one.</summary>
    public TirFunction FindInterface => _findInterface ??= DefineFindInterface();

    /// <summary>Object's own Equals: true for the same object.</summary>
    public TirFunction ObjectEquals => _equals ??= DefineEquals();

    /// <summary>Object's own GetHashCode: the object's address divided by 8.</summary>
    public TirFunction ObjectHashCode => _hashCode ??= DefineHashCode();

    /// <summary>
    /// <c>isinst</c> (<paramref name="cast"/> false: the object, or null) or <c>castclass</c> (true: the object, or the
    /// program stops) against a class's descriptor, or an interface's when <paramref name="isInterface"/>.
    /// </summary>
    public TirFunction TypeTest(bool isInterface, bool cast)
    {
        if (!_tests.TryGetValue((isInterface, cast), out var function))
        {
            function = DefineTypeTest(isInterface, cast);
            _tests.Add((isInterface, cast), function);
        }

        return function;
    }

    private TirFunction DefineIsClass()
    {
        var function = module.AddFunction("ChipSharp.Object.IsClass", TirType.Bool, ("type", TirType.Ptr),
            ("class", TirType.Ptr));
        var current = function.AddSlot("type", TirType.Ptr);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var (loop, up, yes, no) = (function.AddBlock("loop"), function.AddBlock("up"), function.AddBlock("yes"),
            function.AddBlock("no"));
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
        ir.Jump(loop);

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

    private TirFunction DefineTypeTest(bool isInterface, bool cast)
    {
        var kind = isInterface ? "Interface" : "Class";
        var function = module.AddFunction($"ChipSharp.Object.{(cast ? "Cast" : "As")}{kind}", TirType.Ref,
            ("object", TirType.Ref), (isInterface ? "interface" : "class", TirType.Ptr));
        var (value, target) = (function.Parameters[0], function.Parameters[1]);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var (test, yes, no) = (function.AddBlock("test"), function.AddBlock("yes"), function.AddBlock("no"));

        // null is null, for as and for a cast alike
        ir.Branch(ir.Compare(TirCompareOp.Eq, value, ir.Zero(TirType.Ref)), cast ? yes : no, test);

        ir.Block = test;
        var type = ir.TypeOf(value);
        var fits = isInterface
            ? ir.Compare(TirCompareOp.Ne, ir.Call(FindInterface, type, target), ir.Zero(TirType.Ptr))
            : (TirValue)ir.Call(IsClass, type, target);
        ir.Branch(fits, yes, no);

        ir.Block = yes;
        ir.Return(value);

        ir.Block = no;
        if (cast)
        {
            var from = ir.Load(TirType.Ptr, ir.FieldAddress(ir.TypeOf(value), Descriptor, TirTypeDescriptor.NameField));
            var to = ir.Load(TirType.Ptr, ir.FieldAddress(target, Descriptor, TirTypeDescriptor.NameField));
            ir.Call(_invalidCast ??= module.AddExternalFunction(InvalidCastName, TirType.Void, ("from", TirType.Ptr),
                ("to", TirType.Ptr)), from, to);
        }

        ir.Return(ir.Zero(TirType.Ref));
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
