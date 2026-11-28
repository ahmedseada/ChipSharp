using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>
/// The instructions of a function whose code is written: those that do something (store, call, make an object, check,
/// end the block, read volatile memory), and those whose value one of them uses, through others, in the blocks the
/// function can reach (<see cref="Blocks"/>). A value nothing uses is not computed (C would warn of a variable never
/// read), and a store to a slot nothing reads is not made: that slot is not even declared (C would warn of a variable
/// set but not used). Each slot left out may leave others unread, so this goes on until none is. What only an
/// instruction left out names (a string literal whose address is never used, a function only a block nothing reaches
/// calls) is not written either (see <see cref="CReachable"/>).
/// </summary>
internal sealed record CLiveness(
    IReadOnlySet<TirInstruction> Live,
    IReadOnlySet<TirInstruction> Used,
    IReadOnlyDictionary<TirInstruction, int> Uses,
    IReadOnlySet<TirBlock> Blocks)
{
    public static CLiveness Find(TirFunction function)
    {
        var blocks = ReachableBlocks(function);
        var read = new HashSet<TirSlot>(function.Slots);
        while (true)
        {
            var live = new HashSet<TirInstruction>();
            var used = new HashSet<TirInstruction>();
            var uses = new Dictionary<TirInstruction, int>();
            foreach (var block in function.Blocks.Where(blocks.Contains))
            {
                // a value is used only later in its block: from the end, every use is seen before its value
                for (var i = block.Instructions.Count - 1; i >= 0; i--)
                {
                    var instruction = block.Instructions[i];
                    var acts = instruction switch
                    {
                        TirStore store => read.Contains(store.Slot),
                        TirPointerStore or TirCall or TirCallIndirect or TirNewObject or TirNewArray or TirNullCheck
                            or TirTerminator or TirPointerLoad { IsVolatile: true } => true,
                        _ => false,
                    };
                    if (!acts && !live.Contains(instruction))
                    {
                        continue;
                    }

                    live.Add(instruction);

                    // a branch that goes one way only uses no condition
                    var operands = instruction is TirBranch branch && Taken(branch) is not null ? [] : instruction.Operands;
                    foreach (var operand in operands.OfType<TirInstruction>())
                    {
                        live.Add(operand);
                        used.Add(operand);
                        uses[operand] = uses.GetValueOrDefault(operand) + 1;
                    }
                }
            }

            var stillRead = new HashSet<TirSlot>(live.Select(instruction => instruction switch
            {
                TirLoad load => load.Slot,
                TirSlotAddress address => address.Slot,
                _ => null,
            }).OfType<TirSlot>());
            if (stillRead.SetEquals(read))
            {
                return new CLiveness(live, used, uses, blocks);
            }

            read = stillRead;
        }
    }

    /// <summary>
    /// The block a branch on a constant goes to (C#'s <c>if (typeof(T) == typeof(float))</c> in an instance of generic
    /// code, which the frontend made true or false); null for a branch that may go either way.
    /// </summary>
    public static TirBlock? Taken(TirBranch branch) => Constant(branch.Condition) switch
    {
        null => null,
        0 => branch.WhenFalse,
        _ => branch.WhenTrue,
    };

    /// <summary>
    /// The blocks the function reaches from its entry, a branch on a constant going one way only: the code of the other
    /// way is not written, nor what only it calls or names (the double code of a generic sort's instance for ints).
    /// </summary>
    private static HashSet<TirBlock> ReachableBlocks(TirFunction function)
    {
        var reached = new HashSet<TirBlock>();
        var work = new Stack<TirBlock>();
        if (function.EntryBlock is { } entry)
        {
            work.Push(entry);
        }

        while (work.TryPop(out var block))
        {
            if (!reached.Add(block))
            {
                continue;
            }

            IReadOnlyList<TirBlock> next = block.Terminator switch
            {
                TirBranch branch when Taken(branch) is { } taken => [taken],
                { } terminator => terminator.Targets,
                null => [],
            };
            foreach (var target in next)
            {
                work.Push(target);
            }
        }

        return reached;
    }

    /// <summary>The value of an integer or bool made of constants alone (a constant, a comparison or conversion of them); null for any other.</summary>
    private static long? Constant(TirValue value)
    {
        switch (value)
        {
            case TirConst constant when constant.Type.IsInteger || constant.Type == TirType.Bool:
                return constant.IntegerValue;
            case TirConvert convert when !convert.Type.IsFloat && !convert.Value.Type.IsFloat
                && Constant(convert.Value) is { } converted:
                return Fits(converted, convert.Type, convert.Value.Type, convert.IsUnsigned);
            case TirCompare compare when !compare.Left.Type.IsFloat && !compare.Left.Type.IsAddress
                && Constant(compare.Left) is { } left && Constant(compare.Right) is { } right:
                var bits = compare.Left.Type == TirType.Bool ? 1 : Bits(compare.Left.Type);
                if (bits is null)
                {
                    return null;
                }

                var (signedLeft, signedRight) = (Signed(left, bits.Value), Signed(right, bits.Value));
                var (unsignedLeft, unsignedRight) = (Unsigned(left, bits.Value), Unsigned(right, bits.Value));
                return (compare.Op switch
                {
                    TirCompareOp.Eq => unsignedLeft == unsignedRight,
                    TirCompareOp.Ne => unsignedLeft != unsignedRight,
                    TirCompareOp.Lt => signedLeft < signedRight,
                    TirCompareOp.Le => signedLeft <= signedRight,
                    TirCompareOp.Gt => signedLeft > signedRight,
                    TirCompareOp.Ge => signedLeft >= signedRight,
                    TirCompareOp.LtUnsigned => unsignedLeft < unsignedRight,
                    TirCompareOp.LeUnsigned => unsignedLeft <= unsignedRight,
                    TirCompareOp.GtUnsigned => unsignedLeft > unsignedRight,
                    _ => unsignedLeft >= unsignedRight,
                }) ? 1 : 0;
            default:
                return null;
        }
    }

    /// <summary>A constant converted from <paramref name="from"/> to <paramref name="to"/>, as TIR converts it; null where the width is the target's.</summary>
    private static long? Fits(long value, TirType to, TirType from, bool unsigned)
    {
        var (toBits, fromBits) = (to == TirType.Bool ? 1 : Bits(to), from == TirType.Bool ? 1 : Bits(from));
        if (toBits is null || fromBits is null)
        {
            return null;
        }

        var widened = unsigned || from == TirType.Bool ? (long)Unsigned(value, fromBits.Value) : Signed(value, fromBits.Value);
        return (long)Unsigned(widened, toBits.Value);
    }

    /// <summary>The bits of an integer type; null for nint, whose width only the target knows.</summary>
    private static int? Bits(TirType type) => type.Kind switch
    {
        TirTypeKind.I8 => 8,
        TirTypeKind.I16 => 16,
        TirTypeKind.I32 => 32,
        TirTypeKind.I64 => 64,
        _ => null,
    };

    private static ulong Unsigned(long value, int bits) => bits >= 64 ? (ulong)value : (ulong)value & ((1UL << bits) - 1);

    private static long Signed(long value, int bits) => bits >= 64 ? value : (long)(Unsigned(value, bits) << (64 - bits)) >> (64 - bits);
}
