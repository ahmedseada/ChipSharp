using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>Boxing, unboxing and <c>Nullable&lt;T&gt;</c> in the whole-program pass (see <see cref="Boxing"/> and <see cref="Nullables"/>).</summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// The verdicts on boxing for the instructions of <paramref name="method"/> that have one (null: the instruction is
    /// fine):
    /// <list type="bullet">
    /// <item><c>box</c> of a number, an enum or a struct of the program (a nullable of one too): a heap use, which a
    /// target without a heap names as boxing, or as converting a struct to an interface for a struct that implements
    /// one; of a reference (in generic code), nothing; of anything else, that type's problem;</item>
    /// <item><c>unbox.any</c> and <c>unbox</c> to such a value (<c>unbox.any</c> to a nullable too), and <c>isinst</c>
    /// of one (C#'s <c>o is int n</c>), fine: they make no object; <c>unbox.any</c> to a reference is a cast;</item>
    /// <item><c>constrained.</c> and the <c>callvirt</c> after it of object's ToString, Equals or GetHashCode or of an
    /// interface's method on such a value, which runs on the value with no box;</item>
    /// <item>the members of <c>Nullable&lt;T&gt;</c> ChipSharp compiles (<see cref="NullableMember"/>) and
    /// <c>object.Equals(object, object)</c>.</item>
    /// </list>
    /// </summary>
    private static Dictionary<IlInstruction, Unsupported?> ForBoxing(IlMethod method)
    {
        var verdicts = new Dictionary<IlInstruction, Unsupported?>();
        var assembly = method.Assembly;
        var instructions = method.Body!.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            switch (instructions[i])
            {
                case { OpCode: IlOpCode.Box, Operand: IlTypeOperand { Type: var type } } box:
                    verdicts[box] = ForBox(type, method);
                    break;

                case { OpCode: IlOpCode.Unbox_Any or IlOpCode.Unbox, Operand: IlTypeOperand { Type: var type } } unbox:
                    verdicts[unbox] = ForUnbox(type, unbox.OpCode == IlOpCode.Unbox, method);
                    break;

                case { OpCode: IlOpCode.Isinst or IlOpCode.Castclass, Operand: IlTypeOperand { Type: var type } } test
                    when Boxing.Of(type, assembly) is not null:
                    verdicts[test] = test.OpCode == IlOpCode.Isinst ? null : Unsupported.UnboxingAddresses;
                    break;

                case { OpCode: IlOpCode.Constrained } prefix when i + 1 < instructions.Count
                    && Boxing.ConstrainedAt(instructions, i + 1, assembly) is not null:
                    verdicts[prefix] = null;
                    verdicts[instructions[++i]] = null;
                    break;

                case { OpCode: IlOpCode.Call or IlOpCode.Newobj, Operand: IlMethodOperand { Method: var callee } } call
                    when Nullables.MemberOf(callee) is not null || Boxing.IsObjectEquals(call):
                    verdicts[call] = null;
                    break;
            }
        }

        return verdicts;
    }

    /// <summary>Why a <c>box</c> of <paramref name="type"/> needs what it needs (see <see cref="ForBoxing"/>).</summary>
    private static Unsupported? ForBox(IlType type, IlMethod method)
    {
        var plain = StackArrays.Primitive(ValueTypes.WithoutModifiers(type));
        if (IsReference(plain, method.Assembly))
        {
            return null;
        }

        if (Boxing.Of(plain, method.Assembly) is not { } boxed)
        {
            return ForAnyType(plain, method.Assembly, method) ?? Unsupported.BoxingOf(CSharpNames.Type(plain, method));
        }

        // a struct that implements an interface is boxed to be used through it, most likely
        return Unsupported.HeapUse(boxed.Kind == BoxedKind.Struct && !Nullables.IsNullable(plain)
            && boxed.Definition!.Assembly.Interfaces(boxed.Definition.GenericDefinition ?? boxed.Definition).Count > 0
            ? Unsupported.BoxingToInterface
            : Unsupported.Boxing);
    }

    /// <summary>Why an <c>unbox.any</c> (or an <c>unbox</c>, <paramref name="address"/>) to <paramref name="type"/> cannot be compiled, or null.</summary>
    private static Unsupported? ForUnbox(IlType type, bool address, IlMethod method)
    {
        var plain = StackArrays.Primitive(ValueTypes.WithoutModifiers(type));
        if (IsReference(plain, method.Assembly))
        {
            return address ? Unsupported.UnboxingAddresses : ForTypeTest(plain, method);
        }

        if (Boxing.Of(plain, method.Assembly) is null)
        {
            return ForAnyType(plain, method.Assembly, method) ?? Unsupported.TypeTestsOf(CSharpNames.Type(plain, method));
        }

        return address && Nullables.IsNullable(plain) ? Unsupported.UnboxingAddresses : null;
    }

    /// <summary>True for a reference type: an object, text, an array, an interface, a class.</summary>
    private static bool IsReference(IlType type, IlAssembly assembly) =>
        ObjectTypes.TryMapType(type, assembly, out _) || FixedText.IsText(type) || type is IlArrayType
        || (type is IlNamedType { IsDefinedInThisAssembly: true } named && assembly.FindType(named.FullName) is { } definition
            && !ValueTypes.IsStruct(definition) && !ValueTypes.IsEnum(definition));
}
