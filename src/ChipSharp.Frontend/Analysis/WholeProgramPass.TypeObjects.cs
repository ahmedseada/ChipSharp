using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Type objects in the whole-program pass (see <see cref="TypeObjects"/>): <c>typeof(X)</c> and <c>GetType()</c>
/// compile on every target, as their objects are constants; the rest of reflection is named for what it is.
/// </summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// The verdicts on type objects for the instructions of <paramref name="method"/> that have one (null: the
    /// instruction is fine): both halves of a <c>typeof</c> and a <c>GetType()</c>; a <c>typeof</c> of an open generic
    /// type, <see cref="Unsupported.OpenGenericTypeOf"/>; any other token of a type (a RuntimeTypeHandle),
    /// <see cref="Unsupported.TypeHandles"/>; a member of System.Type, System.Reflection or System.Activator
    /// that ChipSharp.Library's Type does not have, <see cref="Unsupported.Reflection"/>.
    /// </summary>
    private static Dictionary<IlInstruction, Unsupported?> ForTypeObjects(IlMethod method)
    {
        var verdicts = new Dictionary<IlInstruction, Unsupported?>();
        var instructions = method.Body!.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (verdicts.ContainsKey(instruction))
            {
                continue; // the second half of a typeof of an open generic type
            }

            if (TypeObjects.GivesTypeObject(instructions, i) || TypeObjects.EndsTypeOf(instructions, i))
            {
                verdicts[instruction] = null;
            }
            else if (instruction is { OpCode: IlOpCode.Ldtoken, Operand: IlTypeOperand }
                && i + 1 < instructions.Count && TypeObjects.IsGetTypeFromHandle(instructions[i + 1]))
            {
                verdicts[instruction] = Unsupported.OpenGenericTypeOf;
                verdicts[instructions[i + 1]] = Unsupported.OpenGenericTypeOf;
            }
            else if (instruction is { OpCode: IlOpCode.Ldtoken, Operand: IlTypeOperand })
            {
                verdicts[instruction] = Unsupported.TypeHandles;
            }
            else if (instruction is { OpCode: IlOpCode.Call or IlOpCode.Callvirt or IlOpCode.Newobj,
                    Operand: IlMethodOperand { Method: { Definition: null } callee } }
                && IsReflection(callee.DeclaringType))
            {
                verdicts[instruction] = Unsupported.Reflection(CSharpNames.Method(callee));
            }
        }

        return verdicts;
    }

    /// <summary>True for System.Type, a type of System.Reflection and System.Activator: what reflection is.</summary>
    private static bool IsReflection(IlType type) => ValueTypes.WithoutModifiers(type) is IlNamedType named
        && !named.IsDefinedInThisAssembly
        && (named.FullName is "System.Type" or "System.Activator" or "System.RuntimeTypeHandle"
            || named.Namespace == "System.Reflection");
}
