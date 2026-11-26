namespace ChipSharp.Frontend.Il;

/// <summary>
/// One decoded instruction: where it starts, its opcode and its operand (null when the opcode has none).
/// <see cref="ArgumentIndex"/> and <see cref="LocalIndex"/> also give the numbers that short forms such as
/// <c>ldarg.0</c> and <c>stloc.2</c> encode in the opcode itself.
/// </summary>
public sealed class IlInstruction
{
    internal IlInstruction(int offset, IlOpCode opCode, IlOperand? operand, int size)
    {
        Offset = offset;
        OpCode = opCode;
        Operand = operand;
        Size = size;
    }

    /// <summary>Where the instruction starts, in bytes from the start of the method's IL.</summary>
    public int Offset { get; }

    public IlOpCode OpCode { get; }

    /// <summary>The decoded operand; null when the opcode takes none.</summary>
    public IlOperand? Operand { get; }

    /// <summary>The instruction's length in bytes: the opcode (one or two bytes) and its operand.</summary>
    public int Size { get; }

    /// <summary>Where the next instruction starts: where execution goes when this one does not branch.</summary>
    public int NextOffset => Offset + Size;

    /// <summary>The name as ildasm writes it, e.g. "ldc.i4.s".</summary>
    public string Name => OpCode.GetName();

    /// <summary>
    /// The argument number of ldarg.0 to ldarg.3, ldarg(.s), ldarga(.s) and starg(.s); null for other instructions.
    /// In an instance method argument 0 is <c>this</c>.
    /// </summary>
    public int? ArgumentIndex => OpCode switch
    {
        IlOpCode.Ldarg_0 => 0,
        IlOpCode.Ldarg_1 => 1,
        IlOpCode.Ldarg_2 => 2,
        IlOpCode.Ldarg_3 => 3,
        _ => (Operand as IlArgumentOperand)?.Index,
    };

    /// <summary>The local number of ldloc.0 to stloc.3, ldloc(.s), ldloca(.s) and stloc(.s); null for other instructions.</summary>
    public int? LocalIndex => OpCode switch
    {
        IlOpCode.Ldloc_0 or IlOpCode.Stloc_0 => 0,
        IlOpCode.Ldloc_1 or IlOpCode.Stloc_1 => 1,
        IlOpCode.Ldloc_2 or IlOpCode.Stloc_2 => 2,
        IlOpCode.Ldloc_3 or IlOpCode.Stloc_3 => 3,
        _ => (Operand as IlLocalOperand)?.Index,
    };

    /// <summary>The instruction as <see cref="IlPrinter"/> writes it, e.g. "IL_0002:  call       int32 Program::Square(int32)".</summary>
    public override string ToString() => IlPrinter.Print(this);
}
