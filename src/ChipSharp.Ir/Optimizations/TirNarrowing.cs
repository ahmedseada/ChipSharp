namespace ChipSharp.Ir.Optimizations;

/// <summary>
/// Narrowing: arithmetic in 8 or 16 bits (32 for a long) where the values provably fit and the result is provably C#'s,
/// for chips whose registers are narrower than an <c>int</c>. On an 8-bit AVR every <c>int</c> operation takes four
/// registers and four instructions, and C# makes nearly everything an <c>int</c>: IL widens bytes and shorts to int32 on
/// its stack, and loop counters, indices and lengths are ints. From the ranges <see cref="TirRangeAnalysis"/> proves,
/// this rewrites each function of a module in place; the result means what it meant before for every value in range.
/// </summary>
/// <remarks>
/// <para>
/// A value narrowed to N bits is kept as its low N bits; where the full value is needed again (a call, a store to
/// memory, an index, a return, an operation that cannot be narrowed) it is sign- or zero-extended back, which gives the
/// full value exactly because its range fits N bits that way: zero-extended when it is never negative. The rules:
/// </para>
/// <list type="bullet">
/// <item>A slot whose every value fits N bits becomes an N-bit slot: <c>for (int i = 0; i &lt; 7; i++)</c> makes i an
/// i8.</item>
/// <item><c>add</c>, <c>sub</c>, <c>mul</c>, <c>and</c>, <c>or</c>, <c>xor</c>: done in N bits when the result fits N
/// bits. The low N bits of these depend only on the low N bits of the operands, so the operands need not fit: wrap-around
/// in C#'s 32 bits is the same as in N bits whenever the 32-bit result fits.</item>
/// <item><c>shl</c>: the same, when the amount is below N too (TIR's amounts are below the width).</item>
/// <item><c>shr</c>, <c>shr.un</c>: the value fits N bits (unsigned for shr.un, or a shr of a value that is never
/// negative) and the amount is below N.</item>
/// <item><c>div</c>, <c>rem</c>: both operands fit N bits unsigned (then signed and unsigned division agree), or signed
/// with a quotient that fits and never the smallest N-bit value divided by -1; <c>div.un</c>, <c>rem.un</c>: both fit
/// unsigned.</item>
/// <item>A comparison: both operands fit N bits the same way; when neither can be negative a signed comparison becomes
/// the unsigned one, which orders those values alike. Equality needs both in one N-bit reading too.</item>
/// <item>Conversions: a widening one keeps its operand as its narrow form, so <c>(int)b + 1</c> of a byte stays in
/// bytes where it fits; a narrowing one keeps the low bits of the narrowest form it has.</item>
/// </list>
/// <para>
/// Nothing else changes: function signatures, memory (fields, arrays, statics), floats, nint and the instructions that
/// are no arithmetic keep their types, and take the full values. checked arithmetic is not in TIR (ChipSharp does not
/// compile it yet), and a division's checks for 0 and -1 stay where they are, in front of it.
/// </para>
/// </remarks>
public static class TirNarrowing
{
    /// <summary>Narrows every defined function of <paramref name="module"/>.</summary>
    public static void Run(TirModule module, TirNarrowingOptions options)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(options);
        var ranges = TirRangeAnalysis.Run(module, options);
        foreach (var function in module.Functions.Where(f => f.Kind == TirFunctionKind.Defined))
        {
            Rewrite(function, ranges, options);
        }
    }

    private static void Rewrite(TirFunction function, TirRangeAnalysis ranges, TirNarrowingOptions options)
    {
        var cut = CutSlots(function);
        var comparedWithCut = ComparedWithCut(function, cut);
        var slots = new Dictionary<TirSlot, TirSlot>();
        foreach (var slot in function.Slots.ToList())
        {
            if (ranges.IsTracked(slot) && slot.Type.Kind is TirTypeKind.I16 or TirTypeKind.I32 or TirTypeKind.I64
                && !comparedWithCut.Contains(slot)
                && Narrower(ranges.RangeOf(slot), options.Bits(slot.Type), options) is { } type)
            {
                slots[slot] = function.ReplaceSlot(slot, type);
            }
        }

        foreach (var block in function.Blocks)
        {
            new BlockRewrite(block, ranges, options, slots, cut).Run();
        }
    }

    /// <summary>
    /// The slots compared with a value cut from a wider one (<see cref="IsCut"/>), such as an index with an array's
    /// length: that comparison stays wide, so a narrow slot would only give LLVM a second, wide copy of the counter.
    /// </summary>
    private static HashSet<TirSlot> ComparedWithCut(TirFunction function, HashSet<TirSlot> cut)
    {
        var compared = new HashSet<TirSlot>();
        foreach (var compare in function.Blocks.SelectMany(block => block.Instructions).OfType<TirCompare>()
            .Where(compare => compare.Left.Type.IsInteger))
        {
            if (IsCut(compare.Right, cut) && LoadedSlot(compare.Left) is { } left)
            {
                compared.Add(left);
            }

            if (IsCut(compare.Left, cut) && LoadedSlot(compare.Right) is { } right)
            {
                compared.Add(right);
            }
        }

        return compared;
    }

    /// <summary>The slot <paramref name="value"/> is loaded from, through integer conversions; null for any other value.</summary>
    private static TirSlot? LoadedSlot(TirValue value) => value switch
    {
        TirLoad load => load.Slot,
        TirConvert { Value.Type.IsInteger: true } convert => LoadedSlot(convert.Value),
        _ => null,
    };

    /// <summary>
    /// The integer slots that may hold the low bits of a wider value from elsewhere (see <see cref="IsCut"/>), directly
    /// or from another such slot.
    /// </summary>
    private static HashSet<TirSlot> CutSlots(TirFunction function)
    {
        var cut = new HashSet<TirSlot>();
        var stores = function.Blocks.SelectMany(block => block.Instructions).OfType<TirStore>()
            .Where(store => store.Value.Type.IsInteger).ToList();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var store in stores.Where(store => !cut.Contains(store.Slot) && IsCut(store.Value, cut)))
            {
                cut.Add(store.Slot);
                changed = true;
            }
        }

        return cut;
    }

    /// <summary>
    /// True when the integer <paramref name="value"/> is, or is computed from, the low bits of a value as wide as an int
    /// that comes from elsewhere: a parameter, a call, a load from memory, an array's length (and the slots in
    /// <paramref name="cut"/>). Its range may be known to fit, but LLVM does not know it, so it cannot see that two such
    /// comparisons, one of the low bits and one of the whole value, agree. Constants and values that are narrow in TIR
    /// already (a byte loaded or passed) are not cut.
    /// </summary>
    private static bool IsCut(TirValue value, HashSet<TirSlot> cut) => value switch
    {
        TirConst or TirZero => false,
        TirLoad load => cut.Contains(load.Slot),
        TirConvert { Value.Type.IsInteger: true } convert => IsCut(convert.Value, cut),
        TirBinary binary => IsCut(binary.Left, cut) || IsCut(binary.Right, cut),
        _ => value.Type.Kind is not (TirTypeKind.I8 or TirTypeKind.I16),
    };

    /// <summary>The narrowest allowed type below <paramref name="bits"/> whose one reading or the other holds every value of the range.</summary>
    private static TirType? Narrower(TirRange range, int bits, TirNarrowingOptions options)
    {
        foreach (var width in options.Widths.Where(w => w < bits).Order())
        {
            if (range.FitsUnsigned(width) || range.FitsSigned(width))
            {
                return TypeOf(width);
            }
        }

        return null;
    }

    private static TirType TypeOf(int bits) => bits switch
    {
        8 => TirType.I8,
        16 => TirType.I16,
        32 => TirType.I32,
        _ => throw new ArgumentOutOfRangeException(nameof(bits), bits, "Narrowing goes to 8, 16 or 32 bits."),
    };

    /// <summary>The rewrite of one block, instruction by instruction, into a new list.</summary>
    private sealed class BlockRewrite(TirBlock block, TirRangeAnalysis ranges, TirNarrowingOptions options,
        IReadOnlyDictionary<TirSlot, TirSlot> slots, HashSet<TirSlot> cut)
    {
        private readonly List<TirInstruction> _output = [];

        /// <summary>For each value of the block: what stands for it now, of its own type.</summary>
        private readonly Dictionary<TirValue, TirValue> _same = [];

        /// <summary>
        /// For an integer value: a value of a narrower type that holds its low bits, and whose sign or zero extension is
        /// the value itself (its range fits that width).
        /// </summary>
        private readonly Dictionary<TirValue, Narrow> _narrow = [];

        /// <summary>The low bits of a value, as each narrower type, once made.</summary>
        private readonly Dictionary<(TirValue, TirType), TirValue> _lowBits = [];

        /// <summary>Integer constants, which are written only where something takes them.</summary>
        private readonly HashSet<TirInstruction> _pending = [];

        public void Run()
        {
            foreach (var instruction in block.Instructions)
            {
                Rewrite(instruction);
            }

            block.ReplaceInstructions(_output);
        }

        private void Rewrite(TirInstruction instruction)
        {
            switch (instruction)
            {
                case TirConst { Type.IsInteger: true } or TirZero { Type.IsInteger: true }:
                    _pending.Add(instruction);
                    return;

                case TirBinary { Type.IsInteger: true } binary when Narrowed(binary) is { Op: var op, Type: var type }:
                    var narrowed = Emit(new TirBinary(op, LowBits(binary.Left, type), LowBits(binary.Right, type)));
                    _narrow[binary] = new Narrow(narrowed, Range(binary).FitsUnsigned(Bits(type)));
                    return;

                case TirCompare { Left.Type.IsInteger: true } compare when Narrowed(compare) is { Op: var op, Type: var type }:
                    _same[compare] = Emit(new TirCompare(op, LowBits(compare.Left, type), LowBits(compare.Right, type)));
                    return;

                case TirConvert { Type.IsInteger: true, Value.Type.IsInteger: true } convert:
                    RewriteConvert(convert);
                    return;

                case TirLoad load when slots.TryGetValue(load.Slot, out var slot):
                    // extended back as every value of the slot allows
                    _narrow[load] = new Narrow(Emit(new TirLoad(slot)), ranges.RangeOf(load.Slot).FitsUnsigned(Bits(slot.Type)));
                    return;

                case TirStore store when slots.TryGetValue(store.Slot, out var slot):
                    Emit(new TirStore(slot, LowBits(store.Value, slot.Type)));
                    return;

                default:
                    var copy = Copy(instruction);
                    _output.Add(copy);
                    if (instruction.HasResult)
                    {
                        _same[instruction] = copy;
                    }

                    return;
            }
        }

        /// <summary>
        /// A widening conversion's narrow form is its operand (or the operand's own); a narrowing one is the low bits;
        /// one between nint and an integer of its width changes nothing, and keeps the operand's narrow form.
        /// </summary>
        private void RewriteConvert(TirConvert convert)
        {
            var (from, to) = (Bits(convert.Value.Type), Bits(convert.Type));
            var range = Range(convert);
            if (to > from)
            {
                _narrow[convert] = _narrow.TryGetValue(convert.Value, out var narrower) && Fits(range, Bits(narrower.Value.Type))
                    ? narrower with { Unsigned = range.FitsUnsigned(Bits(narrower.Value.Type)) }
                    : new Narrow(Value(convert.Value), convert.IsUnsigned);
                return;
            }

            if (to < from)
            {
                _same[convert] = LowBits(convert.Value, convert.Type);
            }
            else
            {
                var copy = Copy(convert);
                _output.Add(copy);
                _same[convert] = copy;
            }

            if (_narrow.TryGetValue(convert.Value, out var narrow) && Bits(narrow.Value.Type) < to
                && Fits(range, Bits(narrow.Value.Type)))
            {
                _narrow[convert] = narrow with { Unsigned = range.FitsUnsigned(Bits(narrow.Value.Type)) };
            }
        }

        /// <summary>The operation and type a binary operation is done in, narrowest first; null when it stays as it is.</summary>
        private (TirBinaryOp Op, TirType Type)? Narrowed(TirBinary binary)
        {
            var (result, left, right) = (Range(binary), Range(binary.Left), Range(binary.Right));
            if (result.IsEmpty || left.IsEmpty || right.IsEmpty)
            {
                return null;
            }

            // a multiplication, division or shift of 32 bits is a helper or a loop on a small chip, its narrow form an
            // instruction or a few: worth cutting a wide operand for; the cheap operations narrow narrow operands only
            var costly = binary.Op is not (TirBinaryOp.Add or TirBinaryOp.Sub or TirBinaryOp.And or TirBinaryOp.Or
                or TirBinaryOp.Xor);
            if (!costly && (!HasNarrowForm(binary.Left) || !HasNarrowForm(binary.Right)))
            {
                return null;
            }

            foreach (var bits in options.Widths.Where(w => w < Bits(binary.Type)).Order())
            {
                var amount = right.Min >= 0 && right.Max < bits;
                TirBinaryOp? op = binary.Op switch
                {
                    TirBinaryOp.Add or TirBinaryOp.Sub or TirBinaryOp.Mul or TirBinaryOp.And or TirBinaryOp.Or
                        or TirBinaryOp.Xor when Fits(result, bits) => binary.Op,
                    TirBinaryOp.Shl when amount && Fits(result, bits) => TirBinaryOp.Shl,
                    TirBinaryOp.Shr when amount && left.FitsUnsigned(bits) => TirBinaryOp.ShrUnsigned,
                    TirBinaryOp.Shr when amount && left.FitsSigned(bits) => TirBinaryOp.Shr,
                    TirBinaryOp.ShrUnsigned when amount && left.FitsUnsigned(bits) => TirBinaryOp.ShrUnsigned,
                    TirBinaryOp.Div or TirBinaryOp.Rem when left.FitsUnsigned(bits) && right.FitsUnsigned(bits) =>
                        binary.Op == TirBinaryOp.Div ? TirBinaryOp.DivUnsigned : TirBinaryOp.RemUnsigned,
                    TirBinaryOp.Div or TirBinaryOp.Rem when left.FitsSigned(bits) && right.FitsSigned(bits)
                        && result.FitsSigned(bits) && !(left.Contains(TirRange.Signed(bits).Min) && right.Contains(-1)) =>
                        binary.Op,
                    TirBinaryOp.DivUnsigned or TirBinaryOp.RemUnsigned
                        when left.FitsUnsigned(bits) && right.FitsUnsigned(bits) => binary.Op,
                    _ => null,
                };
                if (op is { } narrowed)
                {
                    return (narrowed, TypeOf(bits));
                }
            }

            return null;
        }

        /// <summary>The comparison and type a comparison is done in: where both operands fit, read alike.</summary>
        private (TirCompareOp Op, TirType Type)? Narrowed(TirCompare compare)
        {
            var (left, right) = (Range(compare.Left), Range(compare.Right));
            if (left.IsEmpty || right.IsEmpty || !IsNarrow(compare.Left) || !IsNarrow(compare.Right))
            {
                return null;
            }

            var both = left.Join(right);
            foreach (var bits in options.Widths.Where(w => w < Bits(compare.Left.Type)).Order())
            {
                if (both.FitsUnsigned(bits))
                {
                    return (Unsigned(compare.Op), TypeOf(bits));
                }

                if (both.FitsSigned(bits))
                {
                    return (compare.Op, TypeOf(bits));
                }
            }

            return null;
        }

        private static TirCompareOp Unsigned(TirCompareOp op) => op switch
        {
            TirCompareOp.Lt => TirCompareOp.LtUnsigned,
            TirCompareOp.Le => TirCompareOp.LeUnsigned,
            TirCompareOp.Gt => TirCompareOp.GtUnsigned,
            TirCompareOp.Ge => TirCompareOp.GeUnsigned,
            _ => op,
        };

        /// <summary>What stands for <paramref name="value"/> in the new block, of its own type: its extension, when only its narrow form was made.</summary>
        private TirValue Value(TirValue value)
        {
            if (value is TirParameter)
            {
                return value;
            }

            if (_same.TryGetValue(value, out var same))
            {
                return same;
            }

            if (value is TirInstruction instruction && _pending.Remove(instruction))
            {
                _output.Add(instruction);
                _same[value] = value;
                return value;
            }

            if (_narrow.TryGetValue(value, out var narrow))
            {
                var extended = Emit(new TirConvert(narrow.Value, value.Type, narrow.Unsigned));
                _same[value] = extended;
                return extended;
            }

            throw new TirException($"Narrowing reached '{value}' in block '{block.Name}' of '{block.Function.Name}' " +
                "before it was defined.");
        }

        /// <summary>The low bits of the integer <paramref name="value"/>, as <paramref name="type"/>, no wider than it.</summary>
        private TirValue LowBits(TirValue value, TirType type)
        {
            if (value.Type == type)
            {
                return Value(value);
            }

            if (_lowBits.TryGetValue((value, type), out var made))
            {
                return made;
            }

            if (value is TirConst or TirZero)
            {
                var bits = Bits(type);
                var constant = value is TirConst c ? c.IntegerValue : 0;
                made = Emit(new TirConst(type, bits >= 64 ? constant : (constant << (64 - bits)) >> (64 - bits)));
            }
            else if (_narrow.TryGetValue(value, out var narrow))
            {
                // a narrower form: extended, the way the value's range allows; a wider one: cut
                made = narrow.Value.Type == type ? narrow.Value : Emit(new TirConvert(narrow.Value, type, narrow.Unsigned));
            }
            else
            {
                made = Emit(new TirConvert(Value(value), type));
            }

            _lowBits[(value, type)] = made;
            return made;
        }

        private T Emit<T>(T instruction) where T : TirInstruction
        {
            _output.Add(instruction);
            return instruction;
        }

        /// <summary>The instruction with each operand replaced by what stands for it now; the instruction itself when none changed.</summary>
        private TirInstruction Copy(TirInstruction instruction)
        {
            var operands = instruction.Operands;
            var mapped = new TirValue[operands.Count];
            var changed = false;
            for (var i = 0; i < mapped.Length; i++)
            {
                mapped[i] = Value(operands[i]);
                changed |= !ReferenceEquals(mapped[i], operands[i]);
            }

            if (!changed)
            {
                return instruction;
            }

            return instruction switch
            {
                TirBinary b => new TirBinary(b.Op, mapped[0], mapped[1]),
                TirCompare c => new TirCompare(c.Op, mapped[0], mapped[1]),
                TirConvert c => new TirConvert(mapped[0], c.Type, c.IsUnsigned),
                TirStore s => new TirStore(s.Slot, mapped[0]),
                TirCall c => new TirCall(c.Callee, mapped),
                TirCallIndirect c => new TirCallIndirect(c.Type, mapped[0], mapped[1..]),
                TirReturn => new TirReturn(mapped[0]),
                TirBranch b => new TirBranch(mapped[0], b.WhenTrue, b.WhenFalse),
                TirPointerLoad l => new TirPointerLoad(l.Type, mapped[0], l.IsVolatile) { IsUnaligned = l.IsUnaligned },
                TirPointerStore s => new TirPointerStore(mapped[0], mapped[1], s.IsVolatile) { IsUnaligned = s.IsUnaligned },
                TirFieldAddress f => new TirFieldAddress(mapped[0], f.StructType, f.FieldIndex),
                TirElementAddress e => new TirElementAddress(mapped[0], e.ElementType, mapped[1]),
                TirPtrToInt p => new TirPtrToInt(mapped[0], p.Type),
                TirIntToPtr => new TirIntToPtr(mapped[0]),
                TirNewArray n => new TirNewArray(n.TypeDescriptor, mapped[0]),
                TirNullCheck => new TirNullCheck(mapped[0]),
                TirArrayLength => new TirArrayLength(mapped[0]),
                TirArrayData d => new TirArrayData(mapped[0], d.ElementType),
                TirArrayObject => new TirArrayObject(mapped[0]),
                TirTypeOf => new TirTypeOf(mapped[0]),
                _ => throw new TirException($"Narrowing cannot copy '{instruction}': it does not know the instruction."),
            };
        }

        /// <summary>
        /// True for a constant or a value with a narrow form. An operation narrows only these: cutting a wide value
        /// first (<c>trunc (shl i32 1, %n)</c>, a parameter, an int loaded) would cost the cut and the extension after,
        /// and hide from LLVM what it makes of the wide operation itself; where only the low bits of a wide result are
        /// kept, LLVM narrows the operations under the cut on its own.
        /// </summary>
        private bool HasNarrowForm(TirValue value) => value is TirConst or TirZero || _narrow.ContainsKey(value);

        /// <summary>
        /// True for a constant, or a value narrowed already that is not cut from a wider one (see
        /// <see cref="IsCut"/>). A comparison narrows only these: comparing the low bits of a length
        /// (<c>(ushort)i &lt; (ushort)length</c>) would hide from LLVM that the check of the same index against the same
        /// length is always true, and that check would stay.
        /// </summary>
        private bool IsNarrow(TirValue value) =>
            value is TirConst or TirZero || (_narrow.ContainsKey(value) && !IsCut(value, cut));

        private TirRange Range(TirValue value) => ranges.RangeOf(value);

        private int Bits(TirType type) => options.Bits(type);

        private static bool Fits(TirRange range, int bits) => range.FitsUnsigned(bits) || range.FitsSigned(bits);

        /// <summary>
        /// The narrow form of a value: its low bits, and how they extend back to it (zero-extended when
        /// <paramref name="Unsigned"/>, else sign-extended), as its range allows.
        /// </summary>
        private readonly record struct Narrow(TirValue Value, bool Unsigned);
    }
}
