using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Runtime.ArmV6M;

namespace ChipSharp.Runtime.Stm8;

/// <summary>
/// The runtime parts every STM8 chip (STM8S, STM8L, STM8AF) shares, as freestanding C that SDCC compiles with the
/// program (the toolchain "sdcc": sdcc -mstm8). SDCC's linker takes an object whole, but a library's modules only when
/// the program uses them, and every file of the runtime but the start-up is a <see cref="GeneratedFile.Library"/> file,
/// which the toolchain puts in the program's library; so the runtime is made of small modules, and a file whose parts a
/// program may use one without the other is cut into modules (<see cref="Modules"/>):
/// <list type="bullet">
/// <item><see cref="HardwareContract.HeaderFileName"/>: the hardware contract (src/ChipSharp.Runtime.Shared), which every
/// runtime C file includes; and <see cref="HeaderFileName"/>, the fixed-width types and the interrupts' helpers the
/// architecture's and a chip runtime's C share.</item>
/// <item><see cref="StartupFileName"/>: main(), in whose file SDCC writes the start-up (the vector table, the variables
/// cleared and set): it runs <c>chipsharp_chip_init</c>, turns interrupts on and runs <c>chipsharp_entry</c>, then
/// <c>chipsharp_halt</c>. The vector table names the interrupt handlers <see cref="VectorsFileName"/> declares: a chip
/// runtime replaces this empty one with its own.</item>
/// <item><see cref="HaltFileName"/>: how a program stops, <c>chipsharp_halt</c> (halt, with nothing left to wake the
/// chip) and <c>chipsharp_halt_failure</c> (break, then halt), both after <c>chipsharp_before_halt</c>.</item>
/// <item><see cref="InterruptsFileName"/>: <c>chipsharp_interrupts_off</c> and <c>chipsharp_interrupts_back</c>, the
/// helpers in assembly a chip runtime turns interrupts off with around what no handler may see half done.</item>
/// <item><see cref="MultiplyFileName"/>: SDCC's helper for a product of 64 bits (a long's, DateTime's ticks),
/// <c>_mullonglong</c>, in place of SDCC's own, which the program's library comes before: the same products, nine
/// times faster.</item>
/// <item><see cref="FailuresFileName"/>: what an index outside an array (and the other failures .NET throws an exception
/// for) says, .NET's messages, each a module of its own; and <c>chipsharp_hardware_failure</c>, the contract's.</item>
/// <item><see cref="DefaultsFileName"/>: the hooks a chip runtime may define, empty (SDCC has no weak functions: a chip
/// runtime that defines one leaves out its module, <see cref="DefaultModuleNames"/>), and
/// <c>ChipSharp_Runtime_LocalUtcOffset</c>, 0.</item>
/// <item><see cref="StringFileName"/> and <see cref="ExceptionsFileName"/>, shared with the other runtimes: what reads
/// strings and chars, and the text of the exceptions a substitute stops with (in modules).</item>
/// </list>
/// <see cref="InterruptEntry"/> gives each event of the target's "interrupts" table the default the program's
/// <c>[Interrupt]</c> method takes the place of: an empty <c>chipsharp_isr_irq&lt;n&gt;</c> in a module of its own, which
/// the linker takes only when the program has no such method. On its own, this runtime runs a program that only computes.
/// </summary>
public sealed class Stm8Runtime : IArchRuntime
{
    /// <summary>The header with the fixed-width types and the interrupts' helpers.</summary>
    public const string HeaderFileName = "stm8_runtime.h";

    /// <summary>The C file with main(), and so SDCC's start-up and the vector table.</summary>
    public const string StartupFileName = "stm8_startup.c";

    /// <summary>The header of the vector table's handlers, which a chip runtime replaces with its own.</summary>
    public const string VectorsFileName = "stm8_vectors.h";

    public const string HaltFileName = "stm8_halt.c";

    public const string InterruptsFileName = "stm8_interrupts.c";

    /// <summary>The C file with the helper for products of 64 bits, in place of SDCC's own.</summary>
    public const string MultiplyFileName = "stm8_multiply.c";

    /// <summary>The C file with the failures' messages, a module each.</summary>
    public const string FailuresFileName = "stm8_failures.c";

    /// <summary>The C file with the empty hooks (a module each), and the time zone's offset.</summary>
    public const string DefaultsFileName = "stm8_defaults.c";

    /// <summary>Strings and chars (src/ChipSharp.Runtime.Shared), in modules.</summary>
    public const string StringFileName = "chipsharp_string.c";

    /// <summary>The text of the exceptions of the library's substitutes (src/ChipSharp.Runtime.Shared), in modules.</summary>
    public const string ExceptionsFileName = "chipsharp_exceptions.c";

    /// <summary>What a line that starts a module of a file starts with, then the module's name.</summary>
    public const string ModuleMarker = "//@module ";

    /// <summary>
    /// The modules of <see cref="DefaultsFileName"/> that a chip runtime which defines their hook leaves out:
    /// <c>chipsharp_chip_init</c>, <c>chipsharp_before_halt</c>, <c>chipsharp_failure_write</c>.
    /// </summary>
    public static IReadOnlyList<string> DefaultModuleNames { get; } =
        ["stm8_defaults_chip_init.c", "stm8_defaults_before_halt.c", "stm8_defaults_failure_write.c"];

    public string Name => "stm8";

    /// <summary>The start-up and the stops, the interrupts' helpers, the failures, the defaults, strings.</summary>
    public RuntimeSources Startup(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources(
        [
            Source(HardwareContract.HeaderFileName), Source(HeaderFileName), Source(StartupFileName), Source(VectorsFileName),
            Source(HaltFileName), Source(InterruptsFileName), Source(MultiplyFileName),
            .. Modules(Source(FailuresFileName)),
            .. Modules(Source(DefaultsFileName)), .. Modules(Source(StringFileName)),
            .. Modules(Source(ExceptionsFileName)),
        ]);
    }

    /// <summary>
    /// For each event of the target's "interrupts" table, the default of the program's method: an empty
    /// <c>chipsharp_isr_irq&lt;n&gt;</c> in a module of its own (<c>stm8_isr_irq&lt;n&gt;.c</c>), which the program's own
    /// takes the place of.
    /// </summary>
    public RuntimeSources InterruptEntry(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([.. EventNumbers(target).Select(DefaultHandler)]);
    }

    /// <summary>Nothing: an STM8 program makes a step atomic by turning interrupts off around it.</summary>
    public RuntimeSources Atomics(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new RuntimeSources([]);
    }

    /// <summary>The interrupt numbers of the events of the target's "interrupts" table, e.g. 30 for "pin".</summary>
    public static IReadOnlyList<int> EventNumbers(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Settings["interrupts"] is not JsonObject events)
        {
            return [];
        }

        return
        [
            .. events.Select(entry => entry.Value is JsonObject { } value && value["irq"] is JsonValue irq
                    && irq.TryGetValue(out int number)
                ? number
                : throw new ChipSharpException($"Target '{target.Name}': 'interrupts.{entry.Key}' must give the event's " +
                    "interrupt number, e.g. { \"irq\": 30 }.")),
        ];
    }

    /// <summary>The module with the empty default of the program's method for interrupt <paramref name="number"/>.</summary>
    public static GeneratedFile DefaultHandler(int number)
    {
        var symbol = "chipsharp_isr_irq" + number.ToString(CultureInfo.InvariantCulture);
        return new GeneratedFile($"stm8_isr_irq{number.ToString(CultureInfo.InvariantCulture)}.c",
            $"// ChipSharp runtime for STM8 chips: the entry of the program's [Interrupt] method of interrupt {number}, " +
            "empty, for a\n// program that has none (the linker takes this module only then). Written by the stm8 " +
            $"runtime.\n\nvoid {symbol}(void)\n{{\n}}\n") { Library = true };
    }

    /// <summary>
    /// The modules SDCC compiles <paramref name="file"/> into: one for each part of it that starts with a line
    /// <c>//@module &lt;name&gt;</c>, named &lt;file&gt;_&lt;name&gt;.c, each with CHIPSHARP_MODULES defined and the text
    /// before the first such line (its comment, the types, macros and declarations the parts share; no definitions but
    /// static ones every part uses, as each module has its own); the file itself when it has no such line. The parts
    /// are those a program may use one without the other (SDCC's linker takes a module whole): a failure's message, a
    /// group of string functions, a pin function.
    /// </summary>
    public static IReadOnlyList<GeneratedFile> Modules(GeneratedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var lines = file.Content.ReplaceLineEndings("\n").Split('\n');
        var first = Array.FindIndex(lines, line => line.StartsWith(ModuleMarker, StringComparison.Ordinal));
        if (first < 0)
        {
            return [file];
        }

        var stem = Path.GetFileNameWithoutExtension(file.FileName);
        var prelude = string.Join('\n', lines[..first]);
        var modules = new List<GeneratedFile>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var start = first; start < lines.Length;)
        {
            var name = lines[start][ModuleMarker.Length..].Trim();
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || !names.Add(name))
            {
                throw new ChipSharpException($"{file.FileName}: '{lines[start]}' must name a module of its own, in letters, " +
                    "digits and '_'.");
            }

            var end = Array.FindIndex(lines, start + 1, line => line.StartsWith(ModuleMarker, StringComparison.Ordinal));
            end = end < 0 ? lines.Length : end;
            var text = new StringBuilder()
                .Append("// The module \"").Append(name).Append("\" of ").Append(file.FileName)
                .Append(", which the stm8 runtime cuts into modules.\n#define CHIPSHARP_MODULES 1\n\n")
                .Append(prelude).Append('\n')
                .Append(string.Join('\n', lines[(start + 1)..end]).TrimEnd()).Append('\n');
            modules.Add(new GeneratedFile($"{stem}_{name}.c", text.ToString()) { Library = file.Library });
            start = end;
        }

        return modules;
    }

    /// <summary>
    /// One of this runtime's files by name, its own or one of src/ChipSharp.Runtime.Shared it ships, e.g.
    /// <see cref="HaltFileName"/>.
    /// </summary>
    public static GeneratedFile Source(string fileName) =>
        new(fileName, EmbeddedText.Read(typeof(Stm8Runtime), fileName)) { Library = fileName != StartupFileName };
}
