using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Generics;

/// <summary>
/// The default comparer called at once: C#'s <c>EqualityComparer&lt;T&gt;.Default.Equals(a, b)</c> and
/// <c>EqualityComparer&lt;T&gt;.Default.GetHashCode(a)</c>, of which C# makes a record's Equals and GetHashCode. The IL
/// is changed where it is read: <c>call get_Default</c> becomes a <c>nop</c>, and the <c>callvirt</c> of Equals or
/// GetHashCode on it a call of what the comparer's own method calls for T (ChipSharp.Library's Equality, see
/// <see cref="Specialization"/>): <c>OfEquatable&lt;int&gt;</c> for an int, <c>OfObject&lt;Node&gt;</c> for a class, and
/// for a nullable <c>OfNullable&lt;int&gt;</c>, which compares as .NET's NullableEqualityComparer does. So no comparer
/// object is made (a record struct compares with no heap), and a nullable T needs no instance of the comparer.
/// </summary>
/// <remarks>
/// Only a comparer that nothing else takes is folded: between <c>get_Default</c> and the call, in one block, the
/// instructions push the arguments without taking the comparer off the stack (loads of arguments, locals and fields,
/// calls of getters, constants). The comparer kept anywhere (<c>var c = EqualityComparer&lt;T&gt;.Default;</c>) is the
/// object it is.
/// </remarks>
internal static class DefaultComparers
{
    private const string Equality = "ChipSharp.Library.Equality";

    /// <summary><paramref name="body"/> with each default comparer called at once folded (see the summary).</summary>
    public static IlMethodBody Fold(IlMethodBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var instructions = body.Instructions;
        List<IlInstruction>? folded = null;
        HashSet<int>? targets = null;
        for (var i = 0; i < instructions.Count; i++)
        {
            if (Comparer(instructions[i]) is not { } compared)
            {
                continue;
            }

            targets ??= [.. instructions.SelectMany(BranchTargets)];
            if (Consumer(instructions, i, compared, targets, body.Method) is not var (index, replacement))
            {
                continue;
            }

            folded ??= [.. instructions];
            var (getDefault, call) = (folded[i], folded[index]);
            folded[i] = new IlInstruction(getDefault.Offset, IlOpCode.Nop, null, getDefault.Size);
            folded[index] = new IlInstruction(call.Offset, IlOpCode.Call, new IlMethodOperand(replacement), call.Size);
        }

        return folded is null ? body
            : new IlMethodBody(body.Method, body.MaxStack, body.InitLocals, body.CodeSize, body.Locals, folded,
                body.ExceptionRegions);
    }

    /// <summary>The T of <c>call EqualityComparer&lt;T&gt;::get_Default()</c>; null for any other instruction.</summary>
    private static IlType? Comparer(IlInstruction instruction) =>
        instruction is { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: { Name: "get_Default", HasThis: false } callee } }
        && IsEqualityComparer(callee.DeclaringType, out var compared)
            ? compared
            : null;

    /// <summary>True for EqualityComparer&lt;T&gt;, the library's or ChipSharp.Library's own, with its T.</summary>
    private static bool IsEqualityComparer(IlType type, out IlType compared)
    {
        compared = IlType.Void;
        if (type is IlGenericInstanceType { Definition: IlNamedType definition, Arguments: [var argument] }
            && definition.FullName is "System.Collections.Generic.EqualityComparer`1" or "ChipSharp.Library.EqualityComparer`1")
        {
            compared = argument;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The call of Equals or GetHashCode on the comparer made at <paramref name="start"/>, and what it becomes; null when
    /// something else takes the comparer, or a branch or a branch target comes first.
    /// </summary>
    private static (int Index, IlMethodReference Replacement)? Consumer(IReadOnlyList<IlInstruction> instructions, int start,
        IlType compared, HashSet<int> targets, IlMethod method)
    {
        var depth = 0; // what is on the stack above the comparer
        for (var i = start + 1; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (targets.Contains(instruction.Offset) || IsControl(instruction.OpCode))
            {
                return null;
            }

            if (instruction is { OpCode: IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } }
                && IsEqualityComparer(callee.DeclaringType, out var on) && on == compared
                && callee.ParameterTypes.Count == depth && Replacement(callee, compared, method.Assembly) is { } replacement)
            {
                return (i, replacement);
            }

            var (pops, pushes) = Analysis.IlStackEffects.Of(instruction, method);
            if (pops > depth || instruction.OpCode is IlOpCode.Dup or IlOpCode.Pop)
            {
                return null;
            }

            depth += pushes - pops;
        }

        return null;
    }

    /// <summary>What a call of the comparer's Equals or GetHashCode becomes (see the summary); null for another method.</summary>
    private static IlMethodReference? Replacement(IlMethodReference callee, IlType compared, IlAssembly owner)
    {
        var name = callee switch
        {
            { Name: "Equals", ParameterTypes.Count: 2 } => "AreEqual",
            { Name: "GetHashCode", ParameterTypes.Count: 1 } => "HashOf",
            _ => null,
        };
        if (name is null || LibrarySubstitutes.Assembly.FindType(Equality) is not { } equality)
        {
            return null;
        }

        var plain = ValueTypes.WithoutModifiers(compared);
        if (plain is IlGenericInstanceType { Definition: IlNamedType { FullName: "System.Nullable`1" }, Arguments: [var value] })
        {
            return Helper(equality, name == "AreEqual" ? "OfNullable" : "HashOfNullable", value, owner);
        }

        // what the comparer's own method calls for T, as the instance of its code would (open: in generic code, which
        // the instances of that code then make so)
        return (Substitution.IsOpen(plain) ? null : Specialization.DefaultComparison(name, plain, owner))
            ?? Helper(equality, name, plain, owner);
    }

    private static IlMethodReference? Helper(IlTypeDefinition equality, string name, IlType argument, IlAssembly owner) =>
        equality.Methods.FirstOrDefault(method => method.Name == name) is { } helper
            ? Instantiations.Link(new IlMethodReference(new IlNamedType("ChipSharp.Library", "Equality", null, "ChipSharp.Library"),
                helper.Name, helper.Signature, [argument], helper), owner)
            : null;

    /// <summary>True for an instruction after which control may not go on to the next one, or may go elsewhere.</summary>
    private static bool IsControl(IlOpCode opCode) => opCode is IlOpCode.Ret or IlOpCode.Throw or IlOpCode.Rethrow
        or IlOpCode.Endfinally or IlOpCode.Jmp
        || opCode.GetOperandKind() is IlOperandKind.Branch or IlOperandKind.ShortBranch or IlOperandKind.Switch;

    private static IEnumerable<int> BranchTargets(IlInstruction instruction) => instruction.Operand switch
    {
        IlBranchOperand branch => [branch.Target],
        IlSwitchOperand table => table.Targets,
        _ => [],
    };
}
