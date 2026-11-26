using System.Collections.Frozen;
using System.Reflection;
using System.Reflection.Emit;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// How many values an IL instruction takes from the evaluation stack and how many it puts back: for most opcodes
/// the fixed numbers of ECMA-335 Partition III (as System.Reflection.Emit lists them for every opcode), for calls
/// and <c>ret</c> what the signature says.
/// </summary>
internal static class IlStackEffects
{
    private static readonly FrozenDictionary<IlOpCode, (int Pops, int Pushes)> Fixed = Build();

    /// <summary>What <paramref name="instruction"/>, an instruction of <paramref name="method"/>, pops and pushes.</summary>
    public static (int Pops, int Pushes) Of(IlInstruction instruction, IlMethod method)
    {
        switch (instruction.OpCode)
        {
            case IlOpCode.Call or IlOpCode.Callvirt when instruction.Operand is IlMethodOperand { Method: var callee }:
                return (callee.ParameterTypes.Count + (callee.HasThis ? 1 : 0), IsVoid(callee.ReturnType) ? 0 : 1);

            case IlOpCode.Newobj when instruction.Operand is IlMethodOperand { Method: var constructor }:
                return (constructor.ParameterTypes.Count, 1);

            case IlOpCode.Calli when instruction.Operand is IlSignatureOperand { Signature: var signature }:
                return (signature.ParameterTypes.Count + (signature.HasThis ? 1 : 0) + 1,
                    IsVoid(signature.ReturnType) ? 0 : 1);

            case IlOpCode.Ret:
                return (IsVoid(method.ReturnType) ? 0 : 1, 0);

            default:
                return Fixed.GetValueOrDefault(instruction.OpCode);
        }
    }

    private static bool IsVoid(IlType type) => type switch
    {
        IlModifiedType modified => IsVoid(modified.UnmodifiedType),
        _ => type == IlType.Void,
    };

    private static FrozenDictionary<IlOpCode, (int, int)> Build()
    {
        var table = new Dictionary<IlOpCode, (int, int)>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opCode)
            {
                table[(IlOpCode)(ushort)opCode.Value] = (Count(opCode.StackBehaviourPop), Count(opCode.StackBehaviourPush));
            }
        }

        return table.ToFrozenDictionary();
    }

    /// <summary>How many values a stack behaviour stands for; the variable ones (calls, ret) count 0 here.</summary>
    private static int Count(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref or StackBehaviour.Push1
            or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8
            or StackBehaviour.Pushref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1
            or StackBehaviour.Popref_popi or StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
            or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8
            or StackBehaviour.Popref_popi_popref or StackBehaviour.Popref_popi_pop1 => 3,
        _ => 0,
    };
}
