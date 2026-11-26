using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// A basic block of IL: a run of instructions that control enters only at the first and leaves only after the last.
/// It becomes one TIR block (plus one more per extra case when it ends with a <c>switch</c>).
/// </summary>
internal sealed class IlBlock
{
    private IlBlock(IlMethodBody body, int first, int end)
    {
        Body = body;
        First = first;
        End = end;
    }

    public IlMethodBody Body { get; }

    /// <summary>The index in <see cref="IlMethodBody.Instructions"/> of the first instruction.</summary>
    public int First { get; }

    /// <summary>The index after the last instruction: where the next block starts.</summary>
    public int End { get; }

    public int Offset => Body.Instructions[First].Offset;

    public IlInstruction Last => Body.Instructions[End - 1];

    /// <summary>The TIR block it becomes, named after its offset, e.g. "IL_000c".</summary>
    public TirBlock Tir { get; set; } = null!;

    /// <summary>
    /// When it ends with a <c>switch</c> of n targets: the n - 1 TIR blocks that test cases 1 to n - 1
    /// (the block itself tests case 0).
    /// </summary>
    public List<TirBlock> SwitchCases { get; } = [];

    /// <summary>The types on the evaluation stack where it starts, bottom first; null until a way in is seen.</summary>
    public IReadOnlyList<TirType>? EntryStack { get; set; }

    /// <summary>
    /// True when no instruction before it leads to it, so its stack was taken to be empty, as IL requires
    /// (only a later instruction jumps back to it, or nothing does).
    /// </summary>
    public bool EntryStackAssumed { get; set; }

    /// <summary>
    /// Cuts a body into blocks, in order of offset: a block starts at offset 0, at every branch and switch target,
    /// and after every branch, switch and ret.
    /// </summary>
    public static IReadOnlyList<IlBlock> Split(IlMethodBody body)
    {
        var instructions = body.Instructions;
        var starts = new bool[instructions.Count];
        starts[0] = true;
        for (var i = 0; i < instructions.Count; i++)
        {
            foreach (var target in Targets(instructions[i]))
            {
                starts[body.IndexOf(target)] = true;
            }

            if (EndsBlock(instructions[i]) && i + 1 < instructions.Count)
            {
                starts[i + 1] = true;
            }
        }

        var blocks = new List<IlBlock>();
        for (var first = 0; first < instructions.Count;)
        {
            var end = first + 1;
            while (end < instructions.Count && !starts[end])
            {
                end++;
            }

            blocks.Add(new IlBlock(body, first, end));
            first = end;
        }

        return blocks;
    }

    /// <summary>Where a branch or switch can go (not counting the next instruction); nothing for other instructions.</summary>
    public static IReadOnlyList<int> Targets(IlInstruction instruction) => instruction.Operand switch
    {
        IlBranchOperand branch => [branch.Target],
        IlSwitchOperand @switch => @switch.Targets,
        _ => [],
    };

    /// <summary>True for the instructions after which a new block starts: branches, switch and ret.</summary>
    public static bool EndsBlock(IlInstruction instruction) =>
        instruction.Operand is IlBranchOperand or IlSwitchOperand || instruction.OpCode == IlOpCode.Ret;
}
