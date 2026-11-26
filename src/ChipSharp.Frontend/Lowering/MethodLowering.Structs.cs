using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Structs (see <see cref="ValueTypes"/>): a struct value on the evaluation stack is a TIR value of its struct type,
/// copied whole by load and store, so a copy never changes the original. Its fields are reached through its address:
/// <list type="bullet">
/// <item><c>ldflda</c> is <c>fieldaddr</c> on the struct's address; <c>ldfld</c> and <c>stfld</c> load and store
/// through it (after <c>volatile.</c>, volatile). <c>ldfld</c> also reads a field of a struct value: the value goes
/// into a slot first, <c>$temp_&lt;struct&gt;</c>, and is read from there.</item>
/// <item><c>initobj</c> stores <c>zero</c> (C#'s <c>default</c>); <c>ldobj</c>, <c>stobj</c> and <c>cpobj</c> load,
/// store and copy a value of any supported type through an address.</item>
/// <item><c>newobj</c> of a struct's constructor makes the struct in <c>$temp_&lt;struct&gt;</c>: zero it, call the
/// constructor with its address as <c>this</c>, load it. C# mostly calls the constructor on a local's address
/// instead, as it calls the struct's other methods: <c>this</c> is the first parameter, a ptr.</item>
/// </list>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>The slot of each struct type that values of it are put in to reach their fields or be made in.</summary>
    private readonly Dictionary<TirType, TirSlot> _temporarySlots = [];

    private void LowerStruct(IlInstruction instruction)
    {
        switch (instruction.OpCode)
        {
            case IlOpCode.Ldfld:
                LoadField();
                break;

            case IlOpCode.Ldflda:
                var (structType, index, _) = InstanceField();
                Push(_ir.FieldAddress(PopAddress(), structType, index));
                break;

            case IlOpCode.Stfld:
                StoreField();
                break;

            case IlOpCode.Initobj:
                var (type, _) = TypeOperand();
                _ir.Store(PopAddress(), _ir.Zero(type));
                break;

            case IlOpCode.Ldobj:
                var (loaded, ilType) = TypeOperand();
                Push(ToStack(_ir.Load(loaded, PopAddress()), ilType));
                break;

            case IlOpCode.Stobj:
                (type, ilType) = TypeOperand();
                var value = FromStack(PopValue(), type, ilType, "the value it refers to");
                _ir.Store(PopAddress(), value);
                break;

            case IlOpCode.Cpobj:
                (type, _) = TypeOperand();
                var source = PopAddress();
                var copy = _ir.Load(type, source);
                _ir.Store(PopAddress(), copy);
                break;

            case IlOpCode.Newobj:
                NewStruct();
                break;

            default:
                throw Unsupported(instruction);
        }
    }

    private void LoadField()
    {
        var (structType, index, field) = InstanceField();
        var top = PopValue();
        TirValue address;
        if (top.Type == structType)
        {
            // a struct value has no address: it is put in a slot to read the field from
            var slot = TemporarySlot(structType);
            _ir.Store(slot, Materialize(top));
            address = _ir.AddressOf(slot);
        }
        else if (top.Type == TirType.Ptr)
        {
            address = Materialize(top);
        }
        else
        {
            throw Error($"{_instruction.Name} reads a field of {structType}, but the evaluation stack has " +
                StackName(top.Type));
        }

        var value = _ir.Load(structType.Fields[index].Type, _ir.FieldAddress(address, structType, index),
            IsVolatileAccess());
        Push(ToStack(value, field.Type));
    }

    private void StoreField()
    {
        var (structType, index, field) = InstanceField();
        var fieldType = structType.Fields[index].Type;
        var value = FromStack(PopValue(), fieldType, field.Type, $"field {structType}.{field.Name}");
        _ir.Store(_ir.FieldAddress(PopAddress(), structType, index), value, IsVolatileAccess());
    }

    /// <summary>
    /// newobj of a struct's constructor: the arguments stay on the stack, with the address of a zeroed
    /// <c>$temp_&lt;struct&gt;</c> slid in under them as <c>this</c>, for <see cref="Call"/>; then the struct is loaded.
    /// </summary>
    private void NewStruct()
    {
        var reference = ((IlMethodOperand)_instruction.Operand!).Method;
        if (reference.Definition is not { Name: ".ctor" } constructor || !ValueTypes.IsStructInstanceMethod(constructor))
        {
            throw Error($"{_instruction.Name} creates an object of {CSharpNames.Type(reference.DeclaringType)}, " +
                "and objects are not supported yet (they arrive with M3, the heap); structs are");
        }

        var structType = StructOf(constructor.DeclaringType);
        var arguments = new StackValue[constructor.Parameters.Count];
        for (var i = arguments.Length - 1; i >= 0; i--)
        {
            arguments[i] = PopValue();
        }

        var slot = TemporarySlot(structType);
        _ir.Store(slot, _ir.Zero(structType));
        Push(_ir.AddressOf(slot));
        foreach (var argument in arguments)
        {
            Push(argument);
        }

        Call();
        Push(_ir.Load(slot));
    }

    /// <summary>
    /// The instance field that the ldfld, ldflda or stfld being translated names: a field of a struct of the program,
    /// as the struct type, the field's number in it, and the field.
    /// </summary>
    private (TirStructType Struct, int Index, IlField Field) InstanceField()
    {
        var reference = ((IlFieldOperand)_instruction.Operand!).Field;
        if (reference.Definition is not { } field)
        {
            throw Error($"{_instruction.Name} of {reference.DisplayName} is not supported: " +
                "ChipSharp compiles the fields of the program's own structs");
        }

        if (field.IsStatic)
        {
            throw Error($"{_instruction.Name} names {reference.DisplayName}, which is a static field");
        }

        if (!ValueTypes.IsStruct(field.DeclaringType))
        {
            throw Error($"{_instruction.Name} uses {reference.DisplayName}, a field of an object, and objects are not " +
                "supported yet (they arrive with M3, the heap); the fields of structs are");
        }

        return (StructOf(field.DeclaringType), ValueTypes.FieldOf(field).Index, field);
    }

    /// <summary>The TIR struct of a struct of the program, or an error that says why it cannot be compiled.</summary>
    private TirStructType StructOf(IlTypeDefinition type)
    {
        if (type.GenericParameters.Count > 0)
        {
            throw Error($"the generic struct {type} is not supported yet (generic types arrive with M3, the heap)");
        }

        return ValueTypes.ProblemOf(type) is { } problem
            ? throw Error($"{problem.Text}, which is not supported yet")
            : ValueTypes.StructType(type);
    }

    /// <summary>The type an initobj, ldobj, stobj or cpobj names: its TIR type and its IL type.</summary>
    private (TirType Type, IlType IlType) TypeOperand()
    {
        var type = ((IlTypeOperand)_instruction.Operand!).Type;
        if (ValueTypes.Definition(type, _method.Assembly) is { } definition && ValueTypes.IsStruct(definition))
        {
            return (StructOf(definition), type);
        }

        return ValueTypes.TryMapType(type, _method.Assembly, out var tirType) && tirType != TirType.Void
            ? (tirType, type)
            : throw Error($"{_instruction.Name} of {type} is not supported yet (ChipSharp compiles " +
                "bool, whole numbers, and the structs and enums of the program)");
    }

    /// <summary>Pops the address of a struct or another value: a reference (ptr).</summary>
    private TirValue PopAddress()
    {
        var address = PopValue();
        return address.Type == TirType.Ptr
            ? Materialize(address)
            : throw Error($"{_instruction.Name} needs an address, but the evaluation stack has {StackName(address.Type)}");
    }

    private TirSlot TemporarySlot(TirType type)
    {
        if (!_temporarySlots.TryGetValue(type, out var slot))
        {
            slot = AddSlot($"temp_{type.Name}", type);
            _temporarySlots.Add(type, slot);
        }

        return slot;
    }
}
