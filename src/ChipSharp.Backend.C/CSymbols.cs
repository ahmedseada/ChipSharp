using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>
/// The C names of a module's functions, globals and type descriptors, made as the llvm code generator makes its symbols
/// (so that every backend calls the runtime by the same names, and a program's functions have the names a reader of
/// either finds): an external function keeps exactly <see cref="CText.Symbol"/> of its display name, which the runtime
/// defines (<c>ChipSharp_Runtime_WriteLineInt32</c>), and an exported function its export name
/// (<c>chipsharp_isr_irq1</c>); two that clash are an error. Every other function, global and descriptor is
/// <c>static</c>, so only this file sees its name: it is the display name made a C name (<see cref="CText.Identifier"/>,
/// <c>Program_Square</c>), with _2, _3, ... after it when the name is taken. A type descriptor is <c>cs_type_</c> and
/// its type's name (<c>cs_type_Program_Node</c>), unless the runtime defines it (<see cref="TirTypeDescriptor.ExternalSymbol"/>,
/// <c>chipsharp_string_type</c>). The names the generated code needs itself (<see cref="CCodeGenerator.EntrySymbol"/>, the
/// runtime's functions for objects, the C library's) are kept from the program.
/// </summary>
internal sealed class CSymbols
{
    /// <summary>The C library's functions the generated code may call: no function of the program may have their names.</summary>
    public static IReadOnlyList<string> LibrarySymbols { get; } = ["memset", "memcpy", "memmove", "fmod", "fmodf"];

    private CSymbols(Dictionary<TirFunction, string> functions, Dictionary<TirGlobal, string> globals,
        Dictionary<TirTypeDescriptor, string> types, HashSet<string> taken)
    {
        Functions = functions;
        Globals = globals;
        Types = types;
        Taken = taken;
    }

    public IReadOnlyDictionary<TirFunction, string> Functions { get; }

    public IReadOnlyDictionary<TirGlobal, string> Globals { get; }

    public IReadOnlyDictionary<TirTypeDescriptor, string> Types { get; }

    /// <summary>
    /// Every name at file scope so far (and the runtime's, the C library's and the headers'): what a name the generator
    /// makes later (a helper, a typedef, a local) must not be.
    /// </summary>
    public HashSet<string> Taken { get; }

    /// <summary>
    /// External functions first, in module order: they keep their symbol, so two that clash are an error; then the
    /// exported functions, likewise; then the other defined functions, the globals and the type descriptors, in module
    /// order, each taking the first free name.
    /// </summary>
    public static CSymbols Assign(TirModule module)
    {
        var functions = new Dictionary<TirFunction, string>();
        var owners = new Dictionary<string, TirFunction?>(StringComparer.Ordinal) { [CCodeGenerator.EntrySymbol] = null };
        foreach (var function in module.Functions.Where(f => f.Kind == TirFunctionKind.External))
        {
            var symbol = CText.Symbol(function.Name);
            if (owners.TryGetValue(symbol, out var owner))
            {
                throw new ChipSharpException(owner is null
                    ? $"External function '{function.Name}' would have the symbol '{symbol}', which ChipSharp " +
                      "keeps for the program's entry."
                    : $"External functions '{owner.Name}' and '{function.Name}' would both have the symbol " +
                      $"'{symbol}'. The runtime implements each external function under its symbol, so they " +
                      "must differ: rename one of them.");
            }

            owners[symbol] = function;
            functions[function] = symbol;
        }

        foreach (var symbol in CHeap.RuntimeSymbols.Concat(LibrarySymbols))
        {
            owners.TryAdd(symbol, null);
        }

        foreach (var function in module.Functions.Where(f => f is { Kind: TirFunctionKind.Defined, ExportName: not null }))
        {
            var symbol = function.ExportName!;
            if (owners.TryGetValue(symbol, out var owner))
            {
                throw new ChipSharpException(owner is null
                    ? $"Function '{function.Name}' is exported as '{symbol}', which ChipSharp keeps for the program's " +
                      "entry or the runtime."
                    : $"Function '{function.Name}' is exported as '{symbol}', which is also the symbol of '{owner.Name}'.");
            }

            owners[symbol] = function;
            functions[function] = symbol;
        }

        var taken = new HashSet<string>(owners.Keys, StringComparer.Ordinal);
        foreach (var type in module.TypeDescriptors.Where(type => type.ExternalSymbol is not null))
        {
            taken.Add(type.ExternalSymbol!);
        }

        foreach (var function in module.Functions.Where(f => f is { Kind: TirFunctionKind.Defined, ExportName: null }))
        {
            functions[function] = CTypes.Unique(taken, CText.Identifier(function.Name));
        }

        var globals = new Dictionary<TirGlobal, string>();
        foreach (var global in module.Globals)
        {
            globals[global] = CTypes.Unique(taken, CText.Identifier(global.Name));
        }

        var types = new Dictionary<TirTypeDescriptor, string>();
        foreach (var type in module.TypeDescriptors)
        {
            types[type] = type.ExternalSymbol ?? CTypes.Unique(taken, "cs_type_" + CText.Symbol(type.Name));
        }

        return new CSymbols(functions, globals, types, taken);
    }
}
