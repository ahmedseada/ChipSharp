using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Text built into a StringBuilder (see <see cref="StringBuilders"/>): <c>sb.Append($"...")</c>,
/// <c>sb.AppendLine($"...")</c>, whose handler is followed as a DefaultInterpolatedStringHandler is, and
/// <c>sb.AppendFormat("...", a, b)</c> of a literal format, read as string.Format's is. The call that appends it is
/// <see cref="PrintedRole.AppendTo"/>, its parts a build of their own (<see cref="PrintedText.Builds"/>): it makes text,
/// a heap use (as the builder itself is).
/// </summary>
internal sealed partial class PrintedTextFlow
{
    /// <summary>Follows a call that appends built text to a StringBuilder; false for any other instruction.</summary>
    private bool StringBuilderStep(IlInstruction instruction, int index, State state)
    {
        if (instruction is not { OpCode: IlOpCode.Call or IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } })
        {
            return false;
        }

        var stack = state.Stack;
        if (StringBuilders.AppendsHandler(callee) is { } line)
        {
            if (stack is not [.., _, Handler handler])
            {
                return false;
            }

            Pop(stack);
            Pop(stack);
            if (state.Handlers.TryGetValue(handler.Local, out var parts) && !parts.IsBroken)
            {
                AppendTo(instruction, parts, line);
            }
            else
            {
                Problem(instruction, Unsupported.BuildingText);
            }

            state.Handlers = state.Handlers.Remove(handler.Local);
            stack.Add(Unknown);
            return true;
        }

        if (!StringBuilders.IsAppendFormat(callee))
        {
            return false;
        }

        var values = new Value[callee.ParameterTypes.Count - 1];
        for (var i = values.Length - 1; i >= 0; i--)
        {
            values[i] = Pop(stack);
        }

        values = ParamsValues(instruction, callee, values) ?? values; // a params span (PrintedTextFlow.Params.cs)
        var format = Pop(stack);
        Pop(stack);

        // the values are operands 2 and after: the builder and the format come first
        var (formatted, problem) = format is Literal { Text: { } text }
            ? FormatParts(instruction, index, text, values, firstValue: 2)
            : (null, Unsupported.BuildingText);
        if (problem is not null)
        {
            Problem(instruction, problem);
        }
        else
        {
            AppendTo(instruction, formatted!, line: false);
        }

        stack.Add(Unknown);
        return true;
    }

    /// <summary>The call at <paramref name="instruction"/> appends <paramref name="parts"/>, then the end of a line for AppendLine.</summary>
    private void AppendTo(IlInstruction instruction, Parts parts, bool line)
    {
        Own(instruction, PrintedRole.AppendTo);
        Problem(instruction, Unsupported.HeapUse(Unsupported.MakingText));
        if (_record)
        {
            _builds[instruction] = new PrintSite(Joined(parts.Pieces), line);
        }
    }

}
