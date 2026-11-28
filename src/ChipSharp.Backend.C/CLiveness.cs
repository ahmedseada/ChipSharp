using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>
/// The instructions of a function whose code is written: those that do something (store, call, make an object, check,
/// end the block, read volatile memory), and those whose value one of them uses, through others. A value nothing uses
/// is not computed (C would warn of a variable never read), and a store to a slot nothing reads is not made: that slot
/// is not even declared (C would warn of a variable set but not used). Each slot left out may leave others unread, so
/// this goes on until none is. What only an instruction left out names (a string literal whose address is never used)
/// is not written either (see <see cref="CReachable"/>).
/// </summary>
internal sealed record CLiveness(
    IReadOnlySet<TirInstruction> Live,
    IReadOnlySet<TirInstruction> Used,
    IReadOnlyDictionary<TirInstruction, int> Uses)
{
    public static CLiveness Find(TirFunction function)
    {
        var read = new HashSet<TirSlot>(function.Slots);
        while (true)
        {
            var live = new HashSet<TirInstruction>();
            var used = new HashSet<TirInstruction>();
            var uses = new Dictionary<TirInstruction, int>();
            foreach (var block in function.Blocks)
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
                    foreach (var operand in instruction.Operands.OfType<TirInstruction>())
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
                return new CLiveness(live, used, uses);
            }

            read = stillRead;
        }
    }
}
