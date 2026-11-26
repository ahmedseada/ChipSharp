using System.Globalization;

namespace ChipSharp.Frontend.Il;

/// <summary>
/// An instruction's operand, decoded. The opcode fixes which kind it is (<see cref="IlOpCodes.GetOperandKind"/>):
/// <list type="bullet">
/// <item>Int8, UInt8, Int32: <see cref="IlInt32Operand"/>; Int64: <see cref="IlInt64Operand"/></item>
/// <item>Float32: <see cref="IlFloat32Operand"/>; Float64: <see cref="IlFloat64Operand"/></item>
/// <item>ShortBranch, Branch: <see cref="IlBranchOperand"/>; Switch: <see cref="IlSwitchOperand"/></item>
/// <item>ShortArgument, Argument: <see cref="IlArgumentOperand"/>; ShortLocal, Local: <see cref="IlLocalOperand"/></item>
/// <item>Method: <see cref="IlMethodOperand"/>; Field: <see cref="IlFieldOperand"/>; Type: <see cref="IlTypeOperand"/></item>
/// <item>Token (ldtoken): whichever of those three the token names</item>
/// <item>String: <see cref="IlStringOperand"/>; Signature (calli): <see cref="IlSignatureOperand"/></item>
/// </list>
/// An instruction without an operand has null.
/// </summary>
public abstract record IlOperand;

/// <summary>An integer constant of up to 32 bits. An int8 operand (<c>ldc.i4.s</c>) is sign-extended.</summary>
public sealed record IlInt32Operand(int Value) : IlOperand
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A 64-bit integer constant: <c>ldc.i8</c>.</summary>
public sealed record IlInt64Operand(long Value) : IlOperand
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A 32-bit float constant: <c>ldc.r4</c>.</summary>
public sealed record IlFloat32Operand(float Value) : IlOperand
{
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>A 64-bit float constant: <c>ldc.r8</c>.</summary>
public sealed record IlFloat64Operand(double Value) : IlOperand
{
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// Where a branch goes: the absolute offset of the target instruction in the method body (the short and long
/// forms alike). The reader checks that an instruction starts there.
/// </summary>
public sealed record IlBranchOperand(int Target) : IlOperand
{
    public override string ToString() => IlPrinter.Label(Target);
}

/// <summary>The targets of a <c>switch</c>, as absolute offsets; case 0 goes to the first. A value past the end falls through.</summary>
public sealed record IlSwitchOperand(IReadOnlyList<int> Targets) : IlOperand
{
    public bool Equals(IlSwitchOperand? other) => other is not null && Targets.SequenceEqual(other.Targets);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var target in Targets)
        {
            hash.Add(target);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"({string.Join(", ", Targets.Select(IlPrinter.Label))})";
}

/// <summary>An IL argument number. In an instance method argument 0 is <c>this</c> and parameter i is argument i + 1.</summary>
public sealed record IlArgumentOperand(int Index) : IlOperand
{
    public override string ToString() => Index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A local variable number, an index into <see cref="IlMethodBody.Locals"/>.</summary>
public sealed record IlLocalOperand(int Index) : IlOperand
{
    public override string ToString() => $"V_{Index.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>A method: what <c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c> and <c>ldtoken</c> name.</summary>
public sealed record IlMethodOperand(IlMethodReference Method) : IlOperand
{
    public override string ToString() => Method.ToString();
}

/// <summary>A field: what <c>ldfld</c>, <c>stsfld</c>, ... and <c>ldtoken</c> name.</summary>
public sealed record IlFieldOperand(IlFieldReference Field) : IlOperand
{
    public override string ToString() => Field.ToString();
}

/// <summary>A type: what <c>box</c>, <c>newarr</c>, <c>initobj</c>, ... and <c>ldtoken</c> name.</summary>
public sealed record IlTypeOperand(IlType Type) : IlOperand
{
    public override string ToString() => Type.ToString();
}

/// <summary>The string <c>ldstr</c> loads. <c>ToString()</c> quotes and escapes it.</summary>
public sealed record IlStringOperand(string Value) : IlOperand
{
    public override string ToString() => IlPrinter.Quote(Value);
}

/// <summary>The signature of the function pointer <c>calli</c> calls.</summary>
public sealed record IlSignatureOperand(IlMethodSignature Signature) : IlOperand
{
    public override string ToString() => Signature.ToString();
}
