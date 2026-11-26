using System.Collections.Immutable;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Checks that the arrays of one method keep to the rule of <see cref="StackArrays"/>: each array lives on the stack
/// of the method that makes it, so it must not outlive that method, and an array made again in a loop must not
/// still be in use from the time before.
/// </summary>
/// <remarks>
/// It follows what each value on IL's evaluation stack, each local and each argument can be (an abstract
/// interpretation of the method's IL, block by block until nothing changes): an array, and which <c>newarr</c>
/// instructions and array parameters it can come from; a reference into an array (the address of an element from
/// <c>ldelema</c>, of a field of one, or a ref local or ref return that holds one); a long index converted by
/// <c>conv.ovf.i</c>; a field token for an array initializer; a constant; or anything else. Then it gives a verdict on
/// every instruction that has to do with arrays: null when it is fine, or what is not supported. A method that takes
/// an array parameter is checked by the same rule for it, so an array passed down never outlives the method that made
/// it either. A reference into an array is a reference like any other (see <see cref="ValueTypes"/>), which may go
/// where references go, but it must not outlive the array: a method returns one only into an array it was given.
/// </remarks>
internal static class ArrayEscapeAnalysis
{
    /// <summary>The problem with the return type of <paramref name="method"/>: an array it returns would outlive it.</summary>
    public static Unsupported? ForReturnType(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return StackArrays.TryMapType(method.ReturnType, method.Assembly, out _) ? Unsupported.ArrayEscapes : null;
    }

    /// <summary>Adds the problem of <see cref="ForReturnType"/>, if there is one, to <paramref name="problems"/>.</summary>
    public static void CheckReturnType(IlMethod method, List<Problem> problems)
    {
        if (ForReturnType(method) is { } unsupported)
        {
            problems.Add(Problem.InSignature(method, "return type", method.ReturnType, unsupported));
        }
    }

    /// <summary>
    /// The verdict on every instruction of <paramref name="method"/> that has to do with arrays: null when arrays on
    /// the stack allow it, else what is not supported. An instruction that is not in it has nothing to do with arrays.
    /// </summary>
    public static IReadOnlyDictionary<IlInstruction, Unsupported?> Check(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method.Body is { Instructions.Count: > 0 } body ? new Flow(method, body).Run() : new Dictionary<IlInstruction, Unsupported?>();
    }

    private enum Kind
    {
        Other,
        Constant,

        /// <summary>An array; <see cref="Value.Set"/> holds where it can come from (see <see cref="Flow"/>).</summary>
        Array,

        /// <summary>
        /// A reference into an array: the address of an element (ldelema) or of a field of one (ldflda), or a ref
        /// local, ref argument or ref return that may hold one; <see cref="Value.Set"/> holds where its array can come
        /// from. Other references (to a local, an argument, a static field) are <see cref="Other"/>.
        /// </summary>
        ElementAddress,

        /// <summary>A long index converted to native int (conv.ovf.i); <see cref="Value.Set"/> holds the conversions.</summary>
        Index,

        /// <summary>A field token (ldtoken); <see cref="Value.Set"/> holds the ldtoken instructions.</summary>
        Token,
    }

    /// <summary>What a value on the stack can be, as far as arrays care.</summary>
    private readonly record struct Value(Kind Kind, long Constant, ImmutableHashSet<int> Set)
    {
        public static Value Other { get; } = new(Kind.Other, 0, []);

        public static Value Of(Kind kind, ImmutableHashSet<int> set) => new(kind, 0, set);

        public static Value OfConstant(long constant) => new(Kind.Constant, constant, []);

        /// <summary>
        /// What either can be. A reference into an array that meets another reference where ways join (C#'s
        /// <c>ref c ? ref a[0] : ref x</c>) may still be into the array; any other two kinds are just something.
        /// </summary>
        public static Value Merge(Value a, Value b) => (a.Kind, b.Kind) switch
        {
            (Kind.ElementAddress, Kind.Other) => a,
            (Kind.Other, Kind.ElementAddress) => b,
            _ when a.Kind != b.Kind => Other,
            (Kind.Constant, _) => a.Constant == b.Constant ? a : Other,
            (Kind.Other, _) => a,
            _ => a with { Set = a.Set.Union(b.Set) },
        };

        public bool SameAs(Value other) => Kind == other.Kind && Constant == other.Constant && Set.SetEquals(other.Set);
    }

    /// <summary>
    /// The stack, and the arrays each local and argument (after the locals) can hold, or for a reference (a ref local,
    /// a ref parameter) the arrays it can point into, at one point.
    /// </summary>
    private sealed class State(List<Value> stack, ImmutableHashSet<int>[] variables)
    {
        public List<Value> Stack { get; } = stack;

        public ImmutableHashSet<int>[] Variables { get; } = variables;

        public State Clone() => new([.. Stack], [.. Variables]);

        /// <summary>Adds what <paramref name="other"/> can hold; true when that changes anything.</summary>
        public bool MergeFrom(State other)
        {
            var changed = false;
            if (Stack.Count == other.Stack.Count)
            {
                for (var i = 0; i < Stack.Count; i++)
                {
                    var merged = Value.Merge(Stack[i], other.Stack[i]);
                    changed |= !merged.SameAs(Stack[i]);
                    Stack[i] = merged;
                }
            }

            for (var i = 0; i < Variables.Length; i++)
            {
                var merged = Variables[i].Union(other.Variables[i]);
                changed |= merged.Count != Variables[i].Count;
                Variables[i] = merged;
            }

            return changed;
        }
    }

    /// <summary>The evaluation stack had fewer values than an instruction takes: IL that is broken, which the
    /// translation reports; the rest of the block is not followed.</summary>
    private sealed class StackUnderflow : Exception;

    /// <summary>
    /// One method's analysis. Where an array can come from is a number: the IL offset of its <c>newarr</c>, or -1 - i
    /// for argument i. Variables are the locals, then the arguments.
    /// </summary>
    private sealed class Flow
    {
        private readonly IlMethod _method;
        private readonly IlMethodBody _body;
        private readonly IReadOnlyList<IlBlock> _blocks;
        private readonly int[] _blockOf;
        private readonly int _localCount;
        private readonly bool[] _isArray;

        /// <summary>The variables that are references (ref locals, ref parameters, a struct method's this).</summary>
        private readonly bool[] _isReference;
        private readonly Dictionary<IlInstruction, Unsupported?> _verdicts = [];
        private readonly Dictionary<IlInstruction, Unsupported> _pending = [];
        private readonly HashSet<int> _good = [];
        private readonly HashSet<int> _bad = [];
        private readonly Dictionary<int, long> _lengths = [];
        private Dictionary<int, HashSet<int>> _liveAfter = [];
        private bool _record;

        public Flow(IlMethod method, IlMethodBody body)
        {
            _method = method;
            _body = body;
            _blocks = IlBlock.Split(body);
            _blockOf = new int[body.Instructions.Count];
            for (var b = 0; b < _blocks.Count; b++)
            {
                for (var i = _blocks[b].First; i < _blocks[b].End; i++)
                {
                    _blockOf[i] = b;
                }
            }

            _localCount = body.Locals.Count;
            var thisCount = method.Signature.HasThis ? 1 : 0;
            _isArray =
            [
                .. body.Locals.Select(local => StackArrays.TryMapType(local.Type, method.Assembly, out _)),
                .. Enumerable.Repeat(false, thisCount),
                .. method.Parameters.Select(parameter => StackArrays.TryMapType(parameter.Type, method.Assembly, out _)),
            ];
            _isReference =
            [
                .. body.Locals.Select(local => IsReference(local.Type)),
                .. Enumerable.Repeat(ValueTypes.IsStructInstanceMethod(method), thisCount),
                .. method.Parameters.Select(parameter => IsReference(parameter.Type)),
            ];
        }

        private static bool IsReference(IlType type) => ValueTypes.WithoutModifiers(type) is IlByRefType;

        /// <summary>True for a variable the analysis follows: an array, or a reference that may point into one.</summary>
        private bool IsFollowed(int variable) => _isArray[variable] || _isReference[variable];

        public Dictionary<IlInstruction, Unsupported?> Run()
        {
            var entries = FindEntryStates();
            _liveAfter = FindLiveVariablesAfterNewarr();

            _record = true;
            for (var b = 0; b < _blocks.Count; b++)
            {
                Follow(b, entries[b]!.Clone());
            }

            // a conversion or a field token is fine only where every value it makes ends up where it may be used
            foreach (var (producer, unsupported) in _pending)
            {
                _verdicts[producer] = _good.Contains(producer.Offset) && !_bad.Contains(producer.Offset) ? null : unsupported;
            }

            return _verdicts;
        }

        /// <summary>
        /// What can be where each block starts, from following every way into it until nothing changes. A block
        /// that nothing seen so far leads to starts as the method does (like the translation, which reads the IL
        /// front to back): with an empty stack.
        /// </summary>
        private State?[] FindEntryStates()
        {
            var entries = new State?[_blocks.Count];
            var work = new SortedSet<int>();
            while (Array.FindIndex(entries, e => e is null) is var start and >= 0)
            {
                entries[start] = Start();
                work.Add(start);
                while (work.Count > 0)
                {
                    var b = work.Min;
                    work.Remove(b);
                    var state = entries[b]!.Clone();
                    if (!Follow(b, state))
                    {
                        continue;
                    }

                    foreach (var successor in Successors(b))
                    {
                        if (entries[successor] is not { } entry)
                        {
                            entries[successor] = state.Clone();
                            work.Add(successor);
                        }
                        else if (entry.MergeFrom(state))
                        {
                            work.Add(successor);
                        }
                    }
                }
            }

            return entries;
        }

        private State Start()
        {
            var variables = new ImmutableHashSet<int>[_isArray.Length];
            for (var v = 0; v < variables.Length; v++)
            {
                variables[v] = _isArray[v] && v >= _localCount ? [-1 - (v - _localCount)] : [];
            }

            return new State([], variables);
        }

        private IEnumerable<int> Successors(int b)
        {
            var block = _blocks[b];
            foreach (var target in IlBlock.Targets(block.Last))
            {
                yield return _blockOf[_body.IndexOf(target)];
            }

            if (FallsThrough(block.Last) && block.End < _body.Instructions.Count)
            {
                yield return b + 1;
            }
        }

        private static bool FallsThrough(IlInstruction instruction) => instruction.OpCode is not (IlOpCode.Br
            or IlOpCode.Br_S or IlOpCode.Ret or IlOpCode.Throw or IlOpCode.Rethrow or IlOpCode.Jmp or IlOpCode.Leave
            or IlOpCode.Leave_S or IlOpCode.Endfinally or IlOpCode.Endfilter);

        /// <summary>
        /// For every newarr, the array and reference variables that are live after it: read later, on some path,
        /// before they are written. The array an earlier run of the newarr made (or an element of it) must not be in
        /// one of them.
        /// </summary>
        private Dictionary<int, HashSet<int>> FindLiveVariablesAfterNewarr()
        {
            var liveIn = _blocks.Select(_ => new HashSet<int>()).ToArray();
            var liveOut = _blocks.Select(_ => new HashSet<int>()).ToArray();
            for (var changed = true; changed;)
            {
                changed = false;
                for (var b = _blocks.Count - 1; b >= 0; b--)
                {
                    foreach (var successor in Successors(b))
                    {
                        liveOut[b].UnionWith(liveIn[successor]);
                    }

                    var live = new HashSet<int>(liveOut[b]);
                    for (var i = _blocks[b].End - 1; i >= _blocks[b].First; i--)
                    {
                        Step(live, _body.Instructions[i]);
                    }

                    if (!live.SetEquals(liveIn[b]))
                    {
                        liveIn[b] = live;
                        changed = true;
                    }
                }
            }

            var after = new Dictionary<int, HashSet<int>>();
            for (var b = 0; b < _blocks.Count; b++)
            {
                var live = new HashSet<int>(liveOut[b]);
                for (var i = _blocks[b].End - 1; i >= _blocks[b].First; i--)
                {
                    var instruction = _body.Instructions[i];
                    if (instruction.OpCode == IlOpCode.Newarr)
                    {
                        after[instruction.Offset] = [.. live];
                    }

                    Step(live, instruction);
                }
            }

            return after;

            // from the variables live after the instruction to those live before it
            void Step(HashSet<int> live, IlInstruction instruction)
            {
                if (Variable(instruction) is not var (v, reads) || !IsFollowed(v))
                {
                    return;
                }

                if (reads)
                {
                    live.Add(v);
                }
                else
                {
                    live.Remove(v);
                }
            }
        }

        /// <summary>The variable an instruction reads (ldloc, ldarg and their address forms) or writes (stloc, starg).</summary>
        private (int Variable, bool Reads)? Variable(IlInstruction instruction)
        {
            if (instruction.LocalIndex is { } local && local < _localCount)
            {
                return (local, instruction.OpCode is not (IlOpCode.Stloc or IlOpCode.Stloc_S or IlOpCode.Stloc_0
                    or IlOpCode.Stloc_1 or IlOpCode.Stloc_2 or IlOpCode.Stloc_3));
            }

            if (instruction.ArgumentIndex is { } argument && _localCount + argument < _isArray.Length)
            {
                return (_localCount + argument, instruction.OpCode is not (IlOpCode.Starg or IlOpCode.Starg_S));
            }

            return null;
        }

        /// <summary>Follows block <paramref name="b"/> from <paramref name="state"/>; false when its stack runs out.</summary>
        private bool Follow(int b, State state)
        {
            try
            {
                for (var i = _blocks[b].First; i < _blocks[b].End; i++)
                {
                    Step(_body.Instructions[i], i, state);
                    if (!FallsThrough(_body.Instructions[i]))
                    {
                        break;
                    }
                }

                return true;
            }
            catch (StackUnderflow)
            {
                return false;
            }
        }

        private void Step(IlInstruction instruction, int index, State state)
        {
            var stack = state.Stack;

            Value Pop()
            {
                if (stack.Count == 0)
                {
                    throw new StackUnderflow();
                }

                var value = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                return value;
            }

            switch (instruction.OpCode)
            {
                case >= IlOpCode.Ldc_I4_M1 and <= IlOpCode.Ldc_I4_8:
                    stack.Add(Value.OfConstant((int)instruction.OpCode - (int)IlOpCode.Ldc_I4_0));
                    break;

                case IlOpCode.Ldc_I4_S or IlOpCode.Ldc_I4 when instruction.Operand is IlInt32Operand constant:
                    stack.Add(Value.OfConstant(constant.Value));
                    break;

                case IlOpCode.Ldc_I8 when instruction.Operand is IlInt64Operand constant:
                    stack.Add(Value.OfConstant(constant.Value));
                    break;

                case IlOpCode.Dup:
                    var top = Pop();
                    stack.Add(top);
                    stack.Add(top);
                    break;

                case IlOpCode.Pop:
                    Consumed(Pop(), instruction);
                    break;

                case IlOpCode.Newarr:
                    NewArray(instruction, Pop(), state);
                    break;

                case IlOpCode.Ldlen:
                    Pop();
                    Verdict(instruction, null);
                    stack.Add(Value.Other);
                    break;

                case IlOpCode.Ldelema or (>= IlOpCode.Ldelem_I1 and <= IlOpCode.Ldelem_Ref) or IlOpCode.Ldelem:
                    var index1 = Pop();
                    var array = Pop();
                    Element(instruction, index1);
                    stack.Add(instruction.OpCode == IlOpCode.Ldelema
                        ? Value.Of(Kind.ElementAddress, array.Kind == Kind.Array ? array.Set : [])
                        : Value.Other);
                    break;

                case (>= IlOpCode.Stelem_I and <= IlOpCode.Stelem_Ref) or IlOpCode.Stelem:
                    var element = Pop();
                    Element(instruction, Pop());
                    Pop();
                    if (element.Kind == Kind.Array)
                    {
                        Verdict(instruction, Unsupported.ArrayEscapes);
                    }

                    Consumed(element, instruction);
                    break;

                // through a reference, which may point into an array: what is read or written there stays there
                case (>= IlOpCode.Ldind_I1 and <= IlOpCode.Ldind_Ref) or IlOpCode.Ldobj:
                    Pop();
                    stack.Add(Value.Other);
                    break;

                case (>= IlOpCode.Stind_Ref and <= IlOpCode.Stind_R8) or IlOpCode.Stind_I or IlOpCode.Stobj:
                    Consumed(Pop(), instruction);
                    Pop();
                    break;

                case IlOpCode.Initobj:
                    Pop();
                    break;

                case IlOpCode.Cpobj:
                    Pop();
                    Pop();
                    break;

                case IlOpCode.Ldfld or IlOpCode.Ldflda or IlOpCode.Stfld:
                    InstanceField(instruction, Pop, stack);
                    break;

                case IlOpCode.Conv_Ovf_I or IlOpCode.Conv_Ovf_I_Un:
                    var converted = Pop();
                    if (converted.Kind == Kind.Constant && converted.Constant is >= 0 and <= int.MaxValue)
                    {
                        Verdict(instruction, null);
                        stack.Add(converted);
                    }
                    else
                    {
                        Consumed(converted, instruction);
                        Pending(instruction, Unsupported.CheckedArithmetic);
                        stack.Add(Value.Of(Kind.Index, [instruction.Offset]));
                    }

                    break;

                case IlOpCode.Ldtoken:
                    if (instruction.Operand is IlFieldOperand)
                    {
                        Pending(instruction, Unsupported.TypeHandles);
                        stack.Add(Value.Of(Kind.Token, [instruction.Offset]));
                    }
                    else
                    {
                        Verdict(instruction, Unsupported.TypeHandles);
                        stack.Add(Value.Other);
                    }

                    break;

                case IlOpCode.Call when instruction.Operand is IlMethodOperand { Method: var callee }
                    && StackArrays.IsInitializeArray(callee):
                    var token = Pop();
                    InitializeArray(instruction, index, Pop(), token, callee);
                    break;

                case IlOpCode.Call when instruction.Operand is IlMethodOperand { Method: var callee }
                    && StackArrays.IsEmptyArray(callee, _method.Assembly, out _):
                    Verdict(instruction, null);
                    stack.Add(Value.Of(Kind.Array, []));
                    break;

                default:
                    if (Variable(instruction) is var (variable, reads) && !IsAddress(instruction))
                    {
                        if (reads)
                        {
                            var held = state.Variables[variable];
                            stack.Add(_isArray[variable] ? Value.Of(Kind.Array, held)
                                : _isReference[variable] && !held.IsEmpty ? Value.Of(Kind.ElementAddress, held)
                                : Value.Other);
                        }
                        else
                        {
                            var stored = Pop();
                            state.Variables[variable] = stored.Kind is Kind.Array or Kind.ElementAddress ? stored.Set : [];
                            if (stored.Kind is Kind.Index or Kind.Token)
                            {
                                Consumed(stored, instruction);
                            }
                        }
                    }
                    else
                    {
                        Generally(instruction, Pop, stack);
                    }

                    break;
            }
        }

        private static bool IsAddress(IlInstruction instruction) =>
            instruction.OpCode is IlOpCode.Ldloca or IlOpCode.Ldloca_S or IlOpCode.Ldarga or IlOpCode.Ldarga_S;

        /// <summary>
        /// ldfld, ldflda and stfld: an array kept in a field leaves its method (<see cref="FieldOfArrays"/>); the field
        /// of a struct that is an element of an array is in the array too, so its address is a reference into it.
        /// </summary>
        private void InstanceField(IlInstruction instruction, Func<Value> pop, List<Value> stack)
        {
            FieldOfArrays(instruction);
            var stored = instruction.OpCode == IlOpCode.Stfld ? pop() : Value.Other;
            var owner = pop(); // the struct, or a reference to it
            Consumed(stored, instruction);
            if (instruction.OpCode == IlOpCode.Ldflda)
            {
                stack.Add(owner.Kind == Kind.ElementAddress ? owner : Value.Other);
            }
            else if (instruction.OpCode == IlOpCode.Ldfld)
            {
                stack.Add(Value.Other);
            }
        }

        /// <summary>An instruction on a field that holds arrays: an array there outlives the method that made it.</summary>
        private void FieldOfArrays(IlInstruction instruction)
        {
            if ((instruction.Operand as IlFieldOperand)?.Field.FieldType is { } type
                && StackArrays.TryMapType(type, _method.Assembly, out _))
            {
                Verdict(instruction, Unsupported.ArrayEscapes);
            }
        }

        /// <summary>
        /// Any other instruction: what arrays it may not take, then its effect on the stack. A method that returns a
        /// reference may return one into an array (or an element) it was given, so its result can point into those.
        /// </summary>
        private void Generally(IlInstruction instruction, Func<Value> pop, List<Value> stack)
        {
            var callee = (instruction.Operand as IlMethodOperand)?.Method;
            FieldOfArrays(instruction);
            if (callee?.DeclaringType is IlMultiDimensionalArrayType)
            {
                Verdict(instruction, Unsupported.MultiDimensionalArrays);
            }

            // the return type, the callee's parameter or the library method (not available) says whether it is fine
            var checkedElsewhere = instruction.OpCode == IlOpCode.Ret || callee is not null;
            var given = ImmutableHashSet<int>.Empty;
            var (pops, pushes) = IlStackEffects.Of(instruction, _method);
            for (var i = 0; i < pops; i++)
            {
                var value = pop();
                if (value.Kind == Kind.Array && IsComparison(instruction.OpCode))
                {
                    Verdict(instruction, Unsupported.ArrayComparisons);
                }
                else if (value.Kind is not (Kind.Array or Kind.ElementAddress) || !checkedElsewhere)
                {
                    Consumed(value, instruction);
                }
                else if (instruction.OpCode == IlOpCode.Ret && value.Kind == Kind.ElementAddress
                    && value.Set.Any(origin => origin >= 0))
                {
                    // a ref return into an array this method made (not one it was given)
                    Verdict(instruction, Unsupported.ArrayEscapes);
                }
                else
                {
                    given = given.Union(value.Set);
                }
            }

            var returnsReference = callee is not null && IsReference(callee.ReturnType) && !given.IsEmpty;
            for (var i = 0; i < pushes; i++)
            {
                stack.Add(returnsReference ? Value.Of(Kind.ElementAddress, given) : Value.Other);
            }
        }

        private static bool IsComparison(IlOpCode opCode) => opCode is IlOpCode.Ceq or IlOpCode.Cgt or IlOpCode.Cgt_Un
            or IlOpCode.Clt or IlOpCode.Clt_Un or IlOpCode.Brtrue or IlOpCode.Brtrue_S or IlOpCode.Brfalse
            or IlOpCode.Brfalse_S or (>= IlOpCode.Beq_S and <= IlOpCode.Blt_Un_S) or (>= IlOpCode.Beq and <= IlOpCode.Blt_Un);

        /// <summary>
        /// A value taken by an instruction that has no use for what it is: a converted index or a field token is then
        /// not where it may be used; an array leaves what arrays on the stack allow, and a reference into one is
        /// turned into something else than a reference (a pointer or a number: unsafe code), unless the instruction is
        /// unsupported anyway (and reported as it is).
        /// </summary>
        private void Consumed(Value value, IlInstruction instruction)
        {
            switch (value.Kind)
            {
                case Kind.Index or Kind.Token when _record:
                    _bad.UnionWith(value.Set);
                    break;

                case Kind.Array when SupportedSubset.OpCodes.Contains(instruction.OpCode)
                    && instruction.OpCode is not (IlOpCode.Pop or IlOpCode.Ldlen):
                    Verdict(instruction, Unsupported.ArrayEscapes);
                    break;

                case Kind.ElementAddress when SupportedSubset.OpCodes.Contains(instruction.OpCode)
                    && instruction.OpCode != IlOpCode.Pop:
                    Verdict(instruction, Unsupported.UnsafeCode);
                    break;
            }
        }

        /// <summary>newarr: of an element type arrays support, with a constant size, and not made again while the
        /// one from before is still in use.</summary>
        private void NewArray(IlInstruction instruction, Value length, State state)
        {
            var element = StackArrays.ElementType(instruction);
            if (element is null || !StackArrays.TryMapElementType(element, _method.Assembly, out _))
            {
                Verdict(instruction, element is null ? null : WholeProgramPass.ForArrayElement(element, _method.Assembly));
                Consumed(length, instruction);
                state.Stack.Add(Value.Other);
                return;
            }

            if (length is not { Kind: Kind.Constant, Constant: >= 0 and <= int.MaxValue })
            {
                Verdict(instruction, Unsupported.ArraySizes);
                Consumed(length, instruction);
                state.Stack.Add(Value.Other);
                return;
            }

            var offset = instruction.Offset;
            var live = _liveAfter.GetValueOrDefault(offset) ?? [];
            var inUse = state.Stack.Any(v => v.Kind is Kind.Array or Kind.ElementAddress && v.Set.Contains(offset))
                || live.Any(v => state.Variables[v].Contains(offset));
            Verdict(instruction, inUse ? Unsupported.ArrayReuse : null);
            if (_record)
            {
                _lengths[offset] = length.Constant;
            }

            state.Stack.Add(Value.Of(Kind.Array, length.Constant == 0 ? [] : [offset]));
        }

        /// <summary>ldelem, stelem and ldelema: of an element type arrays support; a converted index is used here.</summary>
        private void Element(IlInstruction instruction, Value index)
        {
            if (index.Kind == Kind.Index)
            {
                if (_record)
                {
                    _good.UnionWith(index.Set);
                }
            }
            else
            {
                Consumed(index, instruction);
            }

            var element = StackArrays.ElementType(instruction);
            if (element is not null && !StackArrays.TryMapElementType(element, _method.Assembly, out _))
            {
                Verdict(instruction, WholeProgramPass.ForArrayElement(element, _method.Assembly));
            }
            else if (SupportedSubset.OpCodes.Contains(instruction.OpCode))
            {
                Verdict(instruction, null);
            }
        }

        /// <summary>
        /// <c>newarr T; dup; ldtoken field; call InitializeArray</c>, as C# fills an array of constants: fine when the
        /// field has the data for every element. When the newarr is not supported, it says why, so this says nothing.
        /// </summary>
        private void InitializeArray(IlInstruction call, int index, Value array, Value token, IlMethodReference callee)
        {
            var instructions = _body.Instructions;
            if (index < 3 || instructions[index - 1] is not { OpCode: IlOpCode.Ldtoken, Operand: IlFieldOperand { Field.Definition: { } field } }
                || instructions[index - 2].OpCode != IlOpCode.Dup || instructions[index - 3] is not { OpCode: IlOpCode.Newarr } newarr
                || _blockOf[index - 3] != _blockOf[index] || token.Kind != Kind.Token)
            {
                Consumed(token, call);
                return;
            }

            if (!_record)
            {
                return;
            }

            var supported = _verdicts.TryGetValue(newarr, out var verdict) && verdict is null;
            var size = StackArrays.ElementType(newarr) is { } element ? ElementSize(element, _method.Assembly) : 0;
            var length = _lengths.GetValueOrDefault(newarr.Offset);
            var filled = array.Kind == Kind.Array && size > 0 && length <= int.MaxValue / size
                && _method.Assembly.ReadFieldData(field, (int)length * size) is not null;
            if (supported && !filled)
            {
                Verdict(call, Unsupported.LibraryMethod(CSharpNames.Method(callee)));
                _bad.UnionWith(token.Set);
                return;
            }

            Verdict(call, null);
            _good.UnionWith(token.Set);
        }

        /// <summary>
        /// The bytes an element of an array initializer's data takes: 0 for nint, whose size the data cannot know, and
        /// for a struct, which C# never fills from data.
        /// </summary>
        private static int ElementSize(IlType element, IlAssembly assembly) =>
            StackArrays.TryMapElementType(element, assembly, out var type)
                ? type.Kind switch
                {
                    TirTypeKind.I8 => 1,
                    TirTypeKind.I16 => 2,
                    TirTypeKind.I32 or TirTypeKind.F32 => 4,
                    TirTypeKind.I64 or TirTypeKind.F64 => 8,
                    _ => 0,
                }
                : 0;

        /// <summary>
        /// A conversion or a field token, whose verdict is <paramref name="unsupported"/> unless every value it makes
        /// is used where it may be (decided at the end of the last pass).
        /// </summary>
        private void Pending(IlInstruction instruction, Unsupported unsupported)
        {
            if (_record)
            {
                _pending[instruction] = unsupported;
            }
        }

        /// <summary>Records a verdict in the last pass; a problem already found at the instruction stays.</summary>
        private void Verdict(IlInstruction instruction, Unsupported? unsupported)
        {
            if (_record && !(_verdicts.TryGetValue(instruction, out var found) && found is not null))
            {
                _verdicts[instruction] = unsupported;
            }
        }
    }
}
