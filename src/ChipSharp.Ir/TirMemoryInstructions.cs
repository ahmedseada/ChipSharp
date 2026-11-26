namespace ChipSharp.Ir;

/// <summary>
/// <c>%0 = zero Program.Point</c>: the value of a type with every byte zero: 0, false, 0.0, a null ptr, or a struct
/// or array of zeros. C#'s <c>default</c>; stored through an address, IL's <c>initobj</c>. Any type but void.
/// </summary>
public sealed class TirZero : TirInstruction
{
    public TirZero(TirType type) : base(type)
    {
    }

    public override IReadOnlyList<TirValue> Operands => [];
}

/// <summary>
/// <c>%0 = sizeof Program.Point</c>: how many bytes a type takes on the target, as an i32 (C#'s and IL's
/// <c>sizeof</c>, which <c>stackalloc</c> of a struct uses too). TIR does not know the target, so the code generator
/// fills the number in from <see cref="TirLayout"/>: <c>{ i8, ptr }</c> takes 8 bytes with 4-byte pointers and 16
/// with 8-byte ones.
/// </summary>
public sealed class TirSizeOf : TirInstruction
{
    public TirSizeOf(TirType ofType) : base(TirType.I32)
    {
        ArgumentNullException.ThrowIfNull(ofType);
        OfType = ofType;
    }

    /// <summary>The type whose size this is: any type but void.</summary>
    public TirType OfType { get; }

    public override IReadOnlyList<TirValue> Operands => [];
}

/// <summary>
/// <c>%0 = addr $x</c>: the address of a slot, a ptr. Loads and stores through it reach the slot's memory, so the
/// slot can hold a struct or an array whose parts are read and written one by one.
/// </summary>
public sealed class TirSlotAddress : TirInstruction
{
    public TirSlotAddress(TirSlot slot) : base(TirType.Ptr)
    {
        ArgumentNullException.ThrowIfNull(slot);
        Slot = slot;
    }

    public TirSlot Slot { get; }

    public override IReadOnlyList<TirValue> Operands => [];
}

/// <summary><c>%0 = addr @Program.counter</c>: the address of a global, a ptr.</summary>
public sealed class TirGlobalAddress : TirInstruction
{
    public TirGlobalAddress(TirGlobal global) : base(TirType.Ptr)
    {
        ArgumentNullException.ThrowIfNull(global);
        Global = global;
    }

    public TirGlobal Global { get; }

    public override IReadOnlyList<TirValue> Operands => [];
}

/// <summary>
/// <c>%1 = load i32 [%0]</c>: reads a value of the given type from memory at an address (a ptr). The address must be
/// aligned for the type (see <see cref="TirLayout"/>). When the verifier can see what the address points to (it
/// comes from addr, fieldaddr or elementaddr in the same block), the type must be that.
/// <c>%1 = load volatile i32 [%0]</c> is read exactly once, where it is written: never dropped, merged with another
/// read or moved past another volatile access, as hardware registers need.
/// </summary>
public sealed class TirPointerLoad : TirInstruction
{
    public TirPointerLoad(TirType type, TirValue address, bool isVolatile = false) : base(type)
    {
        ArgumentNullException.ThrowIfNull(address);
        Address = address;
        IsVolatile = isVolatile;
    }

    /// <summary>Where to read: a ptr.</summary>
    public TirValue Address { get; }

    /// <summary>True for a read that must happen exactly as written (a hardware register).</summary>
    public bool IsVolatile { get; }

    public override IReadOnlyList<TirValue> Operands => [Address];
}

/// <summary>
/// <c>store i32 [%0], %1</c>: writes a value to memory at an address (a ptr); the type printed is the value's. Produces
/// nothing. The same rules as <see cref="TirPointerLoad"/> apply, and <c>store volatile i32 [%0], %1</c> is written
/// exactly once, where it is written.
/// </summary>
public sealed class TirPointerStore : TirInstruction
{
    public TirPointerStore(TirValue address, TirValue value, bool isVolatile = false) : base(TirType.Void)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(value);
        Address = address;
        Value = value;
        IsVolatile = isVolatile;
    }

    /// <summary>Where to write: a ptr.</summary>
    public TirValue Address { get; }

    public TirValue Value { get; }

    /// <summary>True for a write that must happen exactly as written (a hardware register).</summary>
    public bool IsVolatile { get; }

    public override IReadOnlyList<TirValue> Operands => [Address, Value];
}

/// <summary>
/// <c>%1 = fieldaddr Program.Point %0, Y</c>: the address of a field of a struct, from the address of the struct
/// (a ptr): the struct's address plus the field's offset (see <see cref="TirLayout"/>).
/// </summary>
public sealed class TirFieldAddress : TirInstruction
{
    public TirFieldAddress(TirValue address, TirStructType structType, int fieldIndex) : base(TirType.Ptr)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(structType);
        Address = address;
        StructType = structType;
        FieldIndex = fieldIndex;
    }

    /// <summary>The address of the struct: a ptr.</summary>
    public TirValue Address { get; }

    public TirStructType StructType { get; }

    /// <summary>Which field, from 0, in <see cref="TirStructType.Fields"/>.</summary>
    public int FieldIndex { get; }

    /// <summary>The field, or null when <see cref="FieldIndex"/> is out of range (the verifier reports that).</summary>
    public TirStructField? Field =>
        FieldIndex >= 0 && FieldIndex < StructType.Fields.Count ? StructType.Fields[FieldIndex] : null;

    public override IReadOnlyList<TirValue> Operands => [Address];
}

/// <summary>
/// <c>%2 = elementaddr i32 %0, %1</c>: the address of element number %1 of a row of i32 values that starts at
/// %0, that is %0 + %1 * (the size of i32). %0 is the address of an array of that element type (element 0 is at
/// the array's address) or of one element (C#'s <c>p[i]</c> on a pointer). The index is i32, i64 or nint, read
/// as signed. Nothing checks that it is in range.
/// </summary>
public sealed class TirElementAddress : TirInstruction
{
    public TirElementAddress(TirValue address, TirType elementType, TirValue index) : base(TirType.Ptr)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(elementType);
        ArgumentNullException.ThrowIfNull(index);
        Address = address;
        ElementType = elementType;
        Index = index;
    }

    /// <summary>Where the row of elements starts (or any element of it): a ptr.</summary>
    public TirValue Address { get; }

    /// <summary>The type of each element; the step from one element to the next is its size.</summary>
    public TirType ElementType { get; }

    public TirValue Index { get; }

    public override IReadOnlyList<TirValue> Operands => [Address, Index];
}

/// <summary>
/// <c>%1 = ptrtoint ptr %0 to nint</c>: an address as a number, of an integer type (any width: a narrower type
/// keeps the low bits, a wider one is zero-extended).
/// </summary>
public sealed class TirPtrToInt : TirInstruction
{
    public TirPtrToInt(TirValue address, TirType to) : base(to)
    {
        ArgumentNullException.ThrowIfNull(address);
        Address = address;
    }

    /// <summary>The address to turn into a number: a ptr.</summary>
    public TirValue Address { get; }

    public override IReadOnlyList<TirValue> Operands => [Address];
}

/// <summary>
/// <c>%1 = inttoptr i32 %0 to ptr</c>: a number as an address, e.g. a hardware register's. The integer is read as
/// unsigned: zero-extended to the pointer width, or cut to it.
/// </summary>
public sealed class TirIntToPtr : TirInstruction
{
    public TirIntToPtr(TirValue value) : base(TirType.Ptr)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>The number: an integer.</summary>
    public TirValue Value { get; }

    public override IReadOnlyList<TirValue> Operands => [Value];
}
