namespace ChipSharp.Frontend.Il;

/// <summary>
/// The IL of a method: its instructions in order, its locals, its stack depth and its exception-handling regions.
/// Every branch and switch target is the offset of one of <see cref="Instructions"/>.
/// </summary>
public sealed class IlMethodBody
{
    internal IlMethodBody(IlMethod method, int maxStack, bool initLocals, int codeSize, IReadOnlyList<IlLocal> locals,
        IReadOnlyList<IlInstruction> instructions, IReadOnlyList<IlExceptionRegion> exceptionRegions)
    {
        Method = method;
        MaxStack = maxStack;
        InitLocals = initLocals;
        CodeSize = codeSize;
        Locals = locals;
        Instructions = instructions;
        ExceptionRegions = exceptionRegions;
    }

    public IlMethod Method { get; }

    /// <summary>The most values the evaluation stack holds at once, as the compiler declared it.</summary>
    public int MaxStack { get; }

    /// <summary>True when the locals start zeroed (C# always sets it unless told to skip it).</summary>
    public bool InitLocals { get; }

    /// <summary>The length of the IL in bytes.</summary>
    public int CodeSize { get; }

    /// <summary>The local variables, by number. Their names live in the PDB, so they print as V_0, V_1, ...</summary>
    public IReadOnlyList<IlLocal> Locals { get; }

    /// <summary>The instructions in order of offset.</summary>
    public IReadOnlyList<IlInstruction> Instructions { get; }

    /// <summary>The try regions and their handlers, innermost first, as the metadata lists them.</summary>
    public IReadOnlyList<IlExceptionRegion> ExceptionRegions { get; }

    /// <summary>The index in <see cref="Instructions"/> of the instruction that starts at <paramref name="offset"/>, or -1.</summary>
    public int IndexOf(int offset)
    {
        int low = 0, high = Instructions.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var at = Instructions[middle].Offset;
            if (at == offset)
            {
                return middle;
            }

            if (at < offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return -1;
    }
}

/// <summary>A local variable: its number and its type. <c>ToString()</c> writes e.g. "int32 V_0".</summary>
public sealed record IlLocal(int Index, IlType Type)
{
    public override string ToString() => $"{Type} V_{Index}";
}

/// <summary>What kind of handler an exception-handling region has.</summary>
public enum IlExceptionRegionKind
{
    /// <summary><c>catch (T)</c>: runs when the exception is a <see cref="IlExceptionRegion.CatchType"/>.</summary>
    Catch,

    /// <summary><c>catch when (...)</c>: the filter at <see cref="IlExceptionRegion.FilterOffset"/> decides.</summary>
    Filter,

    /// <summary><c>finally</c>: runs however the try region is left.</summary>
    Finally,

    /// <summary>Runs only when the try region is left by an exception (C# never emits it).</summary>
    Fault,
}

/// <summary>
/// A try region and its handler, as offsets into the method's IL. The ends are exclusive: an end is the offset of
/// the first instruction after the region, or the code size.
/// </summary>
public sealed record IlExceptionRegion(
    IlExceptionRegionKind Kind,
    int TryOffset,
    int TryLength,
    int HandlerOffset,
    int HandlerLength,
    IlType? CatchType,
    int? FilterOffset)
{
    public int TryEnd => TryOffset + TryLength;

    public int HandlerEnd => HandlerOffset + HandlerLength;
}
