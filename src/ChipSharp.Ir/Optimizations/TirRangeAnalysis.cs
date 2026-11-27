namespace ChipSharp.Ir.Optimizations;

/// <summary>
/// Value-range analysis of a whole module: for every integer temporary, parameter and slot, an interval
/// (<see cref="TirRange"/>) that holds every value it can have when the program runs.
/// </summary>
/// <remarks>
/// <para>
/// Within a function it is abstract interpretation over the blocks: the state at the start of each block gives each
/// integer slot (one whose address is never taken, so only loads and stores reach it) and each integer parameter a
/// range; every slot starts at 0. Constants, the small integers loads and conversions make (a <c>byte</c> widened to
/// int is [0, 255]), masks (<c>&amp; 0xFF</c>), shifts, divisions and remainders, array and span lengths (at most
/// <see cref="TirNarrowingOptions.MaxLength"/>), elements of constant tables and the branches taken all narrow the
/// ranges: on the edge where <c>i &lt; 7</c> is true, the slot <c>i</c> was loaded from holds at most 6. A loop is
/// followed until nothing changes; a range that keeps growing jumps to the next of the function's own constants (or to
/// the edge of a byte, a short or an int, then of its type), so it ends; two passes without that then take back what
/// the jump gave away. <c>for (int i = 0; i &lt; 7; i++)</c> gives i [0, 7]: 7 where the loop ends.
/// </para>
/// <para>
/// Across functions, a function only the program calls (not the entry point, not exported, never reached through an
/// address: no <see cref="TirFunctionAddress"/>, no method table) gets the ranges its callers pass, and every caller
/// gets the range its callee returns. This starts from "any value" and is repeated a few times: each round assumes only
/// what the round before proved, so every round's result holds.
/// </para>
/// <para>
/// A block the analysis finds is never reached is given every value of each type instead, so that what is said of its
/// temporaries holds even then.
/// </para>
/// </remarks>
public sealed class TirRangeAnalysis
{
    private const int MaxRounds = 6;

    private readonly TirNarrowingOptions _options;
    private readonly Dictionary<TirValue, TirRange> _values = [];
    private readonly Dictionary<TirSlot, TirRange> _slots = [];
    private readonly HashSet<TirBlock> _reached = [];

    private TirRangeAnalysis(TirNarrowingOptions options) => _options = options;

    /// <summary>Analyzes every defined function of <paramref name="module"/>.</summary>
    public static TirRangeAnalysis Run(TirModule module, TirNarrowingOptions options)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(options);

        var defined = module.Functions.Where(f => f is { Kind: TirFunctionKind.Defined, EntryBlock: not null }).ToList();
        var open = OpenFunctions(module);
        var summaries = Summaries.Any(defined);
        List<FunctionResult> results;
        for (var round = 1; ; round++)
        {
            var assumed = summaries;
            results = [.. defined.Select(function => new FunctionAnalysis(function, options, assumed).Run())];
            var next = Summaries.From(results, defined, open);
            if (next.SameAs(summaries) || round == MaxRounds)
            {
                break;
            }

            summaries = next;
        }

        var analysis = new TirRangeAnalysis(options);
        foreach (var result in results)
        {
            foreach (var parameter in result.Function.Parameters.Where(p => TirRange.BitsOf(p.Type) > 0))
            {
                analysis._values[parameter] = summaries.Parameter(parameter);
            }

            foreach (var (value, range) in result.Values)
            {
                analysis._values[value] = range;
            }

            foreach (var (slot, range) in result.Slots)
            {
                analysis._slots[slot] = range;
            }

            analysis._reached.UnionWith(result.Reached);
        }

        return analysis;
    }

    /// <summary>
    /// The values an integer temporary or parameter can have; every value of its type for anything the analysis did not
    /// see. (A temporary of a block that is never reached has every value its operations allow.)
    /// </summary>
    public TirRange RangeOf(TirValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _values.TryGetValue(value, out var range) ? range : TirRange.Any(value.Type);
    }

    /// <summary>Every value a tracked slot (<see cref="IsTracked"/>) ever holds, its starting 0 included; every value of its type for another.</summary>
    public TirRange RangeOf(TirSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return _slots.TryGetValue(slot, out var range) ? range : TirRange.Any(slot.Type);
    }

    /// <summary>True for an integer slot whose address is never taken: only its loads and stores reach it, and the analysis follows them.</summary>
    public bool IsTracked(TirSlot slot) => _slots.ContainsKey(slot);

    /// <summary>True when the analysis found that control can reach <paramref name="block"/>.</summary>
    public bool IsReached(TirBlock block) => _reached.Contains(block);

    /// <summary>
    /// The functions whose callers are not all calls of the module: the entry point, the exported ones, and those whose
    /// address is taken (<see cref="TirFunctionAddress"/>) or that a method table holds.
    /// </summary>
    private static HashSet<TirFunction> OpenFunctions(TirModule module)
    {
        var open = new HashSet<TirFunction>();
        if (module.EntryPoint is { } entryPoint)
        {
            open.Add(entryPoint);
        }

        open.UnionWith(module.Functions.Where(f => f.ExportName is not null));
        foreach (var descriptor in module.TypeDescriptors)
        {
            open.UnionWith(descriptor.VirtualMethods.OfType<TirFunction>());
            open.UnionWith(descriptor.Interfaces.SelectMany(map => map.Methods).OfType<TirFunction>());
        }

        open.UnionWith(module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
            .OfType<TirFunctionAddress>().Select(address => address.Function));
        return open;
    }

    /// <summary>What one function's analysis found.</summary>
    private sealed record FunctionResult(
        TirFunction Function,
        Dictionary<TirValue, TirRange> Values,
        Dictionary<TirSlot, TirRange> Slots,
        HashSet<TirBlock> Reached,
        TirRange Returned,
        List<(TirFunction Callee, TirRange[] Arguments)> Calls);

    /// <summary>What a round assumes of each function: the ranges of its parameters and of what it returns.</summary>
    private sealed class Summaries
    {
        private readonly Dictionary<TirFunction, TirRange[]> _parameters = [];
        private readonly Dictionary<TirFunction, TirRange> _returns = [];

        /// <summary>Nothing known: every value of each type.</summary>
        public static Summaries Any(IEnumerable<TirFunction> functions)
        {
            var summaries = new Summaries();
            foreach (var function in functions)
            {
                summaries._parameters[function] = [.. function.Parameters.Select(p => Full(p.Type))];
                summaries._returns[function] = Full(function.ReturnType);
            }

            return summaries;
        }

        /// <summary>What the results of a round prove: an open function's parameters may still be anything.</summary>
        public static Summaries From(IReadOnlyList<FunctionResult> results, IEnumerable<TirFunction> functions,
            HashSet<TirFunction> open)
        {
            var summaries = new Summaries();
            var passed = new Dictionary<TirFunction, TirRange[]>();
            foreach (var (callee, arguments) in results.SelectMany(result => result.Calls))
            {
                if (passed.TryGetValue(callee, out var ranges))
                {
                    for (var i = 0; i < ranges.Length; i++)
                    {
                        ranges[i] = ranges[i].Join(arguments[i]);
                    }
                }
                else
                {
                    passed[callee] = [.. arguments];
                }
            }

            foreach (var function in functions)
            {
                summaries._parameters[function] = !open.Contains(function) && passed.TryGetValue(function, out var ranges)
                    ? ranges
                    : [.. function.Parameters.Select(p => Full(p.Type))];
            }

            foreach (var result in results)
            {
                summaries._returns[result.Function] = result.Returned;
            }

            return summaries;
        }

        public TirRange Parameter(TirParameter parameter) =>
            _parameters.TryGetValue(parameter.Function, out var ranges) ? ranges[parameter.Index] : Full(parameter.Type);

        public TirRange Returned(TirFunction function) =>
            _returns.TryGetValue(function, out var range) ? range : Full(function.ReturnType);

        public bool SameAs(Summaries other) =>
            _parameters.Count == other._parameters.Count
            && _parameters.All(pair => other._parameters.TryGetValue(pair.Key, out var ranges) && ranges.SequenceEqual(pair.Value))
            && _returns.All(pair => other._returns.TryGetValue(pair.Key, out var range) && range == pair.Value);

        private static TirRange Full(TirType type) => TirRange.Any(type);
    }

    /// <summary>The analysis of one function, under what a round assumes of the others.</summary>
    private sealed class FunctionAnalysis
    {
        /// <summary>How many times a block's starting state may grow before its ranges jump (widening).</summary>
        private const int WidenAfter = 2;

        /// <summary>The passes after the ranges stop growing, which take back what the jumps gave away.</summary>
        private const int NarrowingPasses = 2;

        private readonly TirFunction _function;
        private readonly TirNarrowingOptions _options;
        private readonly Summaries _summaries;
        private readonly int _slotCount;

        /// <summary>For each place of a state (the slots, then the parameters): its width in bits, 0 for one not followed.</summary>
        private readonly int[] _bits;

        private readonly long[] _thresholds;
        private readonly List<TirBlock> _order;
        private readonly Dictionary<TirBlock, int> _rank = [];
        private readonly Dictionary<TirBlock, Dictionary<TirValue, List<int>>> _endSlots = [];

        public FunctionAnalysis(TirFunction function, TirNarrowingOptions options, Summaries summaries)
        {
            _function = function;
            _options = options;
            _summaries = summaries;
            _slotCount = function.Slots.Count;

            var instructions = function.Blocks.SelectMany(block => block.Instructions).ToList();
            var addressed = instructions.OfType<TirSlotAddress>().Select(address => address.Slot).ToHashSet();
            _bits =
            [
                .. function.Slots.Select(slot => addressed.Contains(slot) ? 0 : TirRange.BitsOf(slot.Type)),
                .. function.Parameters.Select(parameter => TirRange.BitsOf(parameter.Type)),
            ];
            _thresholds = Thresholds(instructions, options);
            _order = ReversePostorder(function.EntryBlock!);
            for (var i = 0; i < _order.Count; i++)
            {
                _rank[_order[i]] = i;
            }
        }

        public FunctionResult Run()
        {
            var initial = Initial();
            var entries = Ascend(initial);
            if (entries is null)
            {
                // the ranges did not settle: assume every value in every block, which holds
                return Record(_function.Blocks.ToDictionary(block => block, _ => Any()));
            }

            for (var pass = 0; pass < NarrowingPasses; pass++)
            {
                entries = Descend(entries, initial);
            }

            return Record(entries);
        }

        private bool IsFollowed(int place) => _bits[place] > 0;

        /// <summary>The state where the function starts: every slot 0, every parameter what its callers pass.</summary>
        private TirRange[] Initial()
        {
            var state = new TirRange[_bits.Length];
            for (var i = 0; i < _slotCount; i++)
            {
                state[i] = TirRange.Of(0);
            }

            foreach (var parameter in _function.Parameters)
            {
                state[_slotCount + parameter.Index] = IsFollowed(_slotCount + parameter.Index)
                    ? _summaries.Parameter(parameter)
                    : TirRange.Of(0);
            }

            return state;
        }

        /// <summary>A state that holds anywhere: every value of each slot's type.</summary>
        private TirRange[] Any()
        {
            var state = Initial();
            for (var i = 0; i < _slotCount; i++)
            {
                if (IsFollowed(i))
                {
                    state[i] = TirRange.Signed(_bits[i]);
                }
            }

            return state;
        }

        /// <summary>The states where blocks start, grown until they hold; null when they do not settle.</summary>
        private Dictionary<TirBlock, TirRange[]>? Ascend(TirRange[] initial)
        {
            var entries = new Dictionary<TirBlock, TirRange[]> { [_order[0]] = initial };
            var growth = new Dictionary<TirBlock, int>();
            var work = new SortedSet<int> { 0 };
            var limit = 1000 + (100 * _order.Count);
            for (var steps = 0; work.Count > 0; steps++)
            {
                if (steps > limit)
                {
                    return null;
                }

                var block = _order[work.Min];
                work.Remove(work.Min);
                foreach (var (target, state) in Transfer(block, entries[block], [], null, null).Edges)
                {
                    if (!entries.TryGetValue(target, out var old))
                    {
                        entries[target] = state;
                        work.Add(_rank[target]);
                        continue;
                    }

                    var joined = Join(old, state);
                    if (joined.SequenceEqual(old))
                    {
                        continue;
                    }

                    growth[target] = growth.GetValueOrDefault(target) + 1;
                    entries[target] = growth[target] > WidenAfter ? Widen(old, joined) : joined;
                    work.Add(_rank[target]);
                }
            }

            return entries;
        }

        /// <summary>One pass that recomputes each block's start from its predecessors, without widening (it can only shrink).</summary>
        private Dictionary<TirBlock, TirRange[]> Descend(Dictionary<TirBlock, TirRange[]> entries, TirRange[] initial)
        {
            var incoming = new Dictionary<TirBlock, TirRange[]> { [_order[0]] = initial };
            foreach (var block in _order.Where(entries.ContainsKey))
            {
                foreach (var (target, state) in Transfer(block, entries[block], [], null, null).Edges)
                {
                    incoming[target] = incoming.TryGetValue(target, out var other) ? Join(other, state) : state;
                }
            }

            return incoming;
        }

        /// <summary>The ranges of every temporary and slot, with each block's settled start (any value where it is never reached).</summary>
        private FunctionResult Record(Dictionary<TirBlock, TirRange[]> entries)
        {
            var values = new Dictionary<TirValue, TirRange>();
            var slots = new TirRange[_slotCount];
            for (var i = 0; i < _slotCount; i++)
            {
                slots[i] = TirRange.Of(0);
            }

            var returned = TirRange.Empty;
            var calls = new List<(TirFunction, TirRange[])>();
            foreach (var block in _function.Blocks)
            {
                var reached = entries.TryGetValue(block, out var entry);
                var result = Transfer(block, entry ?? Any(), values,
                    (slot, range) => slots[slot.Index] = slots[slot.Index].Join(range), reached ? calls : null);
                if (reached)
                {
                    returned = returned.Join(result.Returned);
                }
            }

            var followed = new Dictionary<TirSlot, TirRange>();
            foreach (var slot in _function.Slots.Where(slot => IsFollowed(slot.Index)))
            {
                followed[slot] = slots[slot.Index];
            }

            return new FunctionResult(_function, values, followed, [.. entries.Keys], returned, calls);
        }

        /// <summary>
        /// Runs a block from the state it starts with: the range of each integer temporary goes into
        /// <paramref name="values"/>, each store of a followed slot to <paramref name="stored"/>, each call (callee and
        /// argument ranges) into <paramref name="calls"/>. Returns where control goes next, with the state on each way
        /// (none for a way that cannot be taken), and the range of the value returned.
        /// </summary>
        private (List<(TirBlock Target, TirRange[] State)> Edges, TirRange Returned) Transfer(TirBlock block,
            TirRange[] entry, Dictionary<TirValue, TirRange> values, Action<TirSlot, TirRange>? stored,
            List<(TirFunction, TirRange[])>? calls)
        {
            var state = (TirRange[])entry.Clone();
            var edges = new List<(TirBlock, TirRange[])>();
            var returned = TirRange.Empty;
            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case TirStore store when IsFollowed(store.Slot.Index):
                        var range = Range(store.Value, state, values);
                        state[store.Slot.Index] = range;
                        stored?.Invoke(store.Slot, range);
                        break;

                    case TirCall call when calls is not null:
                        calls.Add((call.Callee, [.. call.Arguments.Select(argument => TirRange.BitsOf(argument.Type) > 0
                            ? Range(argument, state, values)
                            : TirRange.Any(argument.Type))]));
                        break;

                    case TirReturn { Value: { } value } when TirRange.BitsOf(value.Type) > 0:
                        returned = Range(value, state, values);
                        break;

                    case TirJump jump:
                        edges.Add((jump.Target, state));
                        break;

                    case TirBranch branch:
                        if (Assume(state, branch.Condition, true, block, values) is { } whenTrue)
                        {
                            edges.Add((branch.WhenTrue, whenTrue));
                        }

                        if (Assume(state, branch.Condition, false, block, values) is { } whenFalse)
                        {
                            edges.Add((branch.WhenFalse, whenFalse));
                        }

                        break;
                }

                if (TirRange.BitsOf(instruction.Type) > 0)
                {
                    values[instruction] = Compute(instruction, state, values);
                }
            }

            return (edges, returned);
        }

        /// <summary>The range of the result of an integer instruction, in the state just before it.</summary>
        private TirRange Compute(TirInstruction instruction, TirRange[] state, Dictionary<TirValue, TirRange> values)
        {
            var bits = TirRange.BitsOf(instruction.Type);
            switch (instruction)
            {
                case TirConst constant:
                    return TirRange.Of(constant.IntegerValue);

                case TirZero:
                    return TirRange.Of(0);

                case TirBinary binary:
                    return TirRange.Binary(binary.Op, Range(binary.Left, state, values), Range(binary.Right, state, values), bits);

                case TirConvert { Value.Type: var from } convert:
                    return TirRange.BitsOf(from) > 0
                        ? TirRange.Convert(Range(convert.Value, state, values), TirRange.BitsOf(from), bits, convert.IsUnsigned)
                        : from == TirType.Bool ? TirRange.Of(0, 1) : TirRange.Signed(bits);

                case TirLoad load when IsFollowed(load.Slot.Index):
                    return state[load.Slot.Index];

                case TirCall call:
                    return _summaries.Returned(call.Callee);

                case TirPointerLoad load:
                    return Loaded(load) ?? TirRange.Signed(bits);

                case TirArrayLength:
                    return TirRange.Of(0, Math.Min(_options.MaxLength, int.MaxValue));

                default:
                    return TirRange.Signed(bits);
            }
        }

        /// <summary>
        /// What a load from memory reads, where the address shows it: an array's or span's length, or an element of a
        /// constant table (one of its values).
        /// </summary>
        private TirRange? Loaded(TirPointerLoad load)
        {
            if (load.IsVolatile)
            {
                return null;
            }

            if (load.Address is TirFieldAddress field && _options.LengthFields.Contains((field.StructType, field.FieldIndex)))
            {
                return TirRange.Of(0, Math.Min(_options.MaxLength, (1L << (TirRange.BitsOf(load.Type) - 1)) - 1));
            }

            if (load.Address is TirElementAddress
                {
                    Address: TirGlobalAddress { Global: { IsConstant: true, Elements: { Count: > 0 } elements, Type: TirArrayType table } },
                } element
                && element.ElementType == load.Type && table.Element == load.Type)
            {
                return TirRange.Of(elements.Min(e => e.IntegerValue), elements.Max(e => e.IntegerValue));
            }

            return null;
        }

        private TirRange Range(TirValue value, TirRange[] state, Dictionary<TirValue, TirRange> values) => value switch
        {
            TirParameter parameter when IsFollowed(_slotCount + parameter.Index) => state[_slotCount + parameter.Index],
            _ when values.TryGetValue(value, out var range) => range,
            _ => TirRange.Any(value.Type),
        };

        /// <summary>
        /// The state on the way a branch takes when its condition is <paramref name="truth"/>: a comparison of integers
        /// in the block narrows what was compared (a parameter, the slots that hold the value at the end of the block,
        /// and what a widening conversion was made from); null when that way cannot be taken.
        /// </summary>
        private TirRange[]? Assume(TirRange[] state, TirValue condition, bool truth, TirBlock block,
            Dictionary<TirValue, TirRange> values)
        {
            switch (condition)
            {
                case TirConst { Type.Kind: TirTypeKind.Bool } constant:
                    return (constant.IntegerValue != 0) == truth ? state : null;

                case TirCompare compare when compare.Block == block && TirRange.BitsOf(compare.Left.Type) > 0:
                    var op = truth ? compare.Op : Negate(compare.Op);
                    var (left, right) = Refine(op, Range(compare.Left, state, values), Range(compare.Right, state, values));
                    if (left.IsEmpty || right.IsEmpty)
                    {
                        return null;
                    }

                    var refined = (TirRange[])state.Clone();
                    return Narrow(refined, compare.Left, left, block) && Narrow(refined, compare.Right, right, block)
                        ? refined
                        : null;

                default:
                    return state;
            }
        }

        /// <summary>Keeps only <paramref name="range"/> for everything that holds <paramref name="value"/>; false when that leaves nothing.</summary>
        private bool Narrow(TirRange[] state, TirValue value, TirRange range, TirBlock block)
        {
            if (value is TirParameter parameter && IsFollowed(_slotCount + parameter.Index))
            {
                var place = _slotCount + parameter.Index;
                state[place] = state[place].Intersect(range);
                return !state[place].IsEmpty;
            }

            if (value is not TirInstruction instruction || instruction.Block != block)
            {
                return true;
            }

            foreach (var place in EndSlots(block).GetValueOrDefault(value) ?? [])
            {
                state[place] = state[place].Intersect(range);
                if (state[place].IsEmpty)
                {
                    return false;
                }
            }

            if (value is TirConvert { Value.Type.IsInteger: true } convert
                && TirRange.BitsOf(convert.Value.Type) is var from && from > 0 && from < TirRange.BitsOf(convert.Type))
            {
                var source = convert.IsUnsigned ? BeforeZeroExtension(range, from) : range.Intersect(TirRange.Signed(from));
                return !source.IsEmpty && Narrow(state, convert.Value, source, block);
            }

            return true;
        }

        /// <summary>
        /// The followed slots whose value at the end of the block each temporary is: the last value stored in the slot,
        /// and the loads of it after that store (all of them when there is none).
        /// </summary>
        private Dictionary<TirValue, List<int>> EndSlots(TirBlock block)
        {
            if (_endSlots.TryGetValue(block, out var known))
            {
                return known;
            }

            var lastStore = new Dictionary<int, int>();
            var instructions = block.Instructions;
            for (var i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] is TirStore store && IsFollowed(store.Slot.Index))
                {
                    lastStore[store.Slot.Index] = i;
                }
            }

            var holders = new Dictionary<TirValue, List<int>>();
            void Add(TirValue value, int place)
            {
                if (!holders.TryGetValue(value, out var places))
                {
                    holders[value] = places = [];
                }

                places.Add(place);
            }

            foreach (var (place, at) in lastStore)
            {
                Add(((TirStore)instructions[at]).Value, place);
            }

            for (var i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] is TirLoad load && IsFollowed(load.Slot.Index)
                    && (!lastStore.TryGetValue(load.Slot.Index, out var at) || i > at))
                {
                    Add(load, load.Slot.Index);
                }
            }

            _endSlots[block] = holders;
            return holders;
        }

        private TirRange[] Join(TirRange[] a, TirRange[] b)
        {
            var joined = new TirRange[a.Length];
            for (var i = 0; i < a.Length; i++)
            {
                joined[i] = a[i].Join(b[i]);
            }

            return joined;
        }

        /// <summary>The joined state, with each range that grew past the old one moved out to the next threshold.</summary>
        private TirRange[] Widen(TirRange[] old, TirRange[] joined)
        {
            var widened = (TirRange[])joined.Clone();
            for (var i = 0; i < joined.Length; i++)
            {
                if (!IsFollowed(i) || old[i].IsEmpty || joined[i].IsEmpty)
                {
                    continue;
                }

                var full = TirRange.Signed(_bits[i]);
                var min = joined[i].Min < old[i].Min ? Math.Max(Below(joined[i].Min), full.Min) : joined[i].Min;
                var max = joined[i].Max > old[i].Max ? Math.Min(Above(joined[i].Max), full.Max) : joined[i].Max;
                widened[i] = TirRange.Of(min, max);
            }

            return widened;
        }

        /// <summary>The largest threshold no greater than <paramref name="value"/>.</summary>
        private long Below(long value)
        {
            var i = Array.BinarySearch(_thresholds, value);
            return i >= 0 ? _thresholds[i] : ~i > 0 ? _thresholds[~i - 1] : long.MinValue;
        }

        /// <summary>The smallest threshold no less than <paramref name="value"/>.</summary>
        private long Above(long value)
        {
            var i = Array.BinarySearch(_thresholds, value);
            return i >= 0 ? _thresholds[i] : ~i < _thresholds.Length ? _thresholds[~i] : long.MaxValue;
        }

        /// <summary>The function's integer constants and their neighbours, and the edges of 8-, 16- and 32-bit integers.</summary>
        private static long[] Thresholds(IEnumerable<TirInstruction> instructions, TirNarrowingOptions options)
        {
            var thresholds = new SortedSet<long> { options.MaxLength, options.MaxLength - 1 };
            foreach (var bits in new[] { 8, 16, 32 })
            {
                thresholds.Add(TirRange.Signed(bits).Min);
                thresholds.Add(TirRange.Signed(bits).Max);
                thresholds.Add(TirRange.Unsigned(bits).Max);
            }

            foreach (var constant in instructions.OfType<TirConst>().Where(c => c.Type.IsInteger))
            {
                var value = constant.IntegerValue;
                thresholds.Add(value);
                if (value > long.MinValue)
                {
                    thresholds.Add(value - 1);
                }

                if (value < long.MaxValue)
                {
                    thresholds.Add(value + 1);
                }
            }

            return [.. thresholds];
        }

        private static List<TirBlock> ReversePostorder(TirBlock entry)
        {
            var visited = new HashSet<TirBlock> { entry };
            var postorder = new List<TirBlock>();
            var stack = new Stack<(TirBlock Block, int Next)>();
            stack.Push((entry, 0));
            while (stack.Count > 0)
            {
                var (block, next) = stack.Pop();
                var targets = block.Terminator?.Targets ?? [];
                if (next < targets.Count)
                {
                    stack.Push((block, next + 1));
                    if (visited.Add(targets[next]))
                    {
                        stack.Push((targets[next], 0));
                    }
                }
                else
                {
                    postorder.Add(block);
                }
            }

            postorder.Reverse();
            return postorder;
        }
    }

    /// <summary>The comparison that holds when <paramref name="op"/> does not.</summary>
    internal static TirCompareOp Negate(TirCompareOp op) => op switch
    {
        TirCompareOp.Eq => TirCompareOp.Ne,
        TirCompareOp.Ne => TirCompareOp.Eq,
        TirCompareOp.Lt => TirCompareOp.Ge,
        TirCompareOp.Ge => TirCompareOp.Lt,
        TirCompareOp.Le => TirCompareOp.Gt,
        TirCompareOp.Gt => TirCompareOp.Le,
        TirCompareOp.LtUnsigned => TirCompareOp.GeUnsigned,
        TirCompareOp.GeUnsigned => TirCompareOp.LtUnsigned,
        TirCompareOp.LeUnsigned => TirCompareOp.GtUnsigned,
        _ => TirCompareOp.LeUnsigned,
    };

    /// <summary>
    /// What is left of the two operands' ranges where <c>left op right</c> holds; an empty one when it never does. The
    /// unsigned comparisons narrow as the signed ones when neither side can be negative, and otherwise only a side compared
    /// with one that cannot be: <c>(uint)i &lt; (uint)length</c> puts i in [0, length - 1].
    /// </summary>
    internal static (TirRange Left, TirRange Right) Refine(TirCompareOp op, TirRange left, TirRange right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return (TirRange.Empty, TirRange.Empty);
        }

        if (op is TirCompareOp.LtUnsigned or TirCompareOp.LeUnsigned or TirCompareOp.GtUnsigned or TirCompareOp.GeUnsigned
            && left.Min >= 0 && right.Min >= 0)
        {
            op = op switch
            {
                TirCompareOp.LtUnsigned => TirCompareOp.Lt,
                TirCompareOp.LeUnsigned => TirCompareOp.Le,
                TirCompareOp.GtUnsigned => TirCompareOp.Gt,
                _ => TirCompareOp.Ge,
            };
        }

        return op switch
        {
            TirCompareOp.Lt => (left.Intersect(AtMost((Int128)right.Max - 1)), right.Intersect(AtLeast((Int128)left.Min + 1))),
            TirCompareOp.Le => (left.Intersect(AtMost(right.Max)), right.Intersect(AtLeast(left.Min))),
            TirCompareOp.Gt => (left.Intersect(AtLeast((Int128)right.Min + 1)), right.Intersect(AtMost((Int128)left.Max - 1))),
            TirCompareOp.Ge => (left.Intersect(AtLeast(right.Min)), right.Intersect(AtMost(left.Max))),
            TirCompareOp.Eq => (left.Intersect(right), left.Intersect(right)),
            TirCompareOp.Ne => (Without(left, right), Without(right, left)),
            TirCompareOp.LtUnsigned => (right.Min >= 0 ? left.Intersect(Between(0, (Int128)right.Max - 1)) : left, right),
            TirCompareOp.LeUnsigned => (right.Min >= 0 ? left.Intersect(Between(0, right.Max)) : left, right),
            TirCompareOp.GtUnsigned => (left, left.Min >= 0 ? right.Intersect(Between(0, (Int128)left.Max - 1)) : right),
            _ => (left, left.Min >= 0 ? right.Intersect(Between(0, left.Max)) : right),
        };
    }

    /// <summary>The values of a <paramref name="from"/>-bit integer whose zero extension is in <paramref name="range"/>.</summary>
    private static TirRange BeforeZeroExtension(TirRange range, int from)
    {
        var positive = range.Intersect(TirRange.Of(0, TirRange.Signed(from).Max));
        var negative = range.Intersect(TirRange.Of(1L << (from - 1), (1L << from) - 1));
        return positive.Join(negative.IsEmpty ? TirRange.Empty : TirRange.Of(negative.Min - (1L << from), negative.Max - (1L << from)));
    }

    /// <summary><paramref name="range"/> without the one value of <paramref name="other"/> where it is at an end of it.</summary>
    private static TirRange Without(TirRange range, TirRange other)
    {
        if (!other.IsConstant)
        {
            return range;
        }

        return range.IsConstant ? (range.Min == other.Min ? TirRange.Empty : range)
            : other.Min == range.Min ? TirRange.Of(range.Min + 1, range.Max)
            : other.Min == range.Max ? TirRange.Of(range.Min, range.Max - 1)
            : range;
    }

    private static TirRange AtMost(Int128 bound) =>
        bound < long.MinValue ? TirRange.Empty : TirRange.Of(long.MinValue, bound > long.MaxValue ? long.MaxValue : (long)bound);

    private static TirRange AtLeast(Int128 bound) =>
        bound > long.MaxValue ? TirRange.Empty : TirRange.Of(bound < long.MinValue ? long.MinValue : (long)bound, long.MaxValue);

    private static TirRange Between(Int128 min, Int128 max) => AtLeast(min).Intersect(AtMost(max));
}
