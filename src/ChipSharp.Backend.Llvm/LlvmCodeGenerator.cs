using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// The "llvm" code generator: writes a TIR module as LLVM IR text, one .ll file that clang compiles for any
/// target LLVM knows (PC, ARM, RISC-V, ...).
/// Each function becomes a symbol made from its display name (see <see cref="SymbolName"/>). External functions
/// keep exactly that symbol, since the runtime implements them under it; a defined function whose symbol is
/// already taken gets _2, _3, ... after it. Each slot becomes an alloca that starts at zero, in a block of its own
/// in front of the TIR entry block. <see cref="EntrySymbol"/> calls the module's entry point and returns its int
/// result (0 when it returns nothing); the runtime's startup code calls it.
/// </summary>
public sealed class LlvmCodeGenerator : ICodeGenerator
{
    /// <summary>The function the runtime calls to run the program, as C sees it: <c>int chipsharp_entry(void)</c>.</summary>
    public const string EntrySymbol = "chipsharp_entry";

    public string Name => "llvm";

    /// <summary>
    /// Verifies the module (a <see cref="TirException"/> lists everything wrong with it), then writes one file,
    /// "&lt;module name&gt;.ll", with the target's triple when it has one.
    /// </summary>
    public GeneratedCode Generate(TirModule module, TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(target);
        TirVerifier.Verify(module);

        var types = LlvmTypes.For(target);
        var symbols = AssignSymbols(module);
        var entry = module.EntryPoint is { } entryPoint ? EntryCall(entryPoint, symbols[entryPoint]) : null;

        var text = new StringBuilder();
        text.Append("source_filename = ").Append(LlvmText.Quoted(module.Name)).Append('\n');
        if (!string.IsNullOrEmpty(target.Triple))
        {
            text.Append("target triple = ").Append(LlvmText.Quoted(target.Triple)).Append('\n');
        }

        var externals = module.Functions.Where(f => f.Kind == TirFunctionKind.External).ToList();
        if (externals.Count > 0)
        {
            text.Append('\n');
        }

        foreach (var function in externals)
        {
            var parameters = string.Join(", ", function.Parameters.Select(p => types.Of(p.Type)));
            text.Append($"declare {types.Of(function.ReturnType)} @{symbols[function]}({parameters}) nounwind\n");
        }

        foreach (var function in module.Functions.Where(f => f.Kind == TirFunctionKind.Defined))
        {
            text.Append('\n');
            new LlvmFunctionWriter(function, types, symbols, text).Write();
        }

        if (entry is not null)
        {
            text.Append('\n').Append(entry);
        }

        return new GeneratedCode([new GeneratedFile(FileName(module.Name), text.ToString())]);
    }

    /// <summary>
    /// The symbol a function's display name becomes before any clash is resolved: every character outside
    /// <c>[A-Za-z0-9_]</c> becomes <c>_</c>, and a name that starts with a digit gets a <c>_</c> in front.
    /// "ChipSharp.Runtime.WriteLineInt32" becomes "ChipSharp_Runtime_WriteLineInt32".
    /// </summary>
    public static string SymbolName(string displayName)
    {
        ArgumentException.ThrowIfNullOrEmpty(displayName);
        return LlvmText.Name(displayName);
    }

    /// <summary>
    /// External functions first, in module order: they must keep their symbol, so two that clash are an error.
    /// Then defined functions, in module order, each taking the first free symbol of name, name_2, name_3, ...
    /// <see cref="EntrySymbol"/> is reserved.
    /// </summary>
    private static Dictionary<TirFunction, string> AssignSymbols(TirModule module)
    {
        var symbols = new Dictionary<TirFunction, string>();
        var owners = new Dictionary<string, TirFunction?>(StringComparer.Ordinal) { [EntrySymbol] = null };

        foreach (var function in module.Functions.Where(f => f.Kind == TirFunctionKind.External))
        {
            var symbol = SymbolName(function.Name);
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
            symbols[function] = symbol;
        }

        foreach (var function in module.Functions.Where(f => f.Kind == TirFunctionKind.Defined))
        {
            var wanted = SymbolName(function.Name);
            var symbol = wanted;
            for (var n = 2; owners.ContainsKey(symbol); n++)
            {
                symbol = $"{wanted}_{n}";
            }

            owners[symbol] = function;
            symbols[function] = symbol;
        }

        return symbols;
    }

    /// <summary>
    /// <c>define i32 @chipsharp_entry()</c>, which calls the entry point. The entry point must take nothing and
    /// return void or i32.
    /// </summary>
    private static string EntryCall(TirFunction entryPoint, string symbol)
    {
        if (entryPoint.Parameters.Count > 0)
        {
            throw new ChipSharpException(
                $"The entry point '{entryPoint.Name}' takes parameters; it must take none.");
        }

        var call = entryPoint.ReturnType.Kind switch
        {
            TirTypeKind.Void => $"  call void @{symbol}()\n  ret i32 0\n",
            TirTypeKind.I32 => $"  %result = call i32 @{symbol}()\n  ret i32 %result\n",
            _ => throw new ChipSharpException($"The entry point '{entryPoint.Name}' returns " +
                $"{entryPoint.ReturnType}; it must return void or i32 (int)."),
        };

        return $"define i32 @{EntrySymbol}() nounwind {{\nentry:\n{call}}}\n";
    }

    /// <summary>The module name plus ".ll", with characters that cannot be in a file name replaced by '_'.</summary>
    private static string FileName(string moduleName) =>
        new string([.. moduleName.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)]) + ".ll";
}
