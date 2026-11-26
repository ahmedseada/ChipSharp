namespace ChipSharp.Frontend.Il;

/// <summary>How an instruction's operand is encoded after its opcode (ECMA-335 Partition III).</summary>
public enum IlOperandKind
{
    /// <summary>No operand.</summary>
    None,

    /// <summary>A signed 8-bit constant: <c>ldc.i4.s</c>.</summary>
    Int8,

    /// <summary>An unsigned 8-bit value: the alignment of <c>unaligned.</c> and the flags of <c>no.</c>.</summary>
    UInt8,

    /// <summary>A 32-bit integer constant: <c>ldc.i4</c>.</summary>
    Int32,

    /// <summary>A 64-bit integer constant: <c>ldc.i8</c>.</summary>
    Int64,

    /// <summary>A 32-bit float constant: <c>ldc.r4</c>.</summary>
    Float32,

    /// <summary>A 64-bit float constant: <c>ldc.r8</c>.</summary>
    Float64,

    /// <summary>A branch with a signed 8-bit displacement: the <c>.s</c> forms.</summary>
    ShortBranch,

    /// <summary>A branch with a signed 32-bit displacement.</summary>
    Branch,

    /// <summary><c>switch</c>: a count, then that many 32-bit displacements.</summary>
    Switch,

    /// <summary>An argument number in 8 bits: <c>ldarg.s</c>, <c>ldarga.s</c>, <c>starg.s</c>.</summary>
    ShortArgument,

    /// <summary>An argument number in 16 bits: <c>ldarg</c>, <c>ldarga</c>, <c>starg</c>.</summary>
    Argument,

    /// <summary>A local number in 8 bits: <c>ldloc.s</c>, <c>ldloca.s</c>, <c>stloc.s</c>.</summary>
    ShortLocal,

    /// <summary>A local number in 16 bits: <c>ldloc</c>, <c>ldloca</c>, <c>stloc</c>.</summary>
    Local,

    /// <summary>A method token: <c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c>, <c>ldvirtftn</c>, <c>jmp</c>.</summary>
    Method,

    /// <summary>A field token: <c>ldfld</c>, <c>stsfld</c>, ...</summary>
    Field,

    /// <summary>A type token: <c>box</c>, <c>newarr</c>, <c>initobj</c>, ...</summary>
    Type,

    /// <summary><c>ldtoken</c>: a method, field or type token.</summary>
    Token,

    /// <summary><c>ldstr</c>: a string token.</summary>
    String,

    /// <summary><c>calli</c>: a stand-alone method signature token.</summary>
    Signature,
}

/// <summary>The name ildasm writes and the operand kind of every <see cref="IlOpCode"/>.</summary>
public static class IlOpCodes
{
    private static readonly Dictionary<IlOpCode, (string Name, IlOperandKind OperandKind)> Table = Build();

    /// <summary>Every opcode, in encoding order.</summary>
    public static IReadOnlyList<IlOpCode> All { get; } = [.. Table.Keys.Order()];

    /// <summary>The name as ildasm writes it, e.g. "ldc.i4.s", "ceq", "constrained.".</summary>
    public static string GetName(this IlOpCode opCode) =>
        Table.TryGetValue(opCode, out var info)
            ? info.Name
            : throw new ArgumentOutOfRangeException(nameof(opCode), opCode, "Not an ECMA-335 opcode.");

    /// <summary>How the operand that follows the opcode is encoded.</summary>
    public static IlOperandKind GetOperandKind(this IlOpCode opCode) =>
        Table.TryGetValue(opCode, out var info)
            ? info.OperandKind
            : throw new ArgumentOutOfRangeException(nameof(opCode), opCode, "Not an ECMA-335 opcode.");

    /// <summary>True for the opcodes encoded as 0xFE and a second byte.</summary>
    public static bool IsTwoByte(this IlOpCode opCode) => (ushort)opCode >> 8 == 0xFE;

    /// <summary>True when <paramref name="value"/> (one byte, or 0xFExx) is an ECMA-335 opcode.</summary>
    public static bool IsDefined(int value) => value is >= 0 and <= 0xFFFF && Table.ContainsKey((IlOpCode)value);

    private static Dictionary<IlOpCode, (string, IlOperandKind)> Build()
    {
        var table = new Dictionary<IlOpCode, (string, IlOperandKind)>();

        void Op(IlOpCode opCode, string name, IlOperandKind operand = IlOperandKind.None) =>
            table.Add(opCode, (name, operand));

        Op(IlOpCode.Nop, "nop");
        Op(IlOpCode.Break, "break");
        Op(IlOpCode.Ldarg_0, "ldarg.0");
        Op(IlOpCode.Ldarg_1, "ldarg.1");
        Op(IlOpCode.Ldarg_2, "ldarg.2");
        Op(IlOpCode.Ldarg_3, "ldarg.3");
        Op(IlOpCode.Ldloc_0, "ldloc.0");
        Op(IlOpCode.Ldloc_1, "ldloc.1");
        Op(IlOpCode.Ldloc_2, "ldloc.2");
        Op(IlOpCode.Ldloc_3, "ldloc.3");
        Op(IlOpCode.Stloc_0, "stloc.0");
        Op(IlOpCode.Stloc_1, "stloc.1");
        Op(IlOpCode.Stloc_2, "stloc.2");
        Op(IlOpCode.Stloc_3, "stloc.3");
        Op(IlOpCode.Ldarg_S, "ldarg.s", IlOperandKind.ShortArgument);
        Op(IlOpCode.Ldarga_S, "ldarga.s", IlOperandKind.ShortArgument);
        Op(IlOpCode.Starg_S, "starg.s", IlOperandKind.ShortArgument);
        Op(IlOpCode.Ldloc_S, "ldloc.s", IlOperandKind.ShortLocal);
        Op(IlOpCode.Ldloca_S, "ldloca.s", IlOperandKind.ShortLocal);
        Op(IlOpCode.Stloc_S, "stloc.s", IlOperandKind.ShortLocal);
        Op(IlOpCode.Ldnull, "ldnull");
        Op(IlOpCode.Ldc_I4_M1, "ldc.i4.m1");
        Op(IlOpCode.Ldc_I4_0, "ldc.i4.0");
        Op(IlOpCode.Ldc_I4_1, "ldc.i4.1");
        Op(IlOpCode.Ldc_I4_2, "ldc.i4.2");
        Op(IlOpCode.Ldc_I4_3, "ldc.i4.3");
        Op(IlOpCode.Ldc_I4_4, "ldc.i4.4");
        Op(IlOpCode.Ldc_I4_5, "ldc.i4.5");
        Op(IlOpCode.Ldc_I4_6, "ldc.i4.6");
        Op(IlOpCode.Ldc_I4_7, "ldc.i4.7");
        Op(IlOpCode.Ldc_I4_8, "ldc.i4.8");
        Op(IlOpCode.Ldc_I4_S, "ldc.i4.s", IlOperandKind.Int8);
        Op(IlOpCode.Ldc_I4, "ldc.i4", IlOperandKind.Int32);
        Op(IlOpCode.Ldc_I8, "ldc.i8", IlOperandKind.Int64);
        Op(IlOpCode.Ldc_R4, "ldc.r4", IlOperandKind.Float32);
        Op(IlOpCode.Ldc_R8, "ldc.r8", IlOperandKind.Float64);
        Op(IlOpCode.Dup, "dup");
        Op(IlOpCode.Pop, "pop");
        Op(IlOpCode.Jmp, "jmp", IlOperandKind.Method);
        Op(IlOpCode.Call, "call", IlOperandKind.Method);
        Op(IlOpCode.Calli, "calli", IlOperandKind.Signature);
        Op(IlOpCode.Ret, "ret");
        Op(IlOpCode.Br_S, "br.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Brfalse_S, "brfalse.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Brtrue_S, "brtrue.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Beq_S, "beq.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Bge_S, "bge.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Bgt_S, "bgt.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Ble_S, "ble.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Blt_S, "blt.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Bne_Un_S, "bne.un.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Bge_Un_S, "bge.un.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Bgt_Un_S, "bgt.un.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Ble_Un_S, "ble.un.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Blt_Un_S, "blt.un.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Br, "br", IlOperandKind.Branch);
        Op(IlOpCode.Brfalse, "brfalse", IlOperandKind.Branch);
        Op(IlOpCode.Brtrue, "brtrue", IlOperandKind.Branch);
        Op(IlOpCode.Beq, "beq", IlOperandKind.Branch);
        Op(IlOpCode.Bge, "bge", IlOperandKind.Branch);
        Op(IlOpCode.Bgt, "bgt", IlOperandKind.Branch);
        Op(IlOpCode.Ble, "ble", IlOperandKind.Branch);
        Op(IlOpCode.Blt, "blt", IlOperandKind.Branch);
        Op(IlOpCode.Bne_Un, "bne.un", IlOperandKind.Branch);
        Op(IlOpCode.Bge_Un, "bge.un", IlOperandKind.Branch);
        Op(IlOpCode.Bgt_Un, "bgt.un", IlOperandKind.Branch);
        Op(IlOpCode.Ble_Un, "ble.un", IlOperandKind.Branch);
        Op(IlOpCode.Blt_Un, "blt.un", IlOperandKind.Branch);
        Op(IlOpCode.Switch, "switch", IlOperandKind.Switch);
        Op(IlOpCode.Ldind_I1, "ldind.i1");
        Op(IlOpCode.Ldind_U1, "ldind.u1");
        Op(IlOpCode.Ldind_I2, "ldind.i2");
        Op(IlOpCode.Ldind_U2, "ldind.u2");
        Op(IlOpCode.Ldind_I4, "ldind.i4");
        Op(IlOpCode.Ldind_U4, "ldind.u4");
        Op(IlOpCode.Ldind_I8, "ldind.i8");
        Op(IlOpCode.Ldind_I, "ldind.i");
        Op(IlOpCode.Ldind_R4, "ldind.r4");
        Op(IlOpCode.Ldind_R8, "ldind.r8");
        Op(IlOpCode.Ldind_Ref, "ldind.ref");
        Op(IlOpCode.Stind_Ref, "stind.ref");
        Op(IlOpCode.Stind_I1, "stind.i1");
        Op(IlOpCode.Stind_I2, "stind.i2");
        Op(IlOpCode.Stind_I4, "stind.i4");
        Op(IlOpCode.Stind_I8, "stind.i8");
        Op(IlOpCode.Stind_R4, "stind.r4");
        Op(IlOpCode.Stind_R8, "stind.r8");
        Op(IlOpCode.Add, "add");
        Op(IlOpCode.Sub, "sub");
        Op(IlOpCode.Mul, "mul");
        Op(IlOpCode.Div, "div");
        Op(IlOpCode.Div_Un, "div.un");
        Op(IlOpCode.Rem, "rem");
        Op(IlOpCode.Rem_Un, "rem.un");
        Op(IlOpCode.And, "and");
        Op(IlOpCode.Or, "or");
        Op(IlOpCode.Xor, "xor");
        Op(IlOpCode.Shl, "shl");
        Op(IlOpCode.Shr, "shr");
        Op(IlOpCode.Shr_Un, "shr.un");
        Op(IlOpCode.Neg, "neg");
        Op(IlOpCode.Not, "not");
        Op(IlOpCode.Conv_I1, "conv.i1");
        Op(IlOpCode.Conv_I2, "conv.i2");
        Op(IlOpCode.Conv_I4, "conv.i4");
        Op(IlOpCode.Conv_I8, "conv.i8");
        Op(IlOpCode.Conv_R4, "conv.r4");
        Op(IlOpCode.Conv_R8, "conv.r8");
        Op(IlOpCode.Conv_U4, "conv.u4");
        Op(IlOpCode.Conv_U8, "conv.u8");
        Op(IlOpCode.Callvirt, "callvirt", IlOperandKind.Method);
        Op(IlOpCode.Cpobj, "cpobj", IlOperandKind.Type);
        Op(IlOpCode.Ldobj, "ldobj", IlOperandKind.Type);
        Op(IlOpCode.Ldstr, "ldstr", IlOperandKind.String);
        Op(IlOpCode.Newobj, "newobj", IlOperandKind.Method);
        Op(IlOpCode.Castclass, "castclass", IlOperandKind.Type);
        Op(IlOpCode.Isinst, "isinst", IlOperandKind.Type);
        Op(IlOpCode.Conv_R_Un, "conv.r.un");
        Op(IlOpCode.Unbox, "unbox", IlOperandKind.Type);
        Op(IlOpCode.Throw, "throw");
        Op(IlOpCode.Ldfld, "ldfld", IlOperandKind.Field);
        Op(IlOpCode.Ldflda, "ldflda", IlOperandKind.Field);
        Op(IlOpCode.Stfld, "stfld", IlOperandKind.Field);
        Op(IlOpCode.Ldsfld, "ldsfld", IlOperandKind.Field);
        Op(IlOpCode.Ldsflda, "ldsflda", IlOperandKind.Field);
        Op(IlOpCode.Stsfld, "stsfld", IlOperandKind.Field);
        Op(IlOpCode.Stobj, "stobj", IlOperandKind.Type);
        Op(IlOpCode.Conv_Ovf_I1_Un, "conv.ovf.i1.un");
        Op(IlOpCode.Conv_Ovf_I2_Un, "conv.ovf.i2.un");
        Op(IlOpCode.Conv_Ovf_I4_Un, "conv.ovf.i4.un");
        Op(IlOpCode.Conv_Ovf_I8_Un, "conv.ovf.i8.un");
        Op(IlOpCode.Conv_Ovf_U1_Un, "conv.ovf.u1.un");
        Op(IlOpCode.Conv_Ovf_U2_Un, "conv.ovf.u2.un");
        Op(IlOpCode.Conv_Ovf_U4_Un, "conv.ovf.u4.un");
        Op(IlOpCode.Conv_Ovf_U8_Un, "conv.ovf.u8.un");
        Op(IlOpCode.Conv_Ovf_I_Un, "conv.ovf.i.un");
        Op(IlOpCode.Conv_Ovf_U_Un, "conv.ovf.u.un");
        Op(IlOpCode.Box, "box", IlOperandKind.Type);
        Op(IlOpCode.Newarr, "newarr", IlOperandKind.Type);
        Op(IlOpCode.Ldlen, "ldlen");
        Op(IlOpCode.Ldelema, "ldelema", IlOperandKind.Type);
        Op(IlOpCode.Ldelem_I1, "ldelem.i1");
        Op(IlOpCode.Ldelem_U1, "ldelem.u1");
        Op(IlOpCode.Ldelem_I2, "ldelem.i2");
        Op(IlOpCode.Ldelem_U2, "ldelem.u2");
        Op(IlOpCode.Ldelem_I4, "ldelem.i4");
        Op(IlOpCode.Ldelem_U4, "ldelem.u4");
        Op(IlOpCode.Ldelem_I8, "ldelem.i8");
        Op(IlOpCode.Ldelem_I, "ldelem.i");
        Op(IlOpCode.Ldelem_R4, "ldelem.r4");
        Op(IlOpCode.Ldelem_R8, "ldelem.r8");
        Op(IlOpCode.Ldelem_Ref, "ldelem.ref");
        Op(IlOpCode.Stelem_I, "stelem.i");
        Op(IlOpCode.Stelem_I1, "stelem.i1");
        Op(IlOpCode.Stelem_I2, "stelem.i2");
        Op(IlOpCode.Stelem_I4, "stelem.i4");
        Op(IlOpCode.Stelem_I8, "stelem.i8");
        Op(IlOpCode.Stelem_R4, "stelem.r4");
        Op(IlOpCode.Stelem_R8, "stelem.r8");
        Op(IlOpCode.Stelem_Ref, "stelem.ref");
        Op(IlOpCode.Ldelem, "ldelem", IlOperandKind.Type);
        Op(IlOpCode.Stelem, "stelem", IlOperandKind.Type);
        Op(IlOpCode.Unbox_Any, "unbox.any", IlOperandKind.Type);
        Op(IlOpCode.Conv_Ovf_I1, "conv.ovf.i1");
        Op(IlOpCode.Conv_Ovf_U1, "conv.ovf.u1");
        Op(IlOpCode.Conv_Ovf_I2, "conv.ovf.i2");
        Op(IlOpCode.Conv_Ovf_U2, "conv.ovf.u2");
        Op(IlOpCode.Conv_Ovf_I4, "conv.ovf.i4");
        Op(IlOpCode.Conv_Ovf_U4, "conv.ovf.u4");
        Op(IlOpCode.Conv_Ovf_I8, "conv.ovf.i8");
        Op(IlOpCode.Conv_Ovf_U8, "conv.ovf.u8");
        Op(IlOpCode.Refanyval, "refanyval", IlOperandKind.Type);
        Op(IlOpCode.Ckfinite, "ckfinite");
        Op(IlOpCode.Mkrefany, "mkrefany", IlOperandKind.Type);
        Op(IlOpCode.Ldtoken, "ldtoken", IlOperandKind.Token);
        Op(IlOpCode.Conv_U2, "conv.u2");
        Op(IlOpCode.Conv_U1, "conv.u1");
        Op(IlOpCode.Conv_I, "conv.i");
        Op(IlOpCode.Conv_Ovf_I, "conv.ovf.i");
        Op(IlOpCode.Conv_Ovf_U, "conv.ovf.u");
        Op(IlOpCode.Add_Ovf, "add.ovf");
        Op(IlOpCode.Add_Ovf_Un, "add.ovf.un");
        Op(IlOpCode.Mul_Ovf, "mul.ovf");
        Op(IlOpCode.Mul_Ovf_Un, "mul.ovf.un");
        Op(IlOpCode.Sub_Ovf, "sub.ovf");
        Op(IlOpCode.Sub_Ovf_Un, "sub.ovf.un");
        Op(IlOpCode.Endfinally, "endfinally");
        Op(IlOpCode.Leave, "leave", IlOperandKind.Branch);
        Op(IlOpCode.Leave_S, "leave.s", IlOperandKind.ShortBranch);
        Op(IlOpCode.Stind_I, "stind.i");
        Op(IlOpCode.Conv_U, "conv.u");

        Op(IlOpCode.Arglist, "arglist");
        Op(IlOpCode.Ceq, "ceq");
        Op(IlOpCode.Cgt, "cgt");
        Op(IlOpCode.Cgt_Un, "cgt.un");
        Op(IlOpCode.Clt, "clt");
        Op(IlOpCode.Clt_Un, "clt.un");
        Op(IlOpCode.Ldftn, "ldftn", IlOperandKind.Method);
        Op(IlOpCode.Ldvirtftn, "ldvirtftn", IlOperandKind.Method);
        Op(IlOpCode.Ldarg, "ldarg", IlOperandKind.Argument);
        Op(IlOpCode.Ldarga, "ldarga", IlOperandKind.Argument);
        Op(IlOpCode.Starg, "starg", IlOperandKind.Argument);
        Op(IlOpCode.Ldloc, "ldloc", IlOperandKind.Local);
        Op(IlOpCode.Ldloca, "ldloca", IlOperandKind.Local);
        Op(IlOpCode.Stloc, "stloc", IlOperandKind.Local);
        Op(IlOpCode.Localloc, "localloc");
        Op(IlOpCode.Endfilter, "endfilter");
        Op(IlOpCode.Unaligned, "unaligned.", IlOperandKind.UInt8);
        Op(IlOpCode.Volatile, "volatile.");
        Op(IlOpCode.Tail, "tail.");
        Op(IlOpCode.Initobj, "initobj", IlOperandKind.Type);
        Op(IlOpCode.Constrained, "constrained.", IlOperandKind.Type);
        Op(IlOpCode.Cpblk, "cpblk");
        Op(IlOpCode.Initblk, "initblk");
        Op(IlOpCode.No, "no.", IlOperandKind.UInt8);
        Op(IlOpCode.Rethrow, "rethrow");
        Op(IlOpCode.Sizeof, "sizeof", IlOperandKind.Type);
        Op(IlOpCode.Refanytype, "refanytype");
        Op(IlOpCode.Readonly, "readonly.");

        return table;
    }
}
