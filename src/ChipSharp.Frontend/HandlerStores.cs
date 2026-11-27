using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// What an interrupt handler may not do so that the garbage collector can run with interrupts on (see
/// src/ChipSharp.Runtime.Shared/chipsharp_heap.c): store a reference other than null where the collector reads
/// references. The collector marks what it reaches from the static fields, the stack and the objects, one after another;
/// an interrupt that comes in between and moves a reference from where it has not looked yet to where it has looked
/// already (a static field, a field of an object, an element of an array) would leave that object unmarked, and freed
/// while in use. A handler may read every reference, store numbers and bools anywhere, set a reference to null (that
/// only makes garbage, which the collector frees then or next time), and store references in its own locals.
/// </summary>
internal static class HandlerStores
{
    /// <summary>
    /// The instructions of <paramref name="method"/> that store a reference other than null in a static field, a field
    /// of an object (or of a struct that is not a local of the method), an element of an array or through a ref, and the
    /// calls of the library's methods that copy, move or fill references in an array or a span (Array.Copy, Fill,
    /// Reverse, Sort, Span's CopyTo, Fill, Reverse, ...).
    /// </summary>
    public static IEnumerable<IlInstruction> Find(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var instructions = method.Body?.Instructions ?? [];
        var assembly = method.Assembly;
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            var stores = instruction switch
            {
                { OpCode: IlOpCode.Stsfld, Operand: IlFieldOperand { Field: var field } } =>
                    HoldsReferences(field.FieldType, assembly) && !IsNull(method, i, 0),
                { OpCode: IlOpCode.Stfld, Operand: IlFieldOperand { Field: var field } } =>
                    HoldsReferences(field.FieldType, assembly) && !IsNull(method, i, 0) && !IsLocalAddress(method, i, 1),
                { OpCode: IlOpCode.Stelem_Ref } => !IsNull(method, i, 0),
                { OpCode: IlOpCode.Stelem or IlOpCode.Stobj, Operand: IlTypeOperand { Type: var type } } =>
                    HoldsReferences(type, assembly) && !IsNull(method, i, 0)
                    && !(instruction.OpCode == IlOpCode.Stobj && IsLocalAddress(method, i, 1)),
                { OpCode: IlOpCode.Stind_Ref } => !IsNull(method, i, 0) && !IsLocalAddress(method, i, 1),
                { OpCode: IlOpCode.Cpobj, Operand: IlTypeOperand { Type: var type } } =>
                    HoldsReferences(type, assembly) && !IsLocalAddress(method, i, 1),
                { OpCode: IlOpCode.Call or IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } } =>
                    MovesReferences(method, i, callee),
                _ => false,
            };
            if (stores)
            {
                yield return instruction;
            }
        }
    }

    /// <summary>True when a value of <paramref name="type"/> is or holds a reference the collector follows.</summary>
    public static bool HoldsReferences(IlType type, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(assembly);
        type = ValueTypes.WithoutModifiers(StackArrays.Primitive(type));
        switch (type)
        {
            case IlPrimitiveType primitive:
                return primitive.Kind is IlPrimitiveKind.Object or IlPrimitiveKind.String;
            case IlArrayType:
                return true;
            case IlByRefType or IlPointerType:
                return false;
        }

        if (ObjectTypes.TryMapType(type, assembly, out _))
        {
            return true;
        }

        // a struct holds references when its fields do; what cannot be mapped is taken to hold some
        return !ValueTypes.TryMapType(type, assembly, out var mapped) || Holds(mapped);
    }

    private static bool Holds(TirType type) => type switch
    {
        { Kind: TirTypeKind.Ref } => true,
        TirStructType structType => structType.Fields.Any(field => Holds(field.Type)),
        TirArrayType array => Holds(array.Element),
        _ => false,
    };

    /// <summary>
    /// True for a call of a library method that writes references into an array or a span: what copies, fills, moves or
    /// sorts elements that hold references (Array.Clear and Span's Clear only write nulls).
    /// </summary>
    private static bool MovesReferences(IlMethod method, int index, IlMethodReference callee)
    {
        if (callee.Definition is not null || callee.DeclaringType is not (IlNamedType or IlGenericInstanceType))
        {
            return false;
        }

        var (space, name) = callee.DeclaringType switch
        {
            IlNamedType named => (named.Namespace, named.Name),
            IlGenericInstanceType { Definition: IlNamedType named } => (named.Namespace, named.Name),
            _ => ("", ""),
        };
        if (space != "System")
        {
            return false;
        }

        var writes = callee.Name is "Copy" or "ConstrainedCopy" or "CopyTo" or "TryCopyTo" or "Fill" or "Reverse" or "Sort"
            or "Resize";
        if (!writes || name is not ("Array" or "Span`1" or "MemoryExtensions"))
        {
            return false;
        }

        // the element type: the method's type argument (Fill<T>, Reverse<T>, Sort<T>), the span's (Span<T>.CopyTo), or
        // the static type of the destination array (Array.Copy, array.CopyTo); unknown counts as one of references
        var element = callee.GenericArguments.FirstOrDefault()
            ?? (callee.DeclaringType as IlGenericInstanceType)?.Arguments.FirstOrDefault()
            ?? DestinationElement(method, index, callee);
        return element is null || HoldsReferences(element, method.Assembly);
    }

    /// <summary>The element type of the destination of Array.Copy, ConstrainedCopy or CopyTo, from the IL that pushes it.</summary>
    private static IlType? DestinationElement(IlMethod method, int index, IlMethodReference callee)
    {
        var arguments = callee.ParameterTypes.Count + (callee.HasThis ? 1 : 0);
        var destination = (callee.Name, arguments) switch
        {
            ("CopyTo", _) => 1,               // this.CopyTo(destination, index)
            ("Copy", 3) => 1,                 // Copy(source, destination, length)
            (_, 5) => 2,                      // Copy(source, sourceIndex, destination, destinationIndex, length)
            _ => -1,
        };
        if (destination < 0 || Producer(method, index, arguments - 1 - destination) is not { } producer)
        {
            return null;
        }

        var type = producer switch
        {
            { Operand: IlFieldOperand { Field: var field } } when producer.OpCode is IlOpCode.Ldsfld or IlOpCode.Ldfld =>
                field.FieldType,
            { Operand: IlLocalOperand { Index: var local } } when producer.OpCode is IlOpCode.Ldloc or IlOpCode.Ldloc_S =>
                method.Body!.Locals[local].Type,
            _ => LocalOrArgument(method, producer),
        };
        return type is null ? null : HeapArrays.ElementType(type);
    }

    /// <summary>The type of the local or argument a short form of ldloc or ldarg loads; null for any other instruction.</summary>
    private static IlType? LocalOrArgument(IlMethod method, IlInstruction load)
    {
        var local = load.OpCode switch
        {
            IlOpCode.Ldloc_0 => 0,
            IlOpCode.Ldloc_1 => 1,
            IlOpCode.Ldloc_2 => 2,
            IlOpCode.Ldloc_3 => 3,
            _ => -1,
        };
        if (local >= 0)
        {
            return method.Body!.Locals[local].Type;
        }

        var argument = load switch
        {
            { OpCode: IlOpCode.Ldarg_0 } => 0,
            { OpCode: IlOpCode.Ldarg_1 } => 1,
            { OpCode: IlOpCode.Ldarg_2 } => 2,
            { OpCode: IlOpCode.Ldarg_3 } => 3,
            { OpCode: IlOpCode.Ldarg or IlOpCode.Ldarg_S, Operand: IlArgumentOperand { Index: var index } } => index,
            _ => -1,
        };
        var parameter = argument - (method.IsStatic ? 0 : 1);
        return parameter >= 0 && parameter < method.Parameters.Count ? method.Parameters[parameter].Type : null;
    }

    /// <summary>True when the value <paramref name="depth"/> places below the top of the stack before instruction <paramref name="index"/> is null.</summary>
    private static bool IsNull(IlMethod method, int index, int depth) =>
        Producer(method, index, depth) is { OpCode: IlOpCode.Ldnull };

    /// <summary>True when that value is the address of a local or an argument of the method itself (a struct of its own).</summary>
    private static bool IsLocalAddress(IlMethod method, int index, int depth) =>
        Producer(method, index, depth) is { OpCode: IlOpCode.Ldloca or IlOpCode.Ldloca_S or IlOpCode.Ldarga or IlOpCode.Ldarga_S };

    /// <summary>
    /// The instruction that pushed the value <paramref name="depth"/> places below the top of the evaluation stack (0:
    /// the top) before instruction <paramref name="index"/>, found in the straight run of code before it; null when
    /// that run does not show it (a branch, or a place another branch jumps to, comes first).
    /// </summary>
    private static IlInstruction? Producer(IlMethod method, int index, int depth)
    {
        var instructions = method.Body!.Instructions;
        var targets = BranchTargets(method);
        var skip = depth;
        for (var i = index - 1; i >= 0; i--)
        {
            var instruction = instructions[i];
            if (instruction.Operand is IlBranchOperand or IlSwitchOperand || instruction.OpCode is IlOpCode.Ret
                or IlOpCode.Throw)
            {
                return null;
            }

            var (pops, pushes) = IlStackEffects.Of(instruction, method);
            if (skip < pushes)
            {
                return instruction;
            }

            skip += pops - pushes;
            if (targets.Contains(instruction.Offset))
            {
                return null;
            }
        }

        return null;
    }

    private static HashSet<int> BranchTargets(IlMethod method)
    {
        var targets = new HashSet<int>();
        foreach (var instruction in method.Body!.Instructions)
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

        foreach (var region in method.Body.ExceptionRegions)
        {
            targets.Add(region.HandlerOffset);
        }

        return targets;
    }
}
