using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>An array in a static field of the program that needs no heap (see <see cref="StaticArrays"/>).</summary>
/// <param name="Field">The static field.</param>
/// <param name="ElementType">An element's TIR type in memory (see <see cref="StackArrays.TryMapElementType"/>).</param>
/// <param name="Length">How many elements it has, from its initializer's constant.</param>
/// <param name="Elements">The constants it starts with, one per element; null when every element starts at zero.</param>
/// <param name="IsConstant">
/// True for a constant table: nothing in the program writes an element, so it is read-only data, in flash on a chip.
/// False for a static fixed array: a variable, in RAM.
/// </param>
internal sealed record StaticArray(IlField Field, TirType ElementType, int Length, IReadOnlyList<TirConst>? Elements,
    bool IsConstant);

/// <summary>
/// Arrays in static fields without a heap: an array that a static field is given once, when the program starts, and
/// keeps for good, with a size known before the program runs, is a global, like any static field. So even a chip
/// without a heap has them, as constant tables (<c>static readonly byte[] Digits = { 0x3F, 0x06, ... };</c>, in flash)
/// and as static fixed arrays (<c>static int[] buffer = new int[16];</c>, in RAM).
/// </summary>
/// <remarks>
/// <para>
/// The rule (checked by the whole-program pass, and again by the IL-to-TIR translation, both with
/// <see cref="ArrayEscapeAnalysis"/>): a static field of an array type, of the elements arrays on the stack hold
/// (<see cref="StackArrays"/>), is assigned exactly once in the program, in its own type's static initializer (C#'s
/// field initializer or static constructor), where that runs exactly once (not in a loop, not only on some paths). What
/// it is given is made right there: <c>newarr</c> of a constant size, then nothing but constant elements: the data of
/// <c>RuntimeHelpers.InitializeArray</c> (C#'s <c>{ 1, 2, 3 }</c>), or <c>dup</c>, a constant index, a constant and
/// <c>stelem</c>, or <c>Array.Empty&lt;T&gt;()</c> (<c>[]</c>). Nothing takes the field's address (<c>ref</c>). The
/// array itself then goes wherever an array on the stack may go: into locals, indexed, measured, passed to the
/// program's methods (also to a method that keeps it only while it runs: it outlives them all); not returned, not
/// stored in another field or array, not compared.
/// </para>
/// <para>
/// Which of the two it is: a constant table when no element is ever written anywhere in the program (a
/// <c>stelem</c>, a store through a reference to an element, <c>ref a[i]</c> passed to a method that writes through
/// it), also not by the methods it is passed to, nor the methods they pass it on to (see
/// <see cref="ArrayEscapeAnalysis.FindWrites"/>); a static fixed array otherwise. So a constant table can never be
/// written: the analysis put it in flash because nothing writes it.
/// </para>
/// <para>
/// The representation: the field's elements are a global <c>[N x T]</c> named like the field, e.g.
/// <c>@Program.Digits</c>, that starts with the initializer's constants (or zero): a constant global for a table, a
/// variable for a fixed array (see <see cref="TirModule.AddArray"/>). Reading the field (<c>ldsfld</c>) gives the usual
/// array value of <see cref="StackArrays.ArrayType"/>, the global's address and the length, so indexing, the bounds
/// check, <c>.Length</c> and passing it on work as for any array; an empty array is the empty array value. The
/// initializer's instructions for the field are not compiled at all: a type initializer that does nothing else
/// disappears.
/// </para>
/// </remarks>
internal sealed class StaticArrays
{
    private readonly Dictionary<IlField, StaticArray> _arrays = [];
    private readonly Dictionary<IlField, Unsupported> _problems = [];

    /// <summary>The instructions of each type initializer that make a static array, as ranges of indexes.</summary>
    private readonly Dictionary<IlMethod, List<(int First, int Last)>> _initializers = [];

    private StaticArrays()
    {
    }

    /// <summary>No static arrays.</summary>
    public static StaticArrays None { get; } = new();

    /// <summary>The static arrays of the program, in the order their fields are declared.</summary>
    public IReadOnlyList<StaticArray> Arrays => [.. _arrays.Values.OrderBy(array => array.Field.Token)];

    /// <summary>
    /// The static arrays of the program made of <paramref name="methods"/> (its entry point and every method it
    /// reaches, type initializers too), and why each other static field of an array type it uses is not one.
    /// </summary>
    public static StaticArrays Find(IReadOnlyList<IlMethod> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);

        var arrays = new StaticArrays();
        var uses = new Dictionary<IlField, List<(IlMethod Method, int Index)>>();
        foreach (var method in methods)
        {
            var instructions = method.Body?.Instructions ?? [];
            for (var i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] is
                    {
                        OpCode: IlOpCode.Ldsfld or IlOpCode.Stsfld or IlOpCode.Ldsflda,
                        Operand: IlFieldOperand { Field.Definition: { } field },
                    } && IsCandidate(field))
                {
                    if (!uses.TryGetValue(field, out var list))
                    {
                        uses.Add(field, list = []);
                    }

                    list.Add((method, i));
                }
            }
        }

        foreach (var (field, places) in uses)
        {
            if (arrays.Accept(field, places) is { } problem)
            {
                arrays._problems.Add(field, problem);
            }
        }

        arrays.Classify(methods);
        return arrays;
    }

    /// <summary>
    /// True for a field the rule is about: a static field of a type that is not generic, whose type is an array of
    /// the elements arrays on the stack hold. Arrays of other elements are those elements' problem.
    /// </summary>
    public static bool IsCandidate(IlField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.IsStatic && field.DeclaringType.GenericParameters.Count == 0
            && StaticFields.Type(field) is IlArrayType array
            && StackArrays.TryMapElementType(array.ElementType, field.DeclaringType.Assembly, out _);
    }

    /// <summary>The static array <paramref name="field"/> holds, when it keeps to the rule.</summary>
    public bool TryGet(IlField field, [NotNullWhen(true)] out StaticArray? array) => _arrays.TryGetValue(field, out array);

    /// <summary>
    /// Why the program's uses of <paramref name="field"/>, a candidate (<see cref="IsCandidate"/>), break the rule; null
    /// when it is a static array, and for a field the program does not use.
    /// </summary>
    public Unsupported? Problem(IlField field) => _problems.GetValueOrDefault(field);

    /// <summary>
    /// True for the instruction at <paramref name="index"/> in <paramref name="method"/> when it is part of what makes
    /// a static array in a type initializer: the array's global holds the result already, so it is not compiled.
    /// </summary>
    public bool IsFolded(IlMethod method, int index) =>
        _initializers.TryGetValue(method, out var ranges) && ranges.Any(range => index >= range.First && index <= range.Last);

    /// <summary>
    /// Checks the rule for <paramref name="field"/>, used at <paramref name="places"/>: if it keeps it, adds the array
    /// (as a fixed array until <see cref="Classify"/> knows better) and the range of its initializer.
    /// </summary>
    private Unsupported? Accept(IlField field, List<(IlMethod Method, int Index)> places)
    {
        IlOpCode OpCode((IlMethod Method, int Index) place) => place.Method.Body!.Instructions[place.Index].OpCode;
        if (places.Any(place => OpCode(place) == IlOpCode.Ldsflda))
        {
            return Unsupported.StaticArrayReferences;
        }

        var stores = places.Where(place => OpCode(place) == IlOpCode.Stsfld).ToList();
        if (stores.Count == 0)
        {
            return Unsupported.StaticArrayNull;
        }

        var (initializer, store) = stores[0];
        if (stores.Count > 1 || initializer != StaticInitialization.TypeInitializer(field.DeclaringType))
        {
            return Unsupported.StaticArrayAssignments;
        }

        var assembly = field.DeclaringType.Assembly;
        var element = ((IlArrayType)StaticFields.Type(field)).ElementType;
        StackArrays.TryMapElementType(element, assembly, out var storage);
        var body = initializer.Body!;
        if (Parse(body, store, element, storage!, out var problem) is not (var first, var length, var elements))
        {
            return problem;
        }

        if (!InOneBlock(body, first, store) || !RunsOnce(body, first))
        {
            return Unsupported.StaticArrayAssignments;
        }

        _arrays.Add(field, new StaticArray(field, storage!, length, elements, IsConstant: false));
        if (!_initializers.TryGetValue(initializer, out var ranges))
        {
            _initializers.Add(initializer, ranges = []);
        }

        ranges.Add((first, store));
        return null;
    }

    /// <summary>
    /// Makes each static array a constant table that nothing in the program writes: every method's writes (see
    /// <see cref="ArrayEscapeAnalysis.FindWrites"/>), through the parameters of the methods arrays are passed to, until
    /// nothing changes.
    /// </summary>
    private void Classify(IReadOnlyList<IlMethod> methods)
    {
        if (_arrays.Count == 0)
        {
            return;
        }

        var writes = methods.Where(method => method.Body is { Instructions.Count: > 0 }).Distinct()
            .ToDictionary(method => method, method => ArrayEscapeAnalysis.FindWrites(method, this));
        var parameters = writes.SelectMany(w => w.Value.Arguments.Select(argument => (w.Key, argument))).ToHashSet();
        var fields = writes.SelectMany(w => w.Value.Fields).ToHashSet();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (method, found) in writes)
            {
                // a method without a body of its own (outside the program) may write anything it is given
                foreach (var pass in found.Passes.Where(pass => !writes.ContainsKey(pass.Callee)
                    || parameters.Contains((pass.Callee, pass.Argument))))
                {
                    foreach (var argument in pass.Arguments)
                    {
                        changed |= parameters.Add((method, argument));
                    }

                    foreach (var field in pass.Fields)
                    {
                        changed |= fields.Add(field);
                    }
                }
            }
        }

        foreach (var array in _arrays.Values.ToList())
        {
            _arrays[array.Field] = array with { IsConstant = !fields.Contains(array.Field) };
        }
    }

    /// <summary>
    /// What the static initializer stores at <paramref name="store"/> (its stsfld), when it is made right there by the
    /// rule: the index of its first instruction, its length and its elements (null for zeros). Otherwise null, and
    /// <paramref name="problem"/> says why.
    /// </summary>
    private static (int First, int Length, TirConst[]? Elements)? Parse(IlMethodBody body, int store, IlType element,
        TirType storage, out Unsupported problem)
    {
        var instructions = body.Instructions;
        var end = store > 0 && instructions[store - 1].OpCode == IlOpCode.Volatile ? store - 1 : store;
        problem = Unsupported.StaticArrayInitializers;
        if (end == 0)
        {
            return null;
        }

        var made = instructions[end - 1];
        if (made.OpCode == IlOpCode.Ldnull)
        {
            problem = Unsupported.StaticArrayNull;
            return null;
        }

        if (made is { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: var callee } }
            && StackArrays.IsEmptyArray(callee, body.Method.Assembly, out var empty) && StackArrays.Primitive(empty) == element)
        {
            return (end - 1, 0, null);
        }

        // the newarr closest to the store whose constants reach it
        for (var n = end - 1; n >= 1; n--)
        {
            if (instructions[n].OpCode == IlOpCode.Newarr && StackArrays.ElementType(instructions[n]) == element
                && Integer(instructions[n - 1]) is { } length and >= 0 and <= int.MaxValue
                && Elements(body, n + 1, end, (int)length, storage, out var elements))
            {
                return (n - 1, (int)length, elements);
            }
        }

        return null;
    }

    /// <summary>
    /// The constant elements that the instructions from <paramref name="from"/> to before <paramref name="end"/> give
    /// a new array of <paramref name="length"/> elements of <paramref name="storage"/>, with the array on the stack:
    /// <c>dup; ldtoken; call InitializeArray</c>, and <c>dup; index; constant; stelem</c>, as often as they come.
    /// False for anything else.
    /// </summary>
    private static bool Elements(IlMethodBody body, int from, int end, int length, TirType storage, out TirConst[]? elements)
    {
        var instructions = body.Instructions;
        elements = null;
        for (var i = from; i < end;)
        {
            if (instructions[i].OpCode == IlOpCode.Nop)
            {
                i++;
                continue;
            }

            if (instructions[i].OpCode != IlOpCode.Dup || i + 2 >= end)
            {
                return false;
            }

            if (instructions[i + 1] is { OpCode: IlOpCode.Ldtoken, Operand: IlFieldOperand { Field.Definition: { } data } }
                && instructions[i + 2] is { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: var callee } }
                && StackArrays.IsInitializeArray(callee))
            {
                if (FromData(data, length, storage) is not { } values)
                {
                    return false;
                }

                elements = values;
                i += 3;
                continue;
            }

            if (Integer(instructions[i + 1]) is not { } index || index < 0 || index >= length
                || Value(instructions, i + 2, end) is not (var value, var stelem)
                || stelem >= end || StackArrays.ElementType(instructions[stelem]) is not { } stored
                || instructions[stelem].OpCode is not ((>= IlOpCode.Stelem_I and <= IlOpCode.Stelem_R8) or IlOpCode.Stelem)
                || !StackArrays.TryMapElementType(stored, body.Method.Assembly, out var type) || type != storage
                || Narrow(value, storage) is not { } constant)
            {
                return false;
            }

            elements ??= [.. Enumerable.Range(0, length).Select(_ => new TirConst(storage, 0))];
            elements[(int)index] = constant;
            i = stelem + 1;
        }

        return true;
    }

    /// <summary>
    /// A constant on IL's stack, as the instructions from <paramref name="at"/> make it: ldc (i4, i8, r4, r8), then for
    /// an int32 maybe conv.i8, conv.u8, conv.i or conv.u; with the index of the instruction after it.
    /// </summary>
    private static ((TirType Type, long Integer, double Float) Value, int Next)? Value(
        IReadOnlyList<IlInstruction> instructions, int at, int end)
    {
        if (at >= end)
        {
            return null;
        }

        (TirType Type, long Integer, double Float) value;
        switch (instructions[at])
        {
            case { OpCode: IlOpCode.Ldc_R4, Operand: IlFloat32Operand constant }:
                return ((TirType.F32, 0, constant.Value), at + 1);

            case { OpCode: IlOpCode.Ldc_R8, Operand: IlFloat64Operand constant }:
                return ((TirType.F64, 0, constant.Value), at + 1);

            case { OpCode: IlOpCode.Ldc_I8, Operand: IlInt64Operand constant }:
                return ((TirType.I64, constant.Value, 0), at + 1);

            case var instruction when Integer(instruction) is { } integer:
                value = (TirType.I32, integer, 0);
                break;

            default:
                return null;
        }

        var next = at + 1 < end ? instructions[at + 1].OpCode : IlOpCode.Nop;
        return next switch
        {
            IlOpCode.Conv_I8 => ((TirType.I64, value.Integer, 0), at + 2),
            IlOpCode.Conv_U8 => ((TirType.I64, (uint)value.Integer, 0), at + 2),
            IlOpCode.Conv_I => ((TirType.NativeInt, value.Integer, 0), at + 2),
            IlOpCode.Conv_U => ((TirType.NativeInt, (uint)value.Integer, 0), at + 2),
            _ => (value, at + 1),
        };
    }

    /// <summary>
    /// A constant of IL's stack as stelem stores it into an element of <paramref name="storage"/>: an int32 keeps its
    /// low bits, a float is rounded to float or widened to double. Null when the two do not go together, or for a
    /// native int outside int's range (which would not mean the same on every target).
    /// </summary>
    private static TirConst? Narrow((TirType Type, long Integer, double Float) value, TirType storage) =>
        (storage.Kind, value.Type.Kind) switch
        {
            (TirTypeKind.I8, TirTypeKind.I32) => new TirConst(storage, (sbyte)value.Integer),
            (TirTypeKind.I16, TirTypeKind.I32) => new TirConst(storage, (short)value.Integer),
            (TirTypeKind.I32, TirTypeKind.I32) => new TirConst(storage, (int)value.Integer),
            (TirTypeKind.I64, TirTypeKind.I64) => new TirConst(storage, value.Integer),
            (TirTypeKind.NativeInt, TirTypeKind.NativeInt) when value.Integer is >= int.MinValue and <= int.MaxValue =>
                new TirConst(storage, value.Integer),
            (TirTypeKind.F32, TirTypeKind.F32 or TirTypeKind.F64) => new TirConst(storage, (double)(float)value.Float),
            (TirTypeKind.F64, TirTypeKind.F32 or TirTypeKind.F64) => new TirConst(storage, value.Float),
            _ => null,
        };

    /// <summary>
    /// The elements an array initializer's data gives (little-endian, as C# writes it); null when the field has too
    /// few bytes, and for elements C# never fills from data (nint, structs).
    /// </summary>
    private static TirConst[]? FromData(IlField data, int length, TirType storage)
    {
        var size = storage.Kind switch
        {
            TirTypeKind.I8 => 1,
            TirTypeKind.I16 => 2,
            TirTypeKind.I32 or TirTypeKind.F32 => 4,
            TirTypeKind.I64 or TirTypeKind.F64 => 8,
            _ => 0,
        };
        if (size == 0 || length > int.MaxValue / size
            || data.DeclaringType.Assembly.ReadFieldData(data, length * size) is not { } bytes)
        {
            return null;
        }

        var elements = new TirConst[length];
        for (var i = 0; i < length; i++)
        {
            var element = bytes.AsSpan(i * size, size);
            elements[i] = storage.Kind switch
            {
                TirTypeKind.I8 => new TirConst(storage, (sbyte)element[0]),
                TirTypeKind.I16 => new TirConst(storage, BinaryPrimitives.ReadInt16LittleEndian(element)),
                TirTypeKind.I32 => new TirConst(storage, BinaryPrimitives.ReadInt32LittleEndian(element)),
                TirTypeKind.I64 => new TirConst(storage, BinaryPrimitives.ReadInt64LittleEndian(element)),
                TirTypeKind.F32 => new TirConst(storage, (double)BinaryPrimitives.ReadSingleLittleEndian(element)),
                _ => new TirConst(storage, BinaryPrimitives.ReadDoubleLittleEndian(element)),
            };
        }

        return elements;
    }

    /// <summary>The int32 an ldc.i4 instruction pushes, or null for any other instruction.</summary>
    private static long? Integer(IlInstruction instruction) => instruction switch
    {
        { OpCode: >= IlOpCode.Ldc_I4_M1 and <= IlOpCode.Ldc_I4_8 } => (int)instruction.OpCode - (int)IlOpCode.Ldc_I4_0,
        { OpCode: IlOpCode.Ldc_I4_S or IlOpCode.Ldc_I4, Operand: IlInt32Operand constant } => constant.Value,
        _ => null,
    };

    /// <summary>True when nothing jumps into the instructions after <paramref name="first"/> up to <paramref name="last"/>.</summary>
    private static bool InOneBlock(IlMethodBody body, int first, int last)
    {
        var inside = body.Instructions.Skip(first + 1).Take(last - first).Select(instruction => instruction.Offset).ToHashSet();
        return !body.Instructions.SelectMany(IlBlock.Targets).Any(inside.Contains);
    }

    /// <summary>
    /// True when the block of the instruction at <paramref name="index"/> runs exactly once each time the method does:
    /// every way from the start to a ret goes through it, and no way leads from it back to it.
    /// </summary>
    private static bool RunsOnce(IlMethodBody body, int index)
    {
        var blocks = IlBlock.Split(body);
        var blockOf = new int[body.Instructions.Count];
        for (var b = 0; b < blocks.Count; b++)
        {
            Array.Fill(blockOf, b, blocks[b].First, blocks[b].End - blocks[b].First);
        }

        var block = blockOf[index];

        IEnumerable<int> Successors(int b)
        {
            foreach (var target in IlBlock.Targets(blocks[b].Last))
            {
                yield return blockOf[body.IndexOf(target)];
            }

            if (blocks[b].Last.OpCode is not (IlOpCode.Br or IlOpCode.Br_S or IlOpCode.Ret or IlOpCode.Throw
                or IlOpCode.Rethrow or IlOpCode.Leave or IlOpCode.Leave_S or IlOpCode.Endfinally or IlOpCode.Jmp)
                && b + 1 < blocks.Count)
            {
                yield return b + 1;
            }
        }

        // the blocks reached from the start without going through the block: none of them may end the method
        var seen = new HashSet<int>();
        var pending = new Stack<int>();
        if (block != 0)
        {
            pending.Push(0);
        }

        while (pending.TryPop(out var b))
        {
            if (b == block || !seen.Add(b))
            {
                continue;
            }

            if (blocks[b].Last.OpCode == IlOpCode.Ret || !Successors(b).Any())
            {
                return false;
            }

            foreach (var successor in Successors(b))
            {
                pending.Push(successor);
            }
        }

        // and from the block, it is never reached again
        seen.Clear();
        foreach (var successor in Successors(block))
        {
            pending.Push(successor);
        }

        while (pending.TryPop(out var b))
        {
            if (b == block)
            {
                return false;
            }

            if (seen.Add(b))
            {
                foreach (var successor in Successors(b))
                {
                    pending.Push(successor);
                }
            }
        }

        return true;
    }
}
