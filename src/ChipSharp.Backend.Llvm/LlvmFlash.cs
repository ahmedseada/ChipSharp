using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// Constant data on a chip whose flash is an address space of its own: an AVR, where an ordinary load (LD) reads RAM
/// and only LPM reads flash, at addresses that overlap RAM's (LLVM's address space 1). The target capability
/// <see cref="Capability"/> asks for it: true by default on an AVR, false everywhere else, where flash and RAM are one
/// address space and nothing here changes what the generator writes.
/// <para>
/// <b>Where data goes.</b> Every constant global (<see cref="TirGlobal.IsConstant"/>: string literals, tables, enum
/// names, constant objects) and every type descriptor the module writes stays in flash, in section
/// <see cref="Section"/>, which the linker script puts in flash with the code: none of it is copied to RAM, which an
/// ATmega328P has 2 KB of. LLVM sees these globals in address space 0, so that an address of one written into another
/// (a string's in a constant object) is its byte address (one in address space 1 would be written as a word address,
/// as a function's is); every read of one goes through address space 1.
/// </para>
/// <para>
/// <b>Addresses.</b> TIR has one kind of address, a ptr (or a ref): one may point into RAM or flash, and a load must
/// read the right one. So an address of flash data, as a value of the program, has bit 15 set: it is
/// <c>0x8000 + its flash address</c> (<see cref="Tagged"/>); RAM addresses (below 0x0900 on an ATmega328P) and flash data
/// (below 0x8000: the flash is 32 KB, and the data comes first in it) never have it. A load then reads
///
/// <list type="bullet">
/// <item>from address space 1, with LPM, when TIR shows that its address is one of flash data
/// (<see cref="TirMemoryOf.Address"/>: the element of a table, a field of a constant object);</item>
/// <item>with LD when TIR shows that it is one of RAM (a slot, a variable, an external descriptor of the runtime's);</item>
/// <item>else, when the address may be one of flash data (<see cref="LlvmFlashFlow"/> follows where they go), through a
/// small function of the module, <see cref="Read"/>, which tests bit 15 and reads one way or the other (a few
/// instructions more than a plain read); with LD when it cannot be.</item>
/// </list>
/// Stores never reach flash: nothing writes a constant global. Comparing addresses and turning them into numbers and
/// back works as before, as each address has one value. The runtime gets addresses so too: a pointer or ref the
/// program passes it (a string, a span's data, a message) may have bit 15 set, and then points to flash at
/// <c>address &amp; 0x7FFF</c>, read with LPM; what the runtime makes itself, and the addresses of its own variables and
/// constants (a string's type descriptor), are ordinary RAM addresses.
/// </para>
/// <para>
/// A program for such a chip makes no objects for now: the heap and its garbage collector read objects and descriptors
/// with plain loads (see <see cref="CheckTarget"/>).
/// </para>
/// </summary>
internal sealed class LlvmFlash
{
    /// <summary>The target capability: true when flash is an address space of its own (an AVR's), false when it is not.</summary>
    public const string Capability = "separateFlash";

    /// <summary>The section of flash data, the one LLVM gives data in address space 1 on an AVR.</summary>
    public const string Section = ".progmem.data";

    /// <summary>The symbols of the functions that read through an address that may be either (<see cref="Read"/>).</summary>
    private readonly SortedDictionary<string, string> _readers = new(StringComparer.Ordinal);

    private readonly Dictionary<(string Type, bool IsVolatile), string> _readerSymbols = [];
    private readonly LlvmTypes _types;
    private LlvmFlashFlow? _flow;

    private LlvmFlash(LlvmTypes types, bool isEnabled)
    {
        _types = types;
        IsEnabled = isEnabled;
    }

    /// <summary>True when the target's flash is an address space of its own, and constant data stays there.</summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// What <paramref name="target"/> does with constant data: <see cref="Capability"/> if its file says (true only on an
    /// AVR), else true on an AVR.
    /// </summary>
    public static LlvmFlash For(TargetDescription target, LlvmTypes types)
    {
        var isAvr = LlvmTypes.IsAvr(target);
        var enabled = target.Capabilities.TryGetValue(Capability, out var value)
            ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            : isAvr;
        if (enabled && !isAvr)
        {
            throw new ChipSharpException($"Target '{target.Name}' says its flash is an address space of its own " +
                $"(\"{Capability}\": true); the llvm code generator knows that only of an AVR (triple avr).");
        }

        return new LlvmFlash(types, enabled);
    }

    /// <summary>Throws when the module makes objects for a chip whose constant data stays in flash: not supported yet.</summary>
    public void CheckTarget(TirModule module, TargetDescription target)
    {
        if (IsEnabled && LlvmHeap.UsesHeap(module))
        {
            throw new ChipSharpException($"The program makes objects, and the heap is not supported yet on target " +
                $"'{target.Name}', whose flash is an address space of its own (an AVR's): the garbage collector would " +
                "read objects and type descriptors in flash as if they were in RAM. Use structs and static methods.");
        }
    }

    /// <summary>Finds which values of <paramref name="module"/> may hold an address of flash data (see <see cref="MayBeFlash"/>).</summary>
    public void Follow(TirModule module)
    {
        if (IsEnabled)
        {
            _flow = LlvmFlashFlow.Analyze(module, MemoryOf);
        }
    }

    /// <summary>
    /// Where <paramref name="address"/> points, for the load that reads through it: <see cref="TirMemory.Constants"/>
    /// for flash data, <see cref="TirMemory.Unknown"/> for an address that may be RAM's or flash's, when
    /// <see cref="IsEnabled"/>; <see cref="TirMemory.Variables"/> (an ordinary load reads it) for RAM, for a descriptor
    /// of the runtime's (in RAM too), and for everything on a target with one address space.
    /// </summary>
    public TirMemory MemoryOf(TirValue address)
    {
        if (!IsEnabled)
        {
            return TirMemory.Variables;
        }

        return TirMemoryOf.Root(address) is TirTypeAddress { TypeDescriptor.ExternalSymbol: not null }
            ? TirMemory.Variables
            : TirMemoryOf.Address(address);
    }

    /// <summary>
    /// True when <paramref name="value"/> may hold an address of flash data, as <see cref="Follow"/> found: a load
    /// through an address of <see cref="TirMemory.Unknown"/> memory that may not be flash's reads RAM.
    /// </summary>
    public bool MayBeFlash(TirValue value) => _flow?.MayBeFlash(value) ?? false;

    /// <summary>True for a global that stays in flash: a constant one, when <see cref="IsEnabled"/>.</summary>
    public bool InFlash(TirGlobal global) => IsEnabled && global.IsConstant;

    /// <summary>True for a type descriptor the module writes (not one of the runtime's), when <see cref="IsEnabled"/>.</summary>
    public bool InFlash(TirTypeDescriptor type) => IsEnabled && type.ExternalSymbol is null;

    /// <summary>What a global's definition says after its value: <c>, section ".progmem.data"</c> in flash, else nothing.</summary>
    public static string SectionOf(bool inFlash) => inFlash ? $", section {LlvmText.Quoted(Section)}" : "";

    /// <summary>
    /// The address of the global <paramref name="symbol"/> as a value of the program, a ptr constant: <c>@name</c>, or,
    /// for one in flash, <see cref="Tagged"/>.
    /// </summary>
    public string Address(string symbol, bool inFlash) => inFlash ? Tagged("@" + symbol) : "@" + symbol;

    /// <summary>
    /// <c>getelementptr (i8, ptr @name, i16 -32768)</c>: the address of flash data, a ptr constant, with bit 15 set
    /// (the address plus 0x8000, which wraps around 16 bits).
    /// </summary>
    public string Tagged(string address) =>
        $"getelementptr (i8, ptr {address}, {PointerInteger} {LlvmText.Integer(Tag)})";

    /// <summary>The flash address of the global <paramref name="symbol"/>, a <c>ptr addrspace(1)</c> constant, for LPM.</summary>
    public static string FlashAddress(string symbol) => $"addrspacecast (ptr @{symbol} to ptr addrspace(1))";

    /// <summary>
    /// The symbol of the module's function that reads a <paramref name="type"/> at an address that may be in RAM or in
    /// flash: it tests bit 15 of the address, and reads flash (with the bit cleared) or RAM. Written once per type
    /// (and per volatile or not), see <see cref="Definitions"/>.
    /// </summary>
    public string Read(TirType type, bool isVolatile)
    {
        var typeText = _types.Of(type);
        if (_readerSymbols.TryGetValue((typeText, isVolatile), out var symbol))
        {
            return symbol;
        }

        var wanted = "chipsharp.read." + (isVolatile ? "volatile." : "") + LlvmText.Name(typeText);
        symbol = wanted;
        for (var n = 2; _readers.ContainsKey(symbol); n++)
        {
            symbol = wanted + "_" + n.ToString(CultureInfo.InvariantCulture);
        }

        var align = _types.Alignment(type).ToString(CultureInfo.InvariantCulture);
        var load = isVolatile ? "load volatile" : "load";
        var integer = PointerInteger;
        _readers[symbol] = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"define internal {typeText} @{symbol}(ptr %address) nounwind {{\n")
            .Append("entry:\n")
            .Append(CultureInfo.InvariantCulture, $"  %bits = ptrtoint ptr %address to {integer}\n")
            .Append(CultureInfo.InvariantCulture, $"  %in.flash = icmp slt {integer} %bits, 0\n")
            .Append("  br i1 %in.flash, label %flash, label %ram\n\n")
            .Append("flash:\n")
            .Append("  %tagged = addrspacecast ptr %address to ptr addrspace(1)\n")
            .Append(CultureInfo.InvariantCulture,
                $"  %flash.address = getelementptr i8, ptr addrspace(1) %tagged, {integer} {LlvmText.Integer(Tag)}\n")
            .Append(CultureInfo.InvariantCulture,
                $"  %flash.value = {load} {typeText}, ptr addrspace(1) %flash.address, align {align}\n")
            .Append(CultureInfo.InvariantCulture, $"  ret {typeText} %flash.value\n\n")
            .Append("ram:\n")
            .Append(CultureInfo.InvariantCulture, $"  %ram.value = {load} {typeText}, ptr %address, align {align}\n")
            .Append(CultureInfo.InvariantCulture, $"  ret {typeText} %ram.value\n")
            .Append('}')
            .ToString();
        _readerSymbols[(typeText, isVolatile)] = symbol;
        return symbol;
    }

    /// <summary>The definitions of the functions <see cref="Read"/> named, in order of their symbols.</summary>
    public IEnumerable<string> Definitions => _readers.Values;

    /// <summary>The integer as wide as an address: i16 on an AVR.</summary>
    private string PointerInteger => _types.Of(TirType.NativeInt);

    /// <summary>The bit that marks an address of flash data, as the signed number that adds it: -32768 with 16-bit addresses.</summary>
    private long Tag => -(1L << (_types.Bits(TirType.Ptr) - 1));
}
