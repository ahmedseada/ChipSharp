using System.Collections.Immutable;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>What the analysis of one method says about its arrays (see <see cref="HeapArrays"/>), for the translation.</summary>
/// <param name="Verdicts">
/// The verdict on every instruction that has to do with arrays: null when it is fine, else what is not supported; a heap
/// use (<see cref="Unsupported.IsHeapUse"/>) is fine on a target with a heap, which the translation then takes for granted.
/// </param>
/// <param name="HeapArrays">The IL offsets of the <c>newarr</c> instructions that make an array on the heap.</param>
/// <param name="MayBeNull">
/// The IL offsets of the instructions that measure, index or take an array that may be null (ldlen, ldelem, stelem,
/// ldelema, and the library calls arrays are passed to).
/// </param>
/// <param name="ArraysOnEntry">
/// For the IL offset of each block, the depths of IL's evaluation stack (from 0, the bottom) that hold an array where the
/// block starts (C#'s <c>c ? a : null</c> leaves one there), so that null on its way there is an array's null.
/// </param>
internal sealed record MethodArrays(
    IReadOnlyDictionary<IlInstruction, Unsupported?> Verdicts,
    IReadOnlySet<int> HeapArrays,
    IReadOnlySet<int> MayBeNull,
    IReadOnlyDictionary<int, IReadOnlySet<int>> ArraysOnEntry);

/// <summary>
/// What one method does with the arrays it is given, for <see cref="Frontend.HeapArrays.Find"/>: the arguments it keeps
/// itself, and what it passes to the program's methods.
/// </summary>
internal sealed record HeapSummary(IReadOnlySet<int> KeptArguments, IReadOnlyList<HeapPass> Passes);

/// <summary>
/// An array that a method passes to <paramref name="Callee"/> as its argument <paramref name="Argument"/>: one of the
/// method's own <paramref name="Arguments"/> (by IL argument number), or what <paramref name="MayBeNull"/> says (null, or an
/// array read from a field, an element or a call, which may be null).
/// </summary>
internal sealed record HeapPass(IlMethod Callee, int Argument, IReadOnlySet<int> Arguments, bool MayBeNull);

/// <summary>
/// Arrays on the heap in the analysis (see <see cref="HeapArrays"/>): where an array is kept, which <c>newarr</c> makes
/// one on the heap, and which arrays may be null.
/// </summary>
internal static partial class ArrayEscapeAnalysis
{
    /// <summary>
    /// Where an array read from a field, an element or a call comes from: an array kept before, so an object on the heap
    /// (or the empty array, or null).
    /// </summary>
    private const int LoadedOrigin = int.MaxValue;

    /// <summary>Where null comes from, as one of the arrays a value can be (C#'s <c>c ? a : null</c>).</summary>
    private const int NullOrigin = int.MaxValue - 1;

    /// <summary>
    /// The verdicts of <see cref="Check(IlMethod, StaticArrays)"/>, for the program whose kept and nullable parameters are
    /// <paramref name="heap"/>, with which <c>newarr</c> makes an array on the heap and which arrays may be null.
    /// </summary>
    public static MethodArrays Analyze(IlMethod method, StaticArrays? statics, HeapArrays heap)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(heap);
        if (method.Body is not { Instructions.Count: > 0 } body)
        {
            return new MethodArrays(new Dictionary<IlInstruction, Unsupported?>(), new HashSet<int>(), new HashSet<int>(),
                new Dictionary<int, IReadOnlySet<int>>());
        }

        var flow = new Flow(method, body, statics, heap);
        return new MethodArrays(flow.Run(), flow.HeapNewarrs(), flow.MaybeNullAt, flow.ArraysOnEntry);
    }

    /// <summary>What <paramref name="method"/> keeps of what it is given, and passes on (see <see cref="HeapSummary"/>).</summary>
    public static HeapSummary Summarize(IlMethod method, StaticArrays statics)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method.Body is not { Instructions.Count: > 0 } body)
        {
            return new HeapSummary(new HashSet<int>(), []);
        }

        var flow = new Flow(method, body, statics, heap: null);
        flow.Run();
        return flow.Summary();
    }

    /// <summary>The heap use of a feature: what it is on a target without a heap.</summary>
    private static Unsupported HeapUse(Unsupported feature) => Unsupported.HeapUse(feature);

    private sealed partial class Flow
    {
        /// <summary>The kept and nullable parameters of the program; null while they are being found (<see cref="Summarize"/>).</summary>
        private readonly HeapArrays? _heap;

        /// <summary>Where the arrays that are kept come from.</summary>
        private readonly HashSet<int> _kept = [];

        /// <summary>The newarr instructions that make an array on the heap whether it is kept or not.</summary>
        private readonly HashSet<int> _onHeap = [];

        private readonly List<(IlMethod Callee, int Argument, Value Value)> _heapPasses = [];

        /// <summary>The instructions that take an array that may be null.</summary>
        public HashSet<int> MaybeNullAt { get; } = [];

        /// <summary>The depths of the evaluation stack that hold an array where each block starts, by the block's offset.</summary>
        public Dictionary<int, IReadOnlySet<int>> ArraysOnEntry { get; } = [];

        private void FindArraysOnEntry(State?[] entries)
        {
            for (var b = 0; b < entries.Length; b++)
            {
                var stack = entries[b]?.Stack ?? [];
                var depths = Enumerable.Range(0, stack.Count).Where(d => stack[d].Kind == Kind.Array).ToHashSet();
                if (depths.Count > 0)
                {
                    ArraysOnEntry[_blocks[b].Offset] = depths;
                }
            }
        }

        /// <summary>The newarr instructions that make an array on the heap: kept, of a size that is not constant, or made again while in use.</summary>
        public HashSet<int> HeapNewarrs() => [.. _onHeap, .. _kept.Where(IsNewarr)];

        public HeapSummary Summary()
        {
            var passes = _heapPasses.Select(pass => new HeapPass(pass.Callee, pass.Argument,
                pass.Value.Kind == Kind.Array ? Arguments(pass.Value.Set) : new HashSet<int>(),
                pass.Value.Kind is Kind.Null or Kind.Other
                    || (pass.Value.Kind == Kind.Array && pass.Value.Set.Any(o => o is LoadedOrigin or NullOrigin))))
                .ToList();
            return new HeapSummary(Arguments(_kept), passes);
        }

        private static bool IsNewarr(int origin) => origin is >= 0 and < NullOrigin;

        private static bool IsArgument(int origin) => origin is < 0 and > int.MinValue + 0xFFFF;

        private static HashSet<int> Arguments(IEnumerable<int> origins) => [.. origins.Where(IsArgument).Select(o => -1 - o)];

        /// <summary>
        /// <paramref name="value"/> is kept at <paramref name="instruction"/> (see <see cref="HeapArrays"/>), which needs a
        /// heap: a heap use there, unless the place says otherwise (<see cref="Kept"/>).
        /// </summary>
        private void Escapes(IlInstruction instruction, Value value)
        {
            Verdict(instruction, HeapUse(Unsupported.ArrayEscapes));
            Kept(instruction, value);
        }

        /// <summary>
        /// <paramref name="value"/> is kept at <paramref name="instruction"/>: each newarr it can come from makes it on the
        /// heap, each argument it can be is a kept parameter, and a static array that needs no heap must not be kept.
        /// </summary>
        private void Kept(IlInstruction instruction, Value value)
        {
            if (!_record || value.Kind is not (Kind.Array or Kind.ElementAddress))
            {
                return;
            }

            _kept.UnionWith(value.Set);
            if (_statics is not null && value.Set.Any(origin => origin <= int.MinValue + 0xFFFF))
            {
                Verdict(instruction, Unsupported.StaticArrayKept);
            }
        }

        /// <summary>A newarr that makes its array on the heap: <paramref name="why"/> on a target without a heap.</summary>
        private void OnHeap(IlInstruction newarr, Unsupported why)
        {
            Verdict(newarr, HeapUse(why));
            if (_record)
            {
                _onHeap.Add(newarr.Offset);
            }
        }

        /// <summary>
        /// A call passes <paramref name="value"/> as the argument <paramref name="argument"/> (by IL argument number) of
        /// <paramref name="callee"/>: kept when the callee keeps that parameter; an array given to a parameter that is
        /// not an array (<c>object</c>) becomes an object there, so it is kept too.
        /// </summary>
        private void PassedOnHeap(IlInstruction call, IlMethodReference callee, int argument, Value value, IlType? parameter)
        {
            if (callee.Definition is not { } definition)
            {
                if (value.Kind is Kind.Array or Kind.Null or Kind.Other && MayBeNull(value))
                {
                    Nullable(call);
                }

                return;
            }

            if (parameter is not null && !StackArrays.TryMapType(ValueTypes.WithoutModifiers(parameter), _method.Assembly, out _))
            {
                if (value.Kind == Kind.Array && ValueTypes.WithoutModifiers(parameter) is not IlByRefType)
                {
                    Kept(call, value);
                }

                return;
            }

            if (_record)
            {
                _heapPasses.Add((definition, argument, value));
            }

            if (_heap?.Keeps(definition, argument) == true)
            {
                Kept(call, value);
            }
        }

        /// <summary>
        /// What an instruction that reads a field, an element or a call result of IL type <paramref name="type"/>
        /// pushes: for an array, one that was kept (or null), of that type; anything else is just something.
        /// </summary>
        private Value Loaded(IlType? type) =>
            type is not null && StackArrays.TryMapType(ValueTypes.WithoutModifiers(type), _method.Assembly, out _)
                ? Value.Of(Kind.Array, [LoadedOrigin]) with { Type = ValueTypes.WithoutModifiers(type) }
                : Value.Other;

        /// <summary>An array of IL type <paramref name="type"/> that comes from <paramref name="origins"/>.</summary>
        private static Value ArrayOf(ImmutableHashSet<int> origins, IlType? type) =>
            Value.Of(Kind.Array, origins) with { Type = type is null ? null : ValueTypes.WithoutModifiers(type) };

        /// <summary>
        /// The arrays a value can be, when it goes into an array variable: null as <see cref="NullOrigin"/>, and anything
        /// else that is not known to be an array (one read from an element or a call) as <see cref="LoadedOrigin"/>.
        /// </summary>
        private static ImmutableHashSet<int> ArrayOrigins(Value value) => value.Kind switch
        {
            Kind.Array => value.Set,
            Kind.Null => [NullOrigin],
            _ => [LoadedOrigin],
        };

        /// <summary>
        /// True when <paramref name="array"/>, a value an instruction takes as an array, may be null: null itself, an array
        /// read from a field, an element or a call, or a parameter some caller gives null.
        /// </summary>
        private bool MayBeNull(Value array) => array.Kind switch
        {
            Kind.Null or Kind.Other => true,
            Kind.Array => array.Set.Any(origin => origin is LoadedOrigin or NullOrigin
                || (IsArgument(origin) && _heap?.MayBeNull(_method, -1 - origin) == true)),
            _ => false,
        };

        /// <summary>The instruction takes an array that may be null: the translation tests it.</summary>
        private void Nullable(IlInstruction instruction)
        {
            if (_record)
            {
                MaybeNullAt.Add(instruction.Offset);
            }
        }

        /// <summary>
        /// The heap use of an array type where it is not supported without a heap: an array of references needs one
        /// (<see cref="Unsupported.ArraysOfReferences"/>), and any other array that a field holds leaves its method.
        /// </summary>
        private Unsupported ForArrayField(IlType type) =>
            HeapArrays.ElementType(type) is { } element && HeapArrays.IsReferenceElement(element, _method.Assembly)
                ? HeapUse(Unsupported.ArraysOfReferences)
                : HeapUse(Unsupported.ArrayEscapes);

        /// <summary>
        /// The verdict on an element type that arrays on the stack do not hold: a reference, which arrays on the heap
        /// hold (a heap use), else what the element needs.
        /// </summary>
        private Unsupported? ForElement(IlType element) =>
            HeapArrays.IsReferenceElement(element, _method.Assembly)
                ? HeapUse(Unsupported.ArraysOfReferences)
                : WholeProgramPass.ForArrayElement(element, _method.Assembly);

        /// <summary>
        /// <paramref name="value"/> goes where an array of <paramref name="destination"/> is wanted: an array of other
        /// references made here, or given to this method, would be array covariance, which is not supported.
        /// </summary>
        private void Covariance(IlInstruction instruction, Value value, IlType? destination)
        {
            if (!_record || value.Kind != Kind.Array || destination is null
                || HeapArrays.ElementType(destination) is not { } wanted
                || !HeapArrays.IsReferenceElement(wanted, _method.Assembly))
            {
                return;
            }

            // the value's own type, and the type of each array it can come from
            var elements = value.Set.Select(origin =>
                    IsNewarr(origin) ? StackArrays.ElementType(_body.Instructions[_body.IndexOf(origin)])
                    : IsArgument(origin) && ArgumentType(-1 - origin) is { } type ? HeapArrays.ElementType(type)
                    : null)
                .Append(value.Type is { } own ? HeapArrays.ElementType(own) : null);
            foreach (var array in elements)
            {
                if (array is not null && HeapArrays.IsReferenceElement(array, _method.Assembly)
                    && !ValueTypes.WithoutModifiers(array).Equals(ValueTypes.WithoutModifiers(wanted)))
                {
                    Verdict(instruction, Unsupported.ArrayCovariance);
                }
            }
        }

        /// <summary>The type of argument <paramref name="argument"/> (by IL argument number, <c>this</c> first); null for <c>this</c>.</summary>
        private IlType? ArgumentType(int argument)
        {
            var index = argument - (_method.Signature.HasThis ? 1 : 0);
            return index >= 0 && index < _method.Parameters.Count ? _method.Parameters[index].Type : null;
        }

        /// <summary>The type of variable <paramref name="variable"/>: a local, or an argument after the locals.</summary>
        private IlType? VariableType(int variable) =>
            variable < _localCount ? _body.Locals[variable].Type : ArgumentType(variable - _localCount);

        /// <summary>True for a field of a class: an object's, which the class's own rules judge (an array there is kept).</summary>
        private static bool IsObjectField(IlInstruction instruction) =>
            instruction.Operand is IlFieldOperand { Field.Definition: { IsStatic: false } field }
            && !ValueTypes.IsStruct(field.DeclaringType);
    }
}
