using System.Globalization;
using System.Text;

namespace ChipSharp.Ir;

/// <summary>
/// Writes TIR as stable, readable text for tests and dumps. Lines end with "\n" on every platform.
/// <code>
/// function i32 Program.Square(i32 %n)
/// entry:
///   %0 = mul i32 %n, %n
///   return %0
/// </code>
/// Temporaries are numbered %0, %1, ... per function, in block order; parameters print as %name,
/// slots as $name, globals as @name, blocks by name. A value that does not belong to the function prints as %?.
/// A module lists the structs it uses (<c>struct Program.Point { i32 X, i32 Y }</c>) and its globals
/// (<c>global i32 @Program.counter = 5</c>, <c>constant [2 x i8] @&lt;text 0&gt; = c"A\00"</c>) before its
/// functions. Memory at an address is written in brackets: <c>%1 = load i32 [%0]</c>,
/// <c>store volatile i32 [%0], %1</c>. An exported function says its symbol after its signature:
/// <c>function void &lt;Interrupt timer&gt;() export chipsharp_isr_irq1</c>. The type descriptors of objects on the
/// heap come after the globals: <c>type Program.Node = class Program.Node : System.Object</c> (its object type, then
/// its base, its virtual methods <c>virtual [Program.Node.Run]</c> and its interfaces <c>Program.IRun [Program.Node.Run]</c>),
/// <c>type int[] = array of i32</c>, <c>type Program.IRun = interface</c>; instructions name them:
/// <c>%0 = newobject Program.Node</c>, <c>%2 = newarray int[], %1</c>. A call through an address (a method table's) is
/// <c>%3 = call indirect i32 %2(%0, %1)</c>.
/// </summary>
public static class TirPrinter
{
    public static string Print(TirModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var text = new StringBuilder();
        text.Append("module ").Append(module.Name).Append('\n');
        if (module.EntryPoint is { } entryPoint)
        {
            text.Append("entrypoint ").Append(entryPoint.Name).Append('\n');
        }

        var structs = module.FindStructTypes();
        if (structs.Count > 0 || module.Globals.Count > 0 || module.TypeDescriptors.Count > 0)
        {
            text.Append('\n');
        }

        foreach (var structType in structs)
        {
            text.Append(StructText(structType)).Append('\n');
        }

        foreach (var global in module.Globals)
        {
            text.Append(GlobalText(global)).Append('\n');
        }

        foreach (var type in module.TypeDescriptors)
        {
            text.Append(TypeText(type)).Append('\n');
        }

        foreach (var function in module.Functions)
        {
            text.Append('\n');
            Write(text, function);
        }

        return text.ToString();
    }

    public static string Print(TirFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);

        var text = new StringBuilder();
        Write(text, function);
        return text.ToString();
    }

    /// <summary>One instruction without indentation, e.g. "%2 = add i32 %0, %1", numbered as in its function.</summary>
    public static string Print(TirInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return Print(instruction, new TirNumbering(instruction.Block?.Function));
    }

    internal static string Print(TirInstruction instruction, TirNumbering names)
    {
        var body = instruction switch
        {
            TirConst c => $"const {c.Type} {ConstantText(c)}",
            TirBinary b => $"{Name(b.Op)} {b.Type} {names.Of(b.Left)}, {names.Of(b.Right)}",
            TirCompare c => $"{Name(c.Op)} {c.Left.Type} {names.Of(c.Left)}, {names.Of(c.Right)}",
            TirConvert c => $"{(c.IsUnsigned ? "uconvert" : "convert")} {c.Value.Type} {names.Of(c.Value)} to {c.Type}",
            TirLoad l => $"load {l.Type} {l.Slot}",
            TirStore s => $"store {s.Slot}, {names.Of(s.Value)}",
            TirZero z => $"zero {z.Type}",
            TirSizeOf s => $"sizeof {s.OfType}",
            TirSlotAddress a => $"addr {a.Slot}",
            TirGlobalAddress a => $"addr {a.Global}",
            TirPointerLoad l => $"load {Volatile(l.IsVolatile)}{Unaligned(l.IsUnaligned)}{l.Type} [{names.Of(l.Address)}]",
            TirPointerStore s => $"store {Volatile(s.IsVolatile)}{Unaligned(s.IsUnaligned)}{s.Value.Type} " +
                $"[{names.Of(s.Address)}], {names.Of(s.Value)}",
            TirFieldAddress f => $"fieldaddr {f.StructType} {names.Of(f.Address)}, {FieldText(f)}",
            TirElementAddress e => $"elementaddr {e.ElementType} {names.Of(e.Address)}, {names.Of(e.Index)}",
            TirPtrToInt p => $"ptrtoint {p.Address.Type} {names.Of(p.Address)} to {p.Type}",
            TirIntToPtr p => $"inttoptr {p.Value.Type} {names.Of(p.Value)} to {p.Type}",
            TirNewObject n => $"newobject {n.TypeDescriptor}",
            TirNewArray n => $"newarray {n.TypeDescriptor}, {names.Of(n.Length)}",
            TirNullCheck n => $"nullcheck {names.Of(n.Reference)}",
            TirArrayLength l => $"arraylength {names.Of(l.Array)}",
            TirArrayData d => $"arraydata {d.ElementType} {names.Of(d.Array)}",
            TirArrayObject o => $"arrayobject {names.Of(o.Data)}",
            TirTypeOf t => $"typeof {names.Of(t.Reference)}",
            TirTypeAddress a => $"typeaddr {a.TypeDescriptor}",
            TirFunctionAddress a => $"funcaddr {a.Function.Name}",
            TirCall c => $"call {c.Type} {c.Callee.Name}({string.Join(", ", c.Arguments.Select(names.Of))})",
            TirCallIndirect c =>
                $"call indirect {c.Type} {names.Of(c.Callee)}({string.Join(", ", c.Arguments.Select(names.Of))})",
            TirReturn { Value: null } => "return",
            TirReturn r => $"return {names.Of(r.Value!)}",
            TirJump j => $"jump {j.Target.Name}",
            TirBranch b => $"branch {names.Of(b.Condition)}, {b.WhenTrue.Name}, {b.WhenFalse.Name}",
            _ => instruction.GetType().Name,
        };

        return instruction.HasResult ? $"{names.Of(instruction)} = {body}" : body;
    }

    internal static string Name(TirBinaryOp op) => op switch
    {
        TirBinaryOp.Add => "add",
        TirBinaryOp.Sub => "sub",
        TirBinaryOp.Mul => "mul",
        TirBinaryOp.Div => "div",
        TirBinaryOp.DivUnsigned => "udiv",
        TirBinaryOp.Rem => "rem",
        TirBinaryOp.RemUnsigned => "urem",
        TirBinaryOp.And => "and",
        TirBinaryOp.Or => "or",
        TirBinaryOp.Xor => "xor",
        TirBinaryOp.Shl => "shl",
        TirBinaryOp.Shr => "shr",
        TirBinaryOp.ShrUnsigned => "ushr",
        _ => op.ToString(),
    };

    internal static string Name(TirCompareOp op) => op switch
    {
        TirCompareOp.Eq => "eq",
        TirCompareOp.Ne => "ne",
        TirCompareOp.Lt => "lt",
        TirCompareOp.Le => "le",
        TirCompareOp.Gt => "gt",
        TirCompareOp.Ge => "ge",
        TirCompareOp.LtUnsigned => "ult",
        TirCompareOp.LeUnsigned => "ule",
        TirCompareOp.GtUnsigned => "ugt",
        TirCompareOp.GeUnsigned => "uge",
        _ => op.ToString(),
    };

    private static void Write(StringBuilder text, TirFunction function)
    {
        var names = new TirNumbering(function);
        var parameters = string.Join(", ", function.Parameters.Select(p => $"{p.Type} {p}"));
        if (function.Kind == TirFunctionKind.External)
        {
            text.Append("external ");
        }

        var export = function.ExportName is { } symbol ? $" export {symbol}" : "";
        text.Append($"function {function.ReturnType} {function.Name}({parameters}){export}\n");
        foreach (var slot in function.Slots)
        {
            text.Append($"  {slot}: {slot.Type}\n");
        }

        foreach (var block in function.Blocks)
        {
            text.Append(block.Name).Append(":\n");
            foreach (var instruction in block.Instructions)
            {
                text.Append("  ").Append(Print(instruction, names)).Append('\n');
            }
        }
    }

    private static string Volatile(bool isVolatile) => isVolatile ? "volatile " : "";

    private static string Unaligned(bool isUnaligned) => isUnaligned ? "unaligned " : "";

    /// <summary>The field's name, or #index when the index is out of range (broken TIR still prints).</summary>
    private static string FieldText(TirFieldAddress f) =>
        f.Field?.Name ?? string.Create(CultureInfo.InvariantCulture, $"#{f.FieldIndex}");

    /// <summary><c>struct Program.Point { i32 X, i32 Y }</c></summary>
    private static string StructText(TirStructType structType) =>
        $"struct {structType.Name} {{ {string.Join(", ", structType.Fields)} }}";

    /// <summary>
    /// <c>global i32 @Program.counter</c>, or <c>global i32 @Program.limit = 10</c> with an initial value;
    /// <c>constant [2 x i8] @&lt;text 0&gt; = c"A\00"</c> for read-only data; an array with its elements,
    /// <c>constant [3 x i8] @Program.Digits = [63, 6, 91]</c>.
    /// </summary>
    private static string GlobalText(TirGlobal global) => global switch
    {
        { ObjectType: { } type, Data: { } bytes } =>
            $"constant object {type.Name} {global} = {global.ObjectLength}, {BytesText(bytes)}",
        { Data: { } data } => $"constant {global.Type} {global} = {BytesText(data)}",
        { Header: { } header } => $"constant {global.Type} {global} = object {header}",
        { Elements: { } elements } => $"{(global.IsConstant ? "constant" : "global")} {global.Type} {global} = " +
            $"[{string.Join(", ", elements.Select(ConstantText))}]",
        { IsConstant: true } => $"constant {global.Type} {global}",
        { InitialValue: { } initial } => $"global {global.Type} {global} = {ConstantText(initial)}",
        _ => $"global {global.Type} {global}",
    };

    /// <summary>
    /// <c>type Program.Node = class Program.Node : System.Object virtual [Program.Node.Run] Program.IRun [Program.Node.Run]</c>,
    /// <c>type Program.Node[] = array of ref Program.Node</c>, <c>type Program.IRun = interface</c>; an empty slot of a
    /// method table prints as <c>-</c>.
    /// </summary>
    private static string TypeText(TirTypeDescriptor type)
    {
        static string Methods(IEnumerable<TirFunction?> methods) =>
            $"[{string.Join(", ", methods.Select(method => method?.Name ?? "-"))}]";

        var text = new StringBuilder($"type {type.Name} = ");
        switch (type.Kind)
        {
            case TirTypeDescriptorKind.Class:
                text.Append("class ").Append(type.ObjectType!.Name);
                if (type.Base is { } baseType)
                {
                    text.Append(" : ").Append(baseType.Name);
                }

                if (type.VirtualMethods.Count > 0)
                {
                    text.Append(" virtual ").Append(Methods(type.VirtualMethods));
                }

                foreach (var implemented in type.Interfaces)
                {
                    text.Append(' ').Append(implemented.Interface.Name).Append(' ').Append(Methods(implemented.Methods));
                }

                break;

            case TirTypeDescriptorKind.Array:
                text.Append("array of ").Append(type.ElementType);
                if (type.ElementDescriptor is { } element)
                {
                    text.Append(' ').Append(element.Name);
                }

                break;

            default:
                text.Append("interface");
                break;
        }

        if (type.ExternalSymbol is { } symbol)
        {
            text.Append(" in the runtime as ").Append(symbol);
        }

        return text.ToString();
    }

    /// <summary>Bytes as <c>c"..."</c>: printable ASCII as it is, any other byte (and " and \) as \XX in hex.</summary>
    private static string BytesText(IReadOnlyList<byte> data)
    {
        var text = new StringBuilder("c\"");
        foreach (var b in data)
        {
            if (b is >= 0x20 and < 0x7F && b != '"' && b != '\\')
            {
                text.Append((char)b);
            }
            else
            {
                text.Append('\\').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return text.Append('"').ToString();
    }

    private static string ConstantText(TirConst c) => c.Type.Kind switch
    {
        TirTypeKind.Bool => c.IntegerValue != 0 ? "true" : "false",
        TirTypeKind.F32 => ((float)c.FloatValue).ToString("R", CultureInfo.InvariantCulture),
        TirTypeKind.F64 => c.FloatValue.ToString("R", CultureInfo.InvariantCulture),
        _ => c.IntegerValue.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// The printed names of one function's values: %0, %1, ... for temporaries in block order, %name for
/// parameters, %? for anything that is not in the function (so broken TIR still prints).
/// </summary>
internal sealed class TirNumbering
{
    private readonly Dictionary<TirInstruction, int> _numbers = [];

    public TirNumbering(TirFunction? function)
    {
        foreach (var instruction in function?.Blocks.SelectMany(b => b.Instructions) ?? [])
        {
            if (instruction.HasResult)
            {
                _numbers[instruction] = _numbers.Count;
            }
        }
    }

    public string Of(TirValue value) => value switch
    {
        TirParameter parameter => parameter.ToString(),
        TirInstruction instruction when _numbers.TryGetValue(instruction, out var number) =>
            string.Create(CultureInfo.InvariantCulture, $"%{number}"),
        _ => "%?",
    };
}
