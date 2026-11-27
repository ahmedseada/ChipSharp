namespace ChipSharp.Ir;

/// <summary>
/// Checks that a module is well formed before a code generator reads it. Each error names the function,
/// block and instruction (as <see cref="TirPrinter"/> writes it) and says what is wrong, e.g.
/// <c>Function 'Program.Bad', block 'entry', '%2 = add i32 %0, %1': the operands have different types: i32 and i64.</c>
/// </summary>
public static class TirVerifier
{
    /// <summary>Throws a <see cref="TirException"/> listing every error (one per line) when the module is not well formed.</summary>
    public static void Verify(TirModule module)
    {
        var errors = FindErrors(module);
        if (errors.Count > 0)
        {
            throw new TirException(string.Join("\n", errors));
        }
    }

    /// <summary>
    /// Every error in the module, in order: the entry point and the struct types first, then function by function.
    /// Empty when it is well formed.
    /// </summary>
    public static IReadOnlyList<string> FindErrors(TirModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var errors = new List<string>();
        CheckEntryPoint(module, errors);
        CheckStructNames(module, errors);
        CheckExports(module, errors);
        CheckTypeDescriptors(module, errors);
        foreach (var function in module.Functions)
        {
            CheckFunction(module, function, errors);
        }

        return errors;
    }

    /// <summary>
    /// Two different struct types of one name print the same, and a value of one does not fit where the other is
    /// expected; the frontend most likely made the struct type of one C# struct twice.
    /// </summary>
    private static void CheckStructNames(TirModule module, List<string> errors)
    {
        var byName = module.FindStructTypes().GroupBy(s => s.Name, StringComparer.Ordinal);
        foreach (var clash in byName.Where(g => g.Count() > 1))
        {
            errors.Add($"Module '{module.Name}': {clash.Count()} different struct types are named '{clash.Key}'; " +
                "make one struct type per struct and use it everywhere.");
        }
    }

    /// <summary>The runtime calls an exported function by its symbol: it must have a body, and a symbol of its own.</summary>
    private static void CheckExports(TirModule module, List<string> errors)
    {
        foreach (var function in module.Functions.Where(f => f is { Kind: TirFunctionKind.External, ExportName: not null }))
        {
            errors.Add($"Function '{function.Name}': an external function cannot be exported (as {function.ExportName}); " +
                "the runtime implements it.");
        }

        var bySymbol = module.Functions.Where(f => f.ExportName is not null).GroupBy(f => f.ExportName, StringComparer.Ordinal);
        foreach (var clash in bySymbol.Where(g => g.Count() > 1))
        {
            errors.Add($"Module '{module.Name}': {string.Join(" and ", clash.Select(f => $"'{f.Name}'"))} are exported under " +
                $"one symbol, {clash.Key}; a symbol names one function.");
        }
    }

    /// <summary>
    /// What the program fills in after it made a descriptor: the method tables must hold functions of this module, and an
    /// interface map name interfaces of it.
    /// </summary>
    private static void CheckTypeDescriptors(TirModule module, List<string> errors)
    {
        foreach (var type in module.TypeDescriptors)
        {
            var where = $"Module '{module.Name}', type {type.Name}";
            var methods = type.VirtualMethods.Concat(type.Interfaces.SelectMany(entry => entry.Methods));
            foreach (var foreign in methods.Where(method => method is not null && method.Module != module).Distinct())
            {
                errors.Add($"{where}: its method table names '{foreign!.Name}', a function of module '{foreign.Module.Name}'.");
            }

            // an array type may implement interfaces (the collections' interfaces of arrays), but has no virtual methods
            if (type.Kind != TirTypeDescriptorKind.Class && (type.VirtualMethods.Count > 0 || (type.Interfaces.Count > 0 && !type.IsArray)))
            {
                errors.Add($"{where}: only a class has virtual methods and interfaces (and an array type interfaces), " +
                    $"and this is {(type.IsArray ? "an array type" : "an interface")}.");
            }

            foreach (var entry in type.Interfaces)
            {
                if (entry.Interface.Module != module || entry.Interface.Kind != TirTypeDescriptorKind.Interface)
                {
                    errors.Add($"{where}: it implements {entry.Interface.Name}, which is not an interface of this module.");
                }
            }

            if (type.ElementDescriptor is { } element && (element.Module != module || type.ElementType != TirType.Ref))
            {
                errors.Add($"{where}: its element type {element.Name} must be a type of this module, and only an " +
                    "array of refs has one.");
            }
        }
    }

    private static void CheckEntryPoint(TirModule module, List<string> errors)
    {
        var where = $"Module '{module.Name}'";
        if (module.EntryPoint is not { } entryPoint)
        {
            errors.Add($"{where}: no entry point is set.");
        }
        else if (entryPoint.Module != module)
        {
            errors.Add($"{where}: the entry point '{entryPoint.Name}' is not a function of this module.");
        }
        else if (entryPoint.Kind != TirFunctionKind.Defined)
        {
            errors.Add($"{where}: the entry point '{entryPoint.Name}' is external; it must be a defined function.");
        }
    }

    private static void CheckFunction(TirModule module, TirFunction function, List<string> errors)
    {
        var where = $"Function '{function.Name}'";
        if (function.Kind == TirFunctionKind.External && function.Blocks.Count > 0)
        {
            errors.Add($"{where}: an external function has no body (the runtime implements it), " +
                $"but it has {Count(function.Blocks.Count, "block")}.");
        }
        else if (function.Kind == TirFunctionKind.Defined && function.Blocks.Count == 0)
        {
            errors.Add($"{where}: a defined function needs at least one block, but it has none.");
        }

        // the runtime is written in C, which passes and returns structs by its own rules; an address works everywhere
        if (function.Kind == TirFunctionKind.External)
        {
            foreach (var parameter in function.Parameters.Where(p => p.Type.IsAggregate))
            {
                errors.Add($"{where}: parameter {parameter} is {parameter.Type}; an external function cannot take " +
                    "a struct or an array, so pass its address (ptr) instead.");
            }

            if (function.ReturnType.IsAggregate)
            {
                errors.Add($"{where}: it returns {function.ReturnType}; an external function cannot return a " +
                    "struct or an array, so take the address (ptr) of where to put it instead.");
            }
        }

        var names = new TirNumbering(function);
        foreach (var block in function.Blocks)
        {
            new BlockChecker(module, function, block, names, errors).Check();
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>Checks one block's instructions in order, remembering which temporaries are defined so far.</summary>
    private sealed class BlockChecker(
        TirModule module, TirFunction function, TirBlock block, TirNumbering names, List<string> errors)
    {
        private readonly HashSet<TirInstruction> _defined = [];

        public void Check()
        {
            var instructions = block.Instructions;
            for (var i = 0; i < instructions.Count; i++)
            {
                var instruction = instructions[i];
                var error = (string message) =>
                    errors.Add($"Function '{function.Name}', block '{block.Name}', " +
                        $"'{TirPrinter.Print(instruction, names)}': {message}");

                if (instruction is TirTerminator && i < instructions.Count - 1)
                {
                    error("a terminator (return, jump or branch) must be the last instruction of its block.");
                }

                // the type rules only make sense once every operand is a usable value
                var operandsUsable = true;
                foreach (var operand in instruction.Operands.Distinct())
                {
                    if (OperandProblem(operand) is { } problem)
                    {
                        error(problem);
                        operandsUsable = false;
                    }
                }

                if (operandsUsable && TypeProblem(instruction) is { } typeProblem)
                {
                    error(typeProblem);
                }

                if (ReferenceProblem(instruction) is { } referenceProblem)
                {
                    error(referenceProblem);
                }

                _defined.Add(instruction);
            }

            if (block.Terminator is null)
            {
                errors.Add($"Function '{function.Name}', block '{block.Name}': " +
                    "the block must end with a terminator (return, jump or branch).");
            }
        }

        /// <summary>A parameter of another function, or a temporary used where the rules do not allow it.</summary>
        private string? OperandProblem(TirValue operand)
        {
            switch (operand)
            {
                case TirParameter parameter when parameter.Function != function:
                    return $"uses parameter {parameter} of function '{parameter.Function.Name}'.";

                case TirInstruction { Block: null } instruction:
                    return $"uses '{TirPrinter.Print(instruction)}', which was never added to a block.";

                case TirInstruction { Block: { } owner } instruction when owner.Function != function:
                    return $"uses '{TirPrinter.Print(instruction)}' from function '{owner.Function.Name}'.";

                case TirInstruction { Block: { } owner } instruction when owner != block:
                    return $"uses {names.Of(instruction)} from block '{owner.Name}'; a temporary can only " +
                        "be used in the block that defines it (pass values between blocks through a slot).";

                case TirInstruction instruction when !_defined.Contains(instruction):
                    return $"uses {names.Of(instruction)} before it is defined.";

                case TirInstruction { HasResult: false } instruction:
                    return $"uses '{TirPrinter.Print(instruction, names)}', which produces no value.";

                default:
                    return null;
            }
        }

        private string? TypeProblem(TirInstruction instruction) => instruction switch
        {
            TirBinary b when b.Left.Type != b.Right.Type =>
                $"the operands have different types: {b.Left.Type} and {b.Right.Type}.",
            TirBinary b => OperandKindProblem(TirPrinter.Name(b.Op), b.Type, BinaryAccepts(b.Op)),

            TirCompare c when c.Left.Type != c.Right.Type =>
                $"the operands have different types: {c.Left.Type} and {c.Right.Type}.",
            TirCompare c => OperandKindProblem(TirPrinter.Name(c.Op), c.Left.Type, CompareAccepts(c.Op)),

            TirConvert { Type.Kind: TirTypeKind.Void } => "convert cannot produce void.",
            TirConvert { Type.Kind: TirTypeKind.Bool } => "convert cannot produce bool; compare with zero (ne) instead.",
            TirConvert c when c.Value.Type == TirType.Ref || c.Type == TirType.Ref =>
                "convert does not take or produce ref: a reference to an object is never a number.",
            TirConvert c when c.Value.Type == TirType.Ptr || c.Type == TirType.Ptr =>
                "convert does not take or produce ptr; use ptrtoint or inttoptr.",
            TirConvert c when c.Value.Type.IsAggregate || c.Type.IsAggregate =>
                $"convert works on integers, floats and bool, not on {(c.Value.Type.IsAggregate ? c.Value.Type : c.Type)}.",

            TirStore s when s.Value.Type != s.Slot.Type =>
                $"the value is {s.Value.Type}, but slot {s.Slot} holds {s.Slot.Type}.",

            TirZero { Type.Kind: TirTypeKind.Void } => "zero cannot produce void.",
            TirSizeOf { OfType.Kind: TirTypeKind.Void } => "void takes no memory, so it has no size.",
            TirPointerLoad { Type.Kind: TirTypeKind.Void } => "load cannot produce void.",
            TirPointerLoad l => AddressProblem(l.Address) ?? PointeeProblem("loads", l.Type, l.Address),
            TirPointerStore s => AddressProblem(s.Address) ?? ConstantProblem(s.Address)
                ?? PointeeProblem("stores", s.Value.Type, s.Address),
            TirFieldAddress { Address.Type.Kind: TirTypeKind.Ref } f => ObjectFieldProblem(f) ?? FieldProblem(f),
            TirFieldAddress f => AddressProblem(f.Address) ?? FieldProblem(f),
            TirElementAddress e => AddressProblem(e.Address) ?? ElementProblem(e),
            TirPtrToInt p when p.Address.Type != TirType.Ptr => $"ptrtoint takes a ptr, not {p.Address.Type}.",
            TirPtrToInt p when !p.Type.IsInteger =>
                $"ptrtoint produces an integer (i8, i16, i32, i64 or nint), not {p.Type}.",
            TirIntToPtr p when !p.Value.Type.IsInteger =>
                $"inttoptr takes an integer (i8, i16, i32, i64 or nint), not {p.Value.Type}.",

            TirNewObject n => DescriptorProblem("newobject", n.TypeDescriptor, TirTypeDescriptorKind.Class),
            TirNewArray n => DescriptorProblem("newarray", n.TypeDescriptor, TirTypeDescriptorKind.Array)
                ?? (n.Length.Type == TirType.I32 ? null : $"the length is {n.Length.Type}; it must be i32."),
            TirNullCheck n => ReferenceOperandProblem("nullcheck", n.Reference),
            TirArrayLength l => ReferenceOperandProblem("arraylength", l.Array),
            TirArrayData d => ReferenceOperandProblem("arraydata", d.Array) ?? ArrayDataProblem(d),
            TirArrayObject o when o.Data.Type != TirType.Ptr =>
                $"arrayobject takes the address of an array's elements (ptr), not {o.Data.Type}.",
            TirTypeOf t => ReferenceOperandProblem("typeof", t.Reference),
            TirTypeAddress a => DescriptorProblem("typeaddr", a.TypeDescriptor, null),

            TirCall c => CallProblem(c),
            TirCallIndirect { Callee.Type: var callee } when callee != TirType.Ptr =>
                $"call indirect takes the address of a function (ptr), not {callee}.",

            TirReturn { Value: null } when function.ReturnType != TirType.Void =>
                $"returns nothing, but the function returns {function.ReturnType}.",
            TirReturn { Value: not null } when function.ReturnType == TirType.Void =>
                "returns a value, but the function returns void.",
            TirReturn { Value: { } value } when value.Type != function.ReturnType =>
                $"returns {value.Type}, but the function returns {function.ReturnType}.",

            TirBranch b when b.Condition.Type != TirType.Bool =>
                $"the condition is {b.Condition.Type}; it must be bool.",

            _ => null,
        };

        private static string? CallProblem(TirCall call)
        {
            var callee = call.Callee;
            var parameters = callee.Parameters;
            if (call.Arguments.Count != parameters.Count)
            {
                return $"{callee.Name} takes {Count(parameters.Count, "argument")}, but {call.Arguments.Count} " +
                    $"{(call.Arguments.Count == 1 ? "is" : "are")} given.";
            }

            for (var i = 0; i < parameters.Count; i++)
            {
                if (call.Arguments[i].Type != parameters[i].Type)
                {
                    return $"argument {i + 1} is {call.Arguments[i].Type}, but parameter {parameters[i]} " +
                        $"of {callee.Name} is {parameters[i].Type}.";
                }
            }

            return null;
        }

        /// <summary>An address operand that is not a ptr.</summary>
        private string? AddressProblem(TirValue address) => address.Type == TirType.Ptr ? null
            : address.Type == TirType.Ref
                ? $"the address {names.Of(address)} is ref, an object, not a ptr: fieldaddr reaches its fields, arraydata " +
                  "the elements of an array."
                : $"the address {names.Of(address)} is {address.Type}; it must be ptr.";

        /// <summary>An operand that must be an object (a ref) and is not.</summary>
        private static string? ReferenceOperandProblem(string instruction, TirValue reference) =>
            reference.Type == TirType.Ref ? null : $"{instruction} takes an object (ref), not {reference.Type}.";

        /// <summary>A descriptor of another module, or of another kind than <paramref name="kind"/> (null: any kind).</summary>
        private string? DescriptorProblem(string instruction, TirTypeDescriptor type, TirTypeDescriptorKind? kind)
        {
            if (type.Module != module)
            {
                return $"{instruction} names type {type.Name} of module '{type.Module.Name}'.";
            }

            return kind is { } wanted && type.Kind != wanted
                ? $"{instruction} needs {KindText(wanted)}, and {type.Name} is {KindText(type.Kind)}."
                : null;
        }

        private static string KindText(TirTypeDescriptorKind kind) => kind switch
        {
            TirTypeDescriptorKind.Class => "a class",
            TirTypeDescriptorKind.Array => "an array type",
            _ => "an interface",
        };

        /// <summary>fieldaddr on an object: the struct must be the object type of a class of the module.</summary>
        private string? ObjectFieldProblem(TirFieldAddress f) =>
            module.TypeDescriptors.Any(type => type.ObjectType == f.StructType)
                ? null
                : $"{names.Of(f.Address)} is an object (ref), and {f.StructType} is the object type of no class of this " +
                  "module.";

        /// <summary>arraydata of an element type that no array type of the module has.</summary>
        private string? ArrayDataProblem(TirArrayData d) =>
            module.TypeDescriptors.Any(type => type.IsArray && type.ElementType == d.ElementType)
                ? null
                : $"no array type of this module has elements of {d.ElementType}.";

        /// <summary>
        /// A store into a constant global, where that can be seen: the address is the global's (addr), or an element's
        /// or a field's in it (elementaddr, fieldaddr), all in this block.
        /// </summary>
        private static string? ConstantProblem(TirValue address)
        {
            while (address is TirElementAddress or TirFieldAddress)
            {
                address = address is TirElementAddress element ? element.Address : ((TirFieldAddress)address).Address;
            }

            return address is TirGlobalAddress { Global: { IsConstant: true } global }
                ? $"stores into {global}, which is constant: nothing may write it."
                : null;
        }

        /// <summary>A load or store of another type than the one the address is known to point to.</summary>
        private string? PointeeProblem(string verb, TirType type, TirValue address) =>
            PointeeOf(address) is { } pointee && pointee != type
                ? $"{verb} {type}, but {names.Of(address)} points to {pointee}."
                : null;

        private string? FieldProblem(TirFieldAddress f)
        {
            var structType = f.StructType;
            if (f.Field is null)
            {
                return $"{structType} has {Count(structType.Fields.Count, "field")}; there is no field number " +
                    $"{f.FieldIndex}.";
            }

            return PointeeOf(f.Address) is { } pointee && pointee != structType
                ? $"{names.Of(f.Address)} points to {pointee}, not to the struct {structType}."
                : null;
        }

        private string? ElementProblem(TirElementAddress e)
        {
            var element = e.ElementType;
            if (element == TirType.Void)
            {
                return "elementaddr needs an element type, not void.";
            }

            if (e.Index.Type.Kind is not (TirTypeKind.I32 or TirTypeKind.I64 or TirTypeKind.NativeInt))
            {
                return $"the index is {e.Index.Type}; it must be i32, i64 or nint.";
            }

            return PointeeOf(e.Address) is { } pointee && pointee != element &&
                !(pointee is TirArrayType array && array.Element == element)
                ? $"{names.Of(e.Address)} points to {pointee}; an element address of {element} needs the address " +
                  $"of {element} or of an array of {element}."
                : null;
        }

        /// <summary>
        /// What an address points to when that can be seen: it was made by addr, fieldaddr or elementaddr (which are
        /// in this block, since a temporary is). Null when it cannot be seen, e.g. for a parameter or a loaded ptr.
        /// </summary>
        private static TirType? PointeeOf(TirValue address) => address switch
        {
            TirSlotAddress a => a.Slot.Type,
            TirGlobalAddress a => a.Global.Type,
            TirFieldAddress f => f.Field?.Type,
            TirElementAddress e => e.ElementType,
            TirArrayData d => d.ElementType,
            _ => null,
        };

        /// <summary>A slot, global, jump target or callee that belongs to another function or module.</summary>
        private string? ReferenceProblem(TirInstruction instruction)
        {
            var slot = instruction switch
            {
                TirLoad l => l.Slot,
                TirStore s => s.Slot,
                TirSlotAddress a => a.Slot,
                _ => null,
            };
            if (slot is not null && slot.Function != function)
            {
                return $"uses slot {slot} of function '{slot.Function.Name}'.";
            }

            if (instruction is TirGlobalAddress { Global: var global } && global.Module != module)
            {
                return $"uses global {global} of module '{global.Module.Name}'.";
            }

            if (instruction is TirCall call && call.Callee.Module != module)
            {
                return $"calls '{call.Callee.Name}', which is not a function of module '{module.Name}'.";
            }

            if (instruction is TirFunctionAddress { Function: var addressed }
                && (addressed.Module != module || addressed.Kind != TirFunctionKind.Defined))
            {
                return addressed.Module != module
                    ? $"takes the address of '{addressed.Name}', which is not a function of module '{module.Name}'."
                    : $"takes the address of '{addressed.Name}', an external function; only a defined function's " +
                      "address can be taken (call the external from one).";
            }

            if (instruction is TirTerminator terminator &&
                terminator.Targets.FirstOrDefault(t => t.Function != function) is { } foreign)
            {
                return $"goes to block '{foreign.Name}' of function '{foreign.Function.Name}'; " +
                    "a jump or branch must stay in its own function.";
            }

            return null;
        }

        private static string? OperandKindProblem(string op, TirType type, Accepts accepts)
        {
            var ok = (type.IsInteger && accepts.HasFlag(Accepts.Integers))
                || (type.IsFloat && accepts.HasFlag(Accepts.Floats))
                || (type == TirType.Bool && accepts.HasFlag(Accepts.Bool))
                || (type == TirType.Ptr && accepts.HasFlag(Accepts.Pointers))
                || (type == TirType.Ref && accepts.HasFlag(Accepts.References));
            if (ok)
            {
                return null;
            }

            var kinds = new[]
            {
                (Accepts.Integers, "integers"), (Accepts.Floats, "floats"), (Accepts.Bool, "bool"),
                (Accepts.Pointers, "ptr"), (Accepts.References, "ref"),
            }.Where(k => accepts.HasFlag(k.Item1)).Select(k => k.Item2).ToArray();
            var list = kinds.Length == 1 ? kinds[0] : $"{string.Join(", ", kinds[..^1])} and {kinds[^1]}";
            return $"{op} works on {list}, not on {type}.";
        }

        private static Accepts BinaryAccepts(TirBinaryOp op) => op switch
        {
            TirBinaryOp.Add or TirBinaryOp.Sub or TirBinaryOp.Mul or TirBinaryOp.Div or TirBinaryOp.Rem =>
                Accepts.Integers | Accepts.Floats,
            TirBinaryOp.And or TirBinaryOp.Or or TirBinaryOp.Xor => Accepts.Integers | Accepts.Bool,
            _ => Accepts.Integers,
        };

        /// <summary>
        /// The unsigned comparisons take floats too: there they are unordered (true when either side is NaN), as
        /// IL's clt.un, cgt.un, bge.un and the like need. Addresses (ptr and ref) compare as unsigned numbers.
        /// </summary>
        private static Accepts CompareAccepts(TirCompareOp op) => op switch
        {
            TirCompareOp.Eq or TirCompareOp.Ne =>
                Accepts.Integers | Accepts.Floats | Accepts.Bool | Accepts.Pointers | Accepts.References,
            TirCompareOp.Lt or TirCompareOp.Le or TirCompareOp.Gt or TirCompareOp.Ge =>
                Accepts.Integers | Accepts.Floats,
            _ => Accepts.Integers | Accepts.Floats | Accepts.Pointers | Accepts.References,
        };
    }

    [Flags]
    private enum Accepts
    {
        Integers = 1,
        Floats = 2,
        Bool = 4,
        Pointers = 8,
        References = 16,
    }
}
