using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Contract;

/// <summary>A function as src/ChipSharp.Runtime.Shared/chipsharp_hardware.h declares it, with its section's title and guard.</summary>
internal sealed record DeclaredFunction(
    string Name,
    HardwareType Returns,
    IReadOnlyList<HardwareParameter> Parameters,
    string Section,
    string? Guard,
    bool Reentrant,
    bool NoReturn);

/// <summary>
/// Reads the hardware contract's C header as the tests compare it with ChipSharp.Abstractions' HardwareContract: its
/// sections (<c>// ---- Title: ... (guard) ----</c>) and the prototypes in each, their C types by the contract's names.
/// </summary>
internal static partial class HardwareHeader
{
    /// <summary>The header's path in the repository.</summary>
    public static string Path => System.IO.Path.Combine(Repository.Root, "src", "ChipSharp.Runtime.Shared",
        HardwareContract.HeaderFileName);

    /// <summary>The header's text.</summary>
    public static string Text => File.ReadAllText(Path);

    /// <summary>A runtime's C without its <c>#include</c> of the header, the one header ChipSharp's own C includes.</summary>
    public static string WithoutInclude(string c) =>
        c.Replace($"#include \"{HardwareContract.HeaderFileName}\"", "", StringComparison.Ordinal);

    /// <summary>The C types the contract uses, by their spelling in the header.</summary>
    private static readonly Dictionary<string, HardwareType> Types = new(StringComparer.Ordinal)
    {
        ["void"] = HardwareType.Void,
        ["chipsharp_i32"] = HardwareType.Int32,
        ["chipsharp_u32"] = HardwareType.UInt32,
        ["chipsharp_i64"] = HardwareType.Int64,
        ["chipsharp_f64"] = HardwareType.Float64,
        ["const chipsharp_u8 *"] = HardwareType.Bytes,
        ["chipsharp_u8 *"] = HardwareType.WritableBytes,
        ["const char *"] = HardwareType.Text,
        ["CHIPSHARP_MESSAGE_CHAR *"] = HardwareType.Message,
        ["chipsharp_count"] = HardwareType.Count,
    };

    /// <summary>Every function the header declares, in its order.</summary>
    public static IReadOnlyList<DeclaredFunction> Functions(string text)
    {
        var functions = new List<DeclaredFunction>();
        string? section = null;
        string? guard = null;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (SectionLine().Match(line) is { Success: true } heading)
            {
                section = heading.Groups["title"].Value.Split(':')[0];
                guard = heading.Groups["guard"].Value is "chip" ? null : heading.Groups["guard"].Value;
                continue;
            }

            if (Prototype().Match(line) is not { Success: true } prototype)
            {
                continue;
            }

            Assert.True(section is not null, $"{line}: before the first section");
            var parameters = prototype.Groups["parameters"].Value is "void"
                ? []
                : prototype.Groups["parameters"].Value.Split(", ").Select(Parameter).ToList();
            functions.Add(new DeclaredFunction(prototype.Groups["name"].Value, Type(prototype.Groups["returns"].Value),
                parameters, section!, guard, prototype.Groups["reentrant"].Success, prototype.Groups["noreturn"].Success));
        }

        return functions;
    }

    private static HardwareParameter Parameter(string text)
    {
        var name = Regex.Match(text, @"\w+$").Value;
        return new HardwareParameter(name, Type(text[..^name.Length]));
    }

    private static HardwareType Type(string text)
    {
        var spelled = text.Trim().Replace(" *", "*", StringComparison.Ordinal).Replace("*", " *", StringComparison.Ordinal);
        return Types.TryGetValue(spelled, out var type)
            ? type
            : throw new InvalidOperationException($"'{text}' is not one of the contract's types.");
    }

    [GeneratedRegex(@"^// ---- (?<title>[^()]+) \((?<guard>[^()]+)\) -+$")]
    private static partial Regex SectionLine();

    [GeneratedRegex(@"^(?<noreturn>CHIPSHARP_NORETURN )?(?<returns>[\w ]+?\*?) ?(?<name>\w+)\((?<parameters>[^()]*)\)(?<reentrant> CHIPSHARP_REENTRANT)?;$")]
    private static partial Regex Prototype();
}
