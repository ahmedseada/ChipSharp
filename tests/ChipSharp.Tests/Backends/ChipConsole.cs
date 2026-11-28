using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Backend.C;
using ChipSharp.Ir;

namespace ChipSharp.Tests.Backends;

/// <summary>
/// A startup and a console for the SDCC chips, for the tests only, until the chips' runtimes come (milestone M6, wave 2):
/// <c>main()</c> runs the program and then stops the chip (the PDK14's <c>stopsys</c>, the STM8's <c>halt</c>, with
/// interrupts off: the simulator takes it as the program's end); each <c>Console.Write</c> and <c>WriteLine</c> the
/// program calls reports what it writes to the test, a kind and 32 bits at a time, through two variables and a call of
/// <c>report()</c>, where the simulator's hook reads them (a string char by char, from RAM or code memory, as the
/// program's C has it); and the test writes the text of each as .NET writes it (<see cref="Text"/>): a number in
/// decimal, a float its shortest text, a char as UTF-16 made UTF-8 at the end. Each runtime function that stops the
/// program (an index outside an array, a division by zero) reports which one it is, then stops the chip.
/// </summary>
/// <remarks>
/// So the chip runs only what the C code generator and SDCC made of the program, and a little C here: what it prints is
/// formatted by .NET itself, never by C that could share a mistake with the program's. A runtime function that returns a
/// value (the float math, fixed text's comparisons) is the runtime's own work: a program that calls one is not run yet
/// (<see cref="Missing"/>).
/// </remarks>
internal static class ChipConsole
{
    private const int Int32Kind = 1, UInt32Kind = 2, Int64Kind = 3, UInt64Kind = 4, BoolKind = 5, CharKind = 6, Float32Kind = 7,
        LineKind = 8, LowKind = 9, HighKind = 10, HexKind = 11, EndKind = 12, FailKind = 13;

    /// <summary>What chipsharp_stack_overflow reports as the failure: no runtime function's number.</summary>
    private const int StackOverflow = 0xFFFF;

    /// <summary>The kinds of Write and WriteLine, by the name of what they write: WriteLineInt32 and WriteInt32 are Int32's.</summary>
    private static readonly Dictionary<string, int> Kinds = new(StringComparer.Ordinal)
    {
        ["Int32"] = Int32Kind, ["UInt32"] = UInt32Kind, ["Int64"] = Int64Kind, ["UInt64"] = UInt64Kind, ["Bool"] = BoolKind,
        ["Char"] = CharKind, ["Float32"] = Float32Kind,
    };

    /// <summary>The file of report(), on its own: SDCC leaves out a call of an empty function it can see.</summary>
    public static GeneratedFile ReportFile { get; } = new("report.c", "void report(void) {}\n");

    /// <summary>
    /// The runtime functions <paramref name="module"/> calls that this console has not: each one that returns a value
    /// (none of Console's does), by its TIR name.
    /// </summary>
    public static IReadOnlyList<string> Missing(TirModule module) =>
    [
        .. Externals(module).Where(function => function.ReturnType != TirType.Void).Select(function => function.Name).Order(),
    ];

    /// <summary>
    /// The startup and the console for <paramref name="module"/> on <paramref name="target"/>: main(), the string
    /// descriptors fixed text names, and each runtime function the program calls, as its C declares it.
    /// </summary>
    public static GeneratedFile File(TirModule module, TargetDescription target)
    {
        var layout = new TirLayout(target.PointerSize, 1);
        var types = new CTypes(layout);
        types.Name(module, new HashSet<string>(StringComparer.Ordinal));
        var text = new StringBuilder($$"""
            /* The tests' startup and console for a chip whose runtime is still to come: what the program writes goes to the
               test through cs_kind and cs_value, at each call of report() (see ChipConsole). */
            #include <stdint.h>

            #pragma disable_warning 85

            volatile uint8_t cs_kind;
            volatile uint32_t cs_value;
            void report(void);
            int32_t chipsharp_entry(void);

            #define PUT(kind, value) do { cs_value = (uint32_t)(value); cs_kind = (kind); report(); } while (0)
            #define LENGTH_OFFSET {{layout.ArrayLengthOffset.ToString(CultureInfo.InvariantCulture)}}
            #define DATA_OFFSET {{layout.ArrayDataOffset.ToString(CultureInfo.InvariantCulture)}}

            #ifdef __SDCC_pdk14
            #define STOP() __asm__("disgint\n stopsys")
            #else
            #define STOP() __asm__("sim\n halt")
            #endif

            /* fixed text's descriptors: a string's chars take one byte each, or two */
            const uint8_t chipsharp_string_type = 1;
            const uint8_t chipsharp_wide_string_type = 2;

            static void write_string(const uint8_t *s)
            {
                if (s == 0)
                {
                    return;
                }

                uint8_t wide = *(const uint8_t *const *)s == &chipsharp_wide_string_type;
                uint16_t length = (uint16_t)*(const int32_t *)(s + LENGTH_OFFSET);
                const uint8_t *data = s + DATA_OFFSET;
                for (uint16_t i = 0; i < length; i++)
                {
                    PUT({{CharKind}}, wide ? ((const uint16_t *)data)[i] : data[i]);
                }
            }

            /* what a function that checks the stack calls when it has run out */
            void chipsharp_stack_overflow(void)
            {
                PUT({{FailKind}}, {{StackOverflow}});
                STOP();
            }


            """.ReplaceLineEndings("\n"));

        var failures = Failures(module);
        foreach (var function in Externals(module).Where(function => function.ReturnType == TirType.Void))
        {
            var symbol = CText.Symbol(function.Name);
            var names = function.Parameters.Select((_, i) => "p" + i.ToString(CultureInfo.InvariantCulture)).ToList();
            text.Append(CFunctionWriter.Signature(function, types, symbol, names, reentrant: false)).Append("\n{\n");
            text.Append(Body(function.Name, names, failures));
            text.Append("}\n\n");
        }

        // on the PFS154: the watchdog off (it is on after reset), the system clock IHRC / 8 (2 MHz), before main()
        text.Append("""
            #ifdef __SDCC_pdk14
            __sfr __at(0x03) CLKMD;
            unsigned char __sdcc_external_startup(void) { CLKMD = 0x3c; return 0; }
            #endif

            void main(void)
            {
                int32_t result = chipsharp_entry();
                PUT(
            """.ReplaceLineEndings("\n")).Append(EndKind.ToString(CultureInfo.InvariantCulture))
            .Append(", (uint16_t)result);\n    STOP();\n}\n");
        return new GeneratedFile("chipsharp_console.c", text.ToString());
    }

    /// <summary>
    /// The text the program wrote, as .NET writes it, from what it reported (kind, value), with the program's result;
    /// or why it stopped, when a runtime function stopped it.
    /// </summary>
    public static (byte[] Output, string? Failure, bool Ended) Text(IEnumerable<(byte Kind, uint Value)> reports, TirModule module)
    {
        var text = new StringBuilder();
        uint low = 0, high = 0;
        string? failure = null;
        var ended = false;
        foreach (var (kind, value) in reports)
        {
            switch (kind)
            {
                case Int32Kind:
                    text.Append(((int)value).ToString(CultureInfo.InvariantCulture));
                    break;
                case UInt32Kind:
                    text.Append(value.ToString(CultureInfo.InvariantCulture));
                    break;
                case LowKind:
                    low = value;
                    break;
                case HighKind:
                    high = value;
                    break;
                case Int64Kind:
                    text.Append(((long)(((ulong)value << 32) | low)).ToString(CultureInfo.InvariantCulture));
                    break;
                case UInt64Kind:
                    text.Append((((ulong)value << 32) | low).ToString(CultureInfo.InvariantCulture));
                    break;
                case BoolKind:
                    text.Append(value != 0 ? "True" : "False");
                    break;
                case CharKind:
                    text.Append((char)value);
                    break;
                case Float32Kind:
                    text.Append(BitConverter.UInt32BitsToSingle(value).ToString(CultureInfo.InvariantCulture));
                    break;
                case LineKind:
                    text.Append("\r\n");
                    break;
                case HexKind:
                    // the last count hex digits of the 64 bits, as ChipSharp_Runtime_WriteHexDigits writes them
                    var bits = ((ulong)high << 32) | low;
                    var count = Math.Min((int)(value & 0xFF), 16);
                    var digits = bits.ToString((value >> 8) != 0 ? "X16" : "x16", CultureInfo.InvariantCulture);
                    text.Append(digits[(16 - count)..]);
                    break;
                case EndKind:
                    ended = true;
                    break;
                case FailKind:
                    failure = value == StackOverflow ? "chipsharp_stack_overflow" : Failures(module)[(int)value];
                    break;
            }
        }

        // a high surrogate still waiting for its low one when the program ends is not written, as .NET writes it
        var written = text.Length > 0 && char.IsHighSurrogate(text[^1]) ? text.ToString(0, text.Length - 1) : text.ToString();
        return (Encoding.UTF8.GetBytes(written), failure, ended);
    }

    /// <summary>The C of a runtime function: what it writes, reported, or (a failure) which one it is, and the stop.</summary>
    private static string Body(string name, IReadOnlyList<string> parameters, List<string> failures)
    {
        const string prefix = "ChipSharp.Runtime.";
        var what = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
        var line = what.StartsWith("WriteLine", StringComparison.Ordinal);
        var kind = what.StartsWith("Write", StringComparison.Ordinal) ? what[(line ? "WriteLine" : "Write").Length..] : null;
        var end = line ? $"    PUT({LineKind}, 0);\n" : "";
        string Put(int k, string value) => $"    PUT({k.ToString(CultureInfo.InvariantCulture)}, {value});\n";
        return kind switch
        {
            "" when line => end,
            "String" => $"    write_string({parameters[0]});\n" + end,
            "Int64" or "UInt64" => Put(LowKind, $"(uint32_t){parameters[0]}") +
                Put(Kinds[kind], $"(uint32_t)({parameters[0]} >> 32)") + end,
            "Float32" => $"    union {{ float value; uint32_t bits; }} number;\n    number.value = {parameters[0]};\n" +
                Put(Float32Kind, "number.bits") + end,
            "Chars" => $"    for (uint16_t i = 0; i < (uint16_t){parameters[1]}; i++)\n    {{\n" +
                $"        PUT({CharKind}, ((const uint16_t *){parameters[0]})[i]);\n    }}\n",
            "HexDigits" => Put(LowKind, $"(uint32_t){parameters[0]}") + Put(HighKind, $"(uint32_t)({parameters[0]} >> 32)") +
                Put(HexKind, $"({parameters[1]} & 0xFF) | ({parameters[2]} != 0 ? 0x100 : 0)"),
            not null when Kinds.TryGetValue(kind, out var k) => Put(k, parameters[0]) + end,
            _ => string.Concat(parameters.Select(parameter => $"    (void){parameter};\n")) +
                Put(FailKind, failures.IndexOf(name).ToString(CultureInfo.InvariantCulture)) + "    STOP();\n",
        };
    }

    /// <summary>The runtime functions that stop the program (every one but Console's), in the order of their numbers.</summary>
    private static List<string> Failures(TirModule module) =>
    [
        .. Externals(module).Where(function => function.ReturnType == TirType.Void
                && !function.Name.StartsWith("ChipSharp.Runtime.Write", StringComparison.Ordinal))
            .Select(function => function.Name).Order(StringComparer.Ordinal),
    ];

    /// <summary>The external functions of <paramref name="module"/> but the C library's (SDCC's library has them).</summary>
    private static IEnumerable<TirFunction> Externals(TirModule module) => module.Functions
        .Where(function => function.Kind == TirFunctionKind.External && !CNeeds.IsLibrary(CText.Symbol(function.Name)));
}
