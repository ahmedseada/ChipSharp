using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Virtual calls, interfaces of classes and type tests in the whole-program pass (see <see cref="VirtualMethods"/>):
/// which it accepts, and for the rest which feature to name. A call through a class's virtual method is judged with its
/// class (<see cref="ForClasses"/>); the methods a slot runs are reached through <see cref="VirtualDispatch"/>.
/// </summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// The verdicts on virtual calls, type tests and object's own methods for the instructions of
    /// <paramref name="method"/> that have one (null: the instruction is fine):
    /// <list type="bullet">
    /// <item>a <c>callvirt</c> through a slot of an interface (one an object may implement, see
    /// <see cref="VirtualMethods.IsInterface"/>) or of object's Equals or GetHashCode; of a generic virtual method,
    /// <see cref="Unsupported.GenericVirtualMethods"/>; of object's ToString, <see cref="Unsupported.ObjectMethods"/>
    /// (it makes a string); of its GetType, <see cref="Unsupported.TypeHandles"/>;</item>
    /// <item>a <c>call</c> of object's Equals or GetHashCode (C#'s <c>base.Equals(other)</c>);</item>
    /// <item><c>isinst</c> and <c>castclass</c> (<see cref="ForTypeTest"/>).</item>
    /// </list>
    /// </summary>
    private static Dictionary<IlInstruction, Unsupported?> ForVirtualCalls(IlMethod method)
    {
        var verdicts = new Dictionary<IlInstruction, Unsupported?>();
        var instructions = method.Body!.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            switch (instructions[i])
            {
                case { OpCode: IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } } instruction
                    when i == 0 || instructions[i - 1].OpCode != IlOpCode.Constrained:
                    if (VirtualMethods.IsGenericVirtual(callee))
                    {
                        verdicts[instruction] = Unsupported.GenericVirtualMethods;
                    }
                    else if (VirtualMethods.CalledSlot(instructions, i) is { Kind: not DispatchSlotKind.Class } slot)
                    {
                        verdicts[instruction] = slot.Kind == DispatchSlotKind.Interface
                            && !ObjectTypes.TryMapType(slot.Owner, method.Assembly, out _)
                            ? ForAnyType(slot.Owner, method.Assembly, method) ?? Unsupported.Interfaces
                            : null;
                    }
                    else if (callee is { Definition: null, Name: "GetType", ParameterTypes.Count: 0 }
                        && ObjectTypes.IsSystemObject(callee.DeclaringType))
                    {
                        verdicts[instruction] = Unsupported.TypeHandles;
                    }
                    else if (callee is { Definition: null, Name: "ToString", ParameterTypes.Count: 0 }
                        && ObjectTypes.IsSystemObject(callee.DeclaringType)
                        && VirtualMethods.StackType(method, i, 0) is { } receiver
                        && ObjectTypes.TryMapType(receiver, method.Assembly, out _))
                    {
                        // of an object of the program's: a StringBuilder's ToString is building text, as before
                        verdicts[instruction] = Unsupported.ObjectMethods;
                    }

                    break;

                case { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: var callee } } instruction
                    when VirtualMethods.ObjectSlotOf(callee) is not null:
                    verdicts[instruction] = null;
                    break;

                case { OpCode: IlOpCode.Isinst or IlOpCode.Castclass, Operand: IlTypeOperand { Type: var type } } instruction:
                    verdicts[instruction] = ForTypeTest(type, method);
                    break;
            }
        }

        return verdicts;
    }

    /// <summary>
    /// Why <c>is</c>, <c>as</c> or a cast to <paramref name="type"/> in <paramref name="method"/> cannot be compiled,
    /// or null when it can: to object, to a class of the program (when its objects can be compiled), to an interface
    /// an object may implement, or to an array type whose arrays may be on the heap (see <see cref="HeapArrays"/>). A
    /// value (a number, a struct, an enum, a nullable) is unboxing, which is boxing's; string, a library class and the
    /// other arrays are type tests ChipSharp does not make yet.
    /// </summary>
    private static Unsupported? ForTypeTest(IlType type, IlMethod method)
    {
        var assembly = method.Assembly;
        var plain = ValueTypes.WithoutModifiers(type);
        if (ObjectTypes.IsSystemObject(plain) || VirtualMethods.IsInterface(plain, assembly))
        {
            return null;
        }

        if (ObjectTypes.Class(plain, assembly) is { } target)
        {
            return ForClass(target);
        }

        return plain switch
        {
            IlArrayType { ElementType: var element } when HeapArrays.TryMapElementType(element, assembly, out _) => null,
            IlArrayType or IlMultiDimensionalArrayType => Unsupported.TypeTestsOf(CSharpNames.Type(plain, method)),
            IlGenericInstanceType { Definition: IlNamedType { FullName: "System.Nullable`1" } } => Unsupported.Boxing,
            _ when ValueTypes.TryMapType(plain, assembly, out _) => Unsupported.Boxing,
            IlNamedType { IsDefinedInThisAssembly: true } or IlGenericInstanceType { Definition: IlNamedType { IsDefinedInThisAssembly: true } } =>
                ForAnyType(plain, assembly, method) ?? Unsupported.TypeTestsOf(CSharpNames.Type(plain)),
            _ => Unsupported.TypeTestsOf(CSharpNames.Type(plain)),
        };
    }
}
