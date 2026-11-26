using System.Reflection;
using System.Reflection.Emit;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Tests.Il;

public sealed class IlOpCodeTests
{
    [Fact]
    public void The_table_matches_every_opcode_Reflection_Emit_knows()
    {
        var emitted = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)!)
            .Where(op => !op.Name!.StartsWith("prefix", StringComparison.Ordinal))
            .ToList();

        foreach (var op in emitted)
        {
            var value = (ushort)op.Value;
            Assert.True(IlOpCodes.IsDefined(value), $"{op.Name} (0x{value:X4}) is missing");
            var ours = (IlOpCode)value;
            Assert.Equal(op.Name, ours.GetName());
            Assert.Equal(Expected(op), ours.GetOperandKind());
            Assert.Equal(op.Size == 2, ours.IsTwoByte());
        }

        // ECMA-335 also has no. (0xFE 0x19), which Reflection.Emit lacks
        Assert.Equal([IlOpCode.No], IlOpCodes.All.Except(emitted.Select(op => (IlOpCode)(ushort)op.Value)));
        Assert.Equal("no.", IlOpCode.No.GetName());
        Assert.Equal(IlOperandKind.UInt8, IlOpCode.No.GetOperandKind());
    }

    [Fact]
    public void Gaps_in_the_encoding_are_not_opcodes()
    {
        Assert.False(IlOpCodes.IsDefined(0x24));
        Assert.False(IlOpCodes.IsDefined(0xA6));
        Assert.False(IlOpCodes.IsDefined(0xFE08));
        Assert.False(IlOpCodes.IsDefined(0xFE1F));
        Assert.False(IlOpCodes.IsDefined(0xFE));
        Assert.Throws<ArgumentOutOfRangeException>(() => ((IlOpCode)0x24).GetName());
        Assert.Equal(219, IlOpCodes.All.Count);
    }

    private static IlOperandKind Expected(OpCode op) => op.OperandType switch
    {
        OperandType.InlineNone => IlOperandKind.None,
        OperandType.ShortInlineI => op.Name == "unaligned." ? IlOperandKind.UInt8 : IlOperandKind.Int8,
        OperandType.InlineI => IlOperandKind.Int32,
        OperandType.InlineI8 => IlOperandKind.Int64,
        OperandType.ShortInlineR => IlOperandKind.Float32,
        OperandType.InlineR => IlOperandKind.Float64,
        OperandType.ShortInlineBrTarget => IlOperandKind.ShortBranch,
        OperandType.InlineBrTarget => IlOperandKind.Branch,
        OperandType.InlineSwitch => IlOperandKind.Switch,
        OperandType.ShortInlineVar => op.Name!.Contains("arg", StringComparison.Ordinal) ? IlOperandKind.ShortArgument : IlOperandKind.ShortLocal,
        OperandType.InlineVar => op.Name!.Contains("arg", StringComparison.Ordinal) ? IlOperandKind.Argument : IlOperandKind.Local,
        OperandType.InlineMethod => IlOperandKind.Method,
        OperandType.InlineField => IlOperandKind.Field,
        OperandType.InlineType => IlOperandKind.Type,
        OperandType.InlineTok => IlOperandKind.Token,
        OperandType.InlineString => IlOperandKind.String,
        OperandType.InlineSig => IlOperandKind.Signature,
        var other => throw new InvalidOperationException($"{op.Name}: unexpected operand type {other}"),
    };
}
