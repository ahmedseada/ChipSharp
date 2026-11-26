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

    /// <summary>Every error in the module, in order: the entry point first, then function by function. Empty when it is well formed.</summary>
    public static IReadOnlyList<string> FindErrors(TirModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var errors = new List<string>();
        CheckEntryPoint(module, errors);
        foreach (var function in module.Functions)
        {
            CheckFunction(module, function, errors);
        }

        return errors;
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

            TirStore s when s.Value.Type != s.Slot.Type =>
                $"the value is {s.Value.Type}, but slot {s.Slot} holds {s.Slot.Type}.",

            TirCall c => CallProblem(c),

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

        /// <summary>A slot, jump target or callee that belongs to another function or module.</summary>
        private string? ReferenceProblem(TirInstruction instruction)
        {
            var slot = instruction switch
            {
                TirLoad l => l.Slot,
                TirStore s => s.Slot,
                _ => null,
            };
            if (slot is not null && slot.Function != function)
            {
                return $"uses slot {slot} of function '{slot.Function.Name}'.";
            }

            if (instruction is TirCall call && call.Callee.Module != module)
            {
                return $"calls '{call.Callee.Name}', which is not a function of module '{module.Name}'.";
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
                || (type == TirType.Bool && accepts.HasFlag(Accepts.Bool));
            if (ok)
            {
                return null;
            }

            var kinds = accepts switch
            {
                Accepts.Integers => "integers",
                Accepts.Integers | Accepts.Floats => "integers and floats",
                Accepts.Integers | Accepts.Bool => "integers and bool",
                _ => "integers, floats and bool",
            };
            return $"{op} works on {kinds}, not on {type}.";
        }

        private static Accepts BinaryAccepts(TirBinaryOp op) => op switch
        {
            TirBinaryOp.Add or TirBinaryOp.Sub or TirBinaryOp.Mul or TirBinaryOp.Div or TirBinaryOp.Rem =>
                Accepts.Integers | Accepts.Floats,
            TirBinaryOp.And or TirBinaryOp.Or or TirBinaryOp.Xor => Accepts.Integers | Accepts.Bool,
            _ => Accepts.Integers,
        };

        private static Accepts CompareAccepts(TirCompareOp op) => op switch
        {
            TirCompareOp.Eq or TirCompareOp.Ne => Accepts.Integers | Accepts.Floats | Accepts.Bool,
            TirCompareOp.Lt or TirCompareOp.Le or TirCompareOp.Gt or TirCompareOp.Ge =>
                Accepts.Integers | Accepts.Floats,
            _ => Accepts.Integers,
        };
    }

    [Flags]
    private enum Accepts
    {
        Integers = 1,
        Floats = 2,
        Bool = 4,
    }
}
