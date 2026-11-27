using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Translates the IL of one method into the blocks of its TIR function, simulating IL's evaluation stack with TIR
/// values (see <see cref="IlToTir"/> for the approach). Used once per method.
/// </summary>
internal sealed partial class MethodLowering
{
    private readonly ProgramFunctions _program;
    private readonly IlMethod _method;
    private readonly IlMethodBody _body;
    private readonly TirFunction _function;
    private readonly IReadOnlyList<LoweredArgument> _arguments;
    private readonly IReadOnlyList<IlBlock> _blocks;

    /// <summary>The block that starts at each instruction index; null inside a block.</summary>
    private readonly IlBlock?[] _blockAt;

    /// <summary>
    /// The slot of each argument the method assigns to (<c>starg</c>) or takes the address of (<c>ldarga</c>), by
    /// argument number; null for the others.
    /// </summary>
    private readonly TirSlot?[] _argumentSlots;

    private readonly TirSlot[] _localSlots;
    private readonly Dictionary<(int Depth, TirType Type), TirSlot> _stackSlots = [];
    private readonly Dictionary<TirType, TirSlot> _switchSlots = [];

    /// <summary>The evaluation stack, bottom first. Its values belong to the current TIR block.</summary>
    private readonly List<StackValue> _stack = [];

    private readonly TirBuilder _ir;

    /// <summary>The block and the instruction being translated.</summary>
    private IlBlock _block = null!;
    private IlInstruction _instruction = null!;

    public MethodLowering(ProgramFunctions program, LoweredMethod method)
    {
        _program = program;
        _method = method.Method;
        _body = method.Body;
        _function = method.Function;
        _arguments = method.Arguments;

        CheckSupported();
        _blocks = IlBlock.Split(_body);
        _blockAt = new IlBlock?[_body.Instructions.Count];
        foreach (var block in _blocks)
        {
            _blockAt[block.First] = block;
        }

        _argumentSlots = new TirSlot?[_arguments.Count];
        foreach (var instruction in _body.Instructions)
        {
            if (instruction.OpCode is IlOpCode.Starg or IlOpCode.Starg_S or IlOpCode.Ldarga or IlOpCode.Ldarga_S
                && instruction.ArgumentIndex is { } index
                && index < _arguments.Count && _arguments[index].Parameter is { } parameter)
            {
                _argumentSlots[index] ??= AddSlot(parameter.Name, parameter.Type);
            }
        }

        _localSlots = [.. _body.Locals.Select(local => AddSlot($"V_{local.Index}", LocalSlotType(local)))];

        // the assigned arguments are copied into their slots once, so not in a block that a loop jumps back to
        var copies = _argumentSlots.Any(s => s is not null);
        var loopsToStart = _body.Instructions.Any(i => IlBlock.Targets(i).Contains(0));
        var entry = copies && loopsToStart ? _function.AddBlock("entry") : null;
        foreach (var block in _blocks)
        {
            block.Tir = _function.AddBlock(IlPrinter.Label(block.Offset));
            if (block.Last.Operand is IlSwitchOperand { Targets.Count: > 1 } @switch)
            {
                for (var i = 1; i < @switch.Targets.Count; i++)
                {
                    block.SwitchCases.Add(_function.AddBlock($"{IlPrinter.Label(block.Last.Offset)}_case{i}"));
                }
            }
        }

        _ir = new TirBuilder(entry ?? _blocks[0].Tir);
        for (var i = 0; i < _arguments.Count; i++)
        {
            if (_argumentSlots[i] is { } slot)
            {
                _ir.Store(slot, _arguments[i].Parameter!);
            }
        }

        if (entry is not null)
        {
            _ir.Jump(_blocks[0].Tir);
        }
    }

    /// <summary>Translates every block, in order of offset.</summary>
    public void Lower()
    {
        var instructions = _body.Instructions;
        foreach (var block in _blocks)
        {
            _block = block;
            _ir.Block = block.Tir;
            StartObjectsOfBlock();

            if (block.EntryStack is null)
            {
                // nothing seen so far goes here: only a later instruction jumps back (with an empty stack), or nothing
                block.EntryStack = [];
                block.EntryStackAssumed = true;
            }

            _stack.Clear();
            for (var depth = 0; depth < block.EntryStack.Count; depth++)
            {
                Push(EntryValue(block, depth));
            }

            for (var i = block.First; i < block.End; i++)
            {
                _instruction = instructions[i];
                if (!IsFoldedStaticArray(i))
                {
                    Lower(_instruction);
                }
            }

            if (!IlBlock.EndsBlock(block.Last))
            {
                var next = Next();
                Leave(next);
                _ir.Jump(next.Tir);
            }
        }
    }

    /// <summary>
    /// Rejects a body with exception handling, printed text that is not supported, or an instruction outside
    /// <see cref="SupportedSubset"/> (but those of printed text and of spans).
    /// </summary>
    private void CheckSupported()
    {
        if (_body.Instructions.Count == 0)
        {
            throw LoweringErrors.In(_method, "its body has no instructions");
        }

        if (_body.ExceptionRegions.Count > 0)
        {
            throw LoweringErrors.At(_method, _body.ExceptionRegions[0].TryOffset,
                "try, catch and finally are not supported yet (they arrive later)");
        }

        CheckPrintedText();
        if (_body.Instructions.FirstOrDefault(i => !SupportedSubset.OpCodes.Contains(i.OpCode) && !IsPrintedText(i)
            && !IsSpanInstruction(i) && !IsStringInstruction(i) && !IsBoxingInstruction(i)) is { } unsupported)
        {
            throw Unsupported(unsupported);
        }

        CheckArrays();
        CheckSpans();
    }

    private void Lower(IlInstruction instruction)
    {
        if (LowerPrintedText(instruction) || LowerFloat(instruction))
        {
            return;
        }

        switch (instruction.OpCode)
        {
            case var _ when IsBoxingInstruction(instruction):
                LowerBoxingInstruction(instruction); // see MethodLowering.Boxing.cs
                break;

            case var _ when IsDelegateInstruction(instruction):
                LowerDelegateInstruction(instruction); // see MethodLowering.Delegates.cs
                break;

            case var _ when IsStringInstruction(instruction):
                LowerStringInstruction(instruction);
                break;

            case var _ when IsSpanInstruction(instruction):
                LowerSpanInstruction(instruction);
                break;

            case var _ when IsArrayInstruction(instruction):
                LowerArrayInstruction(instruction);
                break;

            case var _ when IsArrayMethodInstruction(instruction):
                LowerArrayMethod(instruction); // see MethodLowering.ArrayMethods.cs
                break;

            case var _ when IsVirtualInstruction(instruction):
                LowerVirtualInstruction(instruction);
                break;

            case var _ when IsObjectInstruction(instruction):
                LowerObjectInstruction(instruction);
                break;

            case IlOpCode.Nop:
                break;

            case IlOpCode.Ldarg_0 or IlOpCode.Ldarg_1 or IlOpCode.Ldarg_2 or IlOpCode.Ldarg_3 or IlOpCode.Ldarg_S
                or IlOpCode.Ldarg:
                LoadArgument(instruction.ArgumentIndex!.Value);
                break;

            case IlOpCode.Starg_S or IlOpCode.Starg:
                StoreArgument(instruction.ArgumentIndex!.Value);
                break;

            case IlOpCode.Ldloc_0 or IlOpCode.Ldloc_1 or IlOpCode.Ldloc_2 or IlOpCode.Ldloc_3 or IlOpCode.Ldloc_S
                or IlOpCode.Ldloc:
                var local = Local(instruction.LocalIndex!.Value);
                Push(ToStack(_ir.Load(_localSlots[local.Index]), local.Type));
                break;

            case IlOpCode.Stloc_0 or IlOpCode.Stloc_1 or IlOpCode.Stloc_2 or IlOpCode.Stloc_3 or IlOpCode.Stloc_S
                or IlOpCode.Stloc:
                local = Local(instruction.LocalIndex!.Value);
                var slot = _localSlots[local.Index];
                _ir.Store(slot, FromStack(PopValue(), slot.Type, local.Type, $"local V_{local.Index}"));
                break;

            case IlOpCode.Ldsfld:
                LoadStaticField();
                break;

            case IlOpCode.Stsfld:
                StoreStaticField();
                break;

            case IlOpCode.Volatile:
                CheckVolatilePrefix();
                break;

            case IlOpCode.Ldloca_S or IlOpCode.Ldloca or IlOpCode.Ldarga_S or IlOpCode.Ldarga or IlOpCode.Ldsflda
                or (>= IlOpCode.Ldind_I1 and <= IlOpCode.Ldind_R8) or (>= IlOpCode.Stind_I1 and <= IlOpCode.Stind_R8)
                or IlOpCode.Stind_I:
                LowerReference(instruction);
                break;

            case IlOpCode.Ldfld or IlOpCode.Ldflda or IlOpCode.Stfld or IlOpCode.Initobj or IlOpCode.Ldobj
                or IlOpCode.Stobj or IlOpCode.Cpobj or IlOpCode.Newobj:
                LowerStruct(instruction);
                break;

            case IlOpCode.Ldstr or IlOpCode.Ldnull:
                LowerText(instruction);
                break;

            case >= IlOpCode.Ldc_I4_M1 and <= IlOpCode.Ldc_I4_8:
                Push(StackValue.OfConstant(TirType.I32, (int)instruction.OpCode - (int)IlOpCode.Ldc_I4_0));
                break;

            case IlOpCode.Ldc_I4_S or IlOpCode.Ldc_I4:
                Push(StackValue.OfConstant(TirType.I32, ((IlInt32Operand)instruction.Operand!).Value));
                break;

            case IlOpCode.Ldc_I8:
                Push(StackValue.OfConstant(TirType.I64, ((IlInt64Operand)instruction.Operand!).Value));
                break;

            case IlOpCode.Dup:
                var top = PopValue();
                Push(top);
                Push(top);
                break;

            case IlOpCode.Pop:
                PopValue();
                break;

            case IlOpCode.Call:
                Call();
                break;

            case IlOpCode.Ret:
                Return();
                break;

            case IlOpCode.Br_S or IlOpCode.Br:
                var target = Target();
                Leave(target);
                _ir.Jump(target.Tir);
                break;

            case IlOpCode.Brtrue_S or IlOpCode.Brtrue:
                BranchOnValue(jumpWhenTrue: true);
                break;

            case IlOpCode.Brfalse_S or IlOpCode.Brfalse:
                BranchOnValue(jumpWhenTrue: false);
                break;

            case IlOpCode.Switch:
                Switch();
                break;

            case IlOpCode.Neg:
                var negated = PopNumber();
                Push(_ir.Binary(TirBinaryOp.Sub, Zero(negated.Type), negated));
                break;

            case IlOpCode.Not:
                var inverted = PopNumber();
                Push(_ir.Binary(TirBinaryOp.Xor, inverted, _ir.Const(inverted.Type, -1)));
                break;

            case IlOpCode.Shl or IlOpCode.Shr or IlOpCode.Shr_Un:
                Shift(instruction.OpCode switch
                {
                    IlOpCode.Shl => TirBinaryOp.Shl,
                    IlOpCode.Shr => TirBinaryOp.Shr,
                    _ => TirBinaryOp.ShrUnsigned,
                });
                break;

            case IlOpCode.Conv_I1 or IlOpCode.Conv_U1:
                ConvertToSmall(TirType.I8, unsigned: instruction.OpCode == IlOpCode.Conv_U1);
                break;

            case IlOpCode.Conv_I2 or IlOpCode.Conv_U2:
                ConvertToSmall(TirType.I16, unsigned: instruction.OpCode == IlOpCode.Conv_U2);
                break;

            case IlOpCode.Conv_I4 or IlOpCode.Conv_U4:
                Push(Convert(PopValue(), TirType.I32, unsigned: false));
                break;

            case IlOpCode.Conv_I8 or IlOpCode.Conv_U8:
                Push(Convert(PopValue(), TirType.I64, unsigned: instruction.OpCode == IlOpCode.Conv_U8));
                break;

            case IlOpCode.Conv_I or IlOpCode.Conv_U:
                Push(Convert(PopValue(), TirType.NativeInt, unsigned: instruction.OpCode == IlOpCode.Conv_U));
                break;

            default:
                if (ArithmeticOp(instruction.OpCode) is { } arithmetic)
                {
                    var (left, right) = PopOperands();
                    Push(_ir.Binary(arithmetic, left, right));
                }
                else if (CompareOp(instruction.OpCode) is { } comparison)
                {
                    var (left, right) = PopComparands(comparison);
                    Push(StackValue.OfBool(_ir.Compare(comparison, left, right)));
                }
                else if (BranchOp(instruction.OpCode) is { } branch)
                {
                    var (left, right) = PopComparands(branch);
                    Branch(_ir.Compare(branch, left, right));
                }
                else
                {
                    throw Unsupported(instruction);
                }

                break;
        }
    }

    private static TirBinaryOp? ArithmeticOp(IlOpCode opCode) => opCode switch
    {
        IlOpCode.Add => TirBinaryOp.Add,
        IlOpCode.Sub => TirBinaryOp.Sub,
        IlOpCode.Mul => TirBinaryOp.Mul,
        IlOpCode.Div => TirBinaryOp.Div,
        IlOpCode.Div_Un => TirBinaryOp.DivUnsigned,
        IlOpCode.Rem => TirBinaryOp.Rem,
        IlOpCode.Rem_Un => TirBinaryOp.RemUnsigned,
        IlOpCode.And => TirBinaryOp.And,
        IlOpCode.Or => TirBinaryOp.Or,
        IlOpCode.Xor => TirBinaryOp.Xor,
        _ => null,
    };

    /// <summary>ceq, cgt, clt and their unsigned forms: they push 1 or 0.</summary>
    private static TirCompareOp? CompareOp(IlOpCode opCode) => opCode switch
    {
        IlOpCode.Ceq => TirCompareOp.Eq,
        IlOpCode.Cgt => TirCompareOp.Gt,
        IlOpCode.Cgt_Un => TirCompareOp.GtUnsigned,
        IlOpCode.Clt => TirCompareOp.Lt,
        IlOpCode.Clt_Un => TirCompareOp.LtUnsigned,
        _ => null,
    };

    /// <summary>The branches that compare two values. bne.un is plain "not equal": on integers .un changes nothing.</summary>
    private static TirCompareOp? BranchOp(IlOpCode opCode) => opCode switch
    {
        IlOpCode.Beq or IlOpCode.Beq_S => TirCompareOp.Eq,
        IlOpCode.Bne_Un or IlOpCode.Bne_Un_S => TirCompareOp.Ne,
        IlOpCode.Bge or IlOpCode.Bge_S => TirCompareOp.Ge,
        IlOpCode.Bgt or IlOpCode.Bgt_S => TirCompareOp.Gt,
        IlOpCode.Ble or IlOpCode.Ble_S => TirCompareOp.Le,
        IlOpCode.Blt or IlOpCode.Blt_S => TirCompareOp.Lt,
        IlOpCode.Bge_Un or IlOpCode.Bge_Un_S => TirCompareOp.GeUnsigned,
        IlOpCode.Bgt_Un or IlOpCode.Bgt_Un_S => TirCompareOp.GtUnsigned,
        IlOpCode.Ble_Un or IlOpCode.Ble_Un_S => TirCompareOp.LeUnsigned,
        IlOpCode.Blt_Un or IlOpCode.Blt_Un_S => TirCompareOp.LtUnsigned,
        _ => null,
    };

    private void LoadArgument(int index)
    {
        var argument = Argument(index);
        TirValue value = _argumentSlots[index] is { } slot ? _ir.Load(slot) : argument.Parameter!;
        Push(ToStack(value, argument.Type));
    }

    private void StoreArgument(int index)
    {
        var argument = Argument(index);
        var slot = _argumentSlots[index]!;
        _ir.Store(slot, FromStack(PopValue(), slot.Type, argument.Type, $"parameter '{argument.Name}'"));
    }

    private LoweredArgument Argument(int index)
    {
        if (index >= _arguments.Count)
        {
            throw Error($"{_instruction.Name} uses argument {index}, but the method has {_arguments.Count}");
        }

        return _arguments[index].Parameter is not null
            ? _arguments[index]
            : throw Error($"{_instruction.Name} uses the string[] {_arguments[index].Name}, which is left out");
    }

    private IlLocal Local(int index) => index < _body.Locals.Count
        ? _body.Locals[index]
        : throw Error($"{_instruction.Name} uses local {index}, but the method has {_body.Locals.Count}");

    private void Call()
    {
        var callee = _program.Callee(_method, _instruction);
        var parameters = callee.Function.Parameters;
        var values = new StackValue[parameters.Count];
        for (var i = values.Length - 1; i >= 0; i--)
        {
            values[i] = PopValue();
        }

        var arguments = new TirValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            arguments[i] = FromStack(values[i], parameters[i].Type, callee.ParameterTypes[i],
                $"argument {i + 1} of {callee.Name}");
        }

        var call = _ir.Call(callee.Function, arguments);
        if (call.HasResult)
        {
            Push(ToStack(call, callee.ReturnType));
        }
    }

    private void Return()
    {
        if (_function.ReturnType == TirType.Void)
        {
            _ir.Return();
        }
        else
        {
            _ir.Return(FromStack(PopValue(), _function.ReturnType, _method.ReturnType, "the return value"));
        }
    }

    /// <summary>
    /// brtrue and brfalse: a bool is tested as it is, any other value is compared with zero, a string (or a reference)
    /// and an object with null.
    /// </summary>
    private void BranchOnValue(bool jumpWhenTrue)
    {
        var value = PopValue();
        if (value.Type.IsAddress)
        {
            BranchOnAddress(value, jumpWhenTrue);
            return;
        }

        CheckNumber(value);
        if (value.Condition is { } condition)
        {
            Branch(condition, jumpWhenTrue);
            return;
        }

        var integer = Materialize(value);
        Branch(_ir.Compare(jumpWhenTrue ? TirCompareOp.Ne : TirCompareOp.Eq, integer, Zero(integer.Type)));
    }

    /// <summary>
    /// Ends the block with a branch on <paramref name="condition"/>: to the branch target when it is true and to the
    /// next block when it is false, or the other way round when not <paramref name="toTarget"/> (brfalse on a bool).
    /// </summary>
    private void Branch(TirValue condition, bool toTarget = true)
    {
        var (target, next) = (Target(), Next());
        Leave(target, next);
        _ir.Branch(condition, toTarget ? target.Tir : next.Tir, toTarget ? next.Tir : target.Tir);
    }

    /// <summary>
    /// A chain of tests: case 0 in this block, case i in the block <c>IL_xxxx_case{i}</c> (IL_xxxx is the switch);
    /// a value that matches no case (negative ones included, as IL reads it unsigned) goes on to the next instruction.
    /// </summary>
    private void Switch()
    {
        var value = PopNumber();
        var targets = ((IlSwitchOperand)_instruction.Operand!).Targets.Select(BlockAt).ToList();
        var next = Next();
        Leave([.. targets, next]);
        if (targets.Count == 0)
        {
            _ir.Jump(next.Tir);
            return;
        }

        // the case blocks cannot use this block's value, so it waits for them in a slot
        var slot = targets.Count > 1 ? SwitchSlot(value.Type) : null;
        if (slot is not null)
        {
            _ir.Store(slot, value);
        }

        for (var i = 0; i < targets.Count; i++)
        {
            if (i > 0)
            {
                _ir.Block = _block.SwitchCases[i - 1];
                value = _ir.Load(slot!);
            }

            var matches = _ir.Compare(TirCompareOp.Eq, value, _ir.Const(value.Type, i));
            _ir.Branch(matches, targets[i].Tir, i + 1 < targets.Count ? _block.SwitchCases[i] : next.Tir);
        }
    }

    private void Shift(TirBinaryOp op)
    {
        var amount = PopValue();
        var value = PopNumber();
        if (amount.Type == TirType.I64)
        {
            throw Error($"{_instruction.Name} shifts by an int64; IL takes an int32 or native int amount");
        }

        // a count is never negative once the C# compiler has masked it, so it is zero-extended
        Push(_ir.Binary(op, value, Materialize(Convert(amount, value.Type, unsigned: true))));
    }

    /// <summary>conv.i1, conv.u1, conv.i2, conv.u2: keep the low 8 or 16 bits, then sign- or zero-extend back to int32.</summary>
    private void ConvertToSmall(TirType small, bool unsigned) =>
        Push(Convert(Convert(PopValue(), small, unsigned: false), TirType.I32, unsigned));

    /// <summary>
    /// Pops the two operands of an arithmetic or comparison instruction, as ECMA-335 pairs them: both int32, both
    /// int64, or native int with native int or int32 (the int32 is sign-extended).
    /// </summary>
    private (TirValue Left, TirValue Right) PopOperands()
    {
        var right = PopValue();
        var left = PopValue();
        CheckNumber(left);
        CheckNumber(right);
        if (left.Type != right.Type)
        {
            if (left.Type == TirType.NativeInt && right.Type == TirType.I32)
            {
                right = Convert(right, TirType.NativeInt, unsigned: false);
            }
            else if (left.Type == TirType.I32 && right.Type == TirType.NativeInt)
            {
                left = Convert(left, TirType.NativeInt, unsigned: false);
            }
            else
            {
                throw Error($"{_instruction.Name} takes {LoweringErrors.StackTypeName(left.Type)} and " +
                    $"{LoweringErrors.StackTypeName(right.Type)}, which IL does not allow together");
            }
        }

        return (Materialize(left), Materialize(right));
    }

    /// <summary>
    /// A loaded value as IL has it on the stack: a bool or small integer becomes an int32 (a bool is 0 or 1, an
    /// int8, uint8, int16 or uint16 is sign- or zero-extended as its IL type says).
    /// </summary>
    private StackValue ToStack(TirValue value, IlType type) => value.Type.Kind switch
    {
        _ when IsArrayValue(value, type) => ArrayToStack(value, type),
        TirTypeKind.Bool => StackValue.OfBool(value),
        TirTypeKind.I8 or TirTypeKind.I16 => StackValue.Of(_ir.Convert(value, TirType.I32, IsUnsigned(type))),
        TirTypeKind.Ptr => ReferenceToStack(value, type),
        _ => StackValue.Of(value),
    };

    /// <summary>
    /// A stack value made fit for a location of TIR type <paramref name="type"/> (IL type <paramref name="ilType"/>):
    /// a bool is "not zero", a small integer keeps the low bits, an int32 for a native int is sign- or zero-extended
    /// as the IL type says, a double for a float is rounded to float and a float for a double widened.
    /// <paramref name="destination"/> names the location in errors, e.g. "local V_0".
    /// </summary>
    private TirValue FromStack(StackValue value, TirType type, IlType ilType, string destination)
    {
        if (ReferenceOrStructFromStack(value, type, ilType, destination) is { } unconverted)
        {
            return unconverted;
        }

        if ((value.Type == TirType.I64) != (type == TirType.I64) || value.Type.IsFloat != type.IsFloat)
        {
            throw Error($"{destination} has type {ilType}, but the value on the evaluation stack is " +
                LoweringErrors.StackTypeName(value.Type));
        }

        if (type != TirType.Bool)
        {
            return Materialize(Convert(value, type, IsUnsigned(ilType)));
        }

        if (value.Condition is { } condition)
        {
            return condition;
        }

        if (value.IsConstant)
        {
            return _ir.Const(value.Constant != 0);
        }

        return _ir.Compare(TirCompareOp.Ne, Materialize(value), Zero(value.Type));
    }

    /// <summary>
    /// Converts to another integer type: narrowing keeps the low bits; widening sign-extends, or zero-extends when
    /// <paramref name="unsigned"/>. A constant is converted here when the result is the same on every target.
    /// </summary>
    private StackValue Convert(StackValue value, TirType to, bool unsigned)
    {
        CheckNumber(value);
        if (value.Type == to)
        {
            return value;
        }

        var zeroExtend = unsigned && Rank(to) > Rank(value.Type);
        if (value.IsConstant && ConvertConstant(value.Constant, value.Type, to, zeroExtend) is { } constant)
        {
            return StackValue.OfConstant(to, constant);
        }

        return StackValue.Of(value.Condition is { } condition
            ? _ir.Convert(condition, to)
            : _ir.Convert(Materialize(value), to, zeroExtend));
    }

    /// <summary>
    /// <paramref name="value"/> of type <paramref name="from"/> converted to <paramref name="to"/>, or null when the
    /// result depends on how wide nint is (TIR's nint constants must mean the same on every target).
    /// </summary>
    private static long? ConvertConstant(long value, TirType from, TirType to, bool zeroExtend)
    {
        if (zeroExtend)
        {
            if (from == TirType.NativeInt && value < 0)
            {
                return null;
            }

            value = from.Kind switch
            {
                TirTypeKind.I8 => (byte)value,
                TirTypeKind.I16 => (ushort)value,
                TirTypeKind.I32 => (uint)value,
                _ => value,
            };
        }

        long result = to.Kind switch
        {
            TirTypeKind.I8 => (sbyte)value,
            TirTypeKind.I16 => (short)value,
            TirTypeKind.I32 => (int)value,
            _ => value,
        };
        return to != TirType.NativeInt || result is >= int.MinValue and <= int.MaxValue ? result : null;
    }

    /// <summary>Integer types from narrowest to widest; nint lies between i32 and i64 on every target.</summary>
    private static int Rank(TirType type) => type.Kind switch
    {
        TirTypeKind.I8 => 1,
        TirTypeKind.I16 => 2,
        TirTypeKind.I32 => 3,
        TirTypeKind.NativeInt => 4,
        TirTypeKind.I64 => 5,
        _ => 0,
    };

    /// <summary>True for an unsigned integer type, or an enum of one; bool and char count as unsigned.</summary>
    private bool IsUnsigned(IlType type) => ValueTypes.Underlying(type, _method.Assembly) is IlPrimitiveType
    {
        Kind: IlPrimitiveKind.Bool or IlPrimitiveKind.Char or IlPrimitiveKind.UInt8 or IlPrimitiveKind.UInt16
            or IlPrimitiveKind.UInt32 or IlPrimitiveKind.UInt64 or IlPrimitiveKind.NativeUInt,
    };

    private TirConst Zero(TirType type) => _ir.Const(type, 0);

    /// <summary>
    /// Hands the evaluation stack to every block control goes to next: each must expect the same types (the first
    /// to be seen sets them), and the values go into the stack slots, where those blocks load them from.
    /// </summary>
    private void Leave(params IEnumerable<IlBlock> successors)
    {
        NullsAsArrays([.. successors]); // see MethodLowering.HeapArrays.cs
        var types = _stack.Select(v => v.Type).ToArray();
        foreach (var successor in successors)
        {
            // null meeting an object takes the object's type (MethodLowering.Classes.cs)
            var arriving = ArrivingTypes(successor, types);
            if (successor.EntryStack is null)
            {
                successor.EntryStack = arriving;
            }
            else if (!successor.EntryStack.SequenceEqual(arriving))
            {
                var label = IlPrinter.Label(successor.Offset);
                throw Error(successor.EntryStackAssumed
                    ? $"control jumps back to {label} with {LoweringErrors.StackText(arriving)} on the evaluation " +
                      $"stack, but IL wants it empty there: no earlier instruction leads to {label}"
                    : $"control reaches {label} with {LoweringErrors.StackText(arriving)} on the evaluation " +
                      $"stack, but another way into {label} has {LoweringErrors.StackText(successor.EntryStack)}");
            }
        }

        PassReferenceTargets(successors);

        // every value is read before any slot is written, as one may come from a slot that another goes into
        var stores = new List<(TirSlot Slot, TirValue Value)>();
        for (var depth = 0; depth < _stack.Count; depth++)
        {
            if (NullStores(depth, successors) is { } nulls)
            {
                stores.AddRange(nulls);
                continue;
            }

            var slot = StackSlot(depth, types[depth]);
            if (_stack[depth].Slot != slot)
            {
                stores.Add((slot, Materialize(_stack[depth])));
            }
        }

        foreach (var (slot, value) in stores)
        {
            _ir.Store(slot, value);
        }
    }

    /// <summary>The block a branch goes to.</summary>
    private IlBlock Target() => BlockAt(((IlBranchOperand)_instruction.Operand!).Target);

    private IlBlock BlockAt(int offset) => _blockAt[_body.IndexOf(offset)]!;

    /// <summary>The block after the current one: where control goes when it does not branch.</summary>
    private IlBlock Next() => _block.End < _blockAt.Length
        ? _blockAt[_block.End]!
        : throw Error("control runs past the end of the method");

    private TirSlot StackSlot(int depth, TirType type)
    {
        if (!_stackSlots.TryGetValue((depth, type), out var slot))
        {
            slot = AddSlot($"stack{depth}_{type.Name}", type);
            _stackSlots.Add((depth, type), slot);
        }

        return slot;
    }

    private TirSlot SwitchSlot(TirType type)
    {
        if (!_switchSlots.TryGetValue(type, out var slot))
        {
            slot = AddSlot($"switch_{type.Name}", type);
            _switchSlots.Add(type, slot);
        }

        return slot;
    }

    /// <summary>A slot named <paramref name="name"/>, or name_2, name_3, ... when that is taken.</summary>
    private TirSlot AddSlot(string name, TirType type)
    {
        var unique = name;
        for (var n = 2; _function.Slots.Any(s => s.Name == unique); n++)
        {
            unique = $"{name}_{n}";
        }

        return _function.AddSlot(unique, type);
    }

    private void Push(TirValue value) => _stack.Add(StackValue.Of(value));

    private void Push(StackValue value) => _stack.Add(value);

    /// <summary>
    /// Pops a number (an int32, int64, native int, float32 or float64) and writes it as TIR if it was still pending.
    /// The other values have their own: <see cref="PopArray"/>, <see cref="PopAddress"/> and
    /// <see cref="PopReference"/>; a struct is popped with <see cref="PopValue"/> and passed on whole.
    /// </summary>
    private TirValue PopNumber()
    {
        var value = PopValue();
        CheckNumber(value);
        return Materialize(value);
    }

    /// <summary>Pops a value of any type, as it is (a number may still be pending).</summary>
    private StackValue PopValue()
    {
        if (_stack.Count == 0)
        {
            throw Error($"{_instruction.Name} needs a value, but the evaluation stack is empty");
        }

        var value = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return value;
    }

    /// <summary>The value as TIR: a pending constant becomes a constant (a ptr one, which only printed text makes, a null
    /// ptr), a pending bool its 0 or 1, a value in a stack slot a load.</summary>
    private TirValue Materialize(StackValue value)
    {
        if (value.Value is { } tir)
        {
            return tir;
        }

        if (value.Condition is { } condition)
        {
            return _ir.Convert(condition, TirType.I32);
        }

        return value.Slot is { } slot ? _ir.Load(slot)
            : value.Type == TirType.Ptr ? _ir.Zero(TirType.Ptr)
            : _ir.Const(value.Type, value.Constant);
    }

    private ChipSharpException Error(string message) => LoweringErrors.At(_method, _instruction.Offset, message);

    private ChipSharpException Unsupported(IlInstruction instruction) => LoweringErrors.At(_method, instruction.Offset,
        $"{instruction.Name} is not supported yet");
}
