using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Spans in the whole-program pass (see <see cref="Spans"/>): the members ChipSharp compiles itself, stackalloc and
/// constant data, and the rule that a span never outlives an array or stackalloc of its method
/// (<see cref="SpanEscapeAnalysis"/>).
/// </summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// The verdicts on spans for the instructions of <paramref name="method"/> that have one (null: the instruction is
    /// fine, as part of a span): a call of a member ChipSharp compiles itself (its elements must be supported, and
    /// ToArray needs the heap); a stackalloc into a span, of a constant size, with the instructions of its size and
    /// initializer; constant data made into a span; a collection expression as a span; and every place a span would
    /// outlive an array or stackalloc of the method.
    /// </summary>
    internal static Dictionary<IlInstruction, Unsupported?> ForSpans(IlMethod method)
    {
        var verdicts = new Dictionary<IlInstruction, Unsupported?>();
        var body = method.Body!;
        var instructions = body.Instructions;
        var assembly = method.Assembly;

        var made = new HashSet<int>();
        foreach (var allocation in Spans.FindStackallocs(body))
        {
            made.Add(allocation.Constructor);
            verdicts[instructions[allocation.Localloc]] = Unsupported.ForSpan(allocation.Element, assembly)
                ?? (allocation.Count is null ? Unsupported.StackallocSizes : null);
            for (var i = allocation.Start; i < allocation.Localloc; i++)
            {
                verdicts[instructions[i]] = null; // sizeof T and the multiplication, for a struct
            }

            for (var i = allocation.Localloc + 1; i < allocation.Constructor; i++)
            {
                if (instructions[i].OpCode is IlOpCode.Cpblk or IlOpCode.Unaligned)
                {
                    verdicts[instructions[i]] = null; // the copy of an initializer's constant data
                }
            }
        }

        foreach (var data in Spans.FindDataUses(body))
        {
            verdicts[instructions[data.Index]] = data.Element is { } element ? Unsupported.ForSpan(element, assembly) : null;
            if (data.Element is not null)
            {
                made.Add(data.End);
            }
        }

        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            switch (instruction)
            {
                case { OpCode: IlOpCode.Localloc } when !verdicts.ContainsKey(instruction):
                    verdicts[instruction] = MakesSpanLater(instructions, i)
                        ? Unsupported.StackallocInitializers
                        : Unsupported.StackallocPointers;
                    break;

                case { OpCode: IlOpCode.Call or IlOpCode.Newobj, Operand: IlMethodOperand { Method: var callee } }
                    when Spans.Call(callee) is { } call:
                    verdicts[instruction] = ForSpanCall(call, assembly, made.Contains(i));
                    break;

                // a generic substitute (MemoryExtensions) is made for types of the library and of .NET, not the program's
                case { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: { Definition: { } definition } callee } }
                    when LibrarySubstitutes.Defines(definition) && Generics.Instantiations.IsGeneric(definition)
                        && callee.GenericArguments.Any(argument => argument is IlNamedType { IsDefinedInThisAssembly: true }):
                    verdicts[instruction] = Unsupported.LibraryGenericsOfProgramTypes;
                    break;

                case { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: { } callee } }
                    when callee.GenericArguments.Any(Unsupported.IsCollectionExpression):
                case { OpCode: IlOpCode.Initobj, Operand: IlTypeOperand { Type: var type } }
                    when Unsupported.IsCollectionExpression(type):
                    verdicts[instruction] = Unsupported.SpanCollectionExpressions;
                    break;
            }
        }

        foreach (var (instruction, unsupported) in SpanEscapeAnalysis.Check(method))
        {
            if (verdicts.GetValueOrDefault(instruction) is null)
            {
                verdicts[instruction] = unsupported;
            }
        }

        return verdicts;
    }

    /// <summary>
    /// What a call of a member ChipSharp compiles itself needs that the program cannot have, or null.
    /// <paramref name="ofStackallocOrData"/> is true for the constructor that makes a span of a stackalloc or of
    /// constant data, the only pointer a span may be made from.
    /// </summary>
    private static Unsupported? ForSpanCall(SpanCall call, IlAssembly assembly, bool ofStackallocOrData) => call.Operation switch
    {
        SpanOperation.ToArray => Unsupported.SpanToArray,
        SpanOperation.ToText => Unsupported.BuildingText,
        SpanOperation.FromPointer when !ofStackallocOrData => Unsupported.PointerSpans,
        SpanOperation.Read or SpanOperation.Write or SpanOperation.TryRead or SpanOperation.TryWrite
            or SpanOperation.AsBytes => ForPlainData(call.Element, assembly),
        SpanOperation.Cast => ForPlainData(call.Element, assembly) ?? ForPlainData(call.Target!, assembly),
        _ => Unsupported.ForSpan(call.Element, assembly),
    };

    /// <summary>The first field of a struct (or of a struct in it) that holds a reference: a string or an object; null for none.</summary>
    private static (IlTypeDefinition Owner, IlField Field)? ReferenceField(IlType type, IlAssembly assembly)
    {
        if (ValueTypes.Definition(type, assembly) is not { } definition || !ValueTypes.IsStruct(definition))
        {
            return null;
        }

        foreach (var field in ValueTypes.InstanceFields(definition))
        {
            if (ObjectTypes.TryMapType(field.Type, assembly, out _) || HeapArrays.ElementType(field.Type) is not null)
            {
                return (definition, field);
            }

            if (ReferenceField(field.Type, assembly) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }

    /// <summary>
    /// Why a value of <paramref name="type"/> cannot be read or written as bytes (MemoryMarshal): it is not a number, a
    /// bool, an enum or a struct of them (no string or object in it); null when it is.
    /// </summary>
    private static Unsupported? ForPlainData(IlType type, IlAssembly assembly)
    {
        if (StackArrays.TryMapElementType(type, assembly, out _))
        {
            // a reference (a string, an object) is no bytes to read
            if (ReferenceField(type, assembly) is var (owner, field))
            {
                return Unsupported.MemoryMarshalOf(CSharpNames.Type(type),
                    $"the field {CSharpNames.Type(owner.Type)}.{field.Name} has type {CSharpNames.Type(field.Type)}");
            }

            // its bytes are what .NET reads only when it is laid out as C# lays out a struct by default
            return CustomLayout(type, assembly, 0) is { } custom
                ? Unsupported.MemoryMarshalOf(CSharpNames.Type(type), $"{custom} has a [StructLayout] with Pack or Size, " +
                    "which ChipSharp does not lay out yet")
                : null;
        }

        var why = ValueTypes.ProblemOf(type, assembly)?.Text
            ?? (ForAnyType(type, assembly) is { } unsupported ? unsupported.What : "ChipSharp does not compile it");
        return Unsupported.MemoryMarshalOf(CSharpNames.Type(type), why);
    }

    /// <summary>The struct, <paramref name="type"/> or one in its fields, with a custom layout (Pack or Size); null when none has.</summary>
    private static string? CustomLayout(IlType type, IlAssembly assembly, int depth)
    {
        if (depth > 8 || ValueTypes.Definition(type, assembly) is not { } definition || !ValueTypes.IsStruct(definition))
        {
            return null;
        }

        return definition.Assembly.HasCustomLayout(definition) ? CSharpNames.Type(definition.Type)
            : ValueTypes.InstanceFields(definition).Select(field => CustomLayout(field.Type, definition.Assembly, depth + 1))
                .FirstOrDefault(found => found is not null);
    }

    /// <summary>
    /// True when a span constructor from a pointer comes after the localloc at <paramref name="index"/> (a stackalloc
    /// whose initializer branches), not only a pointer (unsafe code).
    /// </summary>
    private static bool MakesSpanLater(IReadOnlyList<IlInstruction> instructions, int index)
    {
        for (var i = index + 1; i < instructions.Count && instructions[i].OpCode != IlOpCode.Localloc; i++)
        {
            if (Spans.IsPointerConstructor(instructions[i], out _))
            {
                return true;
            }
        }

        return false;
    }
}
