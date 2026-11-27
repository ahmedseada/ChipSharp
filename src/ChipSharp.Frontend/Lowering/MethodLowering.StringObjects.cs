using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Strings among the other objects (see <see cref="StringObjectFunctions"/>):
/// <list type="bullet">
/// <item>A virtual call of object's Equals, GetHashCode or ToString on a value that may be a string (one of type object)
/// asks first whether it is one: a string's, else what the call would run on the program's other objects (the one method
/// there is, the method table's, or object's own).</item>
/// <item><c>is string</c>, <c>as string</c> and <c>(string)x</c> test the object's header against the two string
/// descriptors; a cast that fails stops the program with .NET's InvalidCastException message.</item>
/// <item><c>Console.Write(object)</c> and <c>Console.WriteLine(object)</c> of an object print what its ToString gives,
/// and nothing for null, as .NET does.</item>
/// </list>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>A call of object's <paramref name="slot"/> on a value that may be a string, with the targets it has else.</summary>
    private TirValue ObjectCallOrString(DispatchSlot slot, IReadOnlyList<DispatchTarget> targets, List<TirValue> arguments)
    {
        var fallback = targets.Count switch
        {
            0 => null,
            1 => _program.Function(targets[0]),
            _ => _program.ObjectFunctions.VirtualObjectCall(slot.Name, SlotIndex(slot)),
        };
        return _ir.Call(_program.StringObjects.OrString(slot.Name, fallback), arguments);
    }

    /// <summary><c>isinst string</c> or <c>castclass string</c>.</summary>
    private void StringTypeTest(bool cast) =>
        PushObject(_ir.Call(_program.StringObjects.TypeTest(cast), ObjectFromStack(PopValue(), _instruction.Name)));

    /// <summary>
    /// Console.Write(object) or Console.WriteLine(object) of an object: its ToString's text (through its slot, as a
    /// callvirt of it would), nothing for null; then the end of the line for WriteLine.
    /// </summary>
    private void WriteObject(IlInstruction instruction)
    {
        var callee = ((IlMethodOperand)instruction.Operand!).Method;
        var slot = VirtualMethods.CalledSlot(_body.Instructions, _body.IndexOf(instruction.Offset))!;
        var receiverType = StackType(0);
        var value = ObjectFromStack(PopValue(), $"the object {callee.Name} writes");
        var targets = _program.VirtualTables.Dispatch.Targets(slot, receiverType ?? callee.ParameterTypes[0]);
        var called = targets.Count switch
        {
            0 => null,
            1 => _program.Function(targets[0]),
            _ => _program.ObjectFunctions.VirtualObjectCall(slot.Name, SlotIndex(slot)),
        };
        var toString = StringMethods.MayBeString(receiverType) ? _program.StringObjects.OrString(slot.Name, called) : called;

        // no object that could be there is ever made: it is null, which writes nothing
        if (toString is not null)
        {
            _ir.Call(_program.StringObjects.Write(toString), value);
        }

        if (callee.Name == "WriteLine")
        {
            _ir.Call(_program.Runtime("WriteLine"));
        }
    }
}
