using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// The LLVM type of each TIR type for one target: bool is i1, i8 to i64 stay, f32 is float, f64 is double,
/// nint is i32 or i64 depending on the target's pointer size, ptr and ref are ptr, an array is <c>[4 x i32]</c>, and a
/// struct is a named LLVM struct type, <c>%struct.Program_Point</c>, declared once per module
/// (see <see cref="NameStructs"/>). LLVM lays these out exactly as <see cref="TirLayout"/> does: every type is
/// naturally aligned on the targets the generator supports.
/// </summary>
internal sealed class LlvmTypes
{
    private readonly int _pointerBits;
    private readonly Dictionary<TirStructType, string> _structs = [];

    private LlvmTypes(int pointerSize)
    {
        _pointerBits = pointerSize * 8;
        Layout = new TirLayout(pointerSize);
    }

    /// <summary>Sizes, alignments and field offsets on the target.</summary>
    public TirLayout Layout { get; }

    public static LlvmTypes For(TargetDescription target) => target.PointerSize is 4 or 8
        ? new LlvmTypes(target.PointerSize)
        : throw new ChipSharpException(
            $"Target '{target.Name}' has {target.PointerSize}-byte pointers; the llvm code generator supports " +
            "4-byte and 8-byte pointers only.");

    /// <summary>
    /// Gives each struct type the module uses its LLVM name, <c>%struct.</c> and the struct's name made LLVM-safe
    /// like a symbol (<c>%struct.Program_Point</c>), with _2, _3, ... after it when two structs would get the same
    /// name, and returns their definitions in order, e.g. <c>%struct.Program_Point = type { i32, i32 }</c>.
    /// </summary>
    public IReadOnlyList<string> NameStructs(TirModule module)
    {
        var structs = module.FindStructTypes();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var structType in structs)
        {
            var wanted = "%struct." + LlvmText.Name(structType.Name);
            var name = wanted;
            for (var n = 2; !taken.Add(name); n++)
            {
                name = wanted + "_" + n.ToString(CultureInfo.InvariantCulture);
            }

            _structs[structType] = name;
        }

        return [.. structs.Select(s => $"{_structs[s]} = type {{ {string.Join(", ", s.Fields.Select(f => Of(f.Type)))} }}")];
    }

    public string Of(TirType type) => type switch
    {
        TirStructType structType => _structs.TryGetValue(structType, out var name)
            ? name
            : throw new ChipSharpException($"The struct type {structType} was not named for this module."),
        TirArrayType array => string.Create(CultureInfo.InvariantCulture, $"[{array.Count} x {Of(array.Element)}]"),
        _ => type.Kind switch
        {
            TirTypeKind.Void => "void",
            TirTypeKind.F32 => "float",
            TirTypeKind.F64 => "double",
            TirTypeKind.Ptr or TirTypeKind.Ref => "ptr",
            _ => "i" + Bits(type).ToString(CultureInfo.InvariantCulture),
        },
    };

    /// <summary>The width in bits of a scalar: 1 for bool, 32 for i32 and f32, the pointer width for nint and ptr.</summary>
    public int Bits(TirType type) => type.Kind switch
    {
        TirTypeKind.Bool => 1,
        TirTypeKind.I8 => 8,
        TirTypeKind.I16 => 16,
        TirTypeKind.I32 or TirTypeKind.F32 => 32,
        TirTypeKind.I64 or TirTypeKind.F64 => 64,
        TirTypeKind.NativeInt or TirTypeKind.Ptr or TirTypeKind.Ref => _pointerBits,
        _ => throw new ChipSharpException($"The llvm code generator has no bit width for the TIR type {type}."),
    };

    /// <summary>The alignment in bytes of a slot, global, load or store of the type, from <see cref="Layout"/>.</summary>
    public int Alignment(TirType type) => Layout.AlignmentOf(type);
}
