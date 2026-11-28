using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// Objects on the heap, as the llvm code generator writes them for the runtime's allocator and garbage collector
/// (src/ChipSharp.Runtime.Shared/chipsharp_heap.c):
/// <list type="bullet">
/// <item>Each type descriptor is a private constant, <c>@type.Program_Node</c>, laid out as
/// <see cref="TirTypeDescriptor"/> says, with its references (<c>@type.Program_Node.references</c>, u32 offsets), its name
/// and its interface map next to it. It is not <c>unnamed_addr</c>: its address is the type of an object.</item>
/// <item><c>newobject</c> and <c>newarray</c> call <see cref="NewObjectSymbol"/> and <see cref="NewArraySymbol"/> with
/// the descriptor's address; <c>nullcheck</c> branches to a block of the function that calls
/// <see cref="NullReferenceSymbol"/>, which does not return.</item>
/// <item>A module that makes objects is a program that uses the heap: <c>chipsharp_entry</c> first calls
/// <see cref="InitializeSymbol"/> with the table of its static roots, <c>@chipsharp.roots</c>: every ref in the module's
/// globals, as pairs of the address of the first of a run of refs one after another and how many there are
/// (<c>{ ptr, nint }</c>), so that the collector reads exactly the refs of the static fields.</item>
/// </list>
/// Only a target with a heap (<see cref="TargetDescription.HasHeap"/>) may make objects.
/// </summary>
internal static class LlvmHeap
{
    /// <summary><c>void *chipsharp_new_object(const chipsharp_type *type)</c>: a new zeroed object with its header set.</summary>
    public const string NewObjectSymbol = "chipsharp_new_object";

    /// <summary><c>void *chipsharp_new_array(const chipsharp_type *type, int32_t length)</c>: a new array object.</summary>
    public const string NewArraySymbol = "chipsharp_new_array";

    /// <summary><c>void chipsharp_heap_initialize(const chipsharp_root *roots, uintptr_t count)</c>: sets the heap up.</summary>
    public const string InitializeSymbol = "chipsharp_heap_initialize";

    /// <summary>Stops the program with .NET's NullReferenceException message; in each runtime's failures file.</summary>
    public const string NullReferenceSymbol = "ChipSharp_Runtime_NullReference";

    /// <summary>The runtime functions the code generator calls for objects: no function of the program may have their symbols.</summary>
    public static IReadOnlyList<string> RuntimeSymbols { get; } =
        [NewObjectSymbol, NewArraySymbol, InitializeSymbol, NullReferenceSymbol];

    /// <summary>True when the module makes objects (newobject, newarray): its program uses the heap.</summary>
    public static bool UsesHeap(TirModule module) => FirstAllocation(module) is not null;

    /// <summary>Throws when the module makes objects and <paramref name="target"/> has no heap.</summary>
    public static void CheckTarget(TirModule module, TargetDescription target)
    {
        if (target.HasHeap || FirstAllocation(module) is not { } allocation)
        {
            return;
        }

        var function = allocation.Block!.Function;
        throw new ChipSharpException($"{function.Name} creates objects ('{TirPrinter.Print(allocation)}'), which needs " +
            $"a heap, and the target '{target.Name}' has none (its \"heap\" capability is false). Build it for a target " +
            "with a heap, or use structs and static methods.");
    }

    /// <summary>
    /// The symbol of each type descriptor: <c>type.</c> and its name made LLVM-safe like a symbol, with _2, _3, ... when
    /// two would be the same. The dot keeps them apart from the symbols of functions and globals, which have none.
    /// </summary>
    public static Dictionary<TirTypeDescriptor, string> AssignSymbols(TirModule module)
    {
        var symbols = new Dictionary<TirTypeDescriptor, string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in module.TypeDescriptors)
        {
            // a descriptor of the runtime's keeps its symbol, which the runtime's C defines
            if (type.ExternalSymbol is { } external)
            {
                symbols[type] = external;
                taken.Add(external);
                continue;
            }

            var wanted = "type." + LlvmText.Name(type.Name);
            var symbol = wanted;
            for (var n = 2; !taken.Add(symbol); n++)
            {
                symbol = wanted + "_" + n.ToString(CultureInfo.InvariantCulture);
            }

            symbols[type] = symbol;
        }

        return symbols;
    }

    /// <summary>
    /// Writes every type descriptor of the module, with what it points to, as private constants (in flash, with flash's
    /// addresses, on an AVR: see <see cref="LlvmFlash"/>).
    /// </summary>
    public static void WriteDescriptors(StringBuilder text, TirModule module, LlvmTypes types,
        IReadOnlyDictionary<TirTypeDescriptor, string> descriptors, IReadOnlyDictionary<TirFunction, string> functions,
        LlvmFlash flash)
    {
        var layout = types.Layout;
        var word = layout.PointerSize;
        string Function(TirFunction? function) =>
            function is null ? "ptr null" : $"ptr {types.FunctionAddress(functions[function])}";
        string Descriptor(TirTypeDescriptor type) => $"ptr {flash.Address(descriptors[type], flash.InFlash(type))}";

        foreach (var type in module.TypeDescriptors)
        {
            var symbol = descriptors[type];
            if (type.ExternalSymbol is not null)
            {
                // the runtime defines it (a string's, in chipsharp_string.c): the module only names it
                text.Append($"@{symbol} = external constant i8, align {word}\n");
                continue;
            }

            var inFlash = flash.InFlash(type);
            var section = LlvmFlash.SectionOf(inFlash);
            string Part(string name) => flash.Address(name, inFlash);

            var (size, elementSize, flags, references) = type.Kind switch
            {
                TirTypeDescriptorKind.Class => (layout.SizeOf(type.ObjectType!), 0L, 0,
                    layout.ReferenceOffsets(type.ObjectType!)),
                TirTypeDescriptorKind.Array => (layout.ArrayDataOffset, layout.SizeOf(type.ElementType!), 1,
                    layout.ReferenceOffsets(type.ElementType!)),
                _ => (0L, 0L, 2, (IReadOnlyList<long>)[]),
            };

            if (size > uint.MaxValue || elementSize > uint.MaxValue)
            {
                throw new ChipSharpException($"The objects of type {type.Name} are too large: {Math.Max(size, elementSize)} bytes.");
            }

            var referencesValue = "ptr null";
            if (references.Count > 0)
            {
                text.Append($"@{symbol}.references = private constant [{references.Count} x i32] " +
                    $"[{string.Join(", ", references.Select(offset => $"i32 {LlvmText.Integer(offset)}"))}]{section}, " +
                    $"align {types.Alignment(TirType.I32)}\n");
                referencesValue = $"ptr {Part($"{symbol}.references")}";
            }

            var name = Encoding.UTF8.GetBytes((type.RuntimeName ?? type.Name) + "\0");
            text.Append($"@{symbol}.name = private unnamed_addr constant [{name.Length} x i8] {LlvmText.Bytes(name)}" +
                $"{section}, align 1\n");

            var interfacesValue = "ptr null";
            if (type.Interfaces.Count > 0)
            {
                var entries = new List<string>();
                for (var i = 0; i < type.Interfaces.Count; i++)
                {
                    var methods = type.Interfaces[i].Methods;
                    var table = $"@{symbol}.interface{i.ToString(CultureInfo.InvariantCulture)}";
                    text.Append($"{table} = private constant [{methods.Count} x ptr] " +
                        $"[{string.Join(", ", methods.Select(Function))}]{section}, align {word}\n");
                    entries.Add($"{{ ptr, ptr }} {{ {Descriptor(type.Interfaces[i].Interface)}, ptr {Part(table[1..])} }}");
                }

                text.Append($"@{symbol}.interfaces = private constant [{entries.Count} x {{ ptr, ptr }}] " +
                    $"[{string.Join(", ", entries)}]{section}, align {word}\n");
                interfacesValue = $"ptr {Part($"{symbol}.interfaces")}";
            }

            var linked = type.Base ?? type.ElementDescriptor;
            var virtuals = type.VirtualMethods;
            var vtable = $"[{virtuals.Count} x ptr]";
            var vtableValue = virtuals.Count == 0 ? "zeroinitializer" : $"[{string.Join(", ", virtuals.Select(Function))}]";
            text.Append($"@{symbol} = private constant {{ i32, i32, i32, i32, ptr, ptr, ptr, ptr, i32, i32, {vtable} }} {{ " +
                $"i32 {LlvmText.Integer(size)}, i32 {LlvmText.Integer(elementSize)}, i32 {flags}, " +
                $"i32 {references.Count}, {referencesValue}, " +
                $"{(linked is null ? "ptr null" : Descriptor(linked))}, ptr {Part($"{symbol}.name")}, {interfacesValue}, " +
                $"i32 {type.Interfaces.Count}, i32 {virtuals.Count}, {vtable} {vtableValue} }}{section}, align {word}\n");
        }
    }

    /// <summary>
    /// A constant object (<see cref="TirModule.AddConstantObject"/>), laid out as an array object on the heap: its header
    /// (the descriptor's address), its length, zeros up to <see cref="TirLayout.ArrayDataOffset"/>, then its elements'
    /// bytes, as a packed struct, aligned like a heap object's fields need (not at all on an AVR). Private, unnamed_addr
    /// and constant: read-only data, in flash on a chip (in flash's section on an AVR, see <see cref="LlvmFlash"/>), which
    /// the garbage collector never marks (it is outside the heap).
    /// </summary>
    public static string ConstantObject(TirGlobal global, string symbol,
        IReadOnlyDictionary<TirTypeDescriptor, string> descriptors, LlvmTypes types, LlvmFlash flash)
    {
        var layout = types.Layout;
        var padding = layout.ArrayDataOffset - layout.ArrayLengthOffset - 4;
        var bytes = global.Data!;
        var header = flash.Address(descriptors[global.ObjectType!], flash.InFlash(global.ObjectType!));
        var fieldTypes = new List<string> { "ptr", "i32" };
        var fields = new List<string> { $"ptr {header}", $"i32 {global.ObjectLength}" };
        if (padding > 0)
        {
            fieldTypes.Add($"[{padding} x i8]");
            fields.Add($"[{padding} x i8] zeroinitializer");
        }

        if (bytes.Count > 0)
        {
            fieldTypes.Add($"[{bytes.Count} x i8]");
            fields.Add($"[{bytes.Count} x i8] {LlvmText.Bytes(bytes)}");
        }

        var align = Math.Max(types.Alignment(TirType.Ptr), types.Alignment(TirType.I32));
        return $"@{symbol} = private unnamed_addr constant <{{ {string.Join(", ", fieldTypes)} }}> " +
            $"<{{ {string.Join(", ", fields)} }}>{LlvmFlash.SectionOf(flash.InFlash(global))}, align {align}";
    }

    /// <summary>
    /// The table of static roots, <c>@chipsharp.roots</c>, when the module's globals hold refs: one <c>{ ptr, nint }</c>
    /// per run of refs one after another in a global; null when they hold none.
    /// </summary>
    public static string? RootTable(TirModule module, LlvmTypes types, IReadOnlyDictionary<TirGlobal, string> globals,
        out int count)
    {
        var word = types.Layout.PointerSize;
        var nint = types.Of(TirType.NativeInt);
        var entries = new List<string>();
        foreach (var global in module.Globals.Where(global => !global.IsConstant))
        {
            var offsets = types.Layout.ReferenceOffsets(global.Type);
            for (var i = 0; i < offsets.Count;)
            {
                var run = 1;
                while (i + run < offsets.Count && offsets[i + run] == offsets[i] + run * word)
                {
                    run++;
                }

                var address = offsets[i] == 0
                    ? $"ptr @{globals[global]}"
                    : $"ptr getelementptr (i8, ptr @{globals[global]}, {nint} {LlvmText.Integer(offsets[i])})";
                entries.Add($"{{ ptr, {nint} }} {{ {address}, {nint} {run} }}");
                i += run;
            }
        }

        count = entries.Count;
        return entries.Count == 0
            ? null
            : $"@chipsharp.roots = private constant [{entries.Count} x {{ ptr, {nint} }}] [{string.Join(", ", entries)}], " +
              $"align {word}\n";
    }

    /// <summary>The call that sets the heap up, first thing in <c>chipsharp_entry</c>, with <paramref name="roots"/> static roots.</summary>
    public static string InitializeCall(LlvmTypes types, int roots)
    {
        var nint = types.Of(TirType.NativeInt);
        var table = roots == 0 ? "ptr null" : "ptr @chipsharp.roots";
        return $"  call void @{InitializeSymbol}({table}, {nint} {roots.ToString(CultureInfo.InvariantCulture)})\n";
    }

    /// <summary>The declaration of one of <see cref="RuntimeSymbols"/>, e.g. <c>declare ptr @chipsharp_new_object(ptr) nounwind</c>.</summary>
    public static string Declaration(string symbol, LlvmTypes types) => symbol switch
    {
        NewObjectSymbol => $"declare ptr @{NewObjectSymbol}(ptr) nounwind",
        NewArraySymbol => $"declare ptr @{NewArraySymbol}(ptr, i32) nounwind",
        InitializeSymbol => $"declare void @{InitializeSymbol}(ptr, {types.Of(TirType.NativeInt)}) nounwind",
        NullReferenceSymbol => $"declare void @{NullReferenceSymbol}() cold noreturn nounwind",
        _ => throw new ArgumentOutOfRangeException(nameof(symbol), symbol, "Not a runtime function for objects."),
    };

    /// <summary>
    /// The symbol a <c>declare</c> line declares: what comes between @ and (; or an external global's
    /// (<c>@name = external global i8</c>): what comes between @ and the space.
    /// </summary>
    public static string DeclaredSymbol(string declaration)
    {
        var at = declaration.IndexOf('@', StringComparison.Ordinal) + 1;
        return declaration[at..declaration.IndexOfAny(['(', ' '], at)];
    }

    private static TirInstruction? FirstAllocation(TirModule module) => module.Functions
        .SelectMany(function => function.Blocks).SelectMany(block => block.Instructions)
        .FirstOrDefault(instruction => instruction is TirNewObject or TirNewArray or TirCall { Callee.MakesObjects: true });
}
