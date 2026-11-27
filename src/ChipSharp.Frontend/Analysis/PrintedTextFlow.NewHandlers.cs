using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// A <c>$"..."</c> whose handler C# makes with <c>newobj</c> and stores in its local (<c>stloc</c>), rather than calling
/// the constructor on the local's address: what it does where the evaluation stack must be empty first, at the start of a
/// try block, so in the body of a <c>foreach</c> over a collection or of a <c>using</c> (a StringBuilder's handler too,
/// for <c>sb.Append($"...")</c>). The two are the handler's start, as the constructor called on its address is
/// (<see cref="PrintedRole.NewHandler"/>, then <see cref="PrintedRole.Start"/>).
/// </summary>
internal sealed partial class PrintedTextFlow
{
    /// <summary>The handler <c>newobj</c> makes, on the stack until the <c>stloc</c> right after it stores it in its local.</summary>
    private sealed record NewHandler(int Local) : Value;

    /// <summary>Follows a <c>newobj</c> of a handler and the <c>stloc</c> of it; false for any other instruction.</summary>
    private bool NewHandlerStep(IlInstruction instruction, int index, State state)
    {
        var stack = state.Stack;
        switch (instruction)
        {
            case { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: { Name: ".ctor", Definition: null } constructor } }
                when Unsupported.IsInterpolatedStringHandler(constructor.DeclaringType)
                    && constructor.ParameterTypes is [var literals, var holes, ..] && literals == IlType.Int32
                    && holes == IlType.Int32
                    && (constructor.ParameterTypes.Count == 2
                        || (constructor.ParameterTypes.Count == 3 && StringBuilders.IsBuilder(constructor.ParameterTypes[2])))
                    && StoredHandler(index) is { } local && stack.Count >= constructor.ParameterTypes.Count:
                // (a StringBuilder's handler is given the builder too, which the Append it goes to names again)
                for (var i = 0; i < constructor.ParameterTypes.Count; i++)
                {
                    Pop(stack);
                }

                Own(instruction, PrintedRole.NewHandler);
                stack.Add(new NewHandler(local));
                return true;

            case { LocalIndex: { } stored } when instruction.OpCode is IlOpCode.Stloc or IlOpCode.Stloc_S
                    or (>= IlOpCode.Stloc_0 and <= IlOpCode.Stloc_3)
                && stack.Count > 0 && stack[^1] is NewHandler made && made.Local == stored:
                Pop(stack);
                state.Handlers = state.Handlers.SetItem(stored, Parts.None);
                Own(instruction, PrintedRole.Start);
                return true;

            default:
                return false;
        }
    }

    /// <summary>The handler local the instruction after <paramref name="index"/>, in its block, stores in; null for none.</summary>
    private int? StoredHandler(int index)
    {
        var instructions = _body.Instructions;
        return index + 1 < instructions.Count && _blockOf[index + 1] == _blockOf[index]
            && instructions[index + 1] is { LocalIndex: { } local } next
            && next.OpCode is IlOpCode.Stloc or IlOpCode.Stloc_S or (>= IlOpCode.Stloc_0 and <= IlOpCode.Stloc_3)
            && _handlerLocals.Contains(local)
                ? local
                : null;
    }
}
