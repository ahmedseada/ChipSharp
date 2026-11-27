namespace ChipSharp.Ir;

/// <summary>
/// A straight run of instructions that ends with exactly one terminator (return, jump or branch).
/// A temporary (an instruction's result) can only be used later in the same block; values that must
/// reach another block go through a slot. Created with <see cref="TirFunction.AddBlock"/>.
/// </summary>
public sealed class TirBlock
{
    private readonly List<TirInstruction> _instructions = [];

    internal TirBlock(TirFunction function, string name)
    {
        Function = function;
        Name = name;
    }

    public TirFunction Function { get; }

    /// <summary>The label the printer writes, e.g. "entry" or "loop". Unique within the function.</summary>
    public string Name { get; }

    public IReadOnlyList<TirInstruction> Instructions => _instructions;

    /// <summary>The last instruction when it is a terminator; null while the block is still open.</summary>
    public TirTerminator? Terminator => _instructions.Count > 0 ? _instructions[^1] as TirTerminator : null;

    /// <summary>
    /// Adds an instruction at the end and returns it, so its result can be used right away.
    /// An instruction belongs to one block only. Nothing is checked here; <see cref="TirVerifier"/> does that.
    /// </summary>
    public T Append<T>(T instruction) where T : TirInstruction
    {
        ArgumentNullException.ThrowIfNull(instruction);
        if (instruction.Block is { } owner)
        {
            throw new TirException(
                $"'{TirPrinter.Print(instruction)}' is already in block '{owner.Name}' of function '{owner.Function.Name}'; " +
                "an instruction belongs to one block only.");
        }

        instruction.Block = this;
        _instructions.Add(instruction);
        return instruction;
    }

    /// <summary>
    /// Makes <paramref name="instructions"/> the block's instructions, for a pass that rewrites a block
    /// (<see cref="Optimizations.TirNarrowing"/>): each is either one of the block's own or in no block yet. The ones
    /// left out belong to no block afterwards.
    /// </summary>
    internal void ReplaceInstructions(IReadOnlyList<TirInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Block is { } owner && owner != this)
            {
                throw new TirException(
                    $"'{TirPrinter.Print(instruction)}' is in block '{owner.Name}'; it cannot move to block '{Name}'.");
            }
        }

        var kept = new HashSet<TirInstruction>(instructions);
        foreach (var old in _instructions.Where(old => !kept.Contains(old)))
        {
            old.Block = null;
        }

        _instructions.Clear();
        foreach (var instruction in instructions)
        {
            instruction.Block = this;
            _instructions.Add(instruction);
        }
    }

    public override string ToString() => Name;
}
