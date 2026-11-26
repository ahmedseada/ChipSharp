using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ChipSharp.Abstractions;

namespace ChipSharp.Frontend.Il;

/// <summary>
/// Decodes one method body: every instruction with its operand resolved, the locals and the exception regions.
/// Checks that every operand is complete, every token names something of the right kind, and every branch,
/// switch and region boundary is the start of an instruction. Anything else is a <see cref="ChipSharpException"/>.
/// </summary>
internal sealed class IlBodyReader
{
    private readonly IlAssembly _assembly;
    private readonly IlMethod _method;
    private BlobReader _il;

    // where the instruction being read starts; errors are reported there
    private int _start;

    private IlBodyReader(IlAssembly assembly, IlMethod method, BlobReader il)
    {
        _assembly = assembly;
        _method = method;
        _il = il;
    }

    public static IlMethodBody Read(IlAssembly assembly, IlMethod method, MethodBodyBlock block) =>
        new IlBodyReader(assembly, method, block.GetILReader()).Read(block);

    private IlMethodBody Read(MethodBodyBlock block)
    {
        var codeSize = _il.Length;
        var instructions = new List<IlInstruction>();
        try
        {
            while (_il.RemainingBytes > 0)
            {
                _start = _il.Offset;
                instructions.Add(ReadInstruction());
            }

            _start = 0;
            var locals = _assembly.ResolveLocals(block.LocalSignature).Select((type, i) => new IlLocal(i, type)).ToArray();
            var regions = block.ExceptionRegions.Select(ReadRegion).ToArray();
            var body = new IlMethodBody(_method, block.MaxStack, block.LocalVariablesInitialized, codeSize, locals,
                instructions, regions);
            CheckTargets(body);
            return body;
        }
        catch (BadImageFormatException e)
        {
            throw Error(_start, e.Message.TrimEnd('.'), e);
        }
    }

    private IlInstruction ReadInstruction()
    {
        int code = _il.ReadByte();
        if (code == 0xFE)
        {
            if (_il.RemainingBytes == 0)
            {
                throw Error(_start, "the IL ends after 0xFE, the first byte of a two-byte opcode");
            }

            code = 0xFE00 | _il.ReadByte();
        }

        if (!IlOpCodes.IsDefined(code))
        {
            throw Error(_start, code > 0xFF ? $"unknown opcode 0xFE 0x{code & 0xFF:X2}" : $"unknown opcode 0x{code:X2}");
        }

        var opCode = (IlOpCode)code;
        var operand = ReadOperand(opCode);
        return new IlInstruction(_start, opCode, operand, _il.Offset - _start);
    }

    private IlOperand? ReadOperand(IlOpCode opCode)
    {
        var kind = opCode.GetOperandKind();
        Need(kind switch
        {
            IlOperandKind.None => 0,
            IlOperandKind.Int8 or IlOperandKind.UInt8 or IlOperandKind.ShortBranch or IlOperandKind.ShortArgument
                or IlOperandKind.ShortLocal => 1,
            IlOperandKind.Argument or IlOperandKind.Local => 2,
            IlOperandKind.Int64 or IlOperandKind.Float64 => 8,
            _ => 4,
        }, opCode);

        return kind switch
        {
            IlOperandKind.None => null,
            IlOperandKind.Int8 => new IlInt32Operand(_il.ReadSByte()),
            IlOperandKind.UInt8 => new IlInt32Operand(_il.ReadByte()),
            IlOperandKind.Int32 => new IlInt32Operand(_il.ReadInt32()),
            IlOperandKind.Int64 => new IlInt64Operand(_il.ReadInt64()),
            IlOperandKind.Float32 => new IlFloat32Operand(_il.ReadSingle()),
            IlOperandKind.Float64 => new IlFloat64Operand(_il.ReadDouble()),
            IlOperandKind.ShortBranch => Branch(opCode, _il.ReadSByte()),
            IlOperandKind.Branch => Branch(opCode, _il.ReadInt32()),
            IlOperandKind.Switch => Switch(opCode),
            IlOperandKind.ShortArgument => new IlArgumentOperand(_il.ReadByte()),
            IlOperandKind.Argument => new IlArgumentOperand(_il.ReadUInt16()),
            IlOperandKind.ShortLocal => new IlLocalOperand(_il.ReadByte()),
            IlOperandKind.Local => new IlLocalOperand(_il.ReadUInt16()),
            IlOperandKind.Method => new IlMethodOperand(_assembly.ResolveMethod(_assembly.Handle(_il.ReadInt32(),
                "a method", TableIndex.MethodDef, TableIndex.MemberRef, TableIndex.MethodSpec))),
            IlOperandKind.Field => new IlFieldOperand(_assembly.ResolveField(_assembly.Handle(_il.ReadInt32(),
                "a field", TableIndex.Field, TableIndex.MemberRef))),
            IlOperandKind.Type => new IlTypeOperand(_assembly.ResolveType(_assembly.Handle(_il.ReadInt32(),
                "a type", TableIndex.TypeDef, TableIndex.TypeRef, TableIndex.TypeSpec))),
            IlOperandKind.Token => Token(_il.ReadInt32()),
            IlOperandKind.String => new IlStringOperand(_assembly.ResolveString(_il.ReadInt32())),
            IlOperandKind.Signature => new IlSignatureOperand(_assembly.ResolveSignature(_assembly.Handle(_il.ReadInt32(),
                "a signature", TableIndex.StandAloneSig))),
            _ => throw new InvalidOperationException($"Unhandled operand kind {kind}."),
        };
    }

    /// <summary>ldtoken: a method, a field or a type.</summary>
    private IlOperand Token(int token)
    {
        var handle = _assembly.Handle(token, "a method, field or type",
            TableIndex.MethodDef, TableIndex.MemberRef, TableIndex.MethodSpec, TableIndex.Field,
            TableIndex.TypeDef, TableIndex.TypeRef, TableIndex.TypeSpec);
        return handle.Kind switch
        {
            HandleKind.FieldDefinition => new IlFieldOperand(_assembly.ResolveField(handle)),
            HandleKind.MemberReference when _assembly.IsField((MemberReferenceHandle)handle) =>
                new IlFieldOperand(_assembly.ResolveField(handle)),
            HandleKind.MethodDefinition or HandleKind.MemberReference or HandleKind.MethodSpecification =>
                new IlMethodOperand(_assembly.ResolveMethod(handle)),
            _ => new IlTypeOperand(_assembly.ResolveType(handle)),
        };
    }

    private IlBranchOperand Branch(IlOpCode opCode, long displacement) => new(Target(opCode, _il.Offset + displacement));

    private IlSwitchOperand Switch(IlOpCode opCode)
    {
        var count = _il.ReadUInt32();
        if (count > _il.RemainingBytes / 4)
        {
            throw Error(_start, $"the IL ends in the middle of the {count} targets of switch");
        }

        var displacements = new int[count];
        for (var i = 0; i < displacements.Length; i++)
        {
            displacements[i] = _il.ReadInt32();
        }

        // displacements count from the end of the whole instruction
        var next = _il.Offset;
        return new IlSwitchOperand([.. displacements.Select(d => Target(opCode, next + (long)d))]);
    }

    private int Target(IlOpCode opCode, long target)
    {
        if (target < 0 || target >= _il.Length)
        {
            throw Error(_start, $"{opCode.GetName()} jumps to offset {target}, outside the method's {_il.Length} bytes of IL");
        }

        return (int)target;
    }

    private void Need(int bytes, IlOpCode opCode)
    {
        if (_il.RemainingBytes < bytes)
        {
            throw Error(_start, $"the IL ends in the middle of the operand of {opCode.GetName()}");
        }
    }

    private IlExceptionRegion ReadRegion(ExceptionRegion region) => new(
        region.Kind switch
        {
            ExceptionRegionKind.Catch => IlExceptionRegionKind.Catch,
            ExceptionRegionKind.Filter => IlExceptionRegionKind.Filter,
            ExceptionRegionKind.Finally => IlExceptionRegionKind.Finally,
            ExceptionRegionKind.Fault => IlExceptionRegionKind.Fault,
            var other => throw new BadImageFormatException($"unknown exception region kind {other}"),
        },
        region.TryOffset,
        region.TryLength,
        region.HandlerOffset,
        region.HandlerLength,
        region.CatchType.IsNil ? null : _assembly.ResolveType(region.CatchType),
        region.Kind == ExceptionRegionKind.Filter ? region.FilterOffset : null);

    private void CheckTargets(IlMethodBody body)
    {
        foreach (var instruction in body.Instructions)
        {
            IReadOnlyList<int> targets = instruction.Operand switch
            {
                IlBranchOperand branch => [branch.Target],
                IlSwitchOperand table => table.Targets,
                _ => [],
            };

            foreach (var target in targets)
            {
                if (body.IndexOf(target) < 0)
                {
                    throw Error(instruction.Offset, $"{instruction.Name} jumps to offset {target}, inside another instruction");
                }
            }
        }

        foreach (var region in body.ExceptionRegions)
        {
            var kind = region.Kind.ToString().ToLowerInvariant();
            CheckBoundary(body, region, region.TryOffset, $"the try of a {kind} region starts");
            CheckBoundary(body, region, region.TryEnd, $"the try of a {kind} region ends");
            CheckBoundary(body, region, region.HandlerOffset, $"the handler of a {kind} region starts");
            CheckBoundary(body, region, region.HandlerEnd, $"the handler of a {kind} region ends");
            if (region.FilterOffset is { } filter)
            {
                CheckBoundary(body, region, filter, "the filter of a filter region starts");
            }
        }
    }

    private void CheckBoundary(IlMethodBody body, IlExceptionRegion region, int offset, string what)
    {
        // an end may also be the end of the IL
        if (offset != body.CodeSize && body.IndexOf(offset) < 0)
        {
            throw Error(region.TryOffset, $"{what} at offset {offset}, which is not the start of an instruction");
        }
    }

    private ChipSharpException Error(int offset, string message, Exception? inner = null) =>
        _assembly.Error(_method, offset, message, inner);
}
