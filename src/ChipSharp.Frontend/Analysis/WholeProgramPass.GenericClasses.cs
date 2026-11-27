using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Generics over classes and the groundwork of collections in the whole-program pass: what a try block is, when C#
/// made it for a foreach (see <see cref="Unsupported.DisposedEnumerators"/>).
/// </summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// What the try block at <paramref name="tryOffset"/> of <paramref name="method"/> is: one that catches, as the only
    /// try blocks left by now are in a method with a catch (a try/finally, a using and a foreach are plain branches, see
    /// <see cref="TryFinally"/>).
    /// </summary>
    private static Unsupported ForTry(IlMethod method, int tryOffset) => Unsupported.TryCatch;

    /// <summary>
    /// The verdicts on the instructions that leave the try block and end the finally of a foreach in a method that also
    /// catches: none (null), as they are part of the try block's problem (<see cref="ForTry"/>).
    /// </summary>
    private static IEnumerable<KeyValuePair<IlInstruction, Unsupported?>> ForDisposedEnumerators(IlMethod method)
    {
        var body = method.Body!;
        foreach (var region in body.ExceptionRegions.Where(region => IsDisposedEnumerator(method, region)))
        {
            foreach (var instruction in body.Instructions.Where(instruction =>
                instruction.Offset >= region.TryOffset && instruction.Offset < region.HandlerEnd
                && instruction.OpCode is IlOpCode.Leave or IlOpCode.Leave_S or IlOpCode.Endfinally))
            {
                yield return new(instruction, null);
            }
        }
    }

    /// <summary>
    /// The verdicts on the instructions of <paramref name="method"/> that give an array where an interface is wanted (a
    /// parameter, a local, a field or the return value of an interface type): arrays used as interfaces. What the
    /// value is comes from the instruction of the same block that put it there (<see cref="VirtualMethods.StackType"/>);
    /// one from another block the translation finds.
    /// </summary>
    private static IEnumerable<KeyValuePair<IlInstruction, Unsupported?>> ForArraysAsInterfaces(IlMethod method)
    {
        var body = method.Body!;
        var assembly = method.Assembly;
        bool IsArrayAt(int index, int depth) =>
            VirtualMethods.StackType(method, index, depth) is { } type && ValueTypes.WithoutModifiers(type) is IlArrayType;

        bool IsInterface(IlType? type) => type is not null && VirtualMethods.IsInterface(type, assembly);

        for (var i = 0; i < body.Instructions.Count; i++)
        {
            var instruction = body.Instructions[i];
            IEnumerable<(IlType? Type, int Depth)> wanted = instruction switch
            {
                { OpCode: IlOpCode.Call or IlOpCode.Callvirt or IlOpCode.Newobj, Operand: IlMethodOperand { Method: var callee } } =>
                    callee.ParameterTypes.Select((type, k) => (Type: (IlType?)type, Depth: callee.ParameterTypes.Count - 1 - k)),
                { OpCode: >= IlOpCode.Stloc_0 and <= IlOpCode.Stloc_3 or IlOpCode.Stloc_S or IlOpCode.Stloc, LocalIndex: { } local }
                    when local < body.Locals.Count => [(body.Locals[local].Type, 0)],
                { OpCode: IlOpCode.Stfld or IlOpCode.Stsfld, Operand: IlFieldOperand { Field: var field } } =>
                    [(field.FieldType, 0)],
                { OpCode: IlOpCode.Ret } when !method.ReturnType.Equals(IlType.Void) => [(method.ReturnType, 0)],
                _ => [],
            };
            if (wanted.Any(destination => IsInterface(destination.Type) && IsArrayAt(i, destination.Depth)))
            {
                yield return new(instruction, Unsupported.ArraysAsInterfaces);
            }
        }
    }

    /// <summary>True for a finally that disposes an enumerator a try block moves along: C#'s foreach over one.</summary>
    private static bool IsDisposedEnumerator(IlMethod method, IlExceptionRegion region)
    {
        var body = method.Body!;
        bool Calls(int start, int end, string name) => body.Instructions.Any(instruction =>
            instruction.Offset >= start && instruction.Offset < end
            && instruction is
            {
                OpCode: IlOpCode.Call or IlOpCode.Callvirt,
                Operand: IlMethodOperand { Method: { HasThis: true, ParameterTypes.Count: 0 } callee },
            }
            && callee.Name == name);

        return region.Kind == IlExceptionRegionKind.Finally && Calls(region.HandlerOffset, region.HandlerEnd, "Dispose")
            && Calls(region.TryOffset, region.TryEnd, "MoveNext");
    }
}
