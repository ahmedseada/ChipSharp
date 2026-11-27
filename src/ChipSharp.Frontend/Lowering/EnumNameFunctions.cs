using System.Globalization;
using System.Reflection;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The names of an enum's values, as .NET's <c>Enum.ToString()</c> gives them, for printed and kept text: two TIR
/// functions per enum, made when first needed, which take the value as an i64 (sign-extended for an enum of a signed
/// type, else zero-extended):
/// <code>
/// function void ChipSharp.Enum.Write&lt;Program.Color&gt;(i64 %value)     prints the name, with no heap
/// function ref ChipSharp.Enum.Name&lt;Program.Color&gt;(i64 %value)      the name as a string (a literal when it is one name)
/// </code>
/// A value with a member is that member's name (the first one C# declares, when several have it). Any other value of
/// an enum without <c>[Flags]</c> is its number. For a <c>[Flags]</c> enum, 0 without a member is "0", and any other
/// value is the names of its members from the largest value down, each taken when all its bits are in what is left of
/// the value, listed from the smallest up with ", " between them (<c>Read, Write</c>), or its number when bits are left
/// that no member has: .NET's own way.
/// </summary>
internal sealed class EnumNameFunctions(TirModule module, ProgramFunctions program)
{
    private const string FlagsAttribute = "System.FlagsAttribute";

    private readonly Dictionary<IlTypeDefinition, TirFunction> _writes = [];
    private readonly Dictionary<IlTypeDefinition, TirFunction> _names = [];

    /// <summary>The function that prints a value of <paramref name="type"/> by its name.</summary>
    public TirFunction Write(IlTypeDefinition type)
    {
        if (!_writes.TryGetValue(type, out var function))
        {
            function = Define(type, write: true);
            _writes.Add(type, function);
        }

        return function;
    }

    /// <summary>The function that gives a value of <paramref name="type"/> its name as a string.</summary>
    public TirFunction Name(IlTypeDefinition type)
    {
        if (!_names.TryGetValue(type, out var function))
        {
            function = Define(type, write: false);
            _names.Add(type, function);
        }

        return function;
    }

    private TirFunction Define(IlTypeDefinition type, bool write)
    {
        var underlying = (IlPrimitiveType)ValueTypes.EnumType(type)!;
        var bits = PrintedText.Bits(underlying.Kind);
        var signed = PrintedText.IsSigned(underlying.Kind);

        // the members, each value once (the first name C# declares for it), as the i64 the function is given
        var members = new List<(long Value, ulong Bits, string Name)>();
        foreach (var field in type.Fields.Where(field => field.IsStatic && (field.Attributes & FieldAttributes.Literal) != 0))
        {
            if (type.Assembly.FieldConstant(field) is { } raw && members.All(member => member.Bits != raw))
            {
                members.Add((Extend(raw, bits, signed), raw, field.Name));
            }
        }

        var name = $"ChipSharp.Enum.{(write ? "Write" : "Name")}<{CSharpNames.Type(type.Type)}>";
        var function = module.AddFunction(name, write ? TirType.Void : TirType.Ref, ("value", TirType.I64));
        var value = function.Parameters[0];
        var ir = new TirBuilder(function.AddBlock("entry"));

        // a member's own value
        for (var m = 0; m < members.Count; m++)
        {
            var (member, _, memberName) = members[m];
            var found = function.AddBlock(Numbered("member", m));
            var next = function.AddBlock(Numbered("next", m));
            ir.Branch(ir.Compare(TirCompareOp.Eq, value, ir.Const(TirType.I64, member)), found, next);
            ir.Block = found;
            Finish(ir, write, ir.AddressOf(program.Text(memberName)));
            ir.Block = next;
        }

        var flags = type.Assembly.FindAttributes(type, FlagsAttribute).Count > 0;
        var number = function.AddBlock("number");
        if (!flags)
        {
            ir.Jump(number);
        }
        else
        {
            Flags(function, ir, value, members, write, number);
        }

        ir.Block = number;
        if (write)
        {
            ir.Call(program.Runtime(signed ? "WriteInt64" : "WriteUInt64"), value);
            ir.Return();
        }
        else
        {
            ir.Return(ir.Call(program.RuntimeFunction(signed ? "StringOfInt64" : "StringOfUInt64", TirType.Ref,
                TirType.I64, TirType.I32), value, ir.Const(TirType.I32, 0)));
        }

        return function;
    }

    /// <summary>
    /// A [Flags] enum's value that no member has: 0 is its number; else the members are taken from the largest value
    /// down (as unsigned numbers), then written (or put together) from the smallest up, or the number is when bits are
    /// left. Ends every way it does not finish at <paramref name="number"/>.
    /// </summary>
    private void Flags(TirFunction function, TirBuilder ir, TirValue value, List<(long Value, ulong Bits, string Name)> members,
        bool write, TirBlock number)
    {
        var candidates = members.Where(member => member.Bits != 0).OrderBy(member => member.Bits).ToList();
        var nonzero = function.AddBlock("flags");
        ir.Branch(ir.Compare(TirCompareOp.Eq, value, ir.Const(TirType.I64, 0)), number, nonzero);
        ir.Block = nonzero;

        var left = function.AddSlot("left", TirType.I64);
        ir.Store(left, value);
        var taken = new TirSlot[candidates.Count];
        for (var k = candidates.Count - 1; k >= 0; k--)
        {
            var member = ir.Const(TirType.I64, candidates[k].Value);
            var rest = ir.Load(left);
            var all = ir.Compare(TirCompareOp.Eq, ir.Binary(TirBinaryOp.And, rest, member), member);
            taken[k] = function.AddSlot(Numbered("is", k), TirType.Bool);
            ir.Store(taken[k], all);

            // left -= member when all its bits are there: the member masked by all ones or by zero
            var mask = ir.Binary(TirBinaryOp.Sub, ir.Const(TirType.I64, 0), ir.Convert(all, TirType.I64));
            ir.Store(left, ir.Binary(TirBinaryOp.Sub, rest, ir.Binary(TirBinaryOp.And, member, mask)));
        }

        var names = function.AddBlock("names");
        ir.Branch(ir.Compare(TirCompareOp.Ne, ir.Load(left), ir.Const(TirType.I64, 0)), number, names);
        ir.Block = names;

        var any = function.AddSlot("any", TirType.Bool);
        ir.Store(any, ir.Const(false));
        var parts = write ? null : function.AddSlot("parts", new TirArrayType(TirType.Ref, 2 * candidates.Count));
        for (var k = 0; k < candidates.Count; k++)
        {
            var take = function.AddBlock(Numbered("take", k));
            var separator = function.AddBlock(Numbered("separator", k));
            var member = function.AddBlock(Numbered("name", k));
            var next = function.AddBlock(Numbered("taken", k));
            ir.Branch(ir.Load(taken[k]), take, next);

            ir.Block = take;
            ir.Branch(ir.Load(any), separator, member);

            ir.Block = separator;
            Part(ir, parts, 2 * k, ir.AddressOf(program.Text(", ")));
            ir.Jump(member);

            ir.Block = member;
            Part(ir, parts, 2 * k + 1, ir.AddressOf(program.Text(candidates[k].Name)));
            ir.Store(any, ir.Const(true));
            ir.Jump(next);

            ir.Block = next;
        }

        if (parts is null)
        {
            ir.Return();
            return;
        }

        var concat = program.RuntimeFunction("StringConcat", TirType.Ref, TirType.Ptr, TirType.I32);
        ir.Return(ir.Call(concat, ir.AddressOf(parts), ir.Const(TirType.I32, 2 * candidates.Count)));
    }

    /// <summary>Writes a part, or puts it in element <paramref name="index"/> of <paramref name="parts"/>.</summary>
    private void Part(TirBuilder ir, TirSlot? parts, int index, TirValue text)
    {
        if (parts is null)
        {
            ir.Call(program.Runtime("WriteString"), text);
            return;
        }

        ir.Store(ir.ElementAddress(ir.AddressOf(parts), TirType.Ref, ir.Const(TirType.I32, index)), text);
    }

    /// <summary>Ends the block with the name: written (then returns), or returned.</summary>
    private void Finish(TirBuilder ir, bool write, TirValue text)
    {
        if (write)
        {
            ir.Call(program.Runtime("WriteString"), text);
            ir.Return();
        }
        else
        {
            ir.Return(text);
        }
    }

    private static string Numbered(string name, int n) => string.Create(CultureInfo.InvariantCulture, $"{name}{n}");

    /// <summary>The raw bits of a member, <paramref name="bits"/> wide, as the i64 the function is given.</summary>
    private static long Extend(ulong raw, int bits, bool signed) =>
        signed && bits < 64 ? (long)(raw << (64 - bits)) >> (64 - bits) : (long)raw;
}
