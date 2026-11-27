using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// The "llvm" code generator: writes a TIR module as LLVM IR text, one .ll file that clang compiles for any
/// target LLVM knows (PC, ARM, RISC-V, ...).
/// Each function and global becomes a symbol made from its display name (see <see cref="SymbolName"/>). External
/// functions keep exactly that symbol, since the runtime implements them under it, and so do exported functions
/// (<see cref="TirFunction.ExportName"/>), which the runtime calls by it; another defined function or a global whose
/// symbol is already taken gets _2, _3, ... after it. A global is an <c>internal global</c> that starts at
/// zero or at its initial value; a constant global (read-only data) is a <c>private unnamed_addr constant</c>
/// array of bytes, <c>c"..."</c>; a global array starts with its elements (<c>[i32 1, i32 2]</c>), and a constant one
/// (a table) is an <c>internal unnamed_addr constant</c>, read-only data with a symbol. Each slot becomes an alloca that starts at zero, in a block of its own in front
/// of the TIR entry block. Struct types become named LLVM struct types (<c>%struct.Program_Point</c>), and every
/// alignment comes from <see cref="TirLayout"/>. <see cref="EntrySymbol"/> calls the module's entry point and
/// returns its int result (0 when it returns nothing); the runtime's startup code calls it. Objects on the heap (type
/// descriptors, newobject, nullcheck, the static roots) are written as <see cref="LlvmHeap"/> says; a module that
/// makes objects can only be generated for a target with a heap. On a chip, the functions through which the stack can
/// grow without end check it when they start (see <see cref="LlvmStackChecks"/>).
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
        LlvmHeap.CheckTarget(module, target);

        var types = LlvmTypes.For(target);
        var (symbols, globalSymbols) = AssignSymbols(module);
        var typeSymbols = LlvmHeap.AssignSymbols(module);
        var roots = LlvmHeap.RootTable(module, types, globalSymbols, out var rootCount);
        var heapStart = LlvmHeap.UsesHeap(module) ? LlvmHeap.InitializeCall(types, rootCount) : null;
        var entry = module.EntryPoint is { } entryPoint ? EntryCall(entryPoint, symbols[entryPoint], heapStart) : null;

        var text = new StringBuilder();
        text.Append("source_filename = ").Append(LlvmText.Quoted(module.Name)).Append('\n');
        if (!string.IsNullOrEmpty(target.Triple))
        {
            text.Append("target triple = ").Append(LlvmText.Quoted(target.Triple)).Append('\n');
        }

        var structs = types.NameStructs(module);
        if (structs.Count > 0)
        {
            text.Append('\n');
        }

        foreach (var structType in structs)
        {
            text.Append(structType).Append('\n');
        }

        if (module.Globals.Count > 0)
        {
            text.Append('\n');
        }

        foreach (var global in module.Globals)
        {
            // internal, like the defined functions: only this module uses it; read-only data is private (it needs no
            // symbol at all) and constant, so the linker puts it with the code (.rodata, in flash on a chip)
            var align = types.Alignment(global.Type).ToString(CultureInfo.InvariantCulture);
            if (global.ObjectType is not null)
            {
                text.Append(LlvmHeap.ConstantObject(global, globalSymbols[global], typeSymbols, types)).Append('\n');
                continue;
            }

            if (global.Data is { } data)
            {
                text.Append($"@{globalSymbols[global]} = private unnamed_addr constant {types.Of(global.Type)} " +
                    $"{LlvmText.Bytes(data)}, align {align}\n");
                continue;
            }

            // a constant array (a table) keeps its symbol, internal, and is read-only data too; so does a constant
            // object (an empty array), whose header is the address of its type descriptor
            var value = global.Header is { } header ? ObjectValue((TirStructType)global.Type, typeSymbols[header], types)
                : global.Elements is { } elements ? Elements((TirArrayType)global.Type, elements, types)
                : global.InitialValue is { } initial ? LlvmText.Constant(initial)
                : LlvmText.Zero(global.Type);
            var kind = global.IsConstant ? "unnamed_addr constant" : "global";
            text.Append($"@{globalSymbols[global]} = internal {kind} {types.Of(global.Type)} {value}, align {align}\n");
        }

        if (module.TypeDescriptors.Count > 0 || (roots is not null && heapStart is not null))
        {
            text.Append('\n');
            LlvmHeap.WriteDescriptors(text, module, types, typeSymbols, symbols);
            if (heapStart is not null)
            {
                text.Append(roots);
            }
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

        var intrinsics = new SortedSet<string>(StringComparer.Ordinal);
        var stackChecks = LlvmStackChecks.Find(module, target, types.Layout);
        var forgetRegisters = LlvmCollectCalls.ForgetRegisters(target);
        foreach (var function in module.Functions.Where(f => f.Kind == TirFunctionKind.Defined))
        {
            text.Append('\n');
            new LlvmFunctionWriter(function, types, symbols, globalSymbols, typeSymbols, text, intrinsics)
            {
                ChecksStack = stackChecks.Contains(function),
                ForgetRegisters = forgetRegisters,
            }.Write();
        }

        if (heapStart is not null)
        {
            intrinsics.Add(LlvmHeap.Declaration(LlvmHeap.InitializeSymbol, types));
        }

        // the runtime functions for objects that an external function of the module does not declare already
        var externalSymbols = externals.Select(function => symbols[function]).ToHashSet(StringComparer.Ordinal);
        intrinsics.RemoveWhere(declaration => externalSymbols.Contains(LlvmHeap.DeclaredSymbol(declaration)));

        if (entry is not null)
        {
            text.Append('\n').Append(entry);
        }

        // the LLVM intrinsics the functions call, e.g. llvm.fptosi.sat.i32.f64
        if (intrinsics.Count > 0)
        {
            text.Append('\n');
        }

        foreach (var declaration in intrinsics)
        {
            text.Append(declaration).Append('\n');
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
    /// External functions first, in module order: they must keep their symbol, so two that clash are an error; so must
    /// the exported functions, next (see <see cref="LlvmExports"/>). Then the other defined functions, then globals, in
    /// module order, each taking the first free symbol of name, name_2, name_3, ... (functions and globals share LLVM's
    /// one namespace of symbols). <see cref="EntrySymbol"/> is reserved.
    /// </summary>
    private static (Dictionary<TirFunction, string> Functions, Dictionary<TirGlobal, string> Globals) AssignSymbols(
        TirModule module)
    {
        var symbols = new Dictionary<TirFunction, string>();
        var owners = new Dictionary<string, TirFunction?>(StringComparer.Ordinal) { [EntrySymbol] = null };
        var reserved = LlvmHeap.RuntimeSymbols.ToHashSet(StringComparer.Ordinal);
        var globals = new Dictionary<TirGlobal, string>();

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

        foreach (var symbol in reserved)
        {
            owners.TryAdd(symbol, null);
        }

        LlvmExports.Assign(module, owners, symbols);
        foreach (var function in module.Functions.Where(f => f is { Kind: TirFunctionKind.Defined, ExportName: null }))
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

        foreach (var global in module.Globals)
        {
            var wanted = SymbolName(global.Name);
            var symbol = wanted;
            for (var n = 2; owners.ContainsKey(symbol); n++)
            {
                symbol = $"{wanted}_{n}";
            }

            owners[symbol] = null;
            globals[global] = symbol;
        }

        return (symbols, globals);
    }

    /// <summary>
    /// <c>define i32 @chipsharp_entry()</c>, which calls the entry point, after <paramref name="heapStart"/> (the call
    /// that sets the heap up) for a program that uses the heap. The entry point must take nothing and return void or i32.
    /// </summary>
    private static string EntryCall(TirFunction entryPoint, string symbol, string? heapStart)
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

        return $"define i32 @{EntrySymbol}() nounwind {{\nentry:\n{heapStart}{call}}}\n";
    }

    /// <summary>The elements a global array starts with, as an LLVM constant: <c>[i32 1, i32 2]</c>.</summary>
    /// <summary>A constant object: <c>{ ptr @type.int__, i32 0, ptr null }</c>, its header and then zeros.</summary>
    private static string ObjectValue(TirStructType type, string header, LlvmTypes types) =>
        $"{{ ptr @{header}{string.Concat(type.Fields.Skip(1).Select(field =>
            $", {types.Of(field.Type)} {LlvmText.Zero(field.Type)}"))} }}";

    private static string Elements(TirArrayType array, IReadOnlyList<TirConst> elements, LlvmTypes types)
    {
        var element = types.Of(array.Element);
        return $"[{string.Join(", ", elements.Select(value => $"{element} {LlvmText.Constant(value)}"))}]";
    }

    /// <summary>The module name plus ".ll", with characters that cannot be in a file name replaced by '_'.</summary>
    private static string FileName(string moduleName) =>
        new string([.. moduleName.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)]) + ".ll";
}
