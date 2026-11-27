using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// What a program may use of a target's serial ports, analog inputs and PWM outputs (ChipSharp.Hardware's Uart, Adc and
/// Pwm, and Console.Read and Console.ReadLine), from its target file:
/// <list type="bullet">
/// <item>the capabilities "uart" (how many serial ports), "adc" (how many analog inputs), "pwm" (how many PWM
/// outputs) and "consoleInput" (true when the console reads too): a call of a class the target has none of is a
/// problem, and so is Console.ReadLine, which makes a string, on a target without a heap;</item>
/// <item>"pinFunctions", the pins each can use ("uart1.tx": [4, 8, 20, 24], "adc": [26, 27, 28, 29], "pwm": [...]): a
/// pin or a port the program gives as a constant (C# passes it with ldc.i4) that the target does not have for it is a
/// problem, before the program runs. The runtime checks every pin and port when it runs, constant or not.</item>
/// </list>
/// The whole-program pass applies them when it checks a program for a target; checked without one, a program may use
/// what any target has.
/// </summary>
internal static class SerialAnalogRules
{
    private const string Uart = "ChipSharp.Hardware.Uart";
    private const string Adc = "ChipSharp.Hardware.Adc";
    private const string Pwm = "ChipSharp.Hardware.Pwm";

    /// <summary>
    /// Adds a problem to <paramref name="problems"/> for each place in the program's own <paramref name="methods"/> that
    /// uses a serial port, analog input or PWM output <paramref name="target"/> does not have. True when there is one.
    /// </summary>
    public static bool Apply(TargetDescription? target, IReadOnlyList<IlMethod> methods, List<Problem> problems)
    {
        if (target is null)
        {
            return false;
        }

        var found = false;
        foreach (var method in methods.Where(method => !LibrarySubstitutes.Defines(method)))
        {
            var instructions = method.Body?.Instructions ?? [];
            HashSet<int>? targets = null;
            for (var i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] is not { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: var callee } }
                    || callee.DeclaringType is not IlNamedType { FullName: var type })
                {
                    continue;
                }

                targets ??= BranchTargets(instructions);
                int? Constant(int index) => ConstantArgument(method, instructions, i, index, targets);
                if (Check(target, type, callee, Constant) is { } unsupported)
                {
                    problems.Add(Problem.At(method, instructions[i], unsupported));
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>The problem with a call of <paramref name="callee"/>, a method of <paramref name="type"/>; null when there is none.</summary>
    private static Unsupported? Check(TargetDescription target, string type, IlMethodReference callee,
        Func<int, int?> constant)
    {
        switch (type)
        {
            case "System.Console" when callee.Name is "Read" or "ReadLine" && callee.ParameterTypes.Count == 0:
                return !target.HasCapability("consoleInput") ? Unsupported.NoConsoleInput(target.Name)
                    : callee.Name == "ReadLine" && !target.HasHeap ? Unsupported.ReadLineWithoutHeap(target.Name)
                    : null;

            case Uart:
                var ports = Count(target, "uart");
                if (ports == 0)
                {
                    return Unsupported.NoHardware(Uart, "a serial port (UART)", target.Name);
                }

                if (constant(0) is not { } port)
                {
                    return null;
                }

                if (port < 0 || port >= ports)
                {
                    return Unsupported.NoSuchPort(port, ports, target.Name);
                }

                return callee.Name == "Open" && callee.ParameterTypes.Count == 4
                    ? PinProblem(target, $"uart{port}.tx", constant(1), $"UART{port}'s TX")
                        ?? PinProblem(target, $"uart{port}.rx", constant(2), $"UART{port}'s RX")
                    : null;

            case Adc:
                return Count(target, "adc") == 0 ? Unsupported.NoHardware(Adc, "analog inputs (an ADC)", target.Name)
                    : callee.ParameterTypes.Count == 1 ? PinProblem(target, "adc", constant(0), "an analog input")
                    : null;

            case Pwm:
                return Count(target, "pwm") == 0 ? Unsupported.NoHardware(Pwm, "PWM outputs", target.Name)
                    : PinProblem(target, "pwm", constant(0), "a PWM output");

            default:
                return null;
        }
    }

    /// <summary>
    /// The problem with <paramref name="pin"/> as <paramref name="role"/> ("UART1's TX", "an analog input"), when it is
    /// a constant the target's "pinFunctions.<paramref name="function"/>" does not list; null when it is not a
    /// constant, the target lists no pins for the function, or it lists this one.
    /// </summary>
    private static Unsupported? PinProblem(TargetDescription target, string function, int? pin, string role) =>
        pin is { } number && Pins(target, function) is { } pins && !pins.Contains(number)
            ? Unsupported.WrongPin(number, role, pins, target.Name)
            : null;

    /// <summary>A count capability: "uart": 2 is 2, true is 1; false, none or anything else is 0.</summary>
    private static int Count(TargetDescription target, string capability) =>
        !target.Capabilities.TryGetValue(capability, out var value) ? 0
        : int.TryParse(value, out var count) && count > 0 ? count
        : target.HasCapability(capability) ? 1
        : 0;

    /// <summary>The pins the target's "pinFunctions" gives <paramref name="function"/>, in order; null when it gives none.</summary>
    /// <exception cref="ChipSharpException">The entry is not a list of pin numbers.</exception>
    private static IReadOnlyList<int>? Pins(TargetDescription target, string function)
    {
        if (target.Settings["pinFunctions"]?[function] is not { } node)
        {
            return null;
        }

        return node is JsonArray list && list.All(item => item is JsonValue value && value.TryGetValue(out int _))
            ? [.. list.Select(item => item!.GetValue<int>())]
            : throw new ChipSharpException(
                $"Target '{target.Name}': 'pinFunctions.{function}' must be a list of pin numbers, e.g. [26, 27, 28].");
    }

    /// <summary>
    /// The value of argument <paramref name="index"/> of the call at <paramref name="at"/> when C# passes a constant
    /// there (an ldc.i4 that only that argument comes from); null otherwise.
    /// </summary>
    private static int? ConstantArgument(IlMethod method, IReadOnlyList<IlInstruction> instructions, int at, int index,
        HashSet<int> targets)
    {
        if (instructions[at].Operand is not IlMethodOperand { Method: var callee } || index >= callee.ParameterTypes.Count)
        {
            return null;
        }

        // back from the call, through what the arguments after this one take and leave on the stack, to the
        // instruction that pushes it; a branch, or a place another branch comes to, on the way: not one value
        var wanted = callee.ParameterTypes.Count - index; // this argument's place on the stack, from the top
        for (var i = at - 1; i >= 0; i--)
        {
            var instruction = instructions[i];
            if (targets.Contains(instructions[i + 1].Offset)
                || instruction.Operand is IlBranchOperand or IlSwitchOperand
                || instruction.OpCode is IlOpCode.Ret or IlOpCode.Throw or IlOpCode.Rethrow)
            {
                return null;
            }

            var (pops, pushes) = IlStackEffects.Of(instruction, method);
            if (wanted <= pushes)
            {
                return pushes == 1 ? LoadedConstant(instruction) : null;
            }

            wanted += pops - pushes;
        }

        return null;
    }

    /// <summary>The int an ldc.i4 instruction loads; null for any other instruction.</summary>
    private static int? LoadedConstant(IlInstruction instruction) => instruction.OpCode switch
    {
        >= IlOpCode.Ldc_I4_M1 and <= IlOpCode.Ldc_I4_8 => (int)instruction.OpCode - (int)IlOpCode.Ldc_I4_0,
        IlOpCode.Ldc_I4_S or IlOpCode.Ldc_I4 when instruction.Operand is IlInt32Operand { Value: var value } => value,
        _ => null,
    };

    /// <summary>The offsets branches of the method go to.</summary>
    private static HashSet<int> BranchTargets(IReadOnlyList<IlInstruction> instructions)
    {
        var targets = new HashSet<int>();
        foreach (var instruction in instructions)
        {
            switch (instruction.Operand)
            {
                case IlBranchOperand branch:
                    targets.Add(branch.Target);
                    break;
                case IlSwitchOperand table:
                    targets.UnionWith(table.Targets);
                    break;
            }
        }

        return targets;
    }
}
