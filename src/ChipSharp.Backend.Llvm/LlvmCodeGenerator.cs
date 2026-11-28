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
/// grow without end check it when they start (see <see cref="LlvmStackChecks"/>). On an AVR the module says its data
/// layout (see <see cref="LlvmTypes"/>), constant data stays in flash (see <see cref="LlvmFlash"/>), and a function
/// that passes arguments on the stack has a frame pointer (see <see cref="LlvmAvrCalls"/>).
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
        var flash = LlvmFlash.For(target, types);
        flash.CheckTarget(module, target);
        flash.Follow(module);
        CheckSizes(module, types);
        var (symbols, globalSymbols) = AssignSymbols(module);
        var typeSymbols = LlvmHeap.AssignSymbols(module);
        var roots = LlvmHeap.RootTable(module, types, globalSymbols, out var rootCount);
        var heapStart = LlvmHeap.UsesHeap(module) ? LlvmHeap.InitializeCall(types, rootCount) : null;
        var entry = module.EntryPoint is { } entryPoint
            ? EntryCall(entryPoint, symbols[entryPoint], heapStart, flash.FunctionAttributes)
            : null;

        var text = new StringBuilder();
        text.Append("source_filename = ").Append(LlvmText.Quoted(module.Name)).Append('\n');
        if (types.DataLayout is { } dataLayout)
        {
            // before the functions: LLVM puts each in the address space the layout's P names
            text.Append("target datalayout = ").Append(LlvmText.Quoted(dataLayout)).Append('\n');
        }

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
            // symbol at all) and constant, so the linker puts it with the code (.rodata, in flash on a chip; in the
            // flash section on an AVR, see LlvmFlash)
            var align = types.Alignment(global.Type).ToString(CultureInfo.InvariantCulture);
            var section = LlvmFlash.SectionOf(flash.InFlash(global));
            if (global.ObjectType is not null)
            {
                text.Append(LlvmHeap.ConstantObject(global, globalSymbols[global], typeSymbols, types, flash)).Append('\n');
                continue;
            }

            if (global.Data is { } data)
            {
                text.Append($"@{globalSymbols[global]} = private unnamed_addr constant {types.Of(global.Type)} " +
                    $"{LlvmText.Bytes(data)}{section}, align {align}\n");
                continue;
            }

            // a constant array (a table) keeps its symbol, internal, and is read-only data too; so does a constant
            // object (an empty array), whose header is the address of its type descriptor
            var value = global.Header is { } header
                ? ObjectValue((TirStructType)global.Type, header, global.FieldValues, globalSymbols, typeSymbols, types, flash)
                : global.Elements is { } elements ? Elements((TirArrayType)global.Type, elements, types)
                : global.InitialValue is { } initial ? LlvmText.Constant(initial)
                : LlvmText.Zero(global.Type);
            var kind = global.IsConstant ? "unnamed_addr constant" : "global";
            text.Append($"@{globalSymbols[global]} = internal {kind} {types.Of(global.Type)} {value}{section}, " +
                $"align {align}\n");
        }

        if (module.TypeDescriptors.Count > 0 || (roots is not null && heapStart is not null))
        {
            text.Append('\n');
            LlvmHeap.WriteDescriptors(text, module, types, typeSymbols, symbols, flash);
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
            text.Append($"declare {types.Of(function.ReturnType)} @{symbols[function]}({parameters}) nounwind" +
                $"{flash.DeclarationAttributes(symbols[function])}\n");
        }

        var intrinsics = new SortedSet<string>(StringComparer.Ordinal);
        var stackChecks = LlvmStackChecks.Find(module, target, types.Layout);
        var forgetRegisters = LlvmCollectCalls.ForgetRegisters(target);
        var isAvr = LlvmTypes.IsAvr(target);
        var alignsArrays = !target.HasCapability("unalignedAccess");
        foreach (var function in module.Functions.Where(f => f.Kind == TirFunctionKind.Defined))
        {
            text.Append('\n');
            new LlvmFunctionWriter(function, types, symbols, globalSymbols, typeSymbols, text, intrinsics)
            {
                Flash = flash,
                ChecksStack = stackChecks.Contains(function),
                ForgetRegisters = forgetRegisters,
                HasFrame = isAvr && LlvmAvrCalls.NeedsFrame(function, types),
                IsAvr = isAvr,
                StaysOutOfLine = isAvr && LlvmAvrCalls.StaysOutOfLine(function),
                AlignsArrays = alignsArrays,
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

        // on an AVR: the functions that read at an address in RAM or in flash
        foreach (var definition in flash.Definitions)
        {
            text.Append('\n').Append(definition).Append('\n');
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
    /// that sets the heap up) for a program that uses the heap, with the <paramref name="attributes"/> every function of
    /// the module has (see <see cref="LlvmFlash.FunctionAttributes"/>), so that LLVM may inline the entry point into it.
    /// The entry point must take nothing and return void or i32.
    /// </summary>
    private static string EntryCall(TirFunction entryPoint, string symbol, string? heapStart, string attributes)
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

        return $"define i32 @{EntrySymbol}() nounwind{attributes} {{\nentry:\n{heapStart}{call}}}\n";
    }

    /// <summary>
    /// A constant object's value: its header, then each field's (zero, a constant, an address), e.g.
    /// <c>{ ptr @type.int__, i32 0, ptr null }</c>. The addresses are those of the program (see
    /// <see cref="LlvmFlash.Address"/>).
    /// </summary>
    private static string ObjectValue(TirStructType type, TirTypeDescriptor header, IReadOnlyList<TirValue?>? values,
        IReadOnlyDictionary<TirGlobal, string> globalSymbols, IReadOnlyDictionary<TirTypeDescriptor, string> typeSymbols,
        LlvmTypes types, LlvmFlash flash)
    {
        string Type(TirTypeDescriptor descriptor) => flash.Address(typeSymbols[descriptor], flash.InFlash(descriptor));
        string Global(TirGlobal global) => flash.Address(globalSymbols[global], flash.InFlash(global));

        return $"{{ ptr {Type(header)}{string.Concat(type.Fields.Skip(1).Select((field, i) => (values?[i]) switch
        {
            TirConst constant => $", {types.Of(field.Type)} {LlvmText.Constant(constant)}",
            TirGlobalAddress address => $", ptr {Global(address.Global)}",
            TirTypeAddress address => $", ptr {Type(address.TypeDescriptor)}",
            _ => $", {types.Of(field.Type)} {LlvmText.Zero(field.Type)}",
        }))} }}";
    }

    /// <summary>
    /// Throws when a global or a slot is too large for the target's addresses: on an AVR (16-bit addresses, which reach
    /// at most 32767 bytes into anything), 32 KB or more. Nothing is on a target with 32-bit or 64-bit addresses.
    /// </summary>
    private static void CheckSizes(TirModule module, LlvmTypes types)
    {
        var bits = types.Bits(TirType.Ptr);
        if (bits >= 32)
        {
            return;
        }

        var limit = 1L << (bits - 1);
        void Check(string what, long size)
        {
            if (size >= limit)
            {
                throw new ChipSharpException($"{what} takes {size} bytes, more than the {limit - 1} that {bits}-bit " +
                    "addresses reach on the target. Make it smaller.");
            }
        }

        foreach (var global in module.Globals)
        {
            Check($"The global {global.Name}", global.ObjectType is null
                ? types.Layout.SizeOf(global.Type)
                : types.Layout.ArrayDataOffset + global.Data!.Count);
        }

        foreach (var function in module.Functions)
        {
            foreach (var slot in function.Slots)
            {
                Check($"The local {slot.Name} of {function.Name}", types.Layout.SizeOf(slot.Type));
            }
        }
    }

    /// <summary>The elements a global array starts with, as an LLVM constant: <c>[i32 1, i32 2]</c>.</summary>

    private static string Elements(TirArrayType array, IReadOnlyList<TirConst> elements, LlvmTypes types)
    {
        var element = types.Of(array.Element);
        return $"[{string.Join(", ", elements.Select(value => $"{element} {LlvmText.Constant(value)}"))}]";
    }

    /// <summary>The module name plus ".ll", with characters that cannot be in a file name replaced by '_'.</summary>
    private static string FileName(string moduleName) =>
        new string([.. moduleName.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)]) + ".ll";
}
