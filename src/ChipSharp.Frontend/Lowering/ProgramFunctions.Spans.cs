using System.Buffers.Binary;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Spans (see <see cref="Spans"/>): the functions they call, whether the target reads misaligned values, and the
/// constant data C# made spans of, one constant global per field and element type, named after the field.
/// <code>
/// constant [3 x i32] @&lt;PrivateImplementationDetails&gt;.97CA1592... = [10, 20, 30]
/// </code>
/// </summary>
internal sealed partial class ProgramFunctions
{
    private readonly Dictionary<(IlField Field, TirType? Element), TirGlobal> _spanData = [];
    private SpanFunctions? _spans;

    /// <summary>The functions spans use, shared by every method.</summary>
    public SpanFunctions Spans => _spans ??= new SpanFunctions(module, Arrays);

    /// <summary>
    /// True when the target reads and writes a value at any address (a PC); false for one that faults on a misaligned
    /// word (a Cortex-M0+), where MemoryMarshal.Cast checks the span it gives (see <see cref="SpanFunctions.Cast"/>).
    /// </summary>
    public bool UnalignedAccess { get; init; } = true;

    /// <summary>
    /// The constant global with the data of <paramref name="field"/> as <paramref name="count"/> elements of
    /// <paramref name="element"/> (a TIR element type: constant data is little-endian, as C# writes it), or as its bytes
    /// when <paramref name="element"/> is null; null when the field has less data than that.
    /// </summary>
    public TirGlobal? SpanData(IlField field, TirType? element, int count)
    {
        if (_spanData.TryGetValue((field, element), out var global))
        {
            return global;
        }

        var size = element is null ? 1 : Frontend.Spans.DataElementSize(element);
        if (size == 0 || count < 0 || count > int.MaxValue / size
            || field.DeclaringType.Assembly.ReadFieldData(field, count * size) is not { } bytes)
        {
            return null;
        }

        var name = GlobalName($"{field.DeclaringType.FullName}.{field.Name}");
        global = element is null
            ? module.AddConstant(name, bytes)
            : module.AddArray(name, new TirArrayType(element, Math.Max(count, 1)), Elements(bytes, element, count),
                isConstant: true);
        _spanData.Add((field, element), global);
        return global;
    }

    private static TirConst[] Elements(byte[] bytes, TirType element, int count)
    {
        var size = Frontend.Spans.DataElementSize(element);
        var elements = new TirConst[Math.Max(count, 1)];
        for (var i = 0; i < elements.Length; i++)
        {
            var value = i < count ? bytes.AsSpan(i * size, size) : new byte[size];
            elements[i] = element.Kind switch
            {
                TirTypeKind.I8 => new TirConst(element, (sbyte)value[0]),
                TirTypeKind.I16 => new TirConst(element, BinaryPrimitives.ReadInt16LittleEndian(value)),
                TirTypeKind.I32 => new TirConst(element, BinaryPrimitives.ReadInt32LittleEndian(value)),
                TirTypeKind.I64 => new TirConst(element, BinaryPrimitives.ReadInt64LittleEndian(value)),
                TirTypeKind.F32 => new TirConst(element, (double)BinaryPrimitives.ReadSingleLittleEndian(value)),
                _ => new TirConst(element, BinaryPrimitives.ReadDoubleLittleEndian(value)),
            };
        }

        return elements;
    }
}
