namespace ChipSharp.Ir;

/// <summary>
/// <c>%0 = newobject Program.Node</c>: a new object of a class (a <see cref="TirTypeDescriptor"/> of this module), a
/// ref. Every byte of it is zero but its header, which is the descriptor's address: the constructor runs after. When
/// the heap has no room for it, the garbage collector runs first; when there is still no room, the program stops with
/// .NET's OutOfMemoryException message. Only a target with a heap may run it (the code generator refuses the others),
/// and never in an interrupt handler (the runtime stops the program there, saying so).
/// </summary>
public sealed class TirNewObject : TirInstruction
{
    public TirNewObject(TirTypeDescriptor type) : base(TirType.Ref)
    {
        ArgumentNullException.ThrowIfNull(type);
        TypeDescriptor = type;
    }

    /// <summary>The class: a descriptor of kind <see cref="TirTypeDescriptorKind.Class"/>.</summary>
    public TirTypeDescriptor TypeDescriptor { get; }

    public override IReadOnlyList<TirValue> Operands => [];
}

/// <summary>
/// <c>%1 = newarray int[], %0</c>: a new array object of an array type (a <see cref="TirTypeDescriptor"/> of this
/// module) with %0 elements (an i32), a ref; its elements are zero. A negative length stops the program with .NET's
/// OverflowException message; otherwise it is like <see cref="TirNewObject"/>.
/// </summary>
public sealed class TirNewArray : TirInstruction
{
    public TirNewArray(TirTypeDescriptor type, TirValue length) : base(TirType.Ref)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(length);
        TypeDescriptor = type;
        Length = length;
    }

    /// <summary>The array type: a descriptor of kind <see cref="TirTypeDescriptorKind.Array"/>.</summary>
    public TirTypeDescriptor TypeDescriptor { get; }

    /// <summary>How many elements: an i32, read as signed.</summary>
    public TirValue Length { get; }

    public override IReadOnlyList<TirValue> Operands => [Length];
}

/// <summary>
/// <c>nullcheck %0</c>: stops the program with .NET's NullReferenceException message ("Object reference not set to an
/// instance of an object.") when the ref %0 is null; does nothing otherwise. Produces nothing. What comes before a use
/// of an object's fields, elements, header or methods, as .NET checks them.
/// </summary>
public sealed class TirNullCheck : TirInstruction
{
    public TirNullCheck(TirValue reference) : base(TirType.Void)
    {
        ArgumentNullException.ThrowIfNull(reference);
        Reference = reference;
    }

    /// <summary>The object: a ref.</summary>
    public TirValue Reference { get; }

    public override IReadOnlyList<TirValue> Operands => [Reference];
}

/// <summary>
/// <c>%1 = arraylength %0</c>: the length of the array object %0 (a ref, not null), an i32, as <c>newarray</c> made it
/// (at <see cref="TirLayout.ArrayLengthOffset"/>).
/// </summary>
public sealed class TirArrayLength : TirInstruction
{
    public TirArrayLength(TirValue array) : base(TirType.I32)
    {
        ArgumentNullException.ThrowIfNull(array);
        Array = array;
    }

    /// <summary>The array object: a ref.</summary>
    public TirValue Array { get; }

    public override IReadOnlyList<TirValue> Operands => [Array];
}

/// <summary>
/// <c>%1 = arraydata i32 %0</c>: the address (a ptr) of element 0 of the array object %0 (a ref, not null), whose
/// elements are of the given type (an array type of the module has it), at <see cref="TirLayout.ArrayDataOffset"/>.
/// <see cref="TirElementAddress"/> on it reaches element i; nothing checks that i is in range.
/// </summary>
public sealed class TirArrayData : TirInstruction
{
    public TirArrayData(TirValue array, TirType elementType) : base(TirType.Ptr)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentNullException.ThrowIfNull(elementType);
        Array = array;
        ElementType = elementType;
    }

    /// <summary>The array object: a ref.</summary>
    public TirValue Array { get; }

    /// <summary>The type of its elements, what the address points to.</summary>
    public TirType ElementType { get; }

    public override IReadOnlyList<TirValue> Operands => [Array];
}

/// <summary>
/// <c>%1 = typeof %0</c>: the header of the object %0 (a ref, not null), the address of its type descriptor (a ptr),
/// to compare with <see cref="TirTypeAddress"/> or to read the descriptor through (see <see cref="TirTypeDescriptor"/>
/// for its layout: the virtual methods, the base, the interfaces).
/// </summary>
public sealed class TirTypeOf : TirInstruction
{
    public TirTypeOf(TirValue reference) : base(TirType.Ptr)
    {
        ArgumentNullException.ThrowIfNull(reference);
        Reference = reference;
    }

    /// <summary>The object: a ref.</summary>
    public TirValue Reference { get; }

    public override IReadOnlyList<TirValue> Operands => [Reference];
}

/// <summary>
/// <c>%0 = typeaddr Program.Node</c>: the address (a ptr) of a type descriptor of this module, which is the header of
/// each of its objects.
/// </summary>
public sealed class TirTypeAddress : TirInstruction
{
    public TirTypeAddress(TirTypeDescriptor type) : base(TirType.Ptr)
    {
        ArgumentNullException.ThrowIfNull(type);
        TypeDescriptor = type;
    }

    public TirTypeDescriptor TypeDescriptor { get; }

    public override IReadOnlyList<TirValue> Operands => [];
}
