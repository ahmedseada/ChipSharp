using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Generics;

/// <summary>
/// The IL of an instance of a generic method (see <see cref="Instantiations"/>): its generic code with the type arguments
/// in place of the type parameters, in its locals and in every type, method and field its instructions name, and then
/// what the code does through a type parameter made what it does on that type:
/// <list type="bullet">
/// <item><c>constrained. T callvirt I::M</c> on a struct T is a call of the struct's own method for M (an explicit
/// implementation, <c>void IDevice.Tick()</c>, too); on a number, <c>CompareTo</c>, <c>Equals</c> and
/// <c>GetHashCode</c> are calls of ChipSharp.Library's <c>Numbers</c>, which do what .NET's do, and <c>ToString</c>
/// is the number's own ToString, which printed text prints;</item>
/// <item>generic math, <c>constrained. T call INumber&lt;T&gt;::op_Addition</c> and the like on a number, is the
/// operation itself (<see cref="GenericMath"/>);</item>
/// <item><c>box T</c> before <c>Console.WriteLine(object)</c> (C#'s <c>Console.WriteLine(t)</c>) is the WriteLine of
/// T, and before a test against null (a value never is null) nothing;</item>
/// <item><c>typeof(T) == typeof(int)</c> is true or false, and <c>sizeof(T)</c> and <c>Unsafe.SizeOf&lt;T&gt;()</c>
/// of a number, a bool or an enum its size.</item>
/// </list>
/// Each such sequence of instructions is replaced by the new ones in its own bytes, so every other instruction keeps
/// its offset and every branch its target: a <c>nop</c> takes the bytes the new ones do not. What is left (a
/// constrained call that is none of these) the whole-program pass reports.
/// </summary>
internal static partial class Specialization
{
    /// <summary>The IL of <paramref name="instance"/>: that of <paramref name="definition"/> with <paramref name="substitution"/>.</summary>
    public static IlMethodBody? Body(IlMethod instance, IlMethod definition, Substitution substitution)
    {
        if (definition.Body is not { } body)
        {
            return null;
        }

        // the instance's IL names types as its context does: the program's, for the library's generic code over them
        var owner = instance.Assembly;
        IlLocal[] locals = [.. body.Locals.Select(local => local with { Type = substitution.Apply(local.Type) })];
        var instructions = body.Instructions
            .Select(instruction => new IlInstruction(instruction.Offset, instruction.OpCode,
                Operand(instruction.Operand, substitution, owner), instruction.Size))
            .ToList();
        IlExceptionRegion[] regions =
        [
            .. body.ExceptionRegions.Select(region =>
                region with { CatchType = region.CatchType is { } type ? substitution.Apply(type) : null }),
        ];

        Rewrite(owner, instructions, instance);
        return new IlMethodBody(instance, body.MaxStack, body.InitLocals, body.CodeSize, locals, instructions, regions);
    }

    private static IlOperand? Operand(IlOperand? operand, Substitution substitution, IlAssembly owner) => operand switch
    {
        IlTypeOperand type => new IlTypeOperand(substitution.Apply(type.Type)),
        IlMethodOperand method => new IlMethodOperand(substitution.Apply(method.Method, owner)),
        IlFieldOperand field => new IlFieldOperand(substitution.Apply(field.Field, owner)),
        IlSignatureOperand signature => new IlSignatureOperand(substitution.Apply(signature.Signature)),
        _ => operand,
    };

    /// <summary>Replaces what the code does through a type parameter (see the summary), in place.</summary>
    private static void Rewrite(IlAssembly owner, List<IlInstruction> instructions, IlMethod instance)
    {
        var targets = instructions.SelectMany(Targets).ToHashSet();
        for (var i = 0; i < instructions.Count; i++)
        {
            // what the code does through a T that is a reference type (Specialization.References.cs)
            if (RewriteReference(owner, instructions, i, instance, targets))
            {
                continue;
            }

            // a box of a value that the type argument says what it does (Specialization.Boxing.cs)
            if (RewriteBox(owner, instructions, i, targets))
            {
                continue;
            }

            var (count, replacement) = Replacement(owner, instructions, i);
            if (replacement is not null)
            {
                Replace(instructions, i, count, replacement, targets);
            }
        }
    }

    /// <summary>The new instructions for the ones starting at <paramref name="index"/>, and how many they replace; null when none.</summary>
    private static (int Count, IReadOnlyList<(IlOpCode, IlOperand?)>? With) Replacement(IlAssembly owner,
        List<IlInstruction> instructions, int index)
    {
        var instruction = instructions[index];
        var next = index + 1 < instructions.Count ? instructions[index + 1] : null;
        switch (instruction)
        {
            case { OpCode: IlOpCode.Constrained, Operand: IlTypeOperand { Type: var type } }
                when next is { OpCode: IlOpCode.Call or IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } }:
                return (2, ConstrainedCalls.Replacement(owner, type, callee));

            case { OpCode: IlOpCode.Box, Operand: IlTypeOperand { Type: var boxed } }:
                return Box(owner, boxed, next);

            case { OpCode: IlOpCode.Ldtoken, Operand: IlTypeOperand { Type: var first } }
                when TypeComparison(instructions, index, first) is { } equal:
                return (5, [(IlOpCode.Ldc_I4, new IlInt32Operand(equal ? 1 : 0))]);

            case { OpCode: IlOpCode.Sizeof, Operand: IlTypeOperand { Type: var measured } }
                when SizeOf(owner, measured) is { } size:
                return (1, [(IlOpCode.Ldc_I4, new IlInt32Operand(size))]);

            case
            {
                OpCode: IlOpCode.Call,
                Operand: IlMethodOperand
                {
                    Method:
                    {
                        Name: "SizeOf", HasThis: false, Definition: null, GenericArguments: [var measured],
                        DeclaringType: IlNamedType { Namespace: "System.Runtime.CompilerServices", Name: "Unsafe" },
                    },
                },
            } when SizeOf(owner, measured) is { } size:
                return (1, [(IlOpCode.Ldc_I4, new IlInt32Operand(size))]);

            default:
                return (0, null);
        }
    }

    /// <summary>
    /// <c>box T</c> of a value: before <c>Console.Write(object)</c> or <c>Console.WriteLine(object)</c>, T's own Write or
    /// WriteLine of a number, bool or text; before brtrue or brfalse, true (a value is never null); of text, nothing (a
    /// string is an object already). Anything else stays boxing, which the whole-program pass reports.
    /// </summary>
    private static (int, IReadOnlyList<(IlOpCode, IlOperand?)>?) Box(IlAssembly owner, IlType boxed, IlInstruction? next)
    {
        var type = ValueTypes.Underlying(StackArrays.Primitive(boxed), owner);
        if (next is
            {
                OpCode: IlOpCode.Call,
                Operand: IlMethodOperand
                {
                    Method:
                    {
                        Name: "Write" or "WriteLine", HasThis: false, Definition: null,
                        ParameterTypes: [IlPrimitiveType { Kind: IlPrimitiveKind.Object }],
                        DeclaringType: IlNamedType { FullName: "System.Console" } console,
                    } write,
                },
            }
            && ValueTypes.Definition(StackArrays.Primitive(boxed), owner) is not { } // not an enum: .NET prints its name
            && Printed(type) is { } printed)
        {
            var call = new IlMethodReference(console, write.Name, new IlMethodSignature(false, IlType.Void, [printed]), [], null);
            return (2, [(IlOpCode.Call, new IlMethodOperand(call))]);
        }

        if (type is IlPrimitiveType { Kind: IlPrimitiveKind.String })
        {
            return (1, []);
        }

        // (a nullable is null without a value: its box is tested as it is)
        if (next?.OpCode is IlOpCode.Brtrue or IlOpCode.Brtrue_S or IlOpCode.Brfalse or IlOpCode.Brfalse_S
            && ValueTypes.TryMapType(StackArrays.Primitive(boxed), owner, out _) && !Nullables.IsNullable(boxed))
        {
            return (1, [(IlOpCode.Pop, null), (IlOpCode.Ldc_I4_1, null)]);
        }

        return (0, null);
    }

    /// <summary>The parameter type of Console.Write that prints a value of <paramref name="type"/> as .NET prints it boxed.</summary>
    private static IlType? Printed(IlType type) => type is IlPrimitiveType primitive
        ? primitive.Kind switch
        {
            IlPrimitiveKind.Int8 or IlPrimitiveKind.UInt8 or IlPrimitiveKind.Int16 or IlPrimitiveKind.UInt16
                or IlPrimitiveKind.Int32 => IlType.Int32,
            IlPrimitiveKind.UInt32 => IlType.UInt32,
            IlPrimitiveKind.Int64 => IlType.Int64,
            IlPrimitiveKind.UInt64 => IlType.UInt64,
            IlPrimitiveKind.Bool or IlPrimitiveKind.Float32 or IlPrimitiveKind.Float64 or IlPrimitiveKind.String => primitive,
            _ => null,
        }
        : null;

    /// <summary>
    /// <c>typeof(A) == typeof(B)</c> (or !=) starting at <paramref name="index"/>, the ldtoken of A: whether it is true,
    /// when A and B are both known; null for anything else.
    /// </summary>
    private static bool? TypeComparison(List<IlInstruction> instructions, int index, IlType first)
    {
        if (index + 4 >= instructions.Count
            || !IsGetTypeFromHandle(instructions[index + 1])
            || instructions[index + 2] is not { OpCode: IlOpCode.Ldtoken, Operand: IlTypeOperand { Type: var second } }
            || !IsGetTypeFromHandle(instructions[index + 3])
            || instructions[index + 4] is not
            {
                OpCode: IlOpCode.Call,
                Operand: IlMethodOperand { Method: { Name: "op_Equality" or "op_Inequality" } comparison },
            }

            // System.Type's, or in ChipSharp.Library's own generic code its substitute's, which that code calls
            || comparison.DeclaringType is not IlNamedType { FullName: "System.Type" or "ChipSharp.Library.Type" }
            || Substitution.IsOpen(first) || Substitution.IsOpen(second))
        {
            return null;
        }

        var same = StackArrays.Primitive(first) == StackArrays.Primitive(second);
        return comparison.Name == "op_Equality" ? same : !same;
    }

    private static bool IsGetTypeFromHandle(IlInstruction instruction) => instruction is
    {
        OpCode: IlOpCode.Call,
        Operand: IlMethodOperand
        {
            Method: { Name: "GetTypeFromHandle", Definition: null, DeclaringType: IlNamedType { FullName: "System.Type" } },
        },
    };

    /// <summary>The size in bytes of a value of <paramref name="type"/>, as .NET's sizeof gives it, when it is the same on every target.</summary>
    private static int? SizeOf(IlAssembly owner, IlType type) =>
        ValueTypes.Underlying(StackArrays.Primitive(type), owner) is IlPrimitiveType primitive
            ? primitive.Kind switch
            {
                IlPrimitiveKind.Bool or IlPrimitiveKind.Int8 or IlPrimitiveKind.UInt8 => 1,
                IlPrimitiveKind.Char or IlPrimitiveKind.Int16 or IlPrimitiveKind.UInt16 => 2,
                IlPrimitiveKind.Int32 or IlPrimitiveKind.UInt32 or IlPrimitiveKind.Float32 => 4,
                IlPrimitiveKind.Int64 or IlPrimitiveKind.UInt64 or IlPrimitiveKind.Float64 => 8,
                _ => null,
            }
            : null;

    /// <summary>
    /// Replaces <paramref name="count"/> instructions from <paramref name="index"/> with <paramref name="replacement"/>, in
    /// the bytes they took: a nop first takes the bytes the new instructions do not need. Nothing changes when a branch
    /// goes into the middle of them or the new instructions do not fit.
    /// </summary>
    private static void Replace(List<IlInstruction> instructions, int index, int count,
        IReadOnlyList<(IlOpCode OpCode, IlOperand? Operand)> replacement, HashSet<int> targets)
    {
        var start = instructions[index].Offset;
        var end = instructions[index + count - 1].NextOffset;
        var room = end - start - replacement.Sum(r => Size(r.OpCode));
        if (room < 0 || targets.Any(target => target > start && target < end))
        {
            return;
        }

        var made = new List<IlInstruction>();
        var offset = start;
        if (room > 0 || replacement.Count == 0)
        {
            made.Add(new IlInstruction(offset, IlOpCode.Nop, null, room));
            offset += room;
        }

        foreach (var (opCode, operand) in replacement)
        {
            made.Add(new IlInstruction(offset, opCode, operand, Size(opCode)));
            offset += Size(opCode);
        }

        instructions.RemoveRange(index, count);
        instructions.InsertRange(index, made);
    }

    /// <summary>How many bytes an instruction takes: its opcode and its operand.</summary>
    private static int Size(IlOpCode opCode) => (opCode.IsTwoByte() ? 2 : 1) + opCode.GetOperandKind() switch
    {
        IlOperandKind.None => 0,
        IlOperandKind.Int8 or IlOperandKind.UInt8 or IlOperandKind.ShortBranch or IlOperandKind.ShortArgument
            or IlOperandKind.ShortLocal => 1,
        IlOperandKind.Argument or IlOperandKind.Local => 2,
        IlOperandKind.Int64 or IlOperandKind.Float64 => 8,
        _ => 4,
    };

    /// <summary>The offsets an instruction branches to.</summary>
    private static IEnumerable<int> Targets(IlInstruction instruction) => instruction.Operand switch
    {
        IlBranchOperand branch => [branch.Target],
        IlSwitchOperand table => table.Targets,
        _ => [],
    };
}
