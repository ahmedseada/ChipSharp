using ChipSharp.Frontend.Generics;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Generics in the whole-program pass (see <see cref="Instantiations"/>): the program reaches the instances of generic
/// methods and types its code links to, each a method of its own, never the generic code itself. What is left is
/// reported where the code uses it.
/// </summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// The verdicts on generics for the instructions of <paramref name="method"/> that have one (null: the instruction is
    /// fine, as part of one that has a problem):
    /// <list type="bullet">
    /// <item>a call of a generic method or of a method of a generic type, or the use of a field of a generic type, that
    /// is not linked to an instance: why its type arguments are not values (<see cref="Instantiations.Refusal(IlMethodReference)"/>);</item>
    /// <item><c>box</c> of a struct that implements an interface, but for Console.WriteLine: converting it to an
    /// interface (<see cref="Unsupported.BoxingToInterface"/>);</item>
    /// <item>a constrained call on a number that is none of those an instance compiles (see
    /// <see cref="ConstrainedCalls"/>): a library method that is not available, or checked arithmetic; its
    /// <c>constrained.</c> prefix is then part of it.</item>
    /// </list>
    /// </summary>
    private static Dictionary<IlInstruction, Unsupported?> ForGenerics(IlMethod method)
    {
        var verdicts = new Dictionary<IlInstruction, Unsupported?>();
        var instructions = method.Body!.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            switch (instructions[i])
            {
                case { OpCode: IlOpCode.Call or IlOpCode.Callvirt or IlOpCode.Newobj,
                    Operand: IlMethodOperand { Method: { Definition: { } definition } callee } } instruction
                    when Instantiations.IsGeneric(definition):
                    verdicts[instruction] = Instantiations.Refusal(callee)
                        ?? (definition.GenericParameters.Count > 0 ? Unsupported.GenericMethods : Unsupported.GenericTypes);
                    break;

                case { Operand: IlFieldOperand { Field: { Definition.DeclaringType.GenericParameters.Count: > 0 } field } }
                    instruction:
                    verdicts[instruction] = (field.DeclaringType is IlGenericInstanceType instance
                        ? Instantiations.Refusal(instance, method.Assembly)
                        : null) ?? Unsupported.GenericTypes;
                    break;

                case { OpCode: IlOpCode.Box, Operand: IlTypeOperand { Type: var boxed } } instruction
                    when ValueTypes.Definition(boxed, method.Assembly) is { } type && ValueTypes.IsStruct(type)
                        && type.Assembly.Interfaces(type.GenericDefinition ?? type).Count > 0
                        && !IsConsoleWriteOfObject(i + 1 < instructions.Count ? instructions[i + 1] : null):
                    verdicts[instruction] = Unsupported.BoxingToInterface;
                    break;

                case { OpCode: IlOpCode.Constrained, Operand: IlTypeOperand { Type: var type } } prefix
                    when i + 1 < instructions.Count
                        && instructions[i + 1] is { Operand: IlMethodOperand { Method: { Definition: null } callee } } call
                        && StackArrays.Primitive(type) is IlPrimitiveType { Kind: >= IlPrimitiveKind.Bool and <= IlPrimitiveKind.NativeUInt } number:
                    verdicts[prefix] = null;
                    verdicts[call] = callee.Name.StartsWith("op_Checked", StringComparison.Ordinal)
                        && number.Kind is not (IlPrimitiveKind.Float32 or IlPrimitiveKind.Float64)
                        ? Unsupported.CheckedArithmetic
                        : Unsupported.LibraryMethod(CSharpNames.Method(callee));
                    i++;
                    break;
            }
        }

        return verdicts;
    }

    /// <summary>True for a call of Console.Write(object) or Console.WriteLine(object).</summary>
    private static bool IsConsoleWriteOfObject(IlInstruction? instruction) => instruction is
    {
        OpCode: IlOpCode.Call,
        Operand: IlMethodOperand
        {
            Method:
            {
                Name: "Write" or "WriteLine", Definition: null,
                DeclaringType: IlNamedType { FullName: "System.Console" },
                ParameterTypes: [IlPrimitiveType { Kind: IlPrimitiveKind.Object }],
            },
        },
    };
}
