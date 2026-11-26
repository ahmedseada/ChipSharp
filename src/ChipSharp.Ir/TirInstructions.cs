namespace ChipSharp.Ir;

/// <summary>Something an instruction can use as an operand: a <see cref="TirParameter"/> or another instruction's result.</summary>
public abstract class TirValue
{
    private protected TirValue(TirType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
    }

    public TirType Type { get; }
}

/// <summary>
/// One step of a block. An instruction whose <see cref="TirValue.Type"/> is not void produces a temporary,
/// printed as <c>%0</c>, <c>%1</c>, ...: defined once, and usable only later in the same block.
/// Store, a call to a void function and the terminators produce nothing.
/// </summary>
public abstract class TirInstruction : TirValue
{
    private protected TirInstruction(TirType type) : base(type)
    {
    }

    /// <summary>The block this instruction was appended to; null until <see cref="TirBlock.Append"/>.</summary>
    public TirBlock? Block { get; internal set; }

    /// <summary>True when the instruction produces a value (its type is not void).</summary>
    public bool HasResult => Type != TirType.Void;

    /// <summary>Every value the instruction reads, in order (not slots, blocks or callees).</summary>
    public abstract IReadOnlyList<TirValue> Operands { get; }

    /// <summary>The instruction as <see cref="TirPrinter"/> writes it, e.g. "%2 = add i32 %0, %1".</summary>
    public override string ToString() => TirPrinter.Print(this);
}

/// <summary>A constant: <c>%0 = const i32 42</c>, <c>const bool true</c>, <c>const f64 1.5</c>.</summary>
public sealed class TirConst : TirInstruction
{
    /// <summary>
    /// An integer or bool constant (a float type also works: the value is converted).
    /// Integers are signless, so i8 takes -128 to 255 and i32 takes int.MinValue to uint.MaxValue;
    /// i64 takes any long (write a ulong as <c>unchecked((long)value)</c>). An nint constant must fit in
    /// 32 bits signed, so it means the same on every target; build bigger ones with <see cref="TirConvert"/>.
    /// </summary>
    public TirConst(TirType type, long value) : base(type)
    {
        if (type.IsFloat)
        {
            FloatValue = type == TirType.F32 ? (float)value : value;
            return;
        }

        IntegerValue = type.Kind switch
        {
            TirTypeKind.Void => throw new TirException("A constant cannot have type void."),
            TirTypeKind.Ptr => throw new TirException(
                "A constant cannot have type ptr; null is 'zero ptr', and inttoptr turns a number into an address."),
            TirTypeKind.Ref => throw new TirException(
                "A constant cannot have type ref; null is 'zero ref', and only newobject and newarray make objects."),
            TirTypeKind.Struct or TirTypeKind.Array => throw new TirException(
                $"A constant cannot have type {type}; only bool, integers and floats have constants " +
                "('zero' makes a value of any type with every byte zero)."),
            TirTypeKind.Bool => value is 0 or 1
                ? value
                : throw new TirException($"A bool constant must be 0 or 1, not {Text(value)}."),
            TirTypeKind.I8 => value is >= sbyte.MinValue and <= byte.MaxValue
                ? (sbyte)value
                : throw OutOfRange(value, type, sbyte.MinValue, byte.MaxValue),
            TirTypeKind.I16 => value is >= short.MinValue and <= ushort.MaxValue
                ? (short)value
                : throw OutOfRange(value, type, short.MinValue, ushort.MaxValue),
            TirTypeKind.I32 => value is >= int.MinValue and <= uint.MaxValue
                ? (int)value
                : throw OutOfRange(value, type, int.MinValue, uint.MaxValue),
            TirTypeKind.NativeInt => value is >= int.MinValue and <= int.MaxValue
                ? value
                : throw OutOfRange(value, type, int.MinValue, int.MaxValue),
            _ => value,
        };
    }

    /// <summary>A floating-point constant; the type must be f32 or f64 (an f32 value is rounded to float).</summary>
    public TirConst(TirType type, double value) : base(type)
    {
        if (!type.IsFloat)
        {
            throw new TirException($"A floating-point constant needs type f32 or f64, not {type}.");
        }

        FloatValue = type == TirType.F32 ? (float)value : value;
    }

    /// <summary>A bool constant.</summary>
    public TirConst(bool value) : base(TirType.Bool)
    {
        IntegerValue = value ? 1 : 0;
    }

    /// <summary>
    /// The value of an integer or bool constant, sign-extended from its width: an i8 constant made from 255
    /// reads back as -1 (the same bits). Bool is 0 or 1. An nint constant is within int's range.
    /// </summary>
    public long IntegerValue { get; }

    /// <summary>The value of an f32 or f64 constant.</summary>
    public double FloatValue { get; }

    public override IReadOnlyList<TirValue> Operands => [];

    private static string Text(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static TirException OutOfRange(long value, TirType type, long min, long max) =>
        new($"The constant {Text(value)} does not fit in {type}, which takes {Text(min)} to {Text(max)}.");
}

/// <summary>
/// Arithmetic and bit operations. Unsigned variants read their operands as unsigned; the others as signed
/// (division rounds toward zero; the remainder has the sign of the left operand, as in C#).
/// </summary>
public enum TirBinaryOp
{
    Add,
    Sub,
    Mul,
    Div,
    DivUnsigned,
    Rem,
    RemUnsigned,
    And,
    Or,
    Xor,

    /// <summary>Shift left. The amount has the operand type and must be smaller than its width (C# compilers mask it).</summary>
    Shl,

    /// <summary>Shift right, copying the sign bit (C#'s <c>&gt;&gt;</c> on signed types).</summary>
    Shr,

    /// <summary>Shift right, filling with zeros (C#'s <c>&gt;&gt;</c> on unsigned types, <c>&gt;&gt;&gt;</c>).</summary>
    ShrUnsigned,
}

/// <summary>
/// <c>%2 = add i32 %0, %1</c>. Both operands have the same type, which is also the result type.
/// Arithmetic (add, sub, mul, div, rem) works on integers and floats; and, or, xor on integers and bool;
/// the unsigned variants and the shifts on integers only.
/// </summary>
public sealed class TirBinary : TirInstruction
{
    public TirBinary(TirBinaryOp op, TirValue left, TirValue right)
        : base((left ?? throw new ArgumentNullException(nameof(left))).Type)
    {
        ArgumentNullException.ThrowIfNull(right);
        Op = op;
        Left = left;
        Right = right;
    }

    public TirBinaryOp Op { get; }
    public TirValue Left { get; }
    public TirValue Right { get; }

    public override IReadOnlyList<TirValue> Operands => [Left, Right];
}

/// <summary>
/// Comparisons. The unsigned variants read integer operands as unsigned; the others as signed. On floats the
/// unsigned variants are unordered: also true when either side is NaN, which is what IL's <c>clt.un</c>,
/// <c>cgt.un</c>, <c>bge.un</c> and the like mean on floats.
/// </summary>
public enum TirCompareOp
{
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    LtUnsigned,
    LeUnsigned,
    GtUnsigned,
    GeUnsigned,
}

/// <summary>
/// <c>%2 = lt i32 %0, %1</c>: compares two operands of the same type and produces a bool.
/// eq and ne work on integers, floats, bool, ptr and ref; lt, le, gt, ge on integers and floats; the unsigned variants
/// on integers, floats (unordered, see <see cref="TirCompareOp"/>), ptr and ref. Structs and arrays cannot be compared.
/// </summary>
public sealed class TirCompare : TirInstruction
{
    public TirCompare(TirCompareOp op, TirValue left, TirValue right) : base(TirType.Bool)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        Op = op;
        Left = left;
        Right = right;
    }

    public TirCompareOp Op { get; }
    public TirValue Left { get; }
    public TirValue Right { get; }

    public override IReadOnlyList<TirValue> Operands => [Left, Right];
}

/// <summary>
/// <c>%1 = convert i32 %0 to i64</c> (signed) or <c>%1 = uconvert i32 %0 to f64</c> (unsigned).
/// The result type is <see cref="TirValue.Type"/>. Narrowing an integer keeps the low bits. When unsigned,
/// the integer side is read as unsigned: the source when widening or turning an integer into a float, the
/// result when turning a float into an integer. A float becomes an integer rounded toward zero, and saturates as
/// .NET does: a value beyond the integer type's range gives its smallest or largest value, and NaN gives 0. An
/// integer becomes a float, and a float a narrower float, rounded to nearest (ties to even). A bool converts to
/// 0 or 1. Nothing converts to bool (compare with zero instead). Converting to the same type, or between nint and
/// an integer of the target's pointer width, changes nothing. It works on integers, floats and bool only:
/// <see cref="TirPtrToInt"/> and <see cref="TirIntToPtr"/> turn addresses into numbers and back.
/// </summary>
public sealed class TirConvert : TirInstruction
{
    public TirConvert(TirValue value, TirType to, bool unsigned = false) : base(to)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        IsUnsigned = unsigned;
    }

    public TirValue Value { get; }

    /// <summary>True when the integer side is read as unsigned (zero-extend, unsigned int to float, float to unsigned int).</summary>
    public bool IsUnsigned { get; }

    public override IReadOnlyList<TirValue> Operands => [Value];
}

/// <summary>
/// <c>%0 = load i32 $x</c>: reads a slot. The result has the slot's type. (<see cref="TirPointerLoad"/> reads
/// memory at an address.)
/// </summary>
public sealed class TirLoad : TirInstruction
{
    public TirLoad(TirSlot slot) : base((slot ?? throw new ArgumentNullException(nameof(slot))).Type)
    {
        Slot = slot;
    }

    public TirSlot Slot { get; }

    public override IReadOnlyList<TirValue> Operands => [];
}

/// <summary>
/// <c>store $x, %0</c>: writes a value of the slot's type into a slot. Produces nothing. (<see cref="TirPointerStore"/>
/// writes memory at an address.)
/// </summary>
public sealed class TirStore : TirInstruction
{
    public TirStore(TirSlot slot, TirValue value) : base(TirType.Void)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(value);
        Slot = slot;
        Value = value;
    }

    public TirSlot Slot { get; }
    public TirValue Value { get; }

    public override IReadOnlyList<TirValue> Operands => [Value];
}

/// <summary>
/// <c>%1 = call i32 Program.Square(%0)</c>: calls a function of the module, defined or external.
/// The result type is the callee's return type; a call to a void function produces nothing.
/// </summary>
public sealed class TirCall : TirInstruction
{
    public TirCall(TirFunction callee, IEnumerable<TirValue> arguments)
        : base((callee ?? throw new ArgumentNullException(nameof(callee))).ReturnType)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        Callee = callee;
        Arguments = [.. arguments];
        if (Arguments.Any(a => a is null))
        {
            throw new ArgumentException("An argument is null.", nameof(arguments));
        }
    }

    public TirFunction Callee { get; }

    /// <summary>One value per callee parameter, in order.</summary>
    public IReadOnlyList<TirValue> Arguments { get; }

    public override IReadOnlyList<TirValue> Operands => Arguments;
}

/// <summary>The last instruction of every block: <see cref="TirReturn"/>, <see cref="TirJump"/> or <see cref="TirBranch"/>.</summary>
public abstract class TirTerminator : TirInstruction
{
    private protected TirTerminator() : base(TirType.Void)
    {
    }

    /// <summary>The blocks control can go to next (none for a return).</summary>
    public abstract IReadOnlyList<TirBlock> Targets { get; }
}

/// <summary><c>return %0</c>, or <c>return</c> in a void function.</summary>
public sealed class TirReturn(TirValue? value = null) : TirTerminator
{
    /// <summary>The value returned; null in a void function.</summary>
    public TirValue? Value { get; } = value;

    public override IReadOnlyList<TirValue> Operands => Value is null ? [] : [Value];
    public override IReadOnlyList<TirBlock> Targets => [];
}

/// <summary><c>jump loop</c>: continues at another block of the same function.</summary>
public sealed class TirJump : TirTerminator
{
    public TirJump(TirBlock target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
    }

    public TirBlock Target { get; }

    public override IReadOnlyList<TirValue> Operands => [];
    public override IReadOnlyList<TirBlock> Targets => [Target];
}

/// <summary><c>branch %0, then, else</c>: continues at one of two blocks, depending on a bool.</summary>
public sealed class TirBranch : TirTerminator
{
    public TirBranch(TirValue condition, TirBlock whenTrue, TirBlock whenFalse)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(whenTrue);
        ArgumentNullException.ThrowIfNull(whenFalse);
        Condition = condition;
        WhenTrue = whenTrue;
        WhenFalse = whenFalse;
    }

    /// <summary>Must be a bool.</summary>
    public TirValue Condition { get; }

    public TirBlock WhenTrue { get; }
    public TirBlock WhenFalse { get; }

    public override IReadOnlyList<TirValue> Operands => [Condition];
    public override IReadOnlyList<TirBlock> Targets => [WhenTrue, WhenFalse];
}
