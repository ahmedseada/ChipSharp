using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// Exported functions (<see cref="TirFunction.ExportName"/>), such as an interrupt handler's entry, which the runtime's
/// vector table names: each keeps exactly its symbol, and is defined without <c>internal</c>, so that the linker puts
/// it in place of the runtime's weak default of that name.
/// </summary>
internal static class LlvmExports
{
    /// <summary>
    /// Gives every exported function its symbol. Like an external function's, it is fixed, so a clash with a symbol
    /// in <paramref name="owners"/> (the external functions' and <see cref="LlvmCodeGenerator.EntrySymbol"/>) is an error.
    /// </summary>
    public static void Assign(TirModule module, Dictionary<string, TirFunction?> owners, Dictionary<TirFunction, string> symbols)
    {
        foreach (var function in module.Functions.Where(f => f is { Kind: TirFunctionKind.Defined, ExportName: not null }))
        {
            var symbol = function.ExportName!;
            if (owners.TryGetValue(symbol, out var owner))
            {
                throw new ChipSharpException(owner is null
                    ? $"Function '{function.Name}' is exported as '{symbol}', which ChipSharp keeps for the program's entry."
                    : $"Function '{function.Name}' is exported as '{symbol}', which is also the symbol of '{owner.Name}'.");
            }

            owners[symbol] = function;
            symbols[function] = symbol;
        }
    }

    /// <summary>The linkage a defined function is written with: "internal " unless it is exported.</summary>
    public static string Linkage(TirFunction function) => function.ExportName is null ? "internal " : "";
}
