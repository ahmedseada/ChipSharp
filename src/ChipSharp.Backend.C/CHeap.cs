using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>
/// Objects on the heap, as the c code generator writes them for the runtime's allocator and garbage collector
/// (src/ChipSharp.Runtime.Shared/chipsharp_heap.c), the same as the llvm code generator does:
/// <list type="bullet">
/// <item>Each type descriptor is a <c>static const</c> struct, <c>cs_type_Program_Node</c>, laid out as
/// <see cref="TirTypeDescriptor"/> says (a struct of its own for each length of the virtual method table,
/// <c>struct cs_type_2</c>), with its references (<c>cs_type_Program_Node_references</c>, u32 offsets), its name and its
/// interface map next to it. A descriptor the runtime defines is only declared, <c>extern const</c>.</item>
/// <item><c>newobject</c> and <c>newarray</c> call <see cref="NewObjectSymbol"/> and <see cref="NewArraySymbol"/> with
/// the descriptor's address; <c>nullcheck</c> calls <see cref="NullReferenceSymbol"/> (which does not return) when the
/// reference is null.</item>
/// <item>A module that makes objects is a program that uses the heap: <c>chipsharp_entry</c> first calls
/// <see cref="InitializeSymbol"/> with the table of its static roots, <c>cs_roots</c>: every ref in the module's variable
/// globals, as pairs of the address of the first of a run of refs one after another and how many there are.</item>
/// </list>
/// Only a target with a heap (<see cref="TargetDescription.HasHeap"/>) may make objects. C cannot declare a static object
/// before it defines it with SDCC (a tentative definition is a duplicate symbol there), so the descriptors come in an order
/// where each comes after those it names (its base, its element type's, its interfaces').
/// </summary>
internal static class CHeap
{
    /// <summary><c>void *chipsharp_new_object(const chipsharp_type *type)</c>: a new zeroed object with its header set.</summary>
    public const string NewObjectSymbol = "chipsharp_new_object";

    /// <summary><c>void *chipsharp_new_array(const chipsharp_type *type, int length)</c>: a new array object.</summary>
    public const string NewArraySymbol = "chipsharp_new_array";

    /// <summary><c>void chipsharp_heap_initialize(const chipsharp_root *roots, uintptr_t count)</c>: sets the heap up.</summary>
    public const string InitializeSymbol = "chipsharp_heap_initialize";

    /// <summary>Stops the program with .NET's NullReferenceException message; in each runtime's failures file.</summary>
    public const string NullReferenceSymbol = "ChipSharp_Runtime_NullReference";

    /// <summary>The runtime functions the code generator calls for objects: no function of the program may have their symbols.</summary>
    public static IReadOnlyList<string> RuntimeSymbols { get; } =
        [NewObjectSymbol, NewArraySymbol, InitializeSymbol, NullReferenceSymbol];

    /// <summary>The first instruction that makes an object or calls a runtime function that does; null for a program without.</summary>
    public static TirInstruction? FirstAllocation(TirModule module) => module.Functions
        .SelectMany(function => function.Blocks).SelectMany(block => block.Instructions)
        .FirstOrDefault(instruction => instruction is TirNewObject or TirNewArray or TirCall { Callee.MakesObjects: true });

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
    /// The C declaration of one of <see cref="RuntimeSymbols"/>, as the runtime defines it (an address is a
    /// <c>const void *</c> there; an array's length an int32_t, TIR's i32).
    /// </summary>
    public static string Declaration(string symbol, CTypes types) => symbol switch
    {
        NewObjectSymbol => $"void *{NewObjectSymbol}(const void *type);",
        NewArraySymbol => $"void *{NewArraySymbol}(const void *type, int32_t length);",
        InitializeSymbol => $"void {InitializeSymbol}(const void *roots, {types.PointerInteger} count);",
        NullReferenceSymbol => $"void {NullReferenceSymbol}(void);",
        _ => throw new ArgumentOutOfRangeException(nameof(symbol), symbol, "Not a runtime function for objects."),
    };

    /// <summary>
    /// The definition of a constant object, with <paramref name="cType"/> and <paramref name="name"/>: <c>static const</c>
    /// and, when the target names one, its qualifier for constant data, right before the name (a pointer type's
    /// <c>*const</c> before that), e.g. <c>static const struct cs_a5_i8 __code Program_text</c>,
    /// <c>static uint8_t *const cs_type_A_interface0</c>.
    /// </summary>
    public static string Constant(string cType, string name, string? qualifier)
    {
        var at = qualifier is null ? name : qualifier + " " + name;
        return cType.EndsWith('*') ? $"static {cType}const {at}" : $"static const {cType} {at}";
    }

    /// <summary>The type descriptors of <paramref name="module"/>, each after the ones it names.</summary>
    public static List<TirTypeDescriptor> InOrder(TirModule module)
    {
        var ordered = new List<TirTypeDescriptor>();
        var state = new Dictionary<TirTypeDescriptor, bool>(); // false: being visited, true: written

        void Visit(TirTypeDescriptor type)
        {
            if (state.TryGetValue(type, out var done))
            {
                if (!done)
                {
                    throw new ChipSharpException($"The type descriptor {type.Name} names itself through its base, element " +
                        "type or interfaces; the c code generator cannot write it.");
                }

                return;
            }

            state[type] = false;
            foreach (var named in Named(type))
            {
                Visit(named);
            }

            state[type] = true;
            ordered.Add(type);
        }

        foreach (var type in module.TypeDescriptors)
        {
            Visit(type);
        }

        return ordered;
    }

    /// <summary>
    /// Writes the struct types of the descriptors, <c>struct cs_type_N</c> for each length N of a virtual method table
    /// the module has, and returns their tags by N.
    /// </summary>
    public static Dictionary<int, string> WriteDescriptorTypes(StringBuilder text, TirModule module, ISet<string> tags)
    {
        var structs = new Dictionary<int, string>();
        foreach (var count in module.TypeDescriptors.Where(type => type.ExternalSymbol is null)
                     .Select(type => type.VirtualMethods.Count).Distinct().Order())
        {
            var tag = CTypes.Unique(tags, "cs_type_" + count.ToString(CultureInfo.InvariantCulture));
            structs[count] = tag;
            text.Append($"struct {tag}\n{{\n");
            foreach (var field in DescriptorFields)
            {
                text.Append("    ").Append(field).Append(";\n");
            }

            if (count > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"    uint8_t *virtuals[{count}];\n");
            }

            text.Append("};\n\n");
        }

        return structs;
    }

    /// <summary>The checks that each descriptor struct is laid out as <see cref="TirTypeDescriptor.Layout"/> is.</summary>
    public static IEnumerable<string> DescriptorChecks(IReadOnlyDictionary<int, string> structs, TirLayout layout,
        ISet<string> names)
    {
        var offsets = layout.OffsetsOf(TirTypeDescriptor.Layout);
        foreach (var (count, tag) in structs.OrderBy(pair => pair.Key))
        {
            var fields = TirTypeDescriptor.Layout.Fields.Take(count > 0 ? offsets.Count : offsets.Count - 1).Skip(1);
            yield return CTypes.Check(names, "cs_layout_" + tag, [.. fields.Select(field =>
                $"offsetof(struct {tag}, {field.Name}) == {offsets[field.Index].ToString(CultureInfo.InvariantCulture)}")]);
        }
    }

    /// <summary>
    /// Writes every type descriptor of the module with what it points to, each after those it names: the u32 offsets of
    /// its references, its name (UTF-8 ending with a 0 byte), its interfaces' method tables and its interface map, then
    /// the descriptor. A descriptor the runtime defines is declared, <c>extern const</c>.
    /// </summary>
    public static void WriteDescriptors(StringBuilder text, TirModule module, CTypes types, CSymbols symbols,
        IReadOnlyDictionary<int, string> structs, string? qualifier)
    {
        var layout = types.Layout;
        string Function(TirFunction? function) => function is null ? "0" : $"(uint8_t *)&{symbols.Functions[function]}";
        string Descriptor(TirTypeDescriptor type) => $"(uint8_t *)&{symbols.Types[type]}";

        foreach (var type in InOrder(module))
        {
            var symbol = symbols.Types[type];
            if (type.ExternalSymbol is not null)
            {
                // the runtime defines it (a string's, in chipsharp_string.c): the module only names it
                text.Append($"/* {CText.Comment(type.Name)}: the runtime's */\n");
                text.Append("extern const uint8_t ").Append(qualifier is null ? "" : qualifier + " ").Append(symbol)
                    .Append(";\n\n");
                continue;
            }

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

            text.Append($"/* {CText.Comment(type.Name)} */\n");
            var referencesValue = "0";
            if (references.Count > 0)
            {
                var name = CTypes.Unique(symbols.Taken, symbol + "_references");
                text.Append(Constant("uint32_t", name, qualifier))
                    .Append(CultureInfo.InvariantCulture, $"[{references.Count}] = {{ ")
                    .Append(string.Join(", ", references.Select(offset => offset.ToString(CultureInfo.InvariantCulture))))
                    .Append(" };\n");
                referencesValue = $"(uint8_t *){name}";
            }

            var nameBytes = Encoding.UTF8.GetBytes((type.RuntimeName ?? type.Name) + "\0");
            var nameSymbol = CTypes.Unique(symbols.Taken, symbol + "_name");
            text.Append(Constant("uint8_t", nameSymbol, qualifier))
                .Append(CultureInfo.InvariantCulture, $"[{nameBytes.Length}] = {CText.Bytes(nameBytes, "    ")};");
            if (CText.TextOf(nameBytes) is { } nameText)
            {
                text.Append(" /* ").Append(nameText).Append(" */");
            }

            text.Append('\n');

            var interfacesValue = "0";
            if (type.Interfaces.Count > 0)
            {
                var entries = new List<string>();
                for (var i = 0; i < type.Interfaces.Count; i++)
                {
                    var methods = type.Interfaces[i].Methods;
                    var table = CTypes.Unique(symbols.Taken, symbol + "_interface" + i.ToString(CultureInfo.InvariantCulture));
                    text.Append(Constant("uint8_t *", table, qualifier))
                        .Append(CultureInfo.InvariantCulture, $"[{Math.Max(methods.Count, 1)}] = {{ ")
                        .Append(methods.Count == 0 ? "0" : string.Join(", ", methods.Select(Function)))
                        .Append(" };\n");
                    entries.Add($"{Descriptor(type.Interfaces[i].Interface)}, (uint8_t *){table}");
                }

                var map = CTypes.Unique(symbols.Taken, symbol + "_interfaces");
                text.Append(Constant("uint8_t *", map, qualifier))
                    .Append(CultureInfo.InvariantCulture, $"[{2 * entries.Count}] = {{ ")
                    .Append(string.Join(",\n    ", entries)).Append(" };\n");
                interfacesValue = $"(uint8_t *){map}";
            }

            var linked = type.Base ?? type.ElementDescriptor;
            var virtuals = type.VirtualMethods;
            text.Append(Constant("struct " + structs[virtuals.Count], symbol, qualifier)).Append(" =\n{\n")
                .Append(CultureInfo.InvariantCulture,
                    $"    {size}, {elementSize}, {flags}, {references.Count}, {referencesValue},\n")
                .Append(CultureInfo.InvariantCulture,
                    $"    {(linked is null ? "0" : Descriptor(linked))}, (uint8_t *){nameSymbol}, {interfacesValue}, " +
                    $"{type.Interfaces.Count}, {virtuals.Count}");
            if (virtuals.Count > 0)
            {
                text.Append(",\n    { ").Append(string.Join(", ", virtuals.Select(Function))).Append(" }");
            }

            text.Append("\n};\n\n");
        }
    }

    /// <summary>
    /// The struct of a constant object with <paramref name="length"/> bytes of elements, laid out as an array object on
    /// the heap: its header, its length, zeros up to <see cref="TirLayout.ArrayDataOffset"/>, then its elements' bytes.
    /// Written once per length into <paramref name="text"/>; returns its tag.
    /// </summary>
    public static string ObjectType(StringBuilder text, Dictionary<int, string> written, int length, TirLayout layout,
        ISet<string> tags, List<string> checks, ISet<string> names)
    {
        if (written.TryGetValue(length, out var tag))
        {
            return tag;
        }

        tag = CTypes.Unique(tags, "cs_object_" + length.ToString(CultureInfo.InvariantCulture));
        written[length] = tag;
        var padding = layout.ArrayDataOffset - layout.ArrayLengthOffset - 4;
        text.Append($"struct {tag}\n{{\n    uint8_t *header;\n    uint32_t length;\n");
        if (padding > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"    uint8_t padding[{padding}];\n");
        }

        if (length > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"    uint8_t data[{length}];\n");
        }

        text.Append("};\n\n");
        List<string> conditions =
            [$"offsetof(struct {tag}, length) == {layout.ArrayLengthOffset.ToString(CultureInfo.InvariantCulture)}"];
        if (length > 0)
        {
            conditions.Add($"offsetof(struct {tag}, data) == {layout.ArrayDataOffset.ToString(CultureInfo.InvariantCulture)}");
        }

        checks.Add(CTypes.Check(names, "cs_layout_" + tag, conditions));
        return tag;
    }

    /// <summary>
    /// The value of a constant object (<see cref="TirModule.AddConstantObject"/>): its header (the descriptor's
    /// address), its length, zeros, then its elements' bytes.
    /// </summary>
    public static string ObjectValue(TirGlobal global, CSymbols symbols, TirLayout layout)
    {
        var padding = layout.ArrayDataOffset - layout.ArrayLengthOffset - 4;
        var parts = new List<string>
        {
            $"(uint8_t *)&{symbols.Types[global.ObjectType!]}",
            global.ObjectLength.ToString(CultureInfo.InvariantCulture),
        };
        if (padding > 0)
        {
            parts.Add("{ 0 }");
        }

        if (global.Data!.Count > 0)
        {
            parts.Add(CText.Bytes(global.Data, "    "));
        }

        return "{ " + string.Join(", ", parts) + " }";
    }

    /// <summary>
    /// The table of static roots, <c>cs_roots</c>, when the module's variable globals hold refs: one
    /// <c>{ address, count }</c> per run of refs one after another in a global (a struct of its own, <c>cs_root</c>, laid
    /// out as the runtime's <c>chipsharp_root</c>); returns its name and how many entries it has (null and 0 for none).
    /// </summary>
    public static (string? Name, int Count) WriteRoots(StringBuilder text, TirModule module, CTypes types,
        CSymbols symbols, ISet<string> tags, string? qualifier)
    {
        var word = types.Layout.PointerSize;
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
                    ? $"(uint8_t *)&{symbols.Globals[global]}"
                    : $"(uint8_t *)&{symbols.Globals[global]} + {offsets[i].ToString(CultureInfo.InvariantCulture)}";
                entries.Add($"{{ {address}, {run.ToString(CultureInfo.InvariantCulture)} }}");
                i += run;
            }
        }

        if (entries.Count == 0)
        {
            return (null, 0);
        }

        var tag = CTypes.Unique(tags, "cs_root");
        var name = CTypes.Unique(symbols.Taken, "cs_roots");
        text.Append("/* the static roots: where the refs of the static fields are, for the garbage collector */\n")
            .Append($"struct {tag}\n{{\n    uint8_t *address;\n    {types.PointerInteger} count;\n}};\n\n")
            .Append(Constant("struct " + tag, name, qualifier))
            .Append(CultureInfo.InvariantCulture, $"[{entries.Count}] =\n{{\n    ")
            .Append(string.Join(",\n    ", entries)).Append("\n};\n\n");
        return (name, entries.Count);
    }

    /// <summary>The C declarations of the descriptor struct's fields before its virtual method table, in order.</summary>
    private static readonly string[] DescriptorFields =
    [
        "uint32_t size", "uint32_t element_size", "uint32_t flags", "uint32_t reference_count", "uint8_t *references",
        "uint8_t *base", "uint8_t *name", "uint8_t *interfaces", "uint32_t interface_count", "uint32_t virtual_count",
    ];

    /// <summary>The descriptors <paramref name="type"/>'s descriptor names: its base, its element type's, its interfaces'.</summary>
    private static IEnumerable<TirTypeDescriptor> Named(TirTypeDescriptor type)
    {
        if ((type.Base ?? type.ElementDescriptor) is { } linked)
        {
            yield return linked;
        }

        foreach (var entry in type.Interfaces)
        {
            yield return entry.Interface;
        }
    }
}
