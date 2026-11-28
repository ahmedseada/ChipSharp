using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;

namespace ChipSharp.Chips;

/// <summary>The C a header is written in: the compiler's way to reach registers and to name an interrupt's handler.</summary>
public enum CDialect
{
    /// <summary>
    /// SDCC (the Padauk and STM8 chips): an I/O-space register as <c>__sfr __at(address) NAME;</c> (the Padauk's, which
    /// only mov.io, set0, set1 and t0sn reach), a memory one as a volatile pointer; a handler as SDCC's
    /// <c>__interrupt(n)</c>, from which SDCC makes the vector table.
    /// </summary>
    Sdcc,

    /// <summary>Clang and GCC (Arm, RISC-V, AVR): registers as volatile pointers; the vector table a C array of weak handlers.</summary>
    Gnu,
}

/// <summary>
/// Writes the C a chip's runtime uses from its <see cref="ChipDescription"/>, so that no register address, bit, interrupt
/// number or pin is typed by hand:
/// <list type="bullet">
/// <item><see cref="Registers"/> (<c>&lt;chip&gt;_registers.h</c>): each register by its C name, a volatile pointer's
/// target (<c>UART1_SR</c>) or SDCC's <c>__sfr</c>, its address (<c>UART1_SR_ADDRESS</c>), and each field's position and
/// mask (<c>UART1_SR_TXE_POS</c>, <c>UART1_SR_TXE_MASK</c>), a many-bit field's value in place
/// (<c>CLK_CKDIVR_HSIDIV(3)</c>), its named values in place (<c>CLK_CKDIVR_HSIDIV_DIV8</c>); and the chip's constants.</item>
/// <item><see cref="Interrupts"/> (<c>&lt;chip&gt;_interrupts.h</c>): each interrupt's number (<c>STM8S103F3_IRQ_UART1_RX</c>),
/// how many there are, and for SDCC a handler's head (<c>STM8S103F3_HANDLER(UART1_RX)</c>); on a chip with one vector, each
/// source's request and enable bits (<c>PFS154_INTERRUPT_T16_MASK</c>).</item>
/// <item><see cref="Vectors"/> (<c>&lt;chip&gt;_vectors.c</c>, for clang and GCC): the table of the interrupts' handlers by
/// number, each a weak <c>&lt;chip&gt;_irq_&lt;NAME&gt;</c> a runtime replaces, the others the table's default.</item>
/// <item><see cref="Pins"/> (<c>&lt;chip&gt;_pins.h</c>): each pin's number, port, bit and mask, which numbers are pins,
/// each port's registers by role (out, in, direction, ...) and, in memory, their addresses port by port, and the pin of
/// each function only one pin has (<c>STM8S103F3_UART1_TX_PIN</c>).</item>
/// </list>
/// The headers need only <c>&lt;stdint.h&gt;</c>, which every compiler has even freestanding. They are made by the tests
/// from the repository's chips folder and kept next to the runtimes that use them (see the README).
/// </summary>
public static class ChipHeaders
{
    /// <summary>The headers (and for <see cref="CDialect.Gnu"/>, the vector table) of <paramref name="chip"/>.</summary>
    public static IReadOnlyList<GeneratedFile> All(ChipDescription chip, CDialect dialect, string source)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var name = FileStem(chip);
        return
        [
            new GeneratedFile($"{name}_registers.h", Registers(chip, dialect, source)),
            new GeneratedFile($"{name}_interrupts.h", Interrupts(chip, dialect, source)),
            new GeneratedFile($"{name}_pins.h", Pins(chip, source)),
            .. dialect == CDialect.Gnu ? [new GeneratedFile($"{name}_vectors.c", Vectors(chip, source))] : Array.Empty<GeneratedFile>(),
        ];
    }

    /// <summary>The start of the generated files' names: the chip's name in lower case, e.g. "stm8s103f3", "rp2040".</summary>
    public static string FileStem(ChipDescription chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        return Identifier(chip.Name).ToLowerInvariant();
    }

    /// <summary>The registers' header.</summary>
    public static string Registers(ChipDescription chip, CDialect dialect, string source)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var guard = $"{Prefix(chip)}_REGISTERS_H";
        var text = Start(chip, "registers", source, guard).Append("#include <stdint.h>\n");
        var written = new HashSet<string>(StringComparer.Ordinal);
        void Define(string name, string value, string? comment = null)
        {
            if (written.Add(name.Split('(')[0]))
            {
                text.Append("#define ").Append(name).Append(' ').Append(value)
                    .Append(comment is null ? "" : "   // " + Comment(comment)).Append('\n');
            }
        }

        foreach (var peripheral in chip.Peripherals.Where(p => p.Registers.Count > 0))
        {
            text.Append("\n// ---- ").Append(peripheral.Name).Append(peripheral.Description is { } d ? ": " + d : "")
                .Append(peripheral.Space == AddressSpace.Io ? " (I/O space)" : "").Append('\n');
            foreach (var register in peripheral.Registers.OrderBy(r => r.Address))
            {
                var name = Identifier(register.FullName);
                text.Append('\n').Append("// ").Append(name).Append(register.Description is { } rd ? ": " + rd : "")
                    .Append(" (").Append(Access(register.Access))
                    .Append(register.Reset is { } reset ? ", reset " + Hex(reset, register.Size) : "").Append(")\n");
                Define($"{name}_ADDRESS", Literal(register.Address));
                if (peripheral.Space == AddressSpace.Io)
                {
                    if (register.Size != 8)
                    {
                        throw new ChipSharpException($"{chip.Name}: {name} is in the I/O space but {register.Size} bits: " +
                            "an I/O register is a byte.");
                    }

                    if (dialect == CDialect.Sdcc && written.Add(name))
                    {
                        text.Append("__sfr __at(").Append(Hex(register.Address, 8)).Append(") ").Append(name).Append(";\n");
                    }
                }
                else
                {
                    Define(name, $"(*(volatile uint{register.Size}_t *){Literal(register.Address)})");
                }

                foreach (var field in register.Fields)
                {
                    var fieldName = $"{name}_{Identifier(field.Name)}";
                    var comment = field.Description is { } fd
                        ? $"{fd}{(field.Access != register.Access ? $" ({Access(field.Access)})" : "")}"
                        : field.Access != register.Access ? Access(field.Access) : null;
                    Define($"{fieldName}_POS", field.Offset.ToString(CultureInfo.InvariantCulture), comment);
                    Define($"{fieldName}_MASK", Literal(field.Mask));
                    if (field.Width > 1)
                    {
                        Define($"{fieldName}(value)", $"((({Cast(register.Size)}(value)) << {field.Offset}) & {fieldName}_MASK)");
                    }

                    foreach (var value in field.Values)
                    {
                        Define($"{fieldName}_{Identifier(value.Name)}", $"({Literal(value.Value)} << {field.Offset})",
                            value.Description);
                    }
                }
            }
        }

        if (chip.Constants.Count > 0)
        {
            text.Append("\n// ---- Constants\n\n");
            foreach (var constant in chip.Constants)
            {
                Define(Identifier(constant.Name), Literal(constant.Value), constant.Description);
            }
        }

        return End(text, guard);
    }

    /// <summary>The interrupts' header.</summary>
    public static string Interrupts(ChipDescription chip, CDialect dialect, string source)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var prefix = Prefix(chip);
        var guard = $"{prefix}_INTERRUPTS_H";
        var text = Start(chip, "interrupts", source, guard);
        var vectors = chip.Interrupts.Select(i => i.Number).Distinct().Count();
        var shared = chip.Interrupts.Count > 1 && vectors == 1;
        text.Append(shared
            ? $"// One vector, {chip.Interrupts[0].Number}, for every interrupt: the handler finds out which source asked (its\n" +
              "// request bit), each enabled by its enable bit.\n\n"
            : chip.InterruptMatrix
            ? "// The interrupt sources by number: the interrupt matrix routes each to one of the processor's interrupts (the\n" +
              "// source's map register names which), whose handler finds out which source asked.\n\n"
            : "// The interrupts by number (IRQ): the vector table's entry after the processor's own.\n\n");
        foreach (var interrupt in chip.Interrupts)
        {
            var name = $"{prefix}_IRQ_{Identifier(interrupt.Name)}";
            text.Append("#define ").Append(name).Append(' ').Append(interrupt.Number.ToString(CultureInfo.InvariantCulture))
                .Append(interrupt.Description is { } d ? "   // " + Comment(d) : "").Append('\n');
            if (interrupt.Request is { } request)
            {
                var source2 = $"{prefix}_INTERRUPT_{Identifier(interrupt.Name)}";
                text.Append("#define ").Append(source2).Append("_BIT ").Append(request.Bit.ToString(CultureInfo.InvariantCulture))
                    .Append('\n')
                    .Append("#define ").Append(source2).Append("_MASK ").Append(Literal(1UL << request.Bit)).Append('\n')
                    .Append("#define ").Append(source2).Append("_REQUEST ").Append(Identifier(request.Register)).Append('\n')
                    .Append("#define ").Append(source2).Append("_ENABLE ").Append(Identifier(request.Enable)).Append('\n');
            }
        }

        var count = chip.Interrupts.Count == 0 ? 0 : chip.Interrupts.Max(i => i.Number) + 1;
        text.Append("\n// How many entries the table of interrupts has: the highest number, and one.\n")
            .Append("#define ").Append(prefix).Append("_IRQ_COUNT ").Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (dialect == CDialect.Sdcc)
        {
            var stem = FileStem(chip);
            text.Append('\n')
                .Append(shared
                    ? $"// The handler's head: SDCC puts it at the vector (in the file with main(), or declared there).\n#define {prefix}_HANDLER void {stem}_interrupt(void) __interrupt({chip.Interrupts[0].Number})\n"
                    : $"// A handler's head by its interrupt's name, e.g. {prefix}_HANDLER(TIM4_UPD_OVF): SDCC puts it in the vector table\n" +
                      $"// (when it is defined, or declared, in the file with main()).\n#define {prefix}_HANDLER(name) void {stem}_isr_##name(void) __interrupt({prefix}_IRQ_##name)\n");
        }
        else
        {
            text.Append($"\n// The handlers {FileStem(chip)}_vectors.c's table names, each weak: a runtime defines those it uses.\n");
            foreach (var interrupt in chip.Interrupts.DistinctBy(i => i.Number))
            {
                text.Append("void ").Append(Handler(chip, interrupt)).Append("(void);\n");
            }
        }

        return End(text, guard);
    }

    /// <summary>The table of the interrupts' handlers by number, for clang and GCC.</summary>
    public static string Vectors(ChipDescription chip, string source)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var stem = FileStem(chip);
        var text = new StringBuilder()
            .Append("// ").Append(chip.Name).Append(" interrupt handlers: generated by ChipSharp.Chips from ").Append(source)
            .Append("; do not edit.\n//\n")
            .Append("// The handler of each interrupt by its number, from 0: a Cortex-M runtime puts the table after the processor's\n")
            .Append("// 16 entries, a RISC-V one dispatches through it. Each handler is weak, the table's default unless the runtime\n")
            .Append("// defines it.\n\n")
            .Append("#include \"").Append(stem).Append("_interrupts.h\"\n\n")
            .Append("// An interrupt nobody handles: the failure stop's breakpoint would be the runtime's; here the core waits.\n")
            .Append("__attribute__((weak)) void ").Append(stem).Append("_irq_unhandled(void)\n{\n    for (;;)\n    {\n    }\n}\n\n");
        var byNumber = chip.Interrupts.DistinctBy(i => i.Number).ToDictionary(i => i.Number);
        foreach (var interrupt in byNumber.Values.OrderBy(i => i.Number))
        {
            text.Append("void ").Append(Handler(chip, interrupt)).Append("(void) __attribute__((weak, alias(\"")
                .Append(stem).Append("_irq_unhandled\")));\n");
        }

        var count = byNumber.Count == 0 ? 0 : byNumber.Keys.Max() + 1;
        text.Append("\nvoid (*const ").Append(stem).Append("_irq_handlers[").Append(Math.Max(count, 1).ToString(CultureInfo.InvariantCulture))
            .Append("])(void) =\n{\n");
        for (var number = 0; number < Math.Max(count, 1); number++)
        {
            text.Append("    ").Append(byNumber.TryGetValue(number, out var interrupt) ? Handler(chip, interrupt) : stem + "_irq_unhandled")
                .Append(",   // ").Append(number.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return text.Append("};\n").ToString();
    }

    /// <summary>The pins' header.</summary>
    public static string Pins(ChipDescription chip, string source)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var prefix = Prefix(chip);
        var guard = $"{prefix}_PINS_H";
        var text = Start(chip, "pins", source, guard);
        if (chip.Pins.Count == 0)
        {
            return End(text.Append("// The description has no pins.\n"), guard);
        }

        text.Append("// The pins by ChipSharp's numbers (port * 8 + bit), as ChipSharp.Hardware.Pin takes them.\n\n");
        foreach (var pin in chip.Pins.OrderBy(p => p.Number))
        {
            var name = $"{prefix}_PIN_{Identifier(pin.Name)}";
            text.Append("#define ").Append(name).Append(' ').Append(pin.Number.ToString(CultureInfo.InvariantCulture))
                .Append(pin.Functions.Count > 0 ? "   // " + string.Join(", ", pin.Functions) : "").Append('\n');
            if (pin.Port is { } port && pin.Bit is { } bit)
            {
                var index = chip.Ports.Select(p => p.Name).ToList().IndexOf(port);
                text.Append("#define ").Append(name).Append("_PORT ").Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n')
                    .Append("#define ").Append(name).Append("_BIT ").Append(bit.ToString(CultureInfo.InvariantCulture)).Append('\n')
                    .Append("#define ").Append(name).Append("_MASK ").Append(Literal(1UL << bit)).Append('\n');
            }
        }

        var highest = chip.Pins.Max(p => p.Number);
        if (highest < 64)
        {
            var mask = chip.Pins.Aggregate(0UL, (all, pin) => all | 1UL << pin.Number);
            text.Append("\n// Which numbers are pins of this package: bit n for pin n.\n")
                .Append("#define ").Append(prefix).Append("_PINS ").Append(Literal(mask)).Append('\n')
                .Append("#define ").Append(prefix).Append("_IS_PIN(pin) ((pin) >= 0 && (pin) <= ")
                .Append(highest.ToString(CultureInfo.InvariantCulture)).Append(" && ((").Append(prefix)
                .Append("_PINS >> (pin)) & 1u) != 0)\n");
        }

        if (chip.Ports.Count > 0)
        {
            text.Append("\n// The ports, in order (a pin's _PORT), and their registers by role (the registers' header has them).\n")
                .Append("#define ").Append(prefix).Append("_PORT_COUNT ").Append(chip.Ports.Count.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
            foreach (var port in chip.Ports)
            {
                foreach (var (role, register) in port.Registers)
                {
                    text.Append("#define ").Append(prefix).Append("_PORT_").Append(Identifier(port.Name)).Append('_')
                        .Append(Identifier(role).ToUpperInvariant()).Append(' ').Append(Identifier(register)).Append('\n');
                }
            }

            // a role every port has, in memory: its registers' addresses port by port, for a runtime's table
            var roles = chip.Ports[0].Registers.Keys.Where(role => chip.Ports.All(p => p.Registers.ContainsKey(role)));
            foreach (var role in roles)
            {
                var registers = chip.Ports.Select(p => chip.Register(p.Registers[role])!).ToList();
                if (registers.All(r => chip.PeripheralOf(r).Space == AddressSpace.Memory))
                {
                    text.Append("#define ").Append(prefix).Append("_PORT_").Append(Identifier(role).ToUpperInvariant())
                        .Append("_ADDRESSES { ").Append(string.Join(", ", registers.Select(r => Literal(r.Address))))
                        .Append(" }\n");
                }
            }
        }

        var functions = chip.Pins.SelectMany(pin => pin.Functions.Select(function => (Function: function, Pin: pin)))
            .GroupBy(f => f.Function).Where(g => g.Count() == 1).Select(g => g.Single()).OrderBy(f => f.Function, StringComparer.Ordinal)
            .ToList();
        if (functions.Count > 0)
        {
            text.Append("\n// The pin of each function only one pin has.\n");
            foreach (var (function, pin) in functions)
            {
                text.Append("#define ").Append(prefix).Append('_').Append(Identifier(function)).Append("_PIN ")
                    .Append(pin.Number.ToString(CultureInfo.InvariantCulture)).Append("   // ").Append(pin.Name).Append('\n');
            }
        }

        return End(text, guard);
    }

    /// <summary>A handler's name in the vector table: <c>&lt;chip&gt;_irq_&lt;NAME&gt;</c>.</summary>
    public static string Handler(ChipDescription chip, ChipInterrupt interrupt)
    {
        ArgumentNullException.ThrowIfNull(chip);
        ArgumentNullException.ThrowIfNull(interrupt);
        return $"{FileStem(chip)}_irq_{Identifier(interrupt.Name)}";
    }

    /// <summary>What the macros of a chip start with: its name in capitals, e.g. "STM8S103F3".</summary>
    public static string Prefix(ChipDescription chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        return Identifier(chip.Name).ToUpperInvariant();
    }

    /// <summary><paramref name="name"/> as a C identifier: other characters as '_', a leading digit after one.</summary>
    public static string Identifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var text = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_')]);
        return text.Length == 0 || char.IsAsciiDigit(text[0]) ? "_" + text : text;
    }

    private static StringBuilder Start(ChipDescription chip, string what, string source, string guard) => new StringBuilder()
        .Append("// ").Append(chip.Name).Append(' ').Append(what).Append(": generated by ChipSharp.Chips from ").Append(source)
        .Append("; do not edit.\n")
        .Append(chip.Vendor is { } vendor ? $"// {vendor}{(chip.Description is { } d ? ": " + Shorten(d) : "")}\n" : "")
        .Append('\n').Append("#ifndef ").Append(guard).Append("\n#define ").Append(guard).Append("\n\n");

    private static string End(StringBuilder text, string guard) => text.Append("\n#endif // ").Append(guard).Append('\n').ToString();

    private static string Shorten(string text) => text.Length <= 100 ? text : text[..97] + "...";

    /// <summary>The text of a // comment: no backslash at its end, which would make the next line of C part of it.</summary>
    private static string Comment(string text) => text.TrimEnd().TrimEnd('\\');

    private static string Access(RegisterAccess access) => access switch
    {
        RegisterAccess.ReadOnly => "read-only",
        RegisterAccess.WriteOnly => "write-only",
        RegisterAccess.WriteOnce => "write once",
        RegisterAccess.ReadWriteOnce => "read, write once",
        _ => "read-write",
    };

    /// <summary>A C cast of a value to the register's type, so that a shift fits it (a 32-bit register on a 16-bit int).</summary>
    private static string Cast(int size) => size <= 16 ? "(unsigned int)" : size <= 32 ? "(unsigned long)" : "(unsigned long long)";

    /// <summary>An unsigned C literal that fits a chip whose int has 16 bits: 0x12u, 0x12345678ul, ...ull.</summary>
    private static string Literal(ulong value) => value <= 0xffff ? $"0x{value:x}u" : value <= 0xffffffff ? $"0x{value:x}ul" : $"0x{value:x}ull";

    private static string Hex(ulong value, int size) =>
        "0x" + value.ToString("x" + (size / 4).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
