using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Static fields: each is a global (see <see cref="ProgramFunctions.StaticField"/>), reached through its address,
/// taken again at every use: <c>ldsfld</c> is <c>addr</c> then <c>load</c>, <c>stsfld</c> is <c>addr</c> then
/// <c>store</c>. The values are widened and narrowed as for a local. After <c>volatile.</c> (a C# <c>volatile</c>
/// field) the load or store is volatile: it happens exactly once, where the IL has it. <c>string.Empty</c> is no field
/// of the program: it is the literal "" (see <see cref="FixedText"/>).
/// </summary>
internal sealed partial class MethodLowering
{
    private void LoadStaticField()
    {
        if (FixedText.IsEmpty(((IlFieldOperand)_instruction.Operand!).Field))
        {
            Push(Text(""));
            return;
        }

        if (LoadStaticArray())
        {
            return;
        }

        var field = _program.StaticField(_method, _instruction);
        var address = _ir.AddressOf(field.Global);
        Push(ToStack(_ir.Load(field.Global.Type, address, IsVolatileAccess()), field.Type));
    }

    private void StoreStaticField()
    {
        var field = _program.StaticField(_method, _instruction);
        var value = FromStack(PopValue(), field.Global.Type, field.Type, $"field {field.Global.Name}");
        _ir.Store(_ir.AddressOf(field.Global), value, IsVolatileAccess());
    }

    /// <summary>
    /// volatile. changes the instruction after it, which must be an ldsfld, stsfld, ldfld or stfld (a volatile field of a
    /// struct): ChipSharp has no other access it applies to.
    /// </summary>
    private void CheckVolatilePrefix()
    {
        var next = _body.IndexOf(_instruction.Offset) + 1;
        if (next >= _body.Instructions.Count
            || _body.Instructions[next].OpCode is not (IlOpCode.Ldsfld or IlOpCode.Stsfld or IlOpCode.Ldfld or IlOpCode.Stfld))
        {
            throw Error("volatile. must come right before ldsfld, stsfld, ldfld or stfld: ChipSharp " +
                "has no other memory access it applies to");
        }
    }

    /// <summary>True when the instruction being translated comes right after volatile.</summary>
    private bool IsVolatileAccess()
    {
        var index = _body.IndexOf(_instruction.Offset);
        return index > 0 && _body.Instructions[index - 1].OpCode == IlOpCode.Volatile;
    }
}
