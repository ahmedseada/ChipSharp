using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>
/// The "c" code generator: writes a TIR module as one file of portable C99, "&lt;module name&gt;.c", for the chips
/// that only have a C compiler (SDCC's: a Padauk, an STM8) and, through clang, for the PC (target "host-c", where the
/// tests check what it writes against .NET). It is the llvm code generator's twin behind the same contract: the same
/// symbols for the runtime's functions (see <see cref="CSymbols"/>), the same layout of every struct, object and type
/// descriptor (<see cref="TirLayout"/>), <see cref="EntrySymbol"/> for the runtime's startup to call, the same calls
/// for objects (see <see cref="CHeap"/>). A target picks it with <c>"backend": "c"</c>; its C compiler is described by
/// its <c>"c"</c> settings (see <see cref="CSettings"/>).
/// <para>
/// The C means exactly what the C# means whatever the compiler makes of C's loose ends: fixed-width types only (an
/// SDCC int has 16 bits), wrap-around arithmetic in unsigned types, C#'s shifts, divisions and saturating float
/// conversions (see <see cref="CTypes"/> and <see cref="CFunctionWriter"/>). What it counts on of the compiler — two's
/// complement, an arithmetic shift right, an int of at most 32 bits, the pointer size, each struct's layout, a 64-bit
/// double where the program uses one — the file checks when it is compiled, so a compiler that differs stops with the
/// check's name. On a compiler that contracts a * b + c into one rounding (clang), the file asks it not to. It needs
/// <c>-fno-strict-aliasing</c> with clang and gcc, as the program may read memory as another type than it wrote (the
/// llvm code generator writes no type-based aliasing either); SDCC has no such analysis.
/// </para>
/// <para>
/// A program that uses C#'s double is refused for a target whose C double has 32 bits (SDCC's, see
/// <see cref="CSettings.DoubleBits"/>), rather than computed with less precision. A target that asks for stack checks
/// gets them (see <see cref="CStackChecks"/>). Constant data is <c>const</c>: in code memory with SDCC, read through
/// ordinary (generic) pointers. A function that returns a struct gives it through a pointer
/// (<see cref="CFunctionWriter.ResultName"/>), and the C steers around what SDCC 4.6 gets wrong (see the README).
/// </para>
/// </summary>
public sealed class CCodeGenerator : ICodeGenerator
{
    /// <summary>The function the runtime calls to run the program, as C sees it: <c>int32_t chipsharp_entry(void)</c>.</summary>
    public const string EntrySymbol = "chipsharp_entry";

    /// <summary>
    /// What the file marks a function with that may run again while it runs or is called through an address: SDCC's
    /// <c>__reentrant</c> on the processors where it keeps locals in static memory, else nothing (see <see cref="CCallGraph"/>).
    /// </summary>
    public const string ReentrantMacro = "CS_REENTRANT";

    /// <summary>The runtime functions that collect now (chipsharp_heap.c): the registers are cleared before a call to one.</summary>
    private static readonly HashSet<string> CollectSymbols = new(StringComparer.Ordinal)
    {
        "ChipSharp_Runtime_GcCollect", "ChipSharp_Runtime_GcGetTotalMemory",
    };

    public string Name => "c";

    /// <summary>
    /// Verifies the module (a <see cref="TirException"/> lists everything wrong with it), checks that the target can run
    /// it, then writes one file, "&lt;module name&gt;.c".
    /// </summary>
    public GeneratedCode Generate(TirModule module, TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(target);
        TirVerifier.Verify(module);
        var reachable = CReachable.Find(module);
        CHeap.CheckTarget(module, target);
        var settings = CSettings.Read(target);

        var types = new CTypes(new TirLayout(target.PointerSize, settings.MaxAlignment));
        CheckSizes(module, types);
        var symbols = CSymbols.Assign(module);
        symbols.Taken.Add(ReentrantMacro);
        symbols.Taken.Add(CFunctionWriter.ResultName);
        var tags = new HashSet<string>(StringComparer.Ordinal);
        types.Name(module, tags);
        var usesDouble = CheckDouble(module, reachable, settings, target);
        var reentrant = settings.Reentrant is null ? [] : CCallGraph.Reentrant(module);
        var (stackChecks, stackLimit) = CStackChecks.Find(module, target, reachable, types, settings);
        var pointerReturns = PointerReturns(module, reachable, types, settings);
        var needs = new CNeeds(types, symbols.Taken, settings.Reentrant is not null);

        // the parts in the order they are worked out: the data (which adds the structs of constant objects), then the
        // functions (which add declarations and helpers); then the file in the order C needs them
        var structs = new StringBuilder();
        types.WriteDefinitions(structs);
        var descriptorTypes = CHeap.WriteDescriptorTypes(structs, reachable, tags);
        var checks = new List<string>();
        checks.AddRange(types.Checks(symbols.Taken));
        checks.AddRange(CHeap.DescriptorChecks(descriptorTypes, types.Layout, symbols.Taken));

        var data = new StringBuilder();
        CHeap.WriteDescriptors(data, module, reachable, types, symbols, descriptorTypes, settings.ConstQualifier);
        WriteGlobals(data, structs, checks, module, reachable, types, symbols, tags, settings.ConstQualifier,
            target.Endian == Endianness.Big);
        var usesHeap = CHeap.FirstAllocation(module) is not null;
        var (roots, rootCount) = usesHeap
            ? CHeap.WriteRoots(data, module, reachable, types, symbols, tags, settings.ConstQualifier)
            : (null, 0);

        var functions = new StringBuilder();
        var forget = ForgetRegisters(target);

        // where the compiler keeps a function's locals at fixed addresses (SDCC's pdk14), it overlays those of functions
        // that never run at once (leaves): one an interrupt runs must not share them with one Main runs
        var notOverlaid = settings.Reentrant is null ? [] : CCallGraph.InInterrupts(module);
        foreach (var function in module.Functions.Where(f => f.Kind == TirFunctionKind.Defined && reachable.Functions.Contains(f)))
        {
            if (functions.Length > 0)
            {
                functions.Append('\n');
            }

            var apart = notOverlaid.Contains(function) && !reentrant.Contains(function);
            functions.Append(apart ? "/* an interrupt runs it: its locals are its own (SDCC overlays a leaf's) */\n" +
                "#pragma save\n#pragma nooverlay\n" : "");
            new CFunctionWriter(function, types, symbols, needs)
            {
                IsReentrant = reentrant.Contains(function),
                ForgetRegisters = forget,
                PointerReturns = pointerReturns,
                StructReturnPointer = settings.StructReturnPointer,
                StackLimit = stackChecks.Contains(function) ? stackLimit : null,
            }.Write(functions);
            functions.Append(apart ? "#pragma restore\n" : "");
        }

        if (module.EntryPoint is { } entryPoint)
        {
            functions.Append(functions.Length > 0 ? "\n" : "").Append(EntryFunction(entryPoint, symbols.Functions[entryPoint],
                usesHeap ? $"    {needs.Runtime(CHeap.InitializeSymbol)}({roots ?? "0"}, " +
                    $"{rootCount.ToString(CultureInfo.InvariantCulture)});\n" : ""));
        }

        // the runtime's functions that the hardware contract (chipsharp_hardware.h) makes reentrant, as the runtime
        // defines them: a handler may call one while Main is in it
        var runtimeReentrant = settings.Reentrant is null
            ? []
            : module.Functions.Where(f => f.Kind == TirFunctionKind.External && reachable.Functions.Contains(f)
                && HardwareContract.Find(symbols.Functions[f]) is { Reentrant: true }).ToHashSet();
        var text = new StringBuilder();
        WriteHead(text, module, target, types, settings, usesDouble, symbols.Taken);
        if (settings.Reentrant is { } keyword
            && (needs.HasFunctionTypes || reentrant.Any(reachable.Functions.Contains) || runtimeReentrant.Count > 0))
        {
            text.Append("/* the target's compiler keeps the parameters and locals of a function at fixed addresses in RAM unless it\n" +
                $"   is {keyword}: what a function needs that may run again while it runs (it calls itself, maybe through\n" +
                "   others, or runs in an interrupt and outside one) or is called through an address */\n" +
                $"#define {ReentrantMacro} {keyword}\n\n");
        }

        text.Append(structs);
        if (checks.Count > 0)
        {
            text.Append("/* the layout of every struct, as TIR lays it out on the target */\n");
            foreach (var check in checks)
            {
                text.Append(check).Append('\n');
            }

            text.Append('\n');
        }

        var externals = module.Functions.Where(f => f.Kind == TirFunctionKind.External && reachable.Functions.Contains(f))
            .ToList();
        var declaredByModule = externals.Select(f => symbols.Functions[f]).ToHashSet(StringComparer.Ordinal);
        needs.WriteDeclarations(text, declaredByModule);
        var runtime = externals.Where(f => !CNeeds.IsLibrary(symbols.Functions[f])).ToList();
        if (stackLimit is not null && stackChecks.Count > 0)
        {
            text.Append("/* the stack checks: a function through which the stack can grow without end stops the program past\n" +
                "   this, where the room the variables leave the stack ends, less a margin (see CStackChecks) */\n")
                .Append(stackLimit.Definition).Append('\n')
                .Append($"void {CStackChecks.OverflowSymbol}(void);\n\n");
        }
        if (runtime.Count > 0)
        {
            text.Append("/* the runtime's */\n");
            foreach (var function in runtime)
            {
                var names = CFunctionWriter.ParameterNames(function, new HashSet<string>(StringComparer.Ordinal));
                text.Append(CFunctionWriter.Signature(function, types, symbols.Functions[function], names,
                        runtimeReentrant.Contains(function)))
                    .Append(";\n");
            }

            text.Append('\n');
        }

        var defined = module.Functions.Where(f => f.Kind == TirFunctionKind.Defined && reachable.Functions.Contains(f)).ToList();
        if (defined.Count > 0)
        {
            text.Append("/* the program's */\n");
            foreach (var function in defined)
            {
                text.Append(CFunctionWriter.Signature(function, types, symbols.Functions[function],
                    CFunctionWriter.ParameterNames(function, symbols.Taken), reentrant.Contains(function),
                    pointerReturns.Contains(function))).Append(";\n");
            }

            text.Append('\n');
        }

        needs.WriteHelpers(text);
        text.Append(data);
        text.Append(functions);
        return new GeneratedCode([new GeneratedFile(FileName(module.Name), text.ToString())]);
    }

    /// <summary>True for a call of the runtime function <paramref name="symbol"/> that may collect now.</summary>
    internal static bool Collects(string symbol) => CollectSymbols.Contains(symbol);

    /// <summary>
    /// The statement that clears the callee-saved registers before a call that collects, on a target whose processor
    /// it knows (x86-64, Thumb): the collector reads the registers conservatively, and one that keeps a reference the
    /// program no longer uses would keep its object alive through the collection the program asked for (as the llvm
    /// code generator does it). The compiler moves what it still needs from them to the stack first, where the
    /// collector reads it. Null for any other target.
    /// </summary>
    internal static string? ForgetRegisters(TargetDescription target)
    {
        var triple = target.Triple ?? "";
        if (triple.StartsWith("x86_64", StringComparison.Ordinal))
        {
            return "__asm__ volatile(\"xorl %%ebx, %%ebx\\n\\txorl %%ebp, %%ebp\\n\\txorl %%esi, %%esi\\n\\t" +
                "xorl %%edi, %%edi\\n\\txorl %%r12d, %%r12d\\n\\txorl %%r13d, %%r13d\\n\\txorl %%r14d, %%r14d\\n\\t" +
                "xorl %%r15d, %%r15d\" : : : \"rbx\", \"rbp\", \"rsi\", \"rdi\", \"r12\", \"r13\", \"r14\", \"r15\", \"cc\");";
        }

        if (triple.StartsWith("thumbv6m", StringComparison.Ordinal))
        {
            return "__asm__ volatile(\"movs r4, #0\\n\\tmovs r5, #0\\n\\tmovs r6, #0\\n\\tmovs r7, #0\\n\\t" +
                "mov r8, r4\\n\\tmov r9, r4\\n\\tmov r10, r4\\n\\tmov r11, r4\" : : : \"r4\", \"r5\", \"r6\", \"r7\", " +
                "\"r8\", \"r9\", \"r10\", \"r11\", \"cc\");";
        }

        return null;
    }

    /// <summary>The top of the file: what it is, its headers, and the checks of what it counts on of the compiler.</summary>
    private static void WriteHead(StringBuilder text, TirModule module, TargetDescription target, CTypes types,
        CSettings settings, bool usesDouble, ISet<string> names)
    {
        text.Append($"/* {CText.Comment(module.Name)}: C written by ChipSharp's c code generator for target " +
            $"'{CText.Comment(target.Name)}'. */\n\n");
        text.Append("#include <stdint.h>\n#include <stddef.h>\n\n");
        text.Append("#ifdef __SDCC\n" +
            "/* SDCC's warnings of what the code below does on purpose: an address of constant data (in code memory) is an\n" +
            "   ordinary uint8_t * like any other, which nothing writes through (357); a branch whose condition is known,\n" +
            "   and the code it skips (110, 126); a value a loop carries, which it may take for one read before it is set\n" +
            "   (84: every variable here starts with a value) */\n" +
            "#pragma disable_warning 84\n#pragma disable_warning 110\n#pragma disable_warning 126\n" +
            "#pragma disable_warning 357\n#endif\n\n");
        if (UsesFloats(module))
        {
            text.Append("/* C# rounds a * b + c twice: clang may not contract it into one fused multiply-add */\n")
                .Append("#ifdef __clang__\n#pragma STDC FP_CONTRACT OFF\n#endif\n\n");
        }

        var pointer = types.Layout.PointerSize.ToString(CultureInfo.InvariantCulture);
        text.Append("/* what the code below counts on, checked when it is compiled: the pointer size; two's complement, which\n" +
            "   signed operations read their operands in; an arithmetic shift right of a negative number; an int of at\n" +
            "   most 32 bits, so that a uint32_t is never made a (signed) int */\n");
        text.Append(CTypes.Check(names, "cs_pointer_size", [$"sizeof(void *) == {pointer}"])).Append('\n');
        text.Append(CTypes.Check(names, "cs_twos_complement", ["(int8_t)0xFF == -1", "(int32_t)0xFFFFFFFFu == -1"])).Append('\n');
        text.Append(CTypes.Check(names, "cs_arithmetic_shift", ["(-2 >> 1) == -1"])).Append('\n');
        text.Append(CTypes.Check(names, "cs_int_width", ["(unsigned)-1 <= 0xFFFFFFFFu"])).Append('\n');
        if (usesDouble)
        {
            text.Append(CTypes.Check(names, "cs_double_has_64_bits", ["sizeof(double) == 8"])).Append('\n');
        }

        if (CCallGraph.Addressed(module).Count > 0)
        {
            // a function's address is kept as a data address (uint8_t *) and cast back to call it
            text.Append(CTypes.Check(names, "cs_function_pointer_size", ["sizeof(void (*)(void)) <= sizeof(void *)"]))
                .Append('\n');
        }

        if (settings.TypeConditions().ToList() is { Count: > 0 } conditions)
        {
            text.Append("/* the C types as the target file says the compiler has them */\n")
                .Append(CTypes.Check(names, "cs_target_types", conditions)).Append('\n');
        }

        text.Append('\n');
    }

    /// <summary>
    /// <c>int32_t chipsharp_entry(void)</c>, which sets the heap up for a program that uses it, then calls the entry point
    /// and returns its int result (0 when it returns nothing): a C# int, 32 bits, as the hardware contract has it (C's
    /// int has 16 on SDCC's chips). The entry point must take nothing and return void or i32.
    /// </summary>
    private static string EntryFunction(TirFunction entryPoint, string symbol, string heapStart)
    {
        if (entryPoint.Parameters.Count > 0)
        {
            throw new ChipSharpException($"The entry point '{entryPoint.Name}' takes parameters; it must take none.");
        }

        var call = entryPoint.ReturnType.Kind switch
        {
            TirTypeKind.Void => $"    {symbol}();\n    return 0;\n",
            TirTypeKind.I32 => $"    return (int32_t){symbol}();\n",
            _ => throw new ChipSharpException($"The entry point '{entryPoint.Name}' returns " +
                $"{entryPoint.ReturnType}; it must return void or i32 (int)."),
        };

        return "/* the program's entry, which the runtime's startup calls: Main's result, 0 for a void Main */\n" +
            $"int32_t {EntrySymbol}(void)\n{{\n{heapStart}{call}}}\n";
    }

    /// <summary>
    /// Writes the globals, each after the constant globals its fields hold the address of (SDCC takes no declaration of
    /// a static object before its definition): a variable is <c>static</c>, starting at zero or at its initial value or
    /// elements; constant data (a string literal, a table, a constant object) is <c>static const</c> (see
    /// <see cref="CHeap.Constant"/>), its bytes with the text they spell, when they do, in a comment.
    /// </summary>
    private static void WriteGlobals(StringBuilder data, StringBuilder structs, List<string> checks, TirModule module,
        CReachable reachable,
        CTypes types, CSymbols symbols, ISet<string> tags, string? qualifier, bool bigEndian)
    {
        var objectTypes = new Dictionary<int, string>();
        var written = new HashSet<TirGlobal>();
        var visiting = new HashSet<TirGlobal>();

        void Write(TirGlobal global)
        {
            if (written.Contains(global))
            {
                return;
            }

            if (!visiting.Add(global))
            {
                throw new ChipSharpException($"The global {global.Name} holds its own address through other globals; " +
                    "the c code generator cannot write it.");
            }

            foreach (var address in global.FieldValues?.OfType<TirGlobalAddress>() ?? [])
            {
                Write(address.Global);
            }

            written.Add(global);
            var symbol = symbols.Globals[global];
            if (global.Data is { } bytes && CText.TextOf(bytes) is { } text)
            {
                data.Append("/* ").Append(text).Append(" */\n");
            }

            if (global.ObjectType is not null)
            {
                var tag = CHeap.ObjectType(structs, objectTypes, global.Data!.Count, types.Layout, tags, checks,
                    symbols.Taken);
                data.Append(CHeap.Constant("struct " + tag, symbol, qualifier)).Append(" = ")
                    .Append(CHeap.ObjectValue(global, symbols, types.Layout, bigEndian)).Append(";\n\n");
                return;
            }

            var cType = types.Of(global.Type);
            var definition = global.IsConstant
                ? CHeap.Constant(cType, symbol, qualifier)
                : "static " + CTypes.Declare(cType, symbol);
            var value = global switch
            {
                { Data: { } constantBytes } => "{ " + CText.Bytes(constantBytes, "    ") + " }",
                { Header: { } header } => ObjectValue((TirStructType)global.Type, header, global.FieldValues, types, symbols),
                { Elements: { } elements } => "{ { " + string.Join(", ", elements.Select(e => CFunctionWriter.Constant(e, types))) + " } }",
                { InitialValue: { } initial } => CFunctionWriter.Constant(initial, types),
                _ when global.IsConstant => CTypes.ZeroInitializer(global.Type),
                _ => null,
            };
            data.Append(definition).Append(value is null ? "" : " = " + value).Append(";\n\n");
        }

        foreach (var global in module.Globals.Where(reachable.Globals.Contains))
        {
            Write(global);
        }
    }

    /// <summary>
    /// A constant object's value: its header, then each field's (zero, a constant, an address), e.g.
    /// <c>{ (uint8_t *)&amp;cs_type_int__, 0, (uint8_t *)&amp;cs_text_0_ }</c>.
    /// </summary>
    private static string ObjectValue(TirStructType type, TirTypeDescriptor header, IReadOnlyList<TirValue?>? values,
        CTypes types, CSymbols symbols)
    {
        var parts = new List<string> { $"(uint8_t *)&{symbols.Types[header]}" };
        for (var i = 1; i < type.Fields.Count; i++)
        {
            parts.Add((values?[i - 1]) switch
            {
                TirConst constant => CFunctionWriter.Constant(constant, types),
                TirGlobalAddress address => $"(uint8_t *)&{symbols.Globals[address.Global]}",
                TirTypeAddress address => $"(uint8_t *)&{symbols.Types[address.TypeDescriptor]}",
                _ => CTypes.ZeroInitializer(type.Fields[i].Type),
            });
        }

        return "{ " + string.Join(", ", parts) + " }";
    }

    /// <summary>
    /// Throws when the program uses C#'s double on a target whose C double has 32 bits; returns whether it uses one (the
    /// file then checks the double's size).
    /// </summary>
    private static bool CheckDouble(TirModule module, CReachable reachable, CSettings settings, TargetDescription target)
    {
        string? where = null;
        foreach (var global in module.Globals.Where(global => reachable.Globals.Contains(global) && HoldsDouble(global.Type)))
        {
            where ??= $"The global {global.Name}";
        }

        foreach (var function in module.Functions.Where(reachable.Functions.Contains))
        {
            if (where is not null)
            {
                break;
            }

            if (HoldsDouble(function.ReturnType) || function.Parameters.Any(p => HoldsDouble(p.Type)))
            {
                where = $"The function {function.Name}";
                break;
            }

            if (function.Kind != TirFunctionKind.Defined)
            {
                continue;
            }

            // what the function's written code does: not the code of a branch it never takes, nor a slot only that uses
            var live = CLiveness.Find(function).Live;
            var slots = live.Select(instruction => instruction switch
            {
                TirLoad load => load.Slot,
                TirStore store => store.Slot,
                TirSlotAddress address => address.Slot,
                _ => null,
            }).OfType<TirSlot>().ToHashSet();
            if (function.Slots.FirstOrDefault(slot => slots.Contains(slot) && HoldsDouble(slot.Type)) is { } slot)
            {
                where = $"{function.Name} (its local {slot.Name})";
                break;
            }

            var instruction = function.Blocks.SelectMany(block => block.Instructions).Where(live.Contains)
                .FirstOrDefault(instruction =>
                HoldsDouble(instruction.Type) || instruction.Operands.Any(operand => HoldsDouble(operand.Type))
                || instruction is TirPointerLoad { Type: var loaded } && HoldsDouble(loaded)
                || instruction is TirElementAddress { ElementType: var element } && HoldsDouble(element));
            if (instruction is not null)
            {
                where = $"{function.Name} ('{TirPrinter.Print(instruction)}')";
            }
        }

        if (where is null)
        {
            return false;
        }

        if (settings.DoubleBits < 64)
        {
            throw new ChipSharpException($"{where} uses double (C#'s 64-bit floating point), and target " +
                $"'{target.Name}' compiles C whose double has {settings.DoubleBits} bits (its \"double64\" is false): the " +
                "program would compute with less precision than .NET does. Use float instead.");
        }

        return true;
    }

    /// <summary>True for f64, and a struct or array that holds one.</summary>
    private static bool HoldsDouble(TirType type) => type switch
    {
        TirStructType structType => structType.Fields.Any(field => HoldsDouble(field.Type)),
        TirArrayType array => HoldsDouble(array.Element),
        _ => type == TirType.F64,
    };

    /// <summary>
    /// The functions that give their result through a pointer (<see cref="CFunctionWriter.ResultName"/>): on a target whose
    /// "c.structReturn" is "pointer" (SDCC's STM8), each the program has that returns a struct or an array, and on one
    /// whose "c.largeFrame" says how far its compiler reaches into a frame (the STM8's 255 bytes), each one called only
    /// directly that returns 3 or 4 bytes and whose locals may take more.
    /// </summary>
    private static HashSet<TirFunction> PointerReturns(TirModule module, CReachable reachable, CTypes types, CSettings settings)
    {
        var defined = module.Functions.Where(f => f.Kind == TirFunctionKind.Defined && reachable.Functions.Contains(f)).ToList();
        var pointer = defined.Where(function => settings.StructReturnPointer && function.ReturnType.IsAggregate).ToHashSet();
        if (settings.LargeFrame is not { } limit)
        {
            return pointer;
        }

        var addressed = CCallGraph.Addressed(module);
        foreach (var function in defined.Where(f => !addressed.Contains(f) && f.ReturnType != TirType.Void
            && types.Layout.SizeOf(f.ReturnType) is 3 or 4))
        {
            // at most what its parameters, its slots and a temporary for every value it computes take
            var live = CLiveness.Find(function).Live;
            var frame = function.Parameters.Sum(parameter => types.Layout.SizeOf(parameter.Type))
                + function.Slots.Sum(slot => types.Layout.SizeOf(slot.Type))
                + live.Where(instruction => instruction.HasResult && instruction is not TirConst)
                    .Sum(instruction => types.Layout.SizeOf(instruction.Type));
            if (frame > limit)
            {
                pointer.Add(function);
            }
        }

        return pointer;
    }

    /// <summary>True when the module computes with floats or doubles.</summary>
    private static bool UsesFloats(TirModule module) => module.Functions.SelectMany(function => function.Blocks)
        .SelectMany(block => block.Instructions).Any(instruction => instruction.Type.IsFloat
            || instruction.Operands.Any(operand => operand.Type.IsFloat));

    /// <summary>
    /// Throws when a global or a slot is too large for the target's addresses: with 16-bit addresses, which reach at
    /// most 32767 bytes into anything, 32 KB or more.
    /// </summary>
    private static void CheckSizes(TirModule module, CTypes types)
    {
        var bits = types.PointerBits;
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

    /// <summary>The module name plus ".c", with characters that cannot be in a file name replaced by '_'.</summary>
    private static string FileName(string moduleName) =>
        new string([.. moduleName.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)]) + ".c";
}
