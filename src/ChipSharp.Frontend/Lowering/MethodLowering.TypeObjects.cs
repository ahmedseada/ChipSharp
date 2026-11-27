using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Type objects (see <see cref="TypeObjects"/>): <c>typeof(X)</c> is the address of X's type object, a constant (its
/// <c>ldtoken</c> pushes it, and <c>Type.GetTypeFromHandle</c> after it leaves it there); <c>x.GetType()</c> is
/// <c>ChipSharp.Type.Of</c> of the object, after its null check.
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>True for the instructions <see cref="LowerTypeObjectInstruction"/> translates.</summary>
    private bool IsTypeObjectInstruction(IlInstruction instruction)
    {
        if (instruction.OpCode is not (IlOpCode.Ldtoken or IlOpCode.Call or IlOpCode.Callvirt))
        {
            return false;
        }

        var index = _body.IndexOf(instruction.Offset);
        return TypeObjects.GivesTypeObject(_body.Instructions, index) || TypeObjects.EndsTypeOf(_body.Instructions, index);
    }

    private void LowerTypeObjectInstruction(IlInstruction instruction)
    {
        var index = _body.IndexOf(instruction.Offset);
        if (TypeObjects.TypeOf(_body.Instructions, index) is { } type)
        {
            var typeObject = _ir.AddressOf(_program.TypeObject(type, _method.Assembly));
            _notNull.Add(typeObject);
            PushObject(typeObject);
        }
        else if (TypeObjects.IsGetType(instruction))
        {
            PushObject(_ir.Call(_program.TypeOfObject, PopObjectNotNull()));
        }

        // Type.GetTypeFromHandle after the ldtoken of a typeof: the type object is on the stack already
    }
}
