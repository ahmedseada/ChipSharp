using ChipSharp.Frontend.Generics;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Delegates in the whole-program pass (see <see cref="Delegates"/>): which instructions of them it accepts, and for the
/// rest which feature to name. Making a delegate, C#'s object for a closure and Combine, Remove and GetInvocationList are
/// heap uses; invoking one, comparing two and the rest need no heap. The methods a delegate calls are part of the program
/// where it takes their address (<see cref="Delegates.AddressTaken"/>), those of a virtual one through its slot.
/// </summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// The verdicts on delegates for the instructions of <paramref name="method"/> that have one (null: the instruction
    /// is fine): <c>ldftn</c> and <c>ldvirtftn</c> (see <see cref="ForAddressTaken"/>), a delegate type's <c>newobj</c>
    /// and Invoke (the types of its Invoke), the <c>newobj</c> of a closure, the methods of System.Delegate ChipSharp
    /// has, Interlocked.CompareExchange of an object, object's methods on a delegate, and type tests against delegate types.
    /// </summary>
    private static Dictionary<IlInstruction, Unsupported?> ForDelegates(IlMethod method)
    {
        var verdicts = new Dictionary<IlInstruction, Unsupported?>();
        var assembly = method.Assembly;
        var instructions = method.Body!.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            switch (instruction)
            {
                case { OpCode: IlOpCode.Ldftn or IlOpCode.Ldvirtftn, Operand: IlMethodOperand { Method: var target } }:
                    verdicts[instruction] = ForAddressTaken(method, i, target);
                    break;

                case { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var constructor } }
                    when Delegates.IsConstructor(constructor, assembly):
                    verdicts[instruction] = ForInvokeTypes(constructor.DeclaringType, method)
                        ?? Unsupported.HeapUse(Unsupported.DelegateObjects);
                    break;

                case { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: { Definition: null } constructor } }
                    when constructor.ParameterTypes is [IlPrimitiveType { Kind: IlPrimitiveKind.Object },
                        IlPrimitiveType { Kind: IlPrimitiveKind.NativeInt }]:
                    // a delegate type of the library that ChipSharp does not know
                    verdicts[instruction] = Unsupported.LibraryType(CSharpNames.Type(constructor.DeclaringType));
                    break;

                case { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method.Definition.DeclaringType: var type } }
                    when IsClosure(type):
                    verdicts[instruction] = ForClass(type) ?? Unsupported.HeapUse(Unsupported.Closures);
                    break;

                case { OpCode: IlOpCode.Call or IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } }:
                    if (Delegates.IsInvoke(callee, assembly))
                    {
                        verdicts[instruction] = ForInvokeTypes(callee.DeclaringType, method);
                    }
                    else if (callee is { Name: "BeginInvoke" or "EndInvoke", HasThis: true }
                        && Delegates.InvokeSignature(callee.DeclaringType, assembly) is not null)
                    {
                        verdicts[instruction] = Unsupported.AsynchronousDelegates;
                    }
                    else if (Delegates.Call(callee, assembly) is { } call)
                    {
                        verdicts[instruction] = call is DelegateCall.Combine or DelegateCall.Remove or DelegateCall.InvocationList
                            ? Unsupported.HeapUse(Unsupported.DelegateObjects)
                            : null;
                    }
                    else if (instruction.OpCode == IlOpCode.Callvirt
                        && VirtualMethods.CalledSlot(instructions, i) is { Kind: DispatchSlotKind.Object } slot
                        && VirtualMethods.StackType(method, i, slot.Signature.ParameterTypes.Count) is { } receiver
                        && Delegates.IsDelegateType(receiver, assembly))
                    {
                        verdicts[instruction] = null;
                    }

                    break;

                case { OpCode: IlOpCode.Isinst or IlOpCode.Castclass, Operand: IlTypeOperand { Type: var type } }
                    when Delegates.IsDelegateType(type, assembly):
                    verdicts[instruction] = null;
                    break;
            }
        }

        return verdicts;
    }

    /// <summary>
    /// Why the method an <c>ldftn</c> or <c>ldvirtftn</c> at <paramref name="index"/> takes the address of cannot be called
    /// as the delegate made right after, or null when it can: a method of the program (of a class, static, or an instance
    /// of a generic one over values), a virtual one, or a library method ChipSharp calls (Console.WriteLine). An address
    /// that no delegate takes is a function pointer; a method of a struct, or an extension method of a value, would be
    /// called on a box.
    /// </summary>
    private static Unsupported? ForAddressTaken(IlMethod method, int index, IlMethodReference target)
    {
        var assembly = method.Assembly;
        var instructions = method.Body!.Instructions;
        var invoke = index + 1 < instructions.Count
            && instructions[index + 1] is { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var constructor } }
            && Delegates.IsConstructor(constructor, assembly)
            ? Delegates.InvokeSignature(constructor.DeclaringType, assembly)
            : null;
        if (invoke is null)
        {
            // a delegate of a library type ChipSharp does not know is that type's problem (at its newobj); else what is
            // left of Unsupported.Delegates, a function pointer
            return index + 1 < instructions.Count && instructions[index + 1] is
            {
                OpCode: IlOpCode.Newobj,
                Operand: IlMethodOperand { Method.ParameterTypes: [IlPrimitiveType { Kind: IlPrimitiveKind.Object },
                    IlPrimitiveType { Kind: IlPrimitiveKind.NativeInt }] },
            }
                ? null
                : Unsupported.Delegates;
        }

        if (instructions[index].OpCode == IlOpCode.Ldvirtftn && VirtualMethods.IsGenericVirtual(target))
        {
            return Unsupported.GenericVirtualMethods;
        }

        if (target.Definition is not { } definition)
        {
            return !target.HasThis && KnownExternals.TryMap(target, out _)
                && target.ParameterTypes.Count == invoke.ParameterTypes.Count
                ? null
                : Unsupported.LibraryMethod(CSharpNames.Method(target));
        }

        if (Instantiations.IsGeneric(definition))
        {
            return Instantiations.Refusal(target) ?? Unsupported.GenericMethods;
        }

        if (!definition.IsStatic)
        {
            return ValueTypes.IsStruct(definition.DeclaringType) ? Unsupported.DelegateOfValue : null;
        }

        // a static method closed over its first argument (an extension method), which is the delegate's target
        return definition.Parameters.Count == invoke.ParameterTypes.Count + 1
            && !ObjectTypes.TryMapType(definition.Parameters[0].Type, assembly, out _)
            ? Unsupported.DelegateOfValue
            : null;
    }

    /// <summary>
    /// Why a delegate of <paramref name="type"/> cannot be made or invoked in <paramref name="method"/>, or null when it
    /// can: its Invoke takes or returns a type ChipSharp does not compile (that type's problem), or an array or a span,
    /// which the analysis of arrays cannot follow into the method the delegate calls.
    /// </summary>
    private static Unsupported? ForInvokeTypes(IlType type, IlMethod method)
    {
        var assembly = method.Assembly;
        var signature = Delegates.InvokeSignature(type, assembly)!;
        foreach (var part in signature.ParameterTypes.Prepend(signature.ReturnType))
        {
            var plain = ValueTypes.WithoutModifiers(part is IlByRefType reference ? reference.ElementType : part);
            if (plain is IlArrayType or IlMultiDimensionalArrayType || Spans.IsSpan(plain))
            {
                return Unsupported.DelegateArrays;
            }

            if (!SupportedSubset.TryMapType(part, assembly, out _))
            {
                return ForAnyType(part, assembly, method) ?? Unsupported.TypeNamed(CSharpNames.Type(part));
            }
        }

        return null;
    }

    /// <summary>
    /// True for a class C# makes for lambdas: what a lambda captures (<c>&lt;&gt;c__DisplayClass0_0</c>), or the one
    /// object of the lambdas that capture nothing (<c>&lt;&gt;c</c>). An instance of a generic one (a lambda in a generic
    /// method) is a generic class's.
    /// </summary>
    private static bool IsClosure(IlTypeDefinition type) => type.GenericDefinition is null && ObjectTypes.IsClass(type)
        && (type.Name == "<>c" || type.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal));
}
