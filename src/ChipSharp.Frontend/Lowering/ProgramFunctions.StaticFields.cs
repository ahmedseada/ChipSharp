using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>A static field the program reads or writes, as the translation uses it.</summary>
/// <param name="Type">Its IL type without modifiers, which says how a value is widened onto the stack and narrowed back.</param>
/// <param name="Global">The global it lives in, named like the field, e.g. <c>@Program.counter</c>.</param>
internal sealed record LoweredField(IlType Type, TirGlobal Global);

/// <summary>
/// The program's static fields and its start. Each static field the code reads or writes is a global, added when
/// first used. It starts at zero, or at the constant its type initializer gives it when that initializer does nothing
/// else (<see cref="TryFoldInitializer"/>): then the initializer is not compiled at all. The other initializers run
/// in <see cref="Start"/>.
/// </summary>
internal sealed partial class ProgramFunctions
{
    /// <summary>The name of the function a program starts with when it has type initializers to run before Main.</summary>
    public const string StartName = "<Start>";

    private readonly Dictionary<IlField, LoweredField> _fields = [];
    private readonly Dictionary<IlTypeDefinition, IReadOnlyDictionary<IlField, TirConst>> _foldedInitializers = [];

    /// <summary>
    /// When <paramref name="method"/> is a type initializer that only sets static fields of its own type to constants
    /// (C#'s <c>static int limit = 10;</c>), makes those constants the initial values of the fields' globals and
    /// returns true: the initializer is then left out of the TIR. Ask for every method of the program before any
    /// body is translated.
    /// </summary>
    public bool TryFoldInitializer(IlMethod method)
    {
        if (StaticInitialization.TypeInitializer(method.DeclaringType) != method)
        {
            return false;
        }

        if (!_foldedInitializers.ContainsKey(method.DeclaringType))
        {
            if (FindConstants(method, StaticArrays) is not { } constants)
            {
                return false;
            }

            _foldedInitializers.Add(method.DeclaringType, constants);
        }

        return true;
    }

    /// <summary>The static field that the <c>ldsfld</c> or <c>stsfld</c> at <paramref name="instruction"/> in <paramref name="user"/> names.</summary>
    public LoweredField StaticField(IlMethod user, IlInstruction instruction)
    {
        var reference = ((IlFieldOperand)instruction.Operand!).Field;
        if (reference.Definition is not { } field)
        {
            throw LoweringErrors.At(user, instruction.Offset, $"{instruction.Name} of {reference.DisplayName} is not " +
                "supported: ChipSharp compiles the static fields of the program's own types");
        }

        if (field.DeclaringType.GenericParameters.Count > 0)
        {
            throw LoweringErrors.At(user, instruction.Offset, $"the fields of generic types such as {reference.DisplayName} " +
                "are not supported yet (they arrive with M3, the heap)");
        }

        if (!field.IsStatic)
        {
            throw LoweringErrors.At(user, instruction.Offset,
                $"{instruction.Name} names {reference.DisplayName}, which is an instance field");
        }

        if (!_fields.TryGetValue(field, out var lowered))
        {
            var name = $"{field.DeclaringType.FullName}.{field.Name}";
            if (!StaticFields.TryMapType(field, out var type))
            {
                throw LoweringErrors.At(user, instruction.Offset, $"the field {name} has type {StaticFields.Type(field)}, " +
                    "which is not supported yet (ChipSharp compiles bool, whole numbers, float, double, fixed text " +
                    "(string), the structs and enums of the program, and objects of its classes)");
            }

            // a global without an initial value starts at zero, like a field the initializer does not set
            var initialValue = _foldedInitializers.TryGetValue(field.DeclaringType, out var constants)
                && constants.GetValueOrDefault(field) is { IntegerValue: not 0 } constant
                ? constant
                : null;
            lowered = new LoweredField(StaticFields.Type(field), module.AddGlobal(GlobalName(name), type, initialValue));
            _fields.Add(field, lowered);
        }

        return lowered;
    }

    /// <summary>
    /// The function the program starts with. With no type initializer to run, that is the entry point's; otherwise a
    /// function named <see cref="StartName"/> that calls the initializers in <see cref="IlProgram.TypeInitializers"/>'s
    /// order (those not folded into initial values), then the entry point, and returns what it returns.
    /// </summary>
    public TirFunction Start(IlProgram program)
    {
        var main = Get(program.EntryPoint).Function;
        var initializers = program.TypeInitializers.Where(_methods.ContainsKey).Select(m => _methods[m].Function).ToList();
        if (initializers.Count == 0)
        {
            return main;
        }

        var start = module.AddFunction(StartName, main.ReturnType);
        var ir = new TirBuilder(start.AddBlock("entry"));
        foreach (var initializer in initializers)
        {
            ir.Call(initializer);
        }

        var result = ir.Call(main);
        ir.Return(result.HasResult ? result : null);
        return start;
    }

    /// <summary>
    /// The constant <paramref name="initializer"/> leaves in each field of its own type, when that is all it does:
    /// nop, a constant (ldc, then maybe a conversion to int64 or native int) and the stsfld that stores it, over and
    /// over, then ret. Null for any other code. What makes a static array (see <see cref="Frontend.StaticArrays"/>) is
    /// in its global already, and counts for nothing.
    /// </summary>
    private static Dictionary<IlField, TirConst>? FindConstants(IlMethod initializer, StaticArrays arrays)
    {
        if (initializer.Body is not { ExceptionRegions.Count: 0 } body)
        {
            return null;
        }

        var constants = new Dictionary<IlField, TirConst>();
        (TirType Type, long Value)? pending = null;
        for (var index = 0; index < body.Instructions.Count; index++)
        {
            var instruction = body.Instructions[index];
            if (arrays.IsFolded(initializer, index))
            {
                continue;
            }

            switch (instruction.OpCode)
            {
                case IlOpCode.Nop or IlOpCode.Volatile:
                    break;

                case >= IlOpCode.Ldc_I4_M1 and <= IlOpCode.Ldc_I4_8 when pending is null:
                    pending = (TirType.I32, (int)instruction.OpCode - (int)IlOpCode.Ldc_I4_0);
                    break;

                case IlOpCode.Ldc_I4_S or IlOpCode.Ldc_I4 when pending is null:
                    pending = (TirType.I32, ((IlInt32Operand)instruction.Operand!).Value);
                    break;

                case IlOpCode.Ldc_I8 when pending is null:
                    pending = (TirType.I64, ((IlInt64Operand)instruction.Operand!).Value);
                    break;

                case IlOpCode.Conv_I8 when pending is { Type.Kind: TirTypeKind.I32 } value:
                    pending = (TirType.I64, (int)value.Value);
                    break;

                case IlOpCode.Conv_U8 when pending is { Type.Kind: TirTypeKind.I32 } value:
                    pending = (TirType.I64, (uint)value.Value);
                    break;

                case IlOpCode.Conv_I when pending is { Type.Kind: TirTypeKind.I32 } value:
                    pending = (TirType.NativeInt, (int)value.Value);
                    break;

                case IlOpCode.Conv_U when pending is { Type.Kind: TirTypeKind.I32, Value: >= 0 } value:
                    pending = (TirType.NativeInt, value.Value);
                    break;

                case IlOpCode.Stsfld when pending is { } value
                    && instruction.Operand is IlFieldOperand { Field.Definition: { IsStatic: true } field }
                    && field.DeclaringType == initializer.DeclaringType
                    && Narrow(value.Type, value.Value, field) is { } constant:
                    constants[field] = constant;
                    pending = null;
                    break;

                case IlOpCode.Ret when pending is null:
                    return constants;

                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// A constant of IL stack type <paramref name="stackType"/> as <c>stsfld</c> stores it into <paramref name="field"/>
    /// (see <see cref="MethodLowering"/>): a bool is "not zero", a small integer keeps the low bits, an int32 for a
    /// native int is sign- or zero-extended as the field's type says. Null when the types do not go together, or for a
    /// native int that would not mean the same on every target.
    /// </summary>
    private static TirConst? Narrow(TirType stackType, long value, IlField field)
    {
        if (!StaticFields.TryMapType(field, out var type) || (stackType == TirType.I64) != (type == TirType.I64))
        {
            return null;
        }

        var unsigned = StaticFields.Type(field) is IlPrimitiveType { Kind: IlPrimitiveKind.NativeUInt };
        long? narrowed = type.Kind switch
        {
            TirTypeKind.Bool => value != 0 ? 1 : 0,
            TirTypeKind.I8 => (sbyte)value,
            TirTypeKind.I16 => (short)value,
            TirTypeKind.I32 => (int)value,
            TirTypeKind.I64 => value,
            TirTypeKind.NativeInt when stackType == TirType.I32 && unsigned => (uint)value,
            TirTypeKind.NativeInt => value,
            _ => null,
        };

        return narrowed is { } result && (type != TirType.NativeInt || result is >= int.MinValue and <= int.MaxValue)
            ? type == TirType.Bool ? new TirConst(result != 0) : new TirConst(type, result)
            : null;
    }

    /// <summary>A global name: <paramref name="name"/>, or name_2, name_3, ... when another field has it.</summary>
    private string GlobalName(string name)
    {
        var unique = name;
        for (var n = 2; module.Globals.Any(g => g.Name == unique); n++)
        {
            unique = $"{name}_{n}";
        }

        return unique;
    }
}
