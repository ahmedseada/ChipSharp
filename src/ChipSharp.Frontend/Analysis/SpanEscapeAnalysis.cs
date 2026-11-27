using System.Collections.Immutable;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Checks that the spans and references of one method never outlive the arrays and stackallocs of that method they
/// view. C#'s rules for ref structs keep a span of <c>stackalloc</c> or of a variable in its method, but they take an
/// array to be on the heap: <c>Span&lt;int&gt; s = new int[4]; return s;</c> is fine C#. In ChipSharp that array is on
/// the stack (<see cref="StackArrays"/>), so a span of it obeys the array's rule: it may be kept in locals, indexed,
/// sliced and passed to methods, which may hand it back, but it must not leave the method that made the array, and an
/// array (or a stackalloc) made again in a loop must not be in use through a span from the time before. On a target with
/// a heap, an array whose span does is on the heap instead (<see cref="KeptArrays"/>, see <see cref="HeapArrays"/>),
/// which a span may view from anywhere; a stackalloc never is.
/// </summary>
/// <remarks>
/// It follows, for every value on IL's evaluation stack and every local and argument, which arrays and stackallocs of
/// the method a span, a ref struct, an array or a reference there may view (the IL offsets of their <c>newarr</c> and
/// <c>localloc</c>), and which variables an address may point to: an abstract interpretation of the IL, block by
/// block until nothing changes. A call that returns a span, a ref struct or a reference may return one of what it was
/// given; a call that takes a span (or a ref struct) by reference, or a constructor of one called on its address, may
/// put what it was given there. A reference parameter, and a struct method's <c>this</c>, points outside the method.
/// The verdicts: a <c>ret</c> of a span or reference that may view an array made here, a store of one through a
/// reference that may point outside, and a call that may store one there, are <see cref="Unsupported.SpanOfArrayEscapes"/>;
/// a <c>newarr</c> or <c>localloc</c> that runs again while a live variable (or the stack) may still view what it made
/// before is <see cref="Unsupported.SpanReuse"/>. Arrays on their own are <see cref="ArrayEscapeAnalysis"/>'s.
/// </remarks>
internal static class SpanEscapeAnalysis
{
    /// <summary>What an address that points outside the method may point to: memory of the caller's.</summary>
    private const int Outside = int.MinValue;

    /// <summary>
    /// The problems of <paramref name="method"/>'s spans, by instruction; only the instructions that have one. A span of
    /// an array that leaves the method, or that is in use when its array is made again, puts the array on the heap
    /// (<see cref="KeptArrays"/>): a heap use (<see cref="Unsupported.IsHeapUse"/>); one of a stackalloc is a problem.
    /// </summary>
    public static IReadOnlyDictionary<IlInstruction, Unsupported> Check(IlMethod method) => Analyze(method).Verdicts;

    /// <summary>
    /// The IL offsets of the <c>newarr</c> instructions of <paramref name="method"/> whose arrays a span keeps beyond
    /// what the stack allows, so they are on the heap (see <see cref="HeapArrays"/>): a span of one leaves the method, or
    /// is still in use when the newarr runs again.
    /// </summary>
    public static IReadOnlySet<int> KeptArrays(IlMethod method) => Analyze(method).Kept;

    private static (IReadOnlyDictionary<IlInstruction, Unsupported> Verdicts, IReadOnlySet<int> Kept) Analyze(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        // only a method that makes an array or a stackalloc has something that must not outlive it
        if (method.Body is not { } body || !body.Instructions.Any(i => i.OpCode is IlOpCode.Newarr or IlOpCode.Localloc))
        {
            return (new Dictionary<IlInstruction, Unsupported>(), new HashSet<int>());
        }

        var flow = new Flow(method, body);
        return (flow.Run(), flow.Kept);
    }

    /// <summary>The marker of variable <paramref name="variable"/> (locals first, then the arguments) in a set.</summary>
    private static int Variable(int variable) => -1 - variable;

    private static bool IsOrigin(int item) => item >= 0;

    private static bool IsVariable(int item) => item is < 0 and > Outside;

    /// <summary>The stack and what each variable may view or point to, at one point.</summary>
    private sealed class State(List<ImmutableHashSet<int>> stack, ImmutableHashSet<int>[] variables)
    {
        public List<ImmutableHashSet<int>> Stack { get; } = stack;

        public ImmutableHashSet<int>[] Variables { get; } = variables;

        public State Clone() => new([.. Stack], [.. Variables]);

        public bool MergeFrom(State other)
        {
            var changed = false;
            for (var i = 0; i < Math.Min(Stack.Count, other.Stack.Count); i++)
            {
                var merged = Stack[i].Union(other.Stack[i]);
                changed |= merged.Count != Stack[i].Count;
                Stack[i] = merged;
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

    private sealed class StackUnderflow : Exception;

    private sealed class Flow
    {
        private readonly IlMethod _method;
        private readonly IlMethodBody _body;
        private readonly IReadOnlyList<IlBlock> _blocks;
        private readonly int[] _blockOf;
        private readonly int _localCount;
        private readonly IlType[] _variableTypes;
        private readonly Dictionary<IlInstruction, Unsupported> _verdicts = [];
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
            _variableTypes =
            [
                .. body.Locals.Select(local => local.Type),
                .. method.Signature.HasThis ? [ValueTypes.ThisType(method)] : Array.Empty<IlType>(),
                .. method.Parameters.Select(parameter => parameter.Type),
            ];
        }

        public Dictionary<IlInstruction, Unsupported> Run()
        {
            var entries = FindEntryStates();
            _liveAfter = FindLiveVariablesAfterMakers();
            _record = true;
            for (var b = 0; b < _blocks.Count; b++)
            {
                Follow(b, entries[b]!.Clone());
            }

            return _verdicts;
        }

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

        /// <summary>The state at the start: a reference argument (and a struct method's this) points outside.</summary>
        private State Start()
        {
            var variables = new ImmutableHashSet<int>[_variableTypes.Length];
            for (var v = 0; v < variables.Length; v++)
            {
                variables[v] = v >= _localCount && ValueTypes.WithoutModifiers(_variableTypes[v]) is IlByRefType
                    ? [Outside]
                    : [];
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

        private bool Follow(int b, State state)
        {
            try
            {
                for (var i = _blocks[b].First; i < _blocks[b].End; i++)
                {
                    Step(_body.Instructions[i], state);
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

        /// <summary>The variable an instruction reads, writes or takes the address of, and which of those it does.</summary>
        private (int Variable, Access Access)? VariableOf(IlInstruction instruction)
        {
            var access = instruction.OpCode switch
            {
                IlOpCode.Stloc or IlOpCode.Stloc_S or IlOpCode.Stloc_0 or IlOpCode.Stloc_1 or IlOpCode.Stloc_2
                    or IlOpCode.Stloc_3 or IlOpCode.Starg or IlOpCode.Starg_S => Access.Write,
                IlOpCode.Ldloca or IlOpCode.Ldloca_S or IlOpCode.Ldarga or IlOpCode.Ldarga_S => Access.Address,
                _ => Access.Read,
            };
            if (instruction.LocalIndex is { } local && local < _localCount)
            {
                return (local, access);
            }

            return instruction.ArgumentIndex is { } argument && _localCount + argument < _variableTypes.Length
                ? (_localCount + argument, access)
                : null;
        }

        private void Step(IlInstruction instruction, State state)
        {
            var stack = state.Stack;

            ImmutableHashSet<int> Pop()
            {
                if (stack.Count == 0)
                {
                    throw new StackUnderflow();
                }

                var value = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                return value;
            }

            if (VariableOf(instruction) is var (variable, access))
            {
                switch (access)
                {
                    case Access.Read:
                        stack.Add(state.Variables[variable]);
                        break;
                    case Access.Write:
                        state.Variables[variable] = Pop();
                        break;
                    default:
                        stack.Add([Variable(variable)]);
                        break;
                }

                return;
            }

            switch (instruction.OpCode)
            {
                case IlOpCode.Dup:
                    var top = Pop();
                    stack.Add(top);
                    stack.Add(top);
                    break;

                case IlOpCode.Newarr or IlOpCode.Localloc:
                    Pop();
                    Made(instruction, state);
                    stack.Add([instruction.Offset]);
                    break;

                case IlOpCode.Call or IlOpCode.Callvirt or IlOpCode.Newobj
                    when instruction.Operand is IlMethodOperand { Method: var callee }:
                    Call(instruction, callee, Pop, state);
                    break;

                case IlOpCode.Ldfld:
                    var owner = Pop();
                    stack.Add(IsSpanLike(FieldType(instruction)) ? Contents(owner, state) : []);
                    break;

                case IlOpCode.Ldflda or IlOpCode.Ldelema:
                    if (instruction.OpCode == IlOpCode.Ldelema)
                    {
                        Pop();
                    }

                    stack.Add(Pop());
                    break;

                case IlOpCode.Ldobj:
                    var address = Pop();
                    stack.Add(IsSpanLike(TypeOperand(instruction)) ? Contents(address, state) : []);
                    break;

                case IlOpCode.Stfld or IlOpCode.Stobj or (>= IlOpCode.Stind_Ref and <= IlOpCode.Stind_R8) or IlOpCode.Stind_I:
                    var stored = Pop();
                    Store(instruction, Pop(), Origins(stored), state);
                    break;

                case IlOpCode.Cpobj:
                    var source = Pop();
                    Store(instruction, Pop(), IsSpanLike(TypeOperand(instruction)) ? Contents(source, state) : [], state);
                    break;

                case IlOpCode.Ret:
                    if (!IsVoid(_method.ReturnType))
                    {
                        var returned = Pop();
                        var type = ValueTypes.WithoutModifiers(_method.ReturnType);
                        if ((type is IlByRefType || IsSpanLike(type)) && returned.Any(IsOrigin))
                        {
                            Escapes(instruction, Origins(returned));
                        }
                    }

                    break;

                default:
                    var (pops, pushes) = IlStackEffects.Of(instruction, _method);
                    for (var i = 0; i < pops; i++)
                    {
                        Pop();
                    }

                    for (var i = 0; i < pushes; i++)
                    {
                        stack.Add([]);
                    }

                    break;
            }
        }

        /// <summary>
        /// A call: what it may return (when it returns a span, a ref struct or a reference: what it was given), what it
        /// may store through the references it is given (into a span or ref struct they point to), and whether that may
        /// put a view of this method's arrays outside it.
        /// </summary>
        private void Call(IlInstruction instruction, IlMethodReference callee, Func<ImmutableHashSet<int>> pop, State state)
        {
            var isNew = instruction.OpCode == IlOpCode.Newobj;
            var count = callee.ParameterTypes.Count + (callee.HasThis && !isNew ? 1 : 0);
            var values = new ImmutableHashSet<int>[count];
            for (var i = count - 1; i >= 0; i--)
            {
                values[i] = pop();
            }

            var given = ImmutableHashSet<int>.Empty;
            foreach (var value in values)
            {
                given = given.Union(value).Union(Contents(value, state));
            }

            var origins = Origins(given);
            for (var i = 0; i < count; i++)
            {
                if (!MayStoreInto(callee, i, isNew))
                {
                    continue;
                }

                foreach (var item in values[i])
                {
                    if (IsVariable(item))
                    {
                        Assign(-1 - item, origins, state, weak: true);
                    }
                    else if (item == Outside && !origins.IsEmpty)
                    {
                        Escapes(instruction, origins);
                    }
                }
            }

            var returns = isNew ? callee.DeclaringType : callee.ReturnType;
            if (isNew || !IsVoid(returns))
            {
                var type = ValueTypes.WithoutModifiers(returns);
                state.Stack.Add(type is IlByRefType ? given : IsSpanLike(type) ? origins : []);
            }
        }

        /// <summary>
        /// True when the call may store what it is given through its value at <paramref name="position"/> (from 0,
        /// <c>this</c> first): a reference to a span or ref struct, or the <c>this</c> of a constructor of one, or of a
        /// method of a ref struct (or of an array, whose elements a span may view).
        /// </summary>
        private bool MayStoreInto(IlMethodReference callee, int position, bool isNew)
        {
            var hasThis = callee.HasThis && !isNew;
            if (hasThis && position == 0)
            {
                return IsSpanLike(callee.DeclaringType)
                    && (callee.Name == ".ctor" || callee.Definition is not null);
            }

            var index = position - (hasThis ? 1 : 0);
            return index < callee.ParameterTypes.Count
                && ValueTypes.WithoutModifiers(callee.ParameterTypes[index]) is IlByRefType reference
                && IsSpanLike(reference.ElementType);
        }

        /// <summary>A store of <paramref name="origins"/> through <paramref name="address"/>.</summary>
        private void Store(IlInstruction instruction, ImmutableHashSet<int> address, ImmutableHashSet<int> origins,
            State state)
        {
            foreach (var item in address)
            {
                if (IsVariable(item))
                {
                    Assign(-1 - item, origins, state, weak: true);
                }
                else if (item == Outside && origins.Any(IsOrigin))
                {
                    Escapes(instruction, origins);
                }
            }
        }

        private static void Assign(int variable, ImmutableHashSet<int> origins, State state, bool weak) =>
            state.Variables[variable] = weak ? state.Variables[variable].Union(origins) : origins;

        /// <summary>What the variables an address may point to may view, and what it views itself.</summary>
        private static ImmutableHashSet<int> Contents(ImmutableHashSet<int> value, State state)
        {
            var contents = Origins(value);
            foreach (var item in value.Where(IsVariable))
            {
                contents = contents.Union(Origins(state.Variables[-1 - item]));
            }

            return contents;
        }

        private static ImmutableHashSet<int> Origins(ImmutableHashSet<int> value) =>
            value.All(IsOrigin) ? value : [.. value.Where(IsOrigin)];

        /// <summary>
        /// A newarr or localloc: in use when it runs again if the stack, or a variable that is live after it, may still
        /// view what it made before.
        /// </summary>
        private void Made(IlInstruction instruction, State state)
        {
            if (!_record)
            {
                return;
            }

            var offset = instruction.Offset;
            var live = _liveAfter.GetValueOrDefault(offset) ?? [];
            if (state.Stack.Any(value => value.Contains(offset))
                || live.Any(v => state.Variables[v].Contains(offset)))
            {
                MadeAgain(instruction);
            }
        }

        /// <summary>For every newarr and localloc, the variables live after it: read later, on some path, before they are written.</summary>
        private Dictionary<int, HashSet<int>> FindLiveVariablesAfterMakers()
        {
            var liveIn = _blocks.Select(_ => new HashSet<int>()).ToArray();
            for (var changed = true; changed;)
            {
                changed = false;
                for (var b = _blocks.Count - 1; b >= 0; b--)
                {
                    var live = new HashSet<int>();
                    foreach (var successor in Successors(b))
                    {
                        live.UnionWith(liveIn[successor]);
                    }

                    for (var i = _blocks[b].End - 1; i >= _blocks[b].First; i--)
                    {
                        StepBack(live, i);
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
                var live = new HashSet<int>();
                foreach (var successor in Successors(b))
                {
                    live.UnionWith(liveIn[successor]);
                }

                for (var i = _blocks[b].End - 1; i >= _blocks[b].First; i--)
                {
                    var instruction = _body.Instructions[i];
                    if (instruction.OpCode is IlOpCode.Newarr or IlOpCode.Localloc)
                    {
                        after[instruction.Offset] = [.. live];
                    }

                    StepBack(live, i);
                }
            }

            return after;

            // a variable whose address a constructor or initobj is given is written there, not read
            void StepBack(HashSet<int> live, int index)
            {
                if (VariableOf(_body.Instructions[index]) is var (v, access))
                {
                    if (access == Access.Write || (access == Access.Address && IsInitialized(index)))
                    {
                        live.Remove(v);
                    }
                    else
                    {
                        live.Add(v);
                    }
                }
            }
        }

        /// <summary>
        /// True when the address that the ldloca or ldarga at <paramref name="index"/> pushes is taken next, in its
        /// block, by a constructor called on it (as <c>this</c>) or by initobj: the variable is written there.
        /// </summary>
        private bool IsInitialized(int index)
        {
            var depth = 1;
            for (var i = index + 1; i < _blocks[_blockOf[index]].End; i++)
            {
                var instruction = _body.Instructions[i];
                var (pops, pushes) = IlStackEffects.Of(instruction, _method);
                if (pops >= depth)
                {
                    return pops == depth && (instruction.OpCode == IlOpCode.Initobj
                        || instruction is { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: { Name: ".ctor", HasThis: true } } });
                }

                depth += pushes - pops;
            }

            return false;
        }

        private bool IsSpanLike(IlType type) => Spans.IsSpanLike(type, _method.Assembly)
            || (ValueTypes.WithoutModifiers(type) is IlByRefType reference && Spans.IsSpanLike(reference.ElementType, _method.Assembly));

        private static IlType FieldType(IlInstruction instruction) =>
            (instruction.Operand as IlFieldOperand)?.Field.FieldType ?? IlType.Void;

        private static IlType TypeOperand(IlInstruction instruction) =>
            (instruction.Operand as IlTypeOperand)?.Type ?? IlType.Void;

        private static bool IsVoid(IlType type) => ValueTypes.WithoutModifiers(type) == IlType.Void;

        /// <summary>
        /// Records a verdict in the last pass; the first problem found at an instruction stays, but for a heap use, which
        /// a problem that is not one takes the place of.
        /// </summary>
        private void Verdict(IlInstruction instruction, Unsupported unsupported)
        {
            if (_record && (!_verdicts.TryGetValue(instruction, out var found) || (found.IsHeapUse && !unsupported.IsHeapUse)))
            {
                _verdicts[instruction] = unsupported;
            }
        }

        /// <summary>The newarr instructions whose arrays are on the heap because of a span (see <see cref="KeptArrays"/>).</summary>
        public HashSet<int> Kept { get; } = [];

        private bool IsNewarr(int origin) => _body.Instructions[_body.IndexOf(origin)].OpCode == IlOpCode.Newarr;

        /// <summary>
        /// A span or reference that may view <paramref name="origins"/> leaves the method at <paramref name="instruction"/>:
        /// an array made here is then on the heap (a heap use), a stackalloc cannot be.
        /// </summary>
        private void Escapes(IlInstruction instruction, ImmutableHashSet<int> origins)
        {
            var made = origins.Where(IsOrigin).ToList();
            if (made.All(IsNewarr))
            {
                Verdict(instruction, Unsupported.HeapUse(Unsupported.SpanOfArrayEscapes));
                if (_record)
                {
                    Kept.UnionWith(made);
                }
            }
            else
            {
                Verdict(instruction, Unsupported.SpanOfArrayEscapes);
            }
        }

        /// <summary>A newarr or localloc runs again while a span of what it made before may be in use: a newarr's array is then on the heap.</summary>
        private void MadeAgain(IlInstruction instruction)
        {
            if (instruction.OpCode != IlOpCode.Newarr)
            {
                Verdict(instruction, Unsupported.SpanReuse);
                return;
            }

            Verdict(instruction, Unsupported.HeapUse(Unsupported.SpanReuse));
            if (_record)
            {
                Kept.Add(instruction.Offset);
            }
        }
    }

    private enum Access
    {
        Read,
        Write,
        Address,
    }
}
