using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// C#'s <c>throw</c> (see <see cref="Exceptions"/>): a call of ChipSharp.Library's <c>Thrown.Unhandled</c> with the
/// exception, which prints what .NET prints for an exception nothing catches and stops the program, so it never returns;
/// the block ends with a return that never runs (of zero, in a method that returns a value). A try/finally around it is
/// plain branches by now (<see cref="TryFinally"/>), and its finally does not run, as in .NET.
/// </summary>
internal sealed partial class MethodLowering
{
    private void Throw()
    {
        var exception = PopObject();
        _ir.Call(_program.Function(DispatchTarget.Of(Exceptions.Thrower)), exception);
        if (_function.ReturnType == TirType.Void)
        {
            _ir.Return();
        }
        else
        {
            _ir.Return(_ir.Zero(_function.ReturnType));
        }
    }
}
