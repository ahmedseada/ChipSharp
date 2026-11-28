using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// The 64-bit double on a target whose C has none (the capability "double64" is false: SDCC's chips, whose double is its
/// 32-bit float). The program's own methods are scanned for what makes or holds a double: a literal, a conversion to it,
/// a parameter, a result, a local or a field of that type, a call of a method that takes or returns one. A program that
/// computes with less precision than .NET does is refused here, before SDCC runs; the C backend refuses the double of
/// ChipSharp's library that a program reaches (its own check, which sees what the code really uses). Pwm.Duty has its own
/// message (<see cref="Unsupported.DutyWithoutDouble"/>).
/// </summary>
internal static class DoubleRules
{
    private const string Pwm = "ChipSharp.Hardware.Pwm";

    /// <summary>How many instructions before a call of Pwm.Duty can make its double argument (a literal, a conversion).</summary>
    private const int ArgumentInstructions = 4;

    /// <summary>
    /// Adds a problem to <paramref name="problems"/> for each place in the program's own <paramref name="methods"/> that
    /// uses a double on <paramref name="target"/>, when its "double64" is false. True when there is one.
    /// </summary>
    public static bool Apply(TargetDescription? target, IReadOnlyList<IlMethod> methods, List<Problem> problems)
    {
        if (target is null || !target.Capabilities.TryGetValue("double64", out var double64)
            || !string.Equals(double64, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rule = Unsupported.DoubleWithoutDouble64(target.Name);
        var found = false;
        foreach (var method in methods.Where(method => !LibrarySubstitutes.Defines(method)))
        {
            var instructions = method.Body?.Instructions ?? [];
            if (instructions.Count == 0)
            {
                continue;
            }

            // a signature or a local with a double: the first instruction stands for it
            if (IsDouble(method.Signature.ReturnType) || method.Parameters.Any(parameter => IsDouble(parameter.Type))
                || method.Body!.Locals.Any(local => IsDouble(local.Type)))
            {
                problems.Add(Problem.At(method, instructions[0], rule));
                found = true;
            }

            // the double Pwm.Duty takes is in its own message: not the instructions that make its arguments
            var duty = new HashSet<int>();
            for (var i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].Operand is IlMethodOperand { Method: { Name: "Duty", DeclaringType: IlNamedType { FullName: Pwm } } })
                {
                    duty.UnionWith(Enumerable.Range(Math.Max(0, i - ArgumentInstructions), Math.Min(i, ArgumentInstructions)));
                }
            }

            foreach (var instruction in instructions.Where((instruction, i) => !duty.Contains(i) && UsesDouble(instruction)))
            {
                problems.Add(Problem.At(method, instruction, rule));
                found = true;
            }
        }

        return found;
    }

    private static bool UsesDouble(IlInstruction instruction) => instruction switch
    {
        { OpCode: IlOpCode.Ldc_R8 or IlOpCode.Conv_R8 or IlOpCode.Ldelem_R8 or IlOpCode.Ldind_R8 or IlOpCode.Stind_R8 } => true,
        { Operand: IlMethodOperand { Method: var callee } } => callee.DeclaringType is not IlNamedType
            { FullName: Pwm } && (IsDouble(callee.ReturnType) || callee.ParameterTypes.Any(IsDouble)),
        { Operand: IlFieldOperand { Field: var field } } => IsDouble(field.FieldType),
        _ => false,
    };

    private static bool IsDouble(IlType type) => type is IlPrimitiveType { Kind: IlPrimitiveKind.Float64 }
        or IlArrayType { ElementType: IlPrimitiveType { Kind: IlPrimitiveKind.Float64 } };
}
