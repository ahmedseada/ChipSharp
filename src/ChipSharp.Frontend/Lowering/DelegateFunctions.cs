using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The TIR functions of delegates (see <see cref="Delegates"/> for their layout), each added to the module when first
/// needed. They follow .NET's System.Delegate and System.MulticastDelegate (CombineImpl, RemoveImpl, Equals,
/// GetHashCode, GetInvocationList), with invocation lists that are always exactly as long as they need to be: a
/// combined delegate never shares its list, and no list ever changes once made.
/// <code>
/// function ref ChipSharp.Delegate.Combine(ref %a, ref %b)          a + b: a list of a's, then b's; a or b when the other is null
/// function ref ChipSharp.Delegate.Remove(ref %source, ref %value)  source - value: the last occurrence of value's list taken out
/// function bool ChipSharp.Delegate.Equals(ref %this, ref %other)   the same type, target and method (lists: one by one)
/// function i32 ChipSharp.Delegate.GetHashCode(ref %this)
/// function bool ChipSharp.Delegate.op_Equality(ref %a, ref %b)     ==: both null, the same object, or Equals
/// function bool ChipSharp.Delegate.op_Inequality(ref %a, ref %b)
/// function ref ChipSharp.Delegate.GetInvocationList(ref %this)     a new Delegate[]
/// function ref ChipSharp.Delegate.CompareExchange(ptr %location, ref %value, ref %comparand)   with interrupts off
/// function i32 ChipSharp.Delegate.InvokeAll i32(ref, i32)(ref %invocations, i32 %p1)   a multicast delegate's method
/// external function void ChipSharp.Runtime.DelegateTypes()         Combine or Remove of two types: .NET's ArgumentException
/// </code>
/// A multicast delegate is made as an object of System.MulticastDelegate's descriptor whose header is then set to its
/// type's (the first operand's): every delegate type's objects have one size and one layout, so the collector reads it
/// the same either way, and nothing can collect in between. Its method is its type's invoker, which the descriptor
/// holds in its virtual method table after object's slots (<see cref="InvokerIndex"/>).
/// </summary>
internal sealed class DelegateFunctions(TirModule module, ProgramFunctions program)
{
    private const string Prefix = "ChipSharp.Delegate.";

    private static readonly TirStructType Layout = Delegates.ObjectType;

    private readonly Dictionary<string, TirFunction> _invokers = new(StringComparer.Ordinal);
    private TirFunction? _combine;
    private TirFunction? _remove;
    private TirFunction? _equals;
    private TirFunction? _hashCode;
    private TirFunction? _equality;
    private TirFunction? _inequality;
    private TirFunction? _invocationList;
    private TirFunction? _compareExchange;

    /// <summary>True once a function that makes multicast delegates is in the module: every delegate type then needs its invoker.</summary>
    public bool UsesMulticast => _combine is not null || _remove is not null;

    /// <summary>Where a delegate type's invoker is in its descriptor's virtual method table: after object's slots.</summary>
    public int InvokerIndex => program.VirtualTables.ObjectTable.Count;

    public TirFunction Combine => _combine ??= DefineCombine();

    public TirFunction Remove => _remove ??= DefineRemove();

    /// <summary>A delegate's Equals(object): what object's Equals slot runs on one.</summary>
    public TirFunction SameAs => _equals ??= DefineEquals();

    /// <summary>A delegate's GetHashCode(): what object's GetHashCode slot runs on one.</summary>
    public TirFunction HashCode => _hashCode ??= DefineHashCode();

    public TirFunction Equality => _equality ??= DefineEquality();

    public TirFunction Inequality => _inequality ??= DefineInequality();

    public TirFunction InvocationList => _invocationList ??= DefineInvocationList();

    public TirFunction CompareExchange => _compareExchange ??= DefineCompareExchange();

    /// <summary>What object's slot <paramref name="name"/> (Equals or GetHashCode) runs on a delegate.</summary>
    public TirFunction ObjectMethod(string name) => name == "Equals" ? SameAs : HashCode;

    /// <summary>
    /// The invoker of the delegates whose Invoke returns <paramref name="returns"/> and takes
    /// <paramref name="parameters"/> (after the target, a ref): it calls each delegate of the list it is given as its
    /// target with the arguments, in order, and returns what the last returns.
    /// </summary>
    public TirFunction Invoker(TirType returns, IReadOnlyList<TirType> parameters)
    {
        var key = $"{returns}({string.Join(", ", parameters)})";
        if (!_invokers.TryGetValue(key, out var function))
        {
            function = DefineInvoker(returns, parameters);
            _invokers.Add(key, function);
        }

        return function;
    }

    private TirFunction DefineInvoker(TirType returns, IReadOnlyList<TirType> parameters)
    {
        var function = module.AddFunction($"{Prefix}InvokeAll {returns}(ref{string.Concat(parameters.Select(p => $", {p}"))})",
            returns, [("invocations", TirType.Ref), .. parameters.Select((type, i) => ($"p{i + 1}", type))]);
        var code = Begin(function);
        var ir = code.Ir;
        var list = function.Parameters[0];
        var result = returns == TirType.Void ? null : function.AddSlot("result", returns);
        var i = function.AddSlot("i", TirType.I32);
        code.For(i, () => ir.Const(TirType.I32, 0), () => ir.ArrayLength(list), index =>
        {
            var single = ir.Load(TirType.Ref, Element(ir, list, index));
            var arguments = new List<TirValue> { Target(ir, single) };
            arguments.AddRange(function.Parameters.Skip(1));
            var call = ir.CallIndirect(returns, Method(ir, single), arguments);
            if (result is not null)
            {
                ir.Store(result, call);
            }
        });

        ir.Return(result is null ? null : ir.Load(result));
        return function;
    }

    private TirFunction DefineCombine()
    {
        var function = module.AddFunction($"{Prefix}Combine", TirType.Ref, ("a", TirType.Ref), ("b", TirType.Ref));
        var (a, b) = (function.Parameters[0], function.Parameters[1]);
        var code = Begin(function);
        var ir = code.Ir;

        // null + b is b, a + null is a; two types are an ArgumentException
        code.ReturnIf(IsNull(ir, a), () => b);
        code.ReturnIf(IsNull(ir, b), () => a);
        CheckSameType(code, a, b);

        var (countA, countB) = (Count(code, () => a), Count(code, () => b));
        var list = function.AddSlot("list", TirType.Ref);
        ir.Store(list, ir.NewArray(program.DelegateArrayDescriptor,
            ir.Binary(TirBinaryOp.Add, ir.Load(countA), ir.Load(countB))));
        Append(code, list, () => ir.Const(TirType.I32, 0), () => a);
        Append(code, list, () => ir.Load(countA), () => b);
        ir.Return(NewMulticast(ir, ir.TypeOf(a), ir.Load(list)));
        return function;
    }

    /// <summary>
    /// MulticastDelegate.RemoveImpl: a single value is looked for from the end of the source's list (or is the source
    /// itself), a list as a run of the source's, the last one first; what is left is null, the one delegate left, or a
    /// new multicast delegate of the rest, in order. Nothing found gives the source itself.
    /// </summary>
    private TirFunction DefineRemove()
    {
        var function = module.AddFunction($"{Prefix}Remove", TirType.Ref, ("source", TirType.Ref), ("value", TirType.Ref));
        var (source, value) = (function.Parameters[0], function.Parameters[1]);
        var code = Begin(function);
        var ir = code.Ir;
        var (n, i, j) = (function.AddSlot("n", TirType.I32), function.AddSlot("i", TirType.I32),
            function.AddSlot("j", TirType.I32));

        code.ReturnIf(IsNull(ir, source), () => ir.Zero(TirType.Ref));
        code.ReturnIf(IsNull(ir, value), () => source);
        CheckSameType(code, source, value);

        // the source a single delegate: it is null when it equals the value (a list never equals a single one)
        code.ReturnIf(IsNull(ir, Invocations(ir, source)), () => ir.Zero(TirType.Ref), andAlso: () =>
            ir.Binary(TirBinaryOp.And, IsNull(ir, Invocations(ir, value)), SameSingle(ir, source, value)));
        code.ReturnIf(IsNull(ir, Invocations(ir, source)), () => source);

        // how many the value is: 1, or its list's length
        ir.Store(n, ir.ArrayLength(Invocations(ir, source)));
        var count = Count(code, () => value);

        // from the last place the value's run can start, down to 0
        ir.Store(i, ir.Binary(TirBinaryOp.Sub, ir.Load(n), ir.Load(count)));
        var (test, found, next, notFound) = (code.Block("test"), code.Block("found"), code.Block("next"),
            code.Block("not_found"));
        var loop = code.Block("loop");
        ir.Jump(loop);

        ir.Block = loop;
        ir.Branch(ir.Compare(TirCompareOp.Ge, ir.Load(i), ir.Const(TirType.I32, 0)), test, notFound);

        // the value's entries (itself, or its list) against the source's from i on, one by one
        ir.Block = test;
        ir.Store(j, ir.Const(TirType.I32, 0));
        var (compare, same) = (code.Block("compare"), code.Block("same"));
        var inner = code.Block("inner");
        ir.Jump(inner);

        ir.Block = inner;
        ir.Branch(ir.Compare(TirCompareOp.Lt, ir.Load(j), ir.Load(count)), compare, found);

        ir.Block = compare;
        var ours = function.AddSlot("ours", TirType.Ref);
        ir.Store(ours, ir.Load(TirType.Ref, Element(ir, Invocations(ir, source), ir.Binary(TirBinaryOp.Add, ir.Load(i),
            ir.Load(j)))));
        var theirs = code.Select(TirType.Ref, () => IsNull(ir, Invocations(ir, value)), () => value,
            () => ir.Load(TirType.Ref, Element(ir, Invocations(ir, value), ir.Load(j))));
        ir.Branch(SameSingle(ir, ir.Load(ours), ir.Load(theirs)), same, next);

        ir.Block = same;
        ir.Store(j, ir.Binary(TirBinaryOp.Add, ir.Load(j), ir.Const(TirType.I32, 1)));
        ir.Jump(inner);

        ir.Block = next;
        ir.Store(i, ir.Binary(TirBinaryOp.Sub, ir.Load(i), ir.Const(TirType.I32, 1)));
        ir.Jump(loop);

        ir.Block = notFound;
        ir.Return(source);

        // found at i: none left is null; one left is that one (at the start or at the end); else a new list
        ir.Block = found;
        var left = ir.Binary(TirBinaryOp.Sub, ir.Load(n), ir.Load(count));
        code.ReturnIf(ir.Compare(TirCompareOp.Eq, left, ir.Const(TirType.I32, 0)), () => ir.Zero(TirType.Ref));
        code.ReturnIf(ir.Compare(TirCompareOp.Eq, ir.Binary(TirBinaryOp.Sub, ir.Load(n), ir.Load(count)),
            ir.Const(TirType.I32, 1)), () =>
        {
            // invocationList[i != 0 ? 0 : n - 1]
            var start = ir.Compare(TirCompareOp.Ne, ir.Load(i), ir.Const(TirType.I32, 0));
            var last = ir.Binary(TirBinaryOp.Sub, ir.Load(n), ir.Const(TirType.I32, 1));
            var at = ir.Binary(TirBinaryOp.Mul, last, ir.Convert(ir.Binary(TirBinaryOp.Xor, start, ir.Const(true)), TirType.I32));
            return ir.Load(TirType.Ref, Element(ir, Invocations(ir, source), at));
        });

        var list = function.AddSlot("list", TirType.Ref);
        ir.Store(list, ir.NewArray(program.DelegateArrayDescriptor, ir.Binary(TirBinaryOp.Sub, ir.Load(n), ir.Load(count))));
        var k = function.AddSlot("k", TirType.I32);
        code.For(k, () => ir.Const(TirType.I32, 0), () => ir.Load(i), index =>
            ir.Store(Element(ir, ir.Load(list), index), ir.Load(TirType.Ref, Element(ir, Invocations(ir, source), index))));
        code.For(k, () => ir.Binary(TirBinaryOp.Add, ir.Load(i), ir.Load(count)), () => ir.Load(n), index =>
            ir.Store(Element(ir, ir.Load(list), ir.Binary(TirBinaryOp.Sub, index, ir.Load(count))),
                ir.Load(TirType.Ref, Element(ir, Invocations(ir, source), index))));
        ir.Return(NewMulticast(ir, ir.TypeOf(source), ir.Load(list)));
        return function;
    }

    /// <summary>
    /// Delegate.Equals(object): false for null and for an object of another type; else, both single, the same target and
    /// method; both multicast, lists of the same length whose delegates are equal one by one; one of each, false.
    /// </summary>
    private TirFunction DefineEquals()
    {
        var function = module.AddFunction($"{Prefix}Equals", TirType.Bool, ("this", TirType.Ref), ("other", TirType.Ref));
        var (self, other) = (function.Parameters[0], function.Parameters[1]);
        var code = Begin(function);
        var ir = code.Ir;

        code.ReturnIf(IsNull(ir, other), () => ir.Const(false));
        code.ReturnIf(ir.Compare(TirCompareOp.Eq, self, other), () => ir.Const(true));
        code.ReturnIf(ir.Compare(TirCompareOp.Ne, ir.TypeOf(self), ir.TypeOf(other)), () => ir.Const(false));
        code.ReturnIf(IsNull(ir, Invocations(ir, self)), () =>
            ir.Binary(TirBinaryOp.And, IsNull(ir, Invocations(ir, other)), SameSingle(ir, self, other)));
        code.ReturnIf(IsNull(ir, Invocations(ir, other)), () => ir.Const(false));
        code.ReturnIf(ir.Compare(TirCompareOp.Ne, ir.ArrayLength(Invocations(ir, self)), ir.ArrayLength(Invocations(ir, other))),
            () => ir.Const(false));

        var i = function.AddSlot("i", TirType.I32);
        code.For(i, () => ir.Const(TirType.I32, 0), () => ir.ArrayLength(Invocations(ir, self)), index =>
        {
            var mine = ir.Load(TirType.Ref, Element(ir, Invocations(ir, self), index));
            var theirs = ir.Load(TirType.Ref, Element(ir, Invocations(ir, other), index));
            code.ReturnIf(ir.Binary(TirBinaryOp.Xor, SameSingle(ir, mine, theirs), ir.Const(true)), () => ir.Const(false));
        });

        ir.Return(ir.Const(true));
        return function;
    }

    /// <summary>
    /// A delegate's hash code: of a single one, its target's (object's own) times 33 plus its method's address; of a list,
    /// each one's, as .NET adds them (hash * 33 + the next). .NET's numbers are another program's.
    /// </summary>
    private TirFunction DefineHashCode()
    {
        var function = module.AddFunction($"{Prefix}GetHashCode", TirType.I32, ("this", TirType.Ref));
        var self = function.Parameters[0];
        var code = Begin(function);
        var ir = code.Ir;
        code.ReturnIf(IsNull(ir, Invocations(ir, self)), () => SingleHash(ir, self));

        var (hash, i) = (function.AddSlot("hash", TirType.I32), function.AddSlot("i", TirType.I32));
        code.For(i, () => ir.Const(TirType.I32, 0), () => ir.ArrayLength(Invocations(ir, self)), index =>
        {
            var single = ir.Load(TirType.Ref, Element(ir, Invocations(ir, self), index));
            ir.Store(hash, ir.Binary(TirBinaryOp.Add, ir.Binary(TirBinaryOp.Mul, ir.Load(hash), ir.Const(TirType.I32, 33)),
                SingleHash(ir, single)));
        });

        ir.Return(ir.Load(hash));
        return function;
    }

    private TirValue SingleHash(TirBuilder ir, TirValue single)
    {
        // object's own hash of null is 0: it is the address divided by 8
        var target = ir.Binary(TirBinaryOp.Mul, ir.Call(program.ObjectFunctions.ObjectHashCode, Target(ir, single)),
            ir.Const(TirType.I32, 33));
        var method = ir.Convert(ir.Binary(TirBinaryOp.ShrUnsigned, ir.PtrToInt(Method(ir, single), TirType.I64),
            ir.Const(TirType.I64, 2)), TirType.I32);
        return ir.Binary(TirBinaryOp.Add, target, method);
    }

    /// <summary>Delegate's operator ==: when b is null, whether a is; the same object; else b.Equals(a).</summary>
    private TirFunction DefineEquality()
    {
        var function = module.AddFunction($"{Prefix}op_Equality", TirType.Bool, ("a", TirType.Ref), ("b", TirType.Ref));
        var (a, b) = (function.Parameters[0], function.Parameters[1]);
        var code = Begin(function);
        var ir = code.Ir;
        code.ReturnIf(IsNull(ir, b), () => IsNull(ir, a));
        code.ReturnIf(ir.Compare(TirCompareOp.Eq, a, b), () => ir.Const(true));
        ir.Return(ir.Call(SameAs, b, a));
        return function;
    }

    private TirFunction DefineInequality()
    {
        var function = module.AddFunction($"{Prefix}op_Inequality", TirType.Bool, ("a", TirType.Ref), ("b", TirType.Ref));
        var ir = Begin(function).Ir;
        ir.Return(ir.Binary(TirBinaryOp.Xor, ir.Call(Equality, function.Parameters[0], function.Parameters[1]), ir.Const(true)));
        return function;
    }

    /// <summary>MulticastDelegate.GetInvocationList: a new Delegate[] of the delegate itself, or of its list's delegates.</summary>
    private TirFunction DefineInvocationList()
    {
        var function = module.AddFunction($"{Prefix}GetInvocationList", TirType.Ref, ("this", TirType.Ref));
        var self = function.Parameters[0];
        var code = Begin(function);
        var ir = code.Ir;
        var count = Count(code, () => self);
        var list = function.AddSlot("list", TirType.Ref);
        ir.Store(list, ir.NewArray(program.DelegateArrayDescriptor, ir.Load(count)));
        Append(code, list, () => ir.Const(TirType.I32, 0), () => self);
        ir.Return(ir.Load(list));
        return function;
    }

    /// <summary>
    /// Interlocked.CompareExchange of a reference: the location's value, replaced by <c>value</c> when it is
    /// <c>comparand</c>, both with interrupts off (the heap's lock), so that an interrupt handler that writes the location
    /// does so before or after, never in between. A PC program has no interrupts, and no other thread.
    /// </summary>
    private TirFunction DefineCompareExchange()
    {
        var function = module.AddFunction($"{Prefix}CompareExchange", TirType.Ref, ("location", TirType.Ptr),
            ("value", TirType.Ref), ("comparand", TirType.Ref));
        var (location, value, comparand) = (function.Parameters[0], function.Parameters[1], function.Parameters[2]);
        var (state, old) = (function.AddSlot("state", TirType.I32), function.AddSlot("old", TirType.Ref));
        var code = Begin(function);
        var ir = code.Ir;
        var (exchange, done) = (code.Block("exchange"), code.Block("done"));

        ir.Store(state, ir.Call(External("chipsharp_heap_lock", TirType.I32)));
        ir.Store(old, ir.Load(TirType.Ref, location));
        ir.Branch(ir.Compare(TirCompareOp.Eq, ir.Load(old), comparand), exchange, done);

        ir.Block = exchange;
        ir.Store(location, value);
        ir.Jump(done);

        ir.Block = done;
        ir.Call(External("chipsharp_heap_unlock", TirType.Void, ("state", TirType.I32)), ir.Load(state));
        ir.Return(ir.Load(old));
        return function;
    }

    /// <summary>Stops the program with .NET's ArgumentException when <paramref name="a"/> and <paramref name="b"/> are of two types.</summary>
    private void CheckSameType(Code code, TirValue a, TirValue b)
    {
        var ir = code.Ir;
        var (other, same) = (code.Block("other_types"), code.Block("same_type"));
        ir.Branch(ir.Compare(TirCompareOp.Ne, ir.TypeOf(a), ir.TypeOf(b)), other, same);

        ir.Block = other;
        ir.Call(program.RuntimeFunction("DelegateTypes", TirType.Void));
        ir.Return(ir.Zero(code.Function.ReturnType));

        ir.Block = same;
    }

    /// <summary>
    /// A new multicast delegate of the type at <paramref name="type"/> over <paramref name="list"/>: its target and
    /// invocations the list, its method its type's invoker.
    /// </summary>
    private TirValue NewMulticast(TirBuilder ir, TirValue type, TirValue list)
    {
        var made = ir.NewObject(program.MulticastDescriptor);
        ir.Store(ir.FieldAddress(made, Layout, 0), type);
        ir.Store(ir.FieldAddress(made, Layout, Delegates.TargetField), list);
        ir.Store(ir.FieldAddress(made, Layout, Delegates.InvocationsField), list);
        var slots = ir.FieldAddress(type, TirTypeDescriptor.Layout, TirTypeDescriptor.VirtualsField);
        var invoker = ir.Load(TirType.Ptr, ir.ElementAddress(slots, TirType.Ptr, ir.Const(TirType.I32, InvokerIndex)));
        ir.Store(ir.FieldAddress(made, Layout, Delegates.MethodField), invoker);
        return made;
    }

    /// <summary>A slot that holds how many delegates <paramref name="delegate"/> calls: 1, or its list's length.</summary>
    private static TirSlot Count(Code code, Func<TirValue> @delegate)
    {
        var ir = code.Ir;
        var count = code.Function.AddSlot($"count_{code.Function.Slots.Count}", TirType.I32);
        ir.Store(count, ir.Const(TirType.I32, 1));
        var (list, done) = (code.Block("list"), code.Block("counted"));
        ir.Branch(IsNull(ir, Invocations(ir, @delegate())), done, list);

        ir.Block = list;
        ir.Store(count, ir.ArrayLength(Invocations(ir, @delegate())));
        ir.Jump(done);

        ir.Block = done;
        return count;
    }

    /// <summary>
    /// Writes the delegates <paramref name="delegate"/> calls (itself, or its list's) into the list in
    /// <paramref name="list"/>, from <paramref name="at"/> on.
    /// </summary>
    private static void Append(Code code, TirSlot list, Func<TirValue> at, Func<TirValue> @delegate)
    {
        var ir = code.Ir;
        var (single, many, done) = (code.Block("single"), code.Block("many"), code.Block("appended"));
        ir.Branch(IsNull(ir, Invocations(ir, @delegate())), single, many);

        ir.Block = single;
        ir.Store(Element(ir, ir.Load(list), at()), @delegate());
        ir.Jump(done);

        ir.Block = many;
        var i = code.Function.AddSlot($"i_{code.Function.Slots.Count}", TirType.I32);
        code.For(i, () => ir.Const(TirType.I32, 0), () => ir.ArrayLength(Invocations(ir, @delegate())), index =>
            ir.Store(Element(ir, ir.Load(list), ir.Binary(TirBinaryOp.Add, at(), index)),
                ir.Load(TirType.Ref, Element(ir, Invocations(ir, @delegate()), index))));
        ir.Jump(done);

        ir.Block = done;
    }

    /// <summary>True when two single delegates have the same target and method (their type is the same already).</summary>
    private static TirValue SameSingle(TirBuilder ir, TirValue a, TirValue b) => ir.Binary(TirBinaryOp.And,
        ir.Compare(TirCompareOp.Eq, Target(ir, a), Target(ir, b)), ir.Compare(TirCompareOp.Eq, Method(ir, a), Method(ir, b)));

    private static TirValue Target(TirBuilder ir, TirValue @delegate) =>
        ir.Load(TirType.Ref, ir.FieldAddress(@delegate, Layout, Delegates.TargetField));

    private static TirValue Method(TirBuilder ir, TirValue @delegate) =>
        ir.Load(TirType.Ptr, ir.FieldAddress(@delegate, Layout, Delegates.MethodField));

    private static TirValue Invocations(TirBuilder ir, TirValue @delegate) =>
        ir.Load(TirType.Ref, ir.FieldAddress(@delegate, Layout, Delegates.InvocationsField));

    /// <summary>The address of element <paramref name="index"/> of the Delegate[] <paramref name="list"/>.</summary>
    private static TirValue Element(TirBuilder ir, TirValue list, TirValue index) =>
        ir.ElementAddress(ir.ArrayData(list, TirType.Ref), TirType.Ref, index);

    private static TirValue IsNull(TirBuilder ir, TirValue reference) =>
        ir.Compare(TirCompareOp.Eq, reference, ir.Zero(TirType.Ref));

    /// <summary>A function to write; its fields are reached through System.Delegate's object type, so that descriptor is made.</summary>
    private Code Begin(TirFunction function)
    {
        _ = program.DelegateDescriptor;
        return new Code(function);
    }

    private TirFunction External(string name, TirType returns, params (string Name, TirType Type)[] parameters) =>
        module.Functions.FirstOrDefault(function => function.Kind == TirFunctionKind.External && function.Name == name)
        ?? module.AddExternalFunction(name, returns, parameters);

    /// <summary>A function being written: its builder, and blocks with names of their own.</summary>
    private sealed class Code
    {
        private int _blocks;

        public Code(TirFunction function)
        {
            Function = function;
            Ir = new TirBuilder(function.AddBlock("entry"));
        }

        public TirFunction Function { get; }

        public TirBuilder Ir { get; }

        /// <summary>A new block named <paramref name="name"/> and a number.</summary>
        public TirBlock Block(string name) => Function.AddBlock($"{name}_{++_blocks}");

        /// <summary>
        /// Returns what <paramref name="value"/> gives (made in a block of its own) when <paramref name="condition"/> holds
        /// (and <paramref name="andAlso"/>, tested only then), else goes on in a new block.
        /// </summary>
        public void ReturnIf(TirValue condition, Func<TirValue?> value, Func<TirValue>? andAlso = null)
        {
            var yes = Block("then");
            var also = andAlso is null ? null : Block("and");
            var no = Block("else");
            Ir.Branch(condition, also ?? yes, no);
            if (also is not null)
            {
                Ir.Block = also;
                Ir.Branch(andAlso!(), yes, no);
            }

            Ir.Block = yes;
            Ir.Return(value());
            Ir.Block = no;
        }

        /// <summary>
        /// A slot of <paramref name="type"/> that holds <paramref name="whenTrue"/> when <paramref name="condition"/> holds,
        /// else <paramref name="whenFalse"/>; each is made in a block of its own.
        /// </summary>
        public TirSlot Select(TirType type, Func<TirValue> condition, Func<TirValue> whenTrue, Func<TirValue> whenFalse)
        {
            var slot = Function.AddSlot($"select_{Function.Slots.Count}", type);
            var (yes, no, done) = (Block("yes"), Block("no"), Block("selected"));
            Ir.Branch(condition(), yes, no);
            Ir.Block = yes;
            Ir.Store(slot, whenTrue());
            Ir.Jump(done);
            Ir.Block = no;
            Ir.Store(slot, whenFalse());
            Ir.Jump(done);
            Ir.Block = done;
            return slot;
        }

        /// <summary>
        /// For <paramref name="index"/> from <paramref name="from"/> up to (not including) <paramref name="to"/>, made again
        /// in each block that needs it: <paramref name="body"/> with the index, in a block that goes on to the next; then on
        /// after the loop.
        /// </summary>
        public void For(TirSlot index, Func<TirValue> from, Func<TirValue> to, Action<TirValue> body)
        {
            var (test, step, after) = (Block("loop"), Block("body"), Block("after"));
            Ir.Store(index, from());
            Ir.Jump(test);

            Ir.Block = test;
            Ir.Branch(Ir.Compare(TirCompareOp.Lt, Ir.Load(index), to()), step, after);

            Ir.Block = step;
            body(Ir.Load(index));
            Ir.Store(index, Ir.Binary(TirBinaryOp.Add, Ir.Load(index), Ir.Const(TirType.I32, 1)));
            Ir.Jump(test);

            Ir.Block = after;
        }
    }
}
