using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// The LLVM type of each TIR type for one target: bool is i1, i8 to i64 stay, f32 is float, f64 is double,
/// nint is i16, i32 or i64 depending on the target's pointer size, ptr and ref are ptr, an array is <c>[4 x i32]</c>,
/// and a struct is a named LLVM struct type, <c>%struct.Program_Point</c>, declared once per module
/// (see <see cref="NameStructs"/>). LLVM lays these out exactly as <see cref="TirLayout"/> does: every type is
/// naturally aligned on the targets the generator supports, up to the most alignment of an AVR, 1, where nothing is.
/// <para>
/// An AVR (triple <c>avr</c>: 2-byte pointers, 8-bit registers) is a Harvard machine: its flash, where the code is, is
/// an address space of its own, LLVM's address space 1. The module then says so in its <see cref="DataLayout"/>
/// (<c>P1</c>), so that every function is in address space 1 and a function's address is a <c>ptr addrspace(1)</c>,
/// which <see cref="FunctionAddress"/> turns into the <c>ptr</c> TIR has (the bits do not change); C#'s int stays 32
/// bits, long and double 64 bits, as everywhere. Where constant data goes there, see <see cref="LlvmFlash"/>.
/// </para>
/// </summary>
internal sealed class LlvmTypes
{
    /// <summary>
    /// The data layout of an AVR, as LLVM 22 (the one in Zig 0.17) has it: little endian, functions in address space 1,
    /// 2-byte pointers, and every type aligned to 1 byte.
    /// </summary>
    public const string AvrDataLayout = "e-P1-p:16:8-i8:8-i16:8-i32:8-i64:8-f32:8-f64:8-n8:16-a:8";

    private readonly int _pointerBits;
    private readonly Dictionary<TirStructType, string> _structs = [];

    private LlvmTypes(int pointerSize, int? maxAlignment, string? dataLayout, int programAddressSpace)
    {
        _pointerBits = pointerSize * 8;
        Layout = new TirLayout(pointerSize, maxAlignment);
        DataLayout = dataLayout;
        ProgramAddressSpace = programAddressSpace;
    }

    /// <summary>Sizes, alignments and field offsets on the target.</summary>
    public TirLayout Layout { get; }

    /// <summary>
    /// The <c>target datalayout</c> the module says, an AVR's; null for a target whose layout clang knows from its triple
    /// alone (the module says none, as before).
    /// </summary>
    public string? DataLayout { get; }

    /// <summary>The address space of the functions: 1 on an AVR (its flash), else 0.</summary>
    public int ProgramAddressSpace { get; }

    /// <summary>The types of <paramref name="target"/>: one with 4-byte or 8-byte pointers, or an AVR (2-byte pointers).</summary>
    public static LlvmTypes For(TargetDescription target) => target.PointerSize switch
    {
        4 or 8 => new LlvmTypes(target.PointerSize, null, null, 0),
        2 when IsAvr(target) => new LlvmTypes(2, 1, AvrDataLayout, 1),
        _ => throw new ChipSharpException(
            $"Target '{target.Name}' has {target.PointerSize}-byte pointers; the llvm code generator supports " +
            "4-byte and 8-byte pointers, and the 2-byte pointers of an AVR (triple avr)."),
    };

    /// <summary>True for an AVR: a target whose triple is <c>avr</c> (or starts with <c>avr-</c>).</summary>
    public static bool IsAvr(TargetDescription target) =>
        target.Triple is { } triple && (triple == "avr" || triple.StartsWith("avr-", StringComparison.Ordinal));

    /// <summary>
    /// A defined or external function's address as a TIR ptr: <c>@f</c>, or on an AVR
    /// <c>addrspacecast (ptr addrspace(1) @f to ptr)</c>, a constant that holds the same bits (the function's word
    /// address, which an indirect call jumps to).
    /// </summary>
    public string FunctionAddress(string symbol) => ProgramAddressSpace == 0
        ? "@" + symbol
        : $"addrspacecast (ptr addrspace({ProgramAddressSpace}) @{symbol} to ptr)";

    /// <summary>The type of a function's address for a call through it: <c>ptr</c>, or <c>ptr addrspace(1)</c> on an AVR.</summary>
    public string FunctionPointer => ProgramAddressSpace == 0 ? "ptr" : $"ptr addrspace({ProgramAddressSpace})";

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
