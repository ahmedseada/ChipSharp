using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// References to objects and strings (see <see cref="ObjectReferences"/>): <c>ldind.ref</c> loads the ref the reference
/// refers to, <c>stind.ref</c> stores an object (or null) there.
/// </summary>
internal sealed partial class MethodLowering
{
    private void LowerObjectReference(IlInstruction instruction)
    {
        if (instruction.OpCode == IlOpCode.Ldind_Ref)
        {
            var (address, target) = PopReference();
            CheckObjectTarget(target);
            PushObject(_ir.Load(TirType.Ref, address));
            return;
        }

        var value = PopValue();
        var (destination, to) = PopReference();
        CheckObjectTarget(to);
        _ir.Store(destination, ObjectFromStack(value, "the variable it refers to"));
    }

    /// <summary>A reference that refers to something known (a slot, a field) must refer to a ref.</summary>
    private void CheckObjectTarget(TirType? target)
    {
        if (target is not null && target != TirType.Ref)
        {
            throw Error($"{_instruction.Name} reads or writes an object through a reference to {target}");
        }
    }
}
