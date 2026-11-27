namespace ChipSharp.Ir;

/// <summary>
/// Appends instructions to a block and returns them, so each result can be used as an operand right away.
/// Point <see cref="Block"/> at another block to continue there. It checks nothing; run <see cref="TirVerifier"/>.
/// <code>
/// var square = module.AddFunction("Program.Square", TirType.I32, ("n", TirType.I32));
/// var b = new TirBuilder(square.AddBlock("entry"));
/// var n = square.Parameters[0];
/// b.Return(b.Binary(TirBinaryOp.Mul, n, n));
/// </code>
/// </summary>
public sealed class TirBuilder(TirBlock block)
{
    private TirBlock _block = block ?? throw new ArgumentNullException(nameof(block));

    /// <summary>Where the next instruction goes (at the end).</summary>
    public TirBlock Block
    {
        get => _block;
        set => _block = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The function of the current block.</summary>
    public TirFunction Function => Block.Function;

    /// <summary>An integer or bool constant, e.g. <c>Const(TirType.I32, 42)</c>. See <see cref="TirConst"/> for the ranges.</summary>
    public TirConst Const(TirType type, long value) => Block.Append(new TirConst(type, value));

    /// <summary>A floating-point constant of type f32 or f64.</summary>
    public TirConst Const(TirType type, double value) => Block.Append(new TirConst(type, value));

    public TirConst Const(bool value) => Block.Append(new TirConst(value));

    public TirBinary Binary(TirBinaryOp op, TirValue left, TirValue right) =>
        Block.Append(new TirBinary(op, left, right));

    public TirCompare Compare(TirCompareOp op, TirValue left, TirValue right) =>
        Block.Append(new TirCompare(op, left, right));

    /// <summary>Converts to another type; <paramref name="unsigned"/> reads the integer side as unsigned.</summary>
    public TirConvert Convert(TirValue value, TirType to, bool unsigned = false) =>
        Block.Append(new TirConvert(value, to, unsigned));

    public TirLoad Load(TirSlot slot) => Block.Append(new TirLoad(slot));

    public TirStore Store(TirSlot slot, TirValue value) => Block.Append(new TirStore(slot, value));

    /// <summary>A value of <paramref name="type"/> with every byte zero: a null ptr or ref, a struct or array of zeros, 0.</summary>
    public TirZero Zero(TirType type) => Block.Append(new TirZero(type));

    /// <summary>How many bytes <paramref name="type"/> takes on the target, an i32 the code generator fills in.</summary>
    public TirSizeOf SizeOf(TirType type) => Block.Append(new TirSizeOf(type));

    /// <summary>The address of a slot (a ptr), e.g. to reach the fields of a struct or the elements of an array in it.</summary>
    public TirSlotAddress AddressOf(TirSlot slot) => Block.Append(new TirSlotAddress(slot));

    /// <summary>The address of a global (a ptr).</summary>
    public TirGlobalAddress AddressOf(TirGlobal global) => Block.Append(new TirGlobalAddress(global));

    /// <summary>Reads a <paramref name="type"/> from memory at <paramref name="address"/>; volatile for a hardware register.</summary>
    public TirPointerLoad Load(TirType type, TirValue address, bool isVolatile = false) =>
        Block.Append(new TirPointerLoad(type, address, isVolatile));

    /// <summary>Writes <paramref name="value"/> to memory at <paramref name="address"/>; volatile for a hardware register.</summary>
    public TirPointerStore Store(TirValue address, TirValue value, bool isVolatile = false) =>
        Block.Append(new TirPointerStore(address, value, isVolatile));

    /// <summary>Reads a <paramref name="type"/> from <paramref name="address"/>, which may not be aligned for it.</summary>
    public TirPointerLoad LoadUnaligned(TirType type, TirValue address) =>
        Block.Append(new TirPointerLoad(type, address) { IsUnaligned = true });

    /// <summary>Writes <paramref name="value"/> to <paramref name="address"/>, which may not be aligned for it.</summary>
    public TirPointerStore StoreUnaligned(TirValue address, TirValue value) =>
        Block.Append(new TirPointerStore(address, value) { IsUnaligned = true });

    /// <summary>
    /// The address of field number <paramref name="fieldIndex"/> of the struct at <paramref name="address"/> (a ptr), or
    /// of the object <paramref name="address"/> (a ref) of a class whose object type is <paramref name="structType"/>.
    /// </summary>
    public TirFieldAddress FieldAddress(TirValue address, TirStructType structType, int fieldIndex) =>
        Block.Append(new TirFieldAddress(address, structType, fieldIndex));

    /// <summary>The address of the field named <paramref name="fieldName"/> of the struct at <paramref name="address"/>.</summary>
    public TirFieldAddress FieldAddress(TirValue address, TirStructType structType, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(structType);
        var field = structType.FindField(fieldName)
            ?? throw new TirException($"Struct '{structType.Name}' has no field named {fieldName}.");
        return FieldAddress(address, structType, field.Index);
    }

    /// <summary>
    /// The address of element <paramref name="index"/> (i32, i64 or nint) of a row of <paramref name="elementType"/>
    /// values that starts at <paramref name="address"/>, e.g. an array's.
    /// </summary>
    public TirElementAddress ElementAddress(TirValue address, TirType elementType, TirValue index) =>
        Block.Append(new TirElementAddress(address, elementType, index));

    /// <summary>An address as a number of type <paramref name="to"/> (an integer type).</summary>
    public TirPtrToInt PtrToInt(TirValue address, TirType to) => Block.Append(new TirPtrToInt(address, to));

    /// <summary>A number (an integer) as an address.</summary>
    public TirIntToPtr IntToPtr(TirValue value) => Block.Append(new TirIntToPtr(value));

    /// <summary>A new object of the class <paramref name="type"/> (a ref): zeroed, its header set.</summary>
    public TirNewObject NewObject(TirTypeDescriptor type) => Block.Append(new TirNewObject(type));

    /// <summary>A new array object of the array type <paramref name="type"/> with <paramref name="length"/> (an i32) zeroed elements.</summary>
    public TirNewArray NewArray(TirTypeDescriptor type, TirValue length) => Block.Append(new TirNewArray(type, length));

    /// <summary>Stops the program with .NET's NullReferenceException message when <paramref name="reference"/> (a ref) is null.</summary>
    public TirNullCheck NullCheck(TirValue reference) => Block.Append(new TirNullCheck(reference));

    /// <summary>The length (an i32) of the array object <paramref name="array"/>.</summary>
    public TirArrayLength ArrayLength(TirValue array) => Block.Append(new TirArrayLength(array));

    /// <summary>The address of element 0 of the array object <paramref name="array"/>, whose elements are <paramref name="elementType"/>.</summary>
    public TirArrayData ArrayData(TirValue array, TirType elementType) => Block.Append(new TirArrayData(array, elementType));

    /// <summary>The array object (a ref) whose element 0 is at <paramref name="data"/> (a ptr), as <see cref="ArrayData"/> gave it.</summary>
    public TirArrayObject ArrayObject(TirValue data) => Block.Append(new TirArrayObject(data));

    /// <summary>The header of the object <paramref name="reference"/>: the address of its type descriptor.</summary>
    public TirTypeOf TypeOf(TirValue reference) => Block.Append(new TirTypeOf(reference));

    /// <summary>The address of the type descriptor <paramref name="type"/>.</summary>
    public TirTypeAddress AddressOf(TirTypeDescriptor type) => Block.Append(new TirTypeAddress(type));

    /// <summary>The address of the defined function <paramref name="function"/> (a ptr), for <see cref="CallIndirect"/>.</summary>
    public TirFunctionAddress AddressOf(TirFunction function) => Block.Append(new TirFunctionAddress(function));

    public TirCall Call(TirFunction callee, params IEnumerable<TirValue> arguments) =>
        Block.Append(new TirCall(callee, arguments));

    /// <summary>
    /// Calls the function at the address <paramref name="callee"/> (a ptr, e.g. read from a method table), which returns
    /// <paramref name="returnType"/>.
    /// </summary>
    public TirCallIndirect CallIndirect(TirType returnType, TirValue callee, params IEnumerable<TirValue> arguments) =>
        Block.Append(new TirCallIndirect(returnType, callee, arguments));

    /// <summary>Ends the block, returning <paramref name="value"/> (none in a void function).</summary>
    public TirReturn Return(TirValue? value = null) => Block.Append(new TirReturn(value));

    /// <summary>Ends the block, continuing at <paramref name="target"/>.</summary>
    public TirJump Jump(TirBlock target) => Block.Append(new TirJump(target));

    /// <summary>Ends the block, continuing at <paramref name="whenTrue"/> or <paramref name="whenFalse"/>.</summary>
    public TirBranch Branch(TirValue condition, TirBlock whenTrue, TirBlock whenFalse) =>
        Block.Append(new TirBranch(condition, whenTrue, whenFalse));
}
