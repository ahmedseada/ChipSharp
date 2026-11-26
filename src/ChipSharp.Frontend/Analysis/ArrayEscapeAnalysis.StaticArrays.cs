using System.Collections.Immutable;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>What one method does to the elements of the arrays it is given and of the static arrays it reads.</summary>
/// <param name="Arguments">The arguments (by IL argument number, <c>this</c> first) whose elements it writes: an array
/// parameter's, or what a ref parameter refers to.</param>
/// <param name="Fields">The static arrays whose elements it writes.</param>
/// <param name="Passes">The arrays, and references into arrays, it passes to other methods.</param>
internal sealed record ArrayWrites(
    IReadOnlySet<int> Arguments,
    IReadOnlySet<IlField> Fields,
    IReadOnlyList<ArrayPass> Passes);

/// <summary>
/// An array, or a reference into one, that a method passes to <paramref name="Callee"/> as its argument
/// <paramref name="Argument"/>: one of its own <paramref name="Arguments"/>, or of the static arrays
/// <paramref name="Fields"/> (arrays it made itself do not matter here). The callee writes them when it writes that
/// argument.
/// </summary>
internal sealed record ArrayPass(IlMethod Callee, int Argument, IReadOnlySet<int> Arguments, IReadOnlySet<IlField> Fields);

/// <summary>
/// Arrays in static fields (see <see cref="StaticArrays"/>): reading such a field gives an array that lives as long as
/// the program, which may go wherever an array on the stack may go; and which elements a method writes, which makes
/// an array a constant table or a static fixed array.
/// </summary>
internal static partial class ArrayEscapeAnalysis
{
    /// <summary>
    /// The verdicts of <see cref="Check(IlMethod)"/>, with the arrays in static fields of the program,
    /// <paramref name="statics"/>: reading one is an array; assigning one in its initializer is fine; any use of a static
    /// field of an array type that breaks their rule is what <see cref="StaticArrays.Problem"/> says. Without them
    /// (null) a static field of an array type is an array that leaves its method.
    /// </summary>
    public static IReadOnlyDictionary<IlInstruction, Unsupported?> Check(IlMethod method, StaticArrays? statics)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method.Body is { Instructions.Count: > 0 } body
            ? new Flow(method, body, statics).Run()
            : new Dictionary<IlInstruction, Unsupported?>();
    }

    /// <summary>
    /// Which elements <paramref name="method"/> writes: those of its array arguments, through its ref arguments, of the
    /// static arrays of <paramref name="statics"/>; and which of those it passes to other methods, which may write them.
    /// </summary>
    public static ArrayWrites FindWrites(IlMethod method, StaticArrays statics)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(statics);
        if (method.Body is not { Instructions.Count: > 0 } body)
        {
            return new ArrayWrites(new HashSet<int>(), new HashSet<IlField>(), []);
        }

        var flow = new Flow(method, body, statics);
        flow.Run();
        return flow.Writes();
    }

    private sealed partial class Flow
    {
        private readonly StaticArrays? _statics;

        /// <summary>The static arrays this method reads, by number: where such an array comes from is int.MinValue + i.</summary>
        private readonly Dictionary<IlField, int> _staticOrigins = [];

        /// <summary>Where the arrays whose elements this method writes come from.</summary>
        private readonly HashSet<int> _written = [];

        private readonly List<(IlMethodReference Callee, int Argument, ImmutableHashSet<int> Origins)> _passes = [];

        /// <summary>What <see cref="_written"/> and <see cref="_passes"/> say, in arguments and fields.</summary>
        public ArrayWrites Writes()
        {
            var fields = _staticOrigins.ToDictionary(pair => pair.Value, pair => pair.Key);
            HashSet<int> Arguments(IEnumerable<int> origins) => [.. origins.Where(o => o is < 0 and > int.MinValue + 0xFFFF)
                .Select(o => -1 - o)];
            HashSet<IlField> Fields(IEnumerable<int> origins) => [.. origins.Where(fields.ContainsKey).Select(o => fields[o])];

            var passes = new List<ArrayPass>();
            foreach (var (callee, argument, origins) in _passes)
            {
                // a library method may write what it is given; it has no body to look into
                if (callee.Definition is { } definition)
                {
                    passes.Add(new ArrayPass(definition, argument, Arguments(origins), Fields(origins)));
                }
                else
                {
                    _written.UnionWith(origins);
                }
            }

            return new ArrayWrites(Arguments(_written), Fields(_written), passes);
        }

        /// <summary>The static field an ldsfld, stsfld or ldsflda names, when the rule of static arrays is about it.</summary>
        private IlField? StaticArrayField(IlInstruction instruction) =>
            _statics is not null && instruction.Operand is IlFieldOperand { Field.Definition: { } field }
            && StaticArrays.IsCandidate(field)
                ? field
                : null;

        /// <summary>
        /// ldsfld, stsfld and ldsflda of a static field of an array type: reading a static array is an array that comes
        /// from that field; its initializer's store takes the new array. Any use of a field that breaks the rule has its
        /// problem.
        /// </summary>
        private void AccessStaticArray(IlInstruction instruction, IlField field, Func<Value> pop, List<Value> stack)
        {
            var isArray = _statics!.TryGet(field, out _);
            Verdict(instruction, isArray ? null : _statics.Problem(field) ?? Unsupported.ArrayEscapes);
            switch (instruction.OpCode)
            {
                case IlOpCode.Ldsfld:
                    stack.Add(Value.Of(Kind.Array, isArray ? [StaticOrigin(field)] : []));
                    break;

                case IlOpCode.Stsfld:
                    pop();
                    break;

                default:
                    stack.Add(Value.Other);
                    break;
            }
        }

        private int StaticOrigin(IlField field)
        {
            if (!_staticOrigins.TryGetValue(field, out var origin))
            {
                origin = int.MinValue + _staticOrigins.Count;
                _staticOrigins.Add(field, origin);
            }

            return origin;
        }

        /// <summary>An instruction writes into what <paramref name="target"/> is: an array, or a reference into one.</summary>
        private void Written(Value target)
        {
            if (_record && target.Kind is Kind.Array or Kind.ElementAddress)
            {
                _written.UnionWith(target.Set);
            }
        }

        /// <summary>
        /// A call passes <paramref name="value"/> at <paramref name="position"/> (from 0, <c>this</c> first, which a
        /// newobj makes rather than takes): an array or a reference into one, the callee may write.
        /// </summary>
        private void Passed(IlInstruction call, IlMethodReference callee, int position, Value value)
        {
            if (_record && value.Kind is Kind.Array or Kind.ElementAddress && !value.Set.IsEmpty)
            {
                var argument = position + (call.OpCode == IlOpCode.Newobj && callee.HasThis ? 1 : 0);
                _passes.Add((callee, argument, value.Set));
            }
        }
    }
}
