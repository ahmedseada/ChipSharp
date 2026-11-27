using System.Collections.Immutable;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// A <c>params</c> span of a format (C# 13's): <c>Console.WriteLine("{0} {1} {2} {3}", a, b, c, d)</c> and
/// <c>string.Format</c> of four values or more pass them in a <c>ReadOnlySpan&lt;object&gt;</c> of an InlineArray local:
/// <c>ldloca V; initobj InlineArray4&lt;object&gt;</c>, then for each value <c>ldloca V; ldc i;
/// call InlineArrayElementRef</c>, the value (boxed), <c>stind.ref</c>, and at the end <c>ldloca V; ldc n;
/// call InlineArrayAsReadOnlySpan</c>. Each value is a hole as it would be in the overload of one to three, waiting in its
/// slot from the <c>stind.ref</c> (or the <c>box</c>) that hands it over; the local is never made, only its address taken.
/// The elements are followed in the order of the IL, as C# writes them, one statement from the <c>initobj</c> to the call.
/// </summary>
internal sealed partial class PrintedTextFlow
{
    /// <summary>The values written so far into each InlineArray local of a <c>params</c> span, by local.</summary>
    private readonly Dictionary<int, Value?[]> _inlineArrays = [];

    /// <summary>The InlineArray locals of objects this method has: their sizes, by local.</summary>
    private Dictionary<int, int>? _inlineLocals;

    /// <summary>The InlineArray locals whose span (or address) goes anywhere but to a format: not printed text's.</summary>
    private readonly HashSet<int> _plainInlines = [];

    /// <summary>
    /// For a value an instruction takes that is not for printed text: when it is an InlineArray local's address, element
    /// or span, the local is a plain one, and the analysis runs again without it. True when it was one of those.
    /// </summary>
    private bool ConsumeInline(Value value)
    {
        var local = value switch
        {
            InlineArray array => array.Local,
            ElementAddress element => element.Local,
            ObjectSpan span => span.Local,
            _ => (int?)null,
        };
        if (local is { } plain && _plainInlines.Add(plain))
        {
            _foundPlainArray = true;
        }

        return local is not null;
    }

    /// <summary>
    /// The values of a <c>params</c> span a format takes, when <paramref name="callee"/> takes one; null when it does
    /// not, and no values (with the problem recorded) when what it is given is not one.
    /// </summary>
    private Value[]? ParamsValues(IlInstruction instruction, IlMethodReference callee, Value[] values)
    {
        if (callee.ParameterTypes is not [_, var span] || !IsObjectSpan(span))
        {
            return null;
        }

        if (values is [ObjectSpan objects])
        {
            return [.. objects.Values];
        }

        Problem(instruction, Unsupported.BuildingText);
        return [];
    }

    /// <summary>
    /// The InlineArray locals of objects whose every use is a <c>params</c> span of a format: no slot holds them (see
    /// <see cref="PrintedText.InlineArrayLocals"/>).
    /// </summary>
    public IReadOnlySet<int> InlineArrayLocals => (_inlineLocals ?? [])
        .Where(local => _body.Instructions.Where(instruction => instruction.LocalIndex == local.Key)
            .All(use => use.OpCode is IlOpCode.Ldloca or IlOpCode.Ldloca_S && _roles.ContainsKey(use)))
        .Select(local => local.Key).ToHashSet();

    /// <summary>Follows an instruction of a <c>params</c> span; false for any other instruction.</summary>
    private bool InlineArrayStep(IlInstruction instruction, int index, State state)
    {
        var stack = state.Stack;
        switch (instruction)
        {
            case { OpCode: IlOpCode.Ldloca or IlOpCode.Ldloca_S, LocalIndex: { } local }
                when InlineLocals.ContainsKey(local) && !_plainInlines.Contains(local):
                Own(instruction, PrintedRole.Handler);
                stack.Add(new InlineArray(local));
                return true;

            case { OpCode: IlOpCode.Initobj } when stack is [.., InlineArray array]:
                Pop(stack);
                _inlineArrays[array.Local] = new Value?[InlineLocals[array.Local]];
                Own(instruction, PrintedRole.Start);
                return true;

            case { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: { Name: "InlineArrayElementRef" } } }
                when stack is [.., InlineArray array, Number element] && _inlineArrays.TryGetValue(array.Local, out var values)
                    && element.Constant >= 0 && element.Constant < values.Length:
                Pop(stack);
                Pop(stack);
                Own(instruction, PrintedRole.PartArray);
                stack.Add(new ElementAddress(array.Local, element.Constant));
                return true;

            case { OpCode: IlOpCode.Stind_Ref } when stack is [.., ElementAddress element, _]:
                var value = Pop(stack);
                Pop(stack);
                if (_inlineArrays.TryGetValue(element.Local, out var elements))
                {
                    elements[element.Index] = new Element(instruction, index, value);
                }

                Own(instruction, PrintedRole.ArrayPart);
                return true;

            case { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: { Name: "InlineArrayAsReadOnlySpan" } } }
                when stack is [.., InlineArray array, Number length] && _inlineArrays.TryGetValue(array.Local, out var all)
                    && length.Constant == all.Length && all.All(value => value is not null):
                Pop(stack);
                Pop(stack);
                Own(instruction, PrintedRole.PartArray);
                stack.Add(new ObjectSpan(array.Local, [.. all.Select(value => value!)]));
                return true;

            default:
                return false;
        }
    }

    /// <summary>The InlineArray locals of objects, by local, with their sizes.</summary>
    private Dictionary<int, int> InlineLocals => _inlineLocals ??= _body.Locals
        .Where(local => InlineArraySize(local.Type) is not null)
        .ToDictionary(local => local.Index, local => InlineArraySize(local.Type)!.Value);

    /// <summary>N for <c>InlineArrayN&lt;object&gt;</c>; null for any other type.</summary>
    private static int? InlineArraySize(IlType type) => ValueTypes.WithoutModifiers(type) is IlGenericInstanceType
    {
        Definition: IlNamedType { Namespace: "System.Runtime.CompilerServices", IsDefinedInThisAssembly: false } named,
        Arguments: [var element],
    } && ObjectTypes.IsSystemObject(element) && named.Name.StartsWith("InlineArray", StringComparison.Ordinal)
        && int.TryParse(named.Name["InlineArray".Length..].Split('`')[0], System.Globalization.CultureInfo.InvariantCulture,
            out var size)
        ? size
        : null;

    /// <summary>True for the <c>ReadOnlySpan&lt;object&gt;</c> of a <c>params</c> span.</summary>
    private static bool IsObjectSpan(IlType type) =>
        Spans.IsSpan(type, out var element, out _) && ObjectTypes.IsSystemObject(element);

    /// <summary>The address of InlineArray local <paramref name="Local"/>.</summary>
    private sealed record InlineArray(int Local) : Value;

    /// <summary>The address of element <paramref name="Index"/> of InlineArray local <paramref name="Local"/>.</summary>
    private sealed record ElementAddress(int Local, int Index) : Value;

    /// <summary>A value written into a <c>params</c> span by the <c>stind.ref</c> <paramref name="Store"/> at <paramref name="At"/>.</summary>
    private sealed record Element(IlInstruction Store, int At, Value Value) : Value;

    /// <summary>The <c>params</c> span of a format, of InlineArray local <paramref name="Local"/>: its values in order.</summary>
    private sealed record ObjectSpan(int Local, ImmutableArray<Value> Values) : Value
    {
        public bool Equals(ObjectSpan? other) => other is not null && Local == other.Local && Values.SequenceEqual(other.Values);

        public override int GetHashCode() => Values.Length;
    }
}
