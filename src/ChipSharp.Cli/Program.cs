// chipsharp: the command line. For now it knows the target files and reads IL:
//
//   chipsharp targets list           every target
//   chipsharp targets show <name>    one target, with everything it inherits merged in
//   chipsharp il <assembly.dll>      the IL of every method, ildasm style
//
// Options: --targets <folder>  (default: $env:CHIPSHARP_TARGETS, then the targets folder next to chipsharp.exe)
//          --method <name>     with il: only the methods with this display name, e.g. Program.Main

using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;
using ChipSharp.Targets;

try
{
    var rest = new List<string>(args);
    var folder = TakeOption(rest, "--targets")
        ?? Environment.GetEnvironmentVariable(TargetLoader.EnvironmentVariable)
        ?? Path.Combine(AppContext.BaseDirectory, "targets");
    var methodName = TakeOption(rest, "--method");

    switch (rest)
    {
        case ["targets", "list"]:
        {
            foreach (var targetName in new TargetLoader(folder).Names)
            {
                Console.WriteLine(targetName);
            }

            return 0;
        }

        case ["targets", "show", var name]:
        {
            Print(new TargetLoader(folder).Load(name));
            return 0;
        }

        case ["il", var path]:
        {
            var assembly = IlAssembly.Load(path);
            if (methodName is null)
            {
                Console.Write(IlPrinter.Print(assembly));
                return 0;
            }

            var methods = assembly.FindMethods(methodName);
            if (methods.Count == 0)
            {
                throw new ChipSharpException($"{path} has no method named '{methodName}' (write it as Type.Method, e.g. Program.Main).");
            }

            Console.Write(IlPrinter.Print(methods));
            return 0;
        }

        default:
        {
            Console.WriteLine("""
                usage:
                  chipsharp targets list
                  chipsharp targets show <name>
                  chipsharp il <assembly.dll> [--method <name>]
                options:
                  --targets <folder>   where the target files are (or set CHIPSHARP_TARGETS)
                  --method <name>      il: only the methods with this display name, e.g. Program.Main
                """);
            return 2;
        }
    }
}
catch (ChipSharpException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static string? TakeOption(List<string> args, string option)
{
    var i = args.FindIndex(a => string.Equals(a, option, StringComparison.OrdinalIgnoreCase));
    if (i < 0)
    {
        return null;
    }

    if (i == args.Count - 1)
    {
        throw new ChipSharpException($"{option} needs a value.");
    }

    var value = args[i + 1];
    args.RemoveRange(i, 2);
    return value;
}

static void Print(TargetDescription t)
{
    static void Row(string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Console.WriteLine($"  {label,-13}{value}");
        }
    }

    static string Bytes(ulong n) => n switch
    {
        >= 1UL << 20 when n % (1UL << 20) == 0 => $"{n >> 20} MB",
        >= 1UL << 10 when n % (1UL << 10) == 0 => $"{n >> 10} KB",
        _ => $"{n} bytes",
    };

    Console.WriteLine($"Target {t.Name}   ({string.Join(" -> ", t.InheritanceChain)})");
    Row("backend", t.Backend);
    Row("toolchain", t.Toolchain);
    Row("runtime", t.Runtime);
    Row("triple", t.Triple);
    Row("cpu", t.Cpu);
    Row("pointer size", $"{t.PointerSize} bytes, {t.Endian.ToString().ToLowerInvariant()} endian");
    Row("image", t.Image);
    Row("flasher", t.Flasher);
    if (t.Memory.Count > 0)
    {
        Console.WriteLine("  memory");
        foreach (var (name, region) in t.Memory)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {name,-11}0x{region.Origin:X8}  {Bytes(region.Size)}"));
        }
    }

    Row("capabilities", string.Join(", ", t.Capabilities.Select(c => $"{c.Key}={c.Value}")));
    Row("pins", string.Join(", ", t.Pins.Select(p => $"{p.Key}={p.Value}")));
    Row("incomplete", t.IsComplete
        ? null
        : $"{string.Join(", ", t.MissingValues)} (a target that inherits this one must set it; build a board instead)");
}
